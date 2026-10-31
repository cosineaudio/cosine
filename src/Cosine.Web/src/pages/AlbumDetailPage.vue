<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { Play, Shuffle, ListPlus, ListEnd, User, Heart, MoreHorizontal } from 'lucide-vue-next';
import { getAlbum } from '@/api/browsing';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import PageHeader from '@/components/ui/PageHeader.vue';
import TrackRow from '@/components/TrackRow.vue';
import TrackList from '@/components/TrackList.vue';
import FavoriteButton from '@/components/ui/FavoriteButton.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import SkeletonDetailHeader from '@/components/ui/SkeletonDetailHeader.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import { useDominantColor } from '@/composables/useDominantColor';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { countLabel, formatTotalTime } from '@/lib/format';
import type { AlbumDetail, Track } from '@/api/types';

const props = defineProps<{ id: string }>();

const router = useRouter();
const favorites = useFavoritesStore();
const { playTracks, queueTracks } = usePlayback();

const album = ref<AlbumDetail | null>(null);
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);

const tracks = computed(() => album.value?.song ?? []);
const { tint } = useDominantColor(() => (album.value ? album.value.coverArt ?? album.value.id : null));
const isFav = computed(() => (album.value ? favorites.isAlbum(album.value.id) : false));

// Group by disc only when the album genuinely spans multiple discs.
const discs = computed(() => {
  const nums = new Set(tracks.value.map((t) => t.discNumber ?? 1));
  if (nums.size <= 1) return null;
  const map = new Map<number, Track[]>();
  for (const t of tracks.value) {
    const d = t.discNumber ?? 1;
    if (!map.has(d)) map.set(d, []);
    map.get(d)!.push(t);
  }
  return [...map.entries()].sort((a, b) => a[0] - b[0]);
});

function play(shuffle = false) {
  if (!tracks.value.length) return;
  const list = shuffle ? [...tracks.value].sort(() => Math.random() - 0.5) : tracks.value;
  playTracks(list, 0);
}

const menuItems = computed<MenuItem[]>(() => [
  { label: 'Play next', icon: ListPlus, onSelect: () => queueTracks(tracks.value, 'next') },
  { label: 'Add to queue', icon: ListEnd, onSelect: () => queueTracks(tracks.value, 'end') },
  { separator: true, label: '' },
  {
    label: isFav.value ? 'Remove favorite' : 'Favorite',
    icon: Heart,
    onSelect: () => album.value && favorites.toggle('album', album.value.id, album.value.name),
  },
  ...(album.value?.artistId
    ? [{ label: 'Go to artist', icon: User, onSelect: () => router.push({ name: 'artist', params: { id: album.value!.artistId! } }) } as MenuItem]
    : []),
]);

async function load() {
  loading.value = true;
  album.value = null;
  try {
    album.value = await getAlbum(props.id);
    favorites.ensureLoaded();
  } catch { /* not found handled below */ } finally {
    loading.value = false;
  }
}

onMounted(load);
watch(() => props.id, load);
</script>

<template>
  <div v-if="showSkeleton">
    <SkeletonDetailHeader :actions="3" />
    <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-4">
      <SkeletonTrackList :count="10" />
    </div>
  </div>

  <!-- blank until the skeleton's delay elapses; see useDeferredLoading -->
  <template v-else-if="loading" />

  <div v-else-if="album" class="reveal">
    <ContextMenu :items="menuItems" context-menu>
    <PageHeader kicker="Album" :title="album.name" :cover-art="album.coverArt ?? album.id" :tint="tint">
      <template #meta>
        <RouterLink
          v-if="album.artistId"
          :to="{ name: 'artist', params: { id: album.artistId } }"
          class="font-medium text-text transition-colors hover:text-accent"
        >
          {{ album.artist }}
        </RouterLink>
        <span v-else>{{ album.artist }}</span>
        <span v-if="album.year">· {{ album.year }}</span>
        <span>· {{ countLabel(album.songCount, 'track') }}</span>
        <span>· {{ formatTotalTime(album.duration) }}</span>
      </template>
      <template #actions>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control bg-accent px-5 py-2.5 font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
          @click="play(false)"
        >
          <Play :size="18" :stroke-width="2" fill="currentColor" /> Play
        </button>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control border border-border px-4 py-2.5 font-medium text-text transition-colors hover:bg-surface-2"
          @click="play(true)"
        >
          <Shuffle :size="18" :stroke-width="2" /> Shuffle
        </button>
        <div class="rounded-control border border-border p-1.5">
          <FavoriteButton :active="isFav" :size="20" @toggle="favorites.toggle('album', album.id, album.name)" />
        </div>
        <ContextMenu :items="menuItems">
          <button
            type="button"
            class="grid h-10 w-10 place-items-center rounded-control border border-border text-muted transition-colors hover:bg-surface-2 hover:text-text"
            aria-label="More options"
          >
            <MoreHorizontal :size="20" :stroke-width="2" />
          </button>
        </ContextMenu>
      </template>
    </PageHeader>
    </ContextMenu>

    <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-4">
      <!-- multi-disc -->
      <template v-if="discs">
        <section v-for="[disc, discTracks] in discs" :key="disc" class="mb-8">
          <h3 class="mb-2 px-2 text-xs font-medium uppercase tracking-widest text-faint">Disc {{ disc }}</h3>
          <div class="grid gap-0.5">
            <TrackRow
              v-for="track in discTracks"
              :key="track.id"
              :track="track"
              :queue="tracks"
              :index="track.track ?? undefined"
            />
          </div>
        </section>
      </template>

      <!-- single disc -->
      <TrackList v-else :tracks="tracks" :queue="tracks" album-numbers />
    </div>
  </div>

  <div v-else class="mx-auto max-w-screen-2xl px-6 py-20 text-center text-muted">Album not found.</div>
</template>
