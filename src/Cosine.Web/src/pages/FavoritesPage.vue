<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { Heart, Play, Shuffle } from 'lucide-vue-next';
import { getStarred2, type Starred } from '@/api/lists';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import AlbumCard from '@/components/AlbumCard.vue';
import ArtistCard from '@/components/ArtistCard.vue';
import TrackList from '@/components/TrackList.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import SkeletonTrackList from '@/components/ui/SkeletonTrackList.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { shuffled } from '@/lib/shuffle';

const favorites = useFavoritesStore();
const { playTracks } = usePlayback();

type Tab = 'songs' | 'albums' | 'artists';
const tab = ref<Tab>('songs');
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);
const data = ref<Starred>({ artist: [], album: [], song: [] });

const counts = computed(() => ({
  songs: data.value.song.length,
  albums: data.value.album.length,
  artists: data.value.artist.length,
}));

const TABS: { key: Tab; label: string }[] = [
  { key: 'songs', label: 'Songs' },
  { key: 'albums', label: 'Albums' },
  { key: 'artists', label: 'Artists' },
];

function playAll(shuffle = false) {
  const songs = data.value.song;
  if (!songs.length) return;
  const list = shuffle ? shuffled(songs) : songs;
  playTracks(list, 0);
}

onMounted(async () => {
  try {
    data.value = await getStarred2();
    // Seed the shared favorites store so buttons everywhere reflect state.
    favorites.seed({ song: data.value.song, album: data.value.album, artist: data.value.artist });
    favorites.loaded = true;
  } catch { /* leave empty */ } finally {
    loading.value = false;
  }
});
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <header class="mb-6 flex flex-wrap items-end justify-between gap-4">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        Favorites
      </h1>
      <div v-if="counts.songs" class="flex items-center gap-2">
        <button
          type="button"
          class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
          @click="playAll(false)"
        >
          <Play :size="16" :stroke-width="2" fill="currentColor" /> Play
        </button>
        <button
          type="button"
          class="flex items-center gap-2 rounded-control border border-border px-4 py-2 text-sm font-medium text-text transition-colors hover:bg-surface-2"
          @click="playAll(true)"
        >
          <Shuffle :size="16" :stroke-width="2" /> Shuffle
        </button>
      </div>
    </header>

    <!-- tabs -->
    <div class="mb-6 flex gap-2">
      <button
        v-for="t in TABS"
        :key="t.key"
        type="button"
        class="rounded-control border px-3 py-1.5 text-sm font-medium transition-colors"
        :class="tab === t.key
          ? 'border-transparent bg-accent text-accent-ink'
          : 'border-border text-muted hover:border-faint hover:text-text'"
        @click="tab = t.key"
      >
        {{ t.label }}
        <span class="ml-1 font-mono text-xs opacity-70">{{ counts[t.key] }}</span>
      </button>
    </div>

    <!-- loading: shaped like whichever tab is open, so the swap does not reflow -->
    <template v-if="showSkeleton">
      <SkeletonTrackList v-if="tab === 'songs'" :count="12" show-art />
      <SkeletonGrid
        v-else
        class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
        :shape="tab === 'artists' ? 'circle' : 'square'"
        :count="12"
      />
    </template>

    <template v-else-if="!loading">
      <!-- songs -->
      <div v-if="tab === 'songs'">
        <TrackList v-if="data.song.length" :tracks="data.song" show-art show-album />
        <EmptyState v-else :icon="Heart" title="No favorite songs" description="Tap the heart on any track to save it here." />
      </div>

      <!-- albums -->
      <div v-else-if="tab === 'albums'">
        <div
          v-if="data.album.length"
          class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
        >
          <AlbumCard v-for="album in data.album" :key="album.id" :album="album" />
        </div>
        <EmptyState v-else :icon="Heart" title="No favorite albums" description="Favorite an album to find it here." />
      </div>

      <!-- artists -->
      <div v-else>
        <div
          v-if="data.artist.length"
          class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
        >
          <ArtistCard v-for="artist in data.artist" :key="artist.id" :artist="artist" />
        </div>
        <EmptyState v-else :icon="Heart" title="No favorite artists" description="Favorite an artist to find them here." />
      </div>
    </template>
  </div>
</template>
