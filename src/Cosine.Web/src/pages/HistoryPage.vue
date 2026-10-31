<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { History, Play } from 'lucide-vue-next';
import { getAlbumList2 } from '@/api/lists';
import { getRecentTracks } from '@/lib/recentTracks';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import SectionHeader from '@/components/ui/SectionHeader.vue';
import Shelf from '@/components/ui/Shelf.vue';
import AlbumCard from '@/components/AlbumCard.vue';
import TrackRow from '@/components/TrackRow.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonShelf from '@/components/ui/SkeletonShelf.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import type { Album, Track } from '@/api/types';

const favorites = useFavoritesStore();
const { playTracks } = usePlayback();

const recent = ref<Track[]>([]);
const recentAlbums = ref<Album[]>([]);
// Only the album shelf is fetched; `recent` comes straight out of localStorage.
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);

function playFrom(i: number) {
  if (!recent.value.length) return;
  playTracks(recent.value, i);
}

onMounted(async () => {
  recent.value = getRecentTracks();
  try {
    recentAlbums.value = await getAlbumList2('recent', { size: 18 });
  } catch { /* optional */ } finally {
    loading.value = false;
  }
  favorites.ensureLoaded();
});
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <header class="mb-6 flex items-end justify-between gap-4">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        History
      </h1>
      <button
        v-if="recent.length"
        type="button"
        class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
        @click="playFrom(0)"
      >
        <Play :size="16" :stroke-width="2" fill="currentColor" /> Play
      </button>
    </header>

    <!-- recently played tracks (local log) -->
    <section v-if="recent.length" class="mb-10">
      <SectionHeader title="Recently played" />
      <div class="grid gap-0.5">
        <TrackRow
          v-for="(track, i) in recent"
          :key="`${track.id}-${i}`"
          :track="track"
          :queue="recent"
          show-art
          show-album
        />
      </div>
    </section>

    <!-- recently added/played albums from the server -->
    <SkeletonShelf v-if="showSkeleton" :count="8" />
    <section v-else-if="recentAlbums.length" class="reveal">
      <SectionHeader title="Recent albums" :see-all="{ name: 'albums' }" />
      <Shelf>
        <div v-for="album in recentAlbums" :key="album.id" class="w-40 shrink-0 snap-start sm:w-44">
          <AlbumCard :album="album" />
        </div>
      </Shelf>
    </section>

    <EmptyState
      v-if="!loading && !recent.length && !recentAlbums.length"
      :icon="History"
      title="Nothing played yet"
      description="Tracks you play will show up here."
    />
  </div>
</template>
