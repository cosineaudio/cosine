<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { Tags, Play, Shuffle } from 'lucide-vue-next';
import { getSongsByGenre, getAlbumList2 } from '@/api/lists';
import { getGenres } from '@/api/browsing';
import { usePlayback } from '@/composables/usePlayback';
import { useFavoritesStore } from '@/stores/favorites';
import TrackList from '@/components/TrackList.vue';
import AlbumCard from '@/components/AlbumCard.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { countLabel } from '@/lib/format';
import { shuffled } from '@/lib/shuffle';
import type { Album, Track } from '@/api/types';
import type { QueueSource } from '@/stores/autoPlay';

const props = defineProps<{ name: string }>();

const { playTracks } = usePlayback();
const favorites = useFavoritesStore();

type Tab = 'songs' | 'albums';
const tab = ref<Tab>('songs');
const songs = ref<Track[]>([]);
const albums = ref<Album[]>([]);
// True library totals for this genre (the tabs below only load a bounded page).
const totalSongs = ref<number | null>(null);
const totalAlbums = ref<number | null>(null);
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);

const decodedName = computed(() => props.name);

/**
 * What auto-play should read on through once this page's rows run out: the rest of the genre,
 * from where the loaded page ends, rather than a blend seeded off the last few tracks. A genre
 * of 3,000 songs is only 200 rows here - running out of rows is not running out of the genre.
 */
const queueSource = computed<QueueSource>(() => ({
  kind: 'genre',
  name: props.name,
  offset: songs.value.length,
}));

function play(shuffle = false) {
  if (!songs.value.length) return;
  const list = shuffle ? shuffled(songs.value) : songs.value;
  playTracks(list, 0, { ...queueSource.value, shuffle });
}

async function load() {
  loading.value = true;
  songs.value = [];
  albums.value = [];
  totalSongs.value = null;
  totalAlbums.value = null;
  try {
    const [s, a, g] = await Promise.all([
      getSongsByGenre(props.name, { count: 200 }),
      getAlbumList2('byGenre', { genre: props.name, size: 100 }),
      getGenres(),
    ]);
    songs.value = s;
    albums.value = a;
    const match = g.genres.genre.find((x) => x.value === props.name);
    totalSongs.value = match?.songCount ?? s.length;
    totalAlbums.value = match?.albumCount ?? a.length;
  } catch { /* leave empty */ } finally {
    loading.value = false;
  }
  favorites.ensureLoaded();
}

onMounted(load);
watch(() => props.name, load);
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <header class="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div>
        <span class="label">Genre</span>
        <h1 class="mt-1 font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
          {{ decodedName }}
        </h1>
        <p v-if="!loading" class="mt-2 font-mono text-xs text-muted">
          {{ countLabel(totalSongs ?? songs.length, 'song') }} · {{ countLabel(totalAlbums ?? albums.length, 'album') }}
        </p>
      </div>
      <div v-if="songs.length" class="flex items-center gap-2">
        <button
          type="button"
          class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
          @click="play(false)"
        >
          <Play :size="16" :stroke-width="2" fill="currentColor" /> Play
        </button>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control border border-border px-4 py-2 text-sm font-medium text-text transition-colors hover:bg-surface-2"
          @click="play(true)"
        >
          <Shuffle :size="16" :stroke-width="2" /> Shuffle
        </button>
      </div>
    </header>

    <div class="mb-6 flex gap-2">
      <button
        type="button"
        class="rounded-control border px-3 py-1.5 text-sm font-medium transition-colors"
        :class="tab === 'songs' ? 'border-transparent bg-accent text-accent-ink' : 'border-border text-muted hover:border-faint hover:text-text'"
        @click="tab = 'songs'"
      >
        Songs
      </button>
      <button
        type="button"
        class="rounded-control border px-3 py-1.5 text-sm font-medium transition-colors"
        :class="tab === 'albums' ? 'border-transparent bg-accent text-accent-ink' : 'border-border text-muted hover:border-faint hover:text-text'"
        @click="tab = 'albums'"
      >
        Albums
      </button>
    </div>

    <!-- loading: shaped like whichever tab is open, so the swap does not reflow -->
    <template v-if="showSkeleton">
      <SkeletonTrackList v-if="tab === 'songs'" :count="12" show-art />
      <SkeletonGrid
        v-else
        class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
        :count="12"
      />
    </template>

    <template v-else-if="!loading">
      <div v-if="tab === 'songs'">
        <TrackList v-if="songs.length" :tracks="songs" :queue-source="queueSource" show-art show-album />
        <EmptyState v-else :icon="Tags" title="No songs" :description="`Nothing tagged “${decodedName}” yet.`" />
      </div>

      <div v-else>
        <div
          v-if="albums.length"
          class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
        >
          <AlbumCard v-for="album in albums" :key="album.id" :album="album" />
        </div>
        <EmptyState v-else :icon="Tags" title="No albums" :description="`Nothing tagged “${decodedName}” yet.`" />
      </div>
    </template>
  </div>
</template>
