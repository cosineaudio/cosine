<script setup lang="ts">
import { ref, computed, watch, onMounted, useTemplateRef } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useIntersectionObserver } from '@vueuse/core';
import { Disc3 } from 'lucide-vue-next';
import { getAlbumList2, type AlbumListType } from '@/api/lists';
import { getGenres } from '@/api/browsing';
import { useFavoritesStore } from '@/stores/favorites';
import AlbumCard from '@/components/AlbumCard.vue';
import EmptyState from '@/components/ui/EmptyState.vue';
import SkeletonGrid from '@/components/ui/SkeletonGrid.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import type { Album } from '@/api/types';

const route = useRoute();
const router = useRouter();
const favorites = useFavoritesStore();

type SortKey = 'newest' | 'alpha' | 'byYear' | 'random' | 'frequent';
const SORTS: { key: SortKey; label: string; type: AlbumListType }[] = [
  { key: 'newest', label: 'Newest', type: 'newest' },
  { key: 'alpha', label: 'A–Z', type: 'alphabeticalByName' },
  { key: 'byYear', label: 'By year', type: 'byYear' },
  { key: 'frequent', label: 'Most played', type: 'frequent' },
  { key: 'random', label: 'Random', type: 'random' },
];

const PAGE_SIZE = 36;

const sort = ref<SortKey>((route.query.sort as SortKey) || 'newest');
const genre = ref<string>((route.query.genre as string) || '');
const genres = ref<{ value: string; albumCount: number }[]>([]);

const albums = ref<Album[]>([]);
const offset = ref(0);
const loading = ref(false);
const done = ref(false);
const seenIds = new Set<string>();

const activeType = computed(() => SORTS.find((s) => s.key === sort.value)?.type ?? 'newest');

// A cold load (or a sort/genre change, which clears the grid) fills the page
// with a full skeleton; an infinite-scroll append only tacks a short row onto
// the end. Sharing one indicator made a scroll look like a page reload.
const loadingFirstPage = computed(() => loading.value && albums.value.length === 0);
const loadingMore = computed(() => loading.value && albums.value.length > 0);
const showFirstPageSkeleton = useDeferredLoading(loadingFirstPage);
const showMoreSkeleton = useDeferredLoading(loadingMore);

async function loadMore() {
  if (loading.value || done.value) return;
  loading.value = true;
  try {
    let batch: Album[];
    if (genre.value) {
      // byGenre overrides the sort dimension; the server sorts alphabetically within a genre.
      batch = await getAlbumList2('byGenre', { size: PAGE_SIZE, offset: offset.value, genre: genre.value });
    } else if (activeType.value === 'byYear') {
      // Newest year first: fromYear > toYear reverses the range.
      batch = await getAlbumList2('byYear', {
        size: PAGE_SIZE,
        offset: offset.value,
        fromYear: new Date().getFullYear() + 1,
        toYear: 0,
      });
    } else {
      batch = await getAlbumList2(activeType.value, { size: PAGE_SIZE, offset: offset.value });
    }
    const fresh = batch.filter((a) => !seenIds.has(a.id));
    fresh.forEach((a) => seenIds.add(a.id));
    albums.value.push(...fresh);
    offset.value += batch.length;
    if (batch.length < PAGE_SIZE) done.value = true;
  } finally {
    loading.value = false;
  }
}

function reset() {
  albums.value = [];
  offset.value = 0;
  done.value = false;
  seenIds.clear();
  loadMore();
}

watch([sort, genre], () => {
  router.replace({ query: { ...(sort.value !== 'newest' ? { sort: sort.value } : {}), ...(genre.value ? { genre: genre.value } : {}) } });
  reset();
});

onMounted(async () => {
  favorites.ensureLoaded();
  try {
    const g = await getGenres();
    genres.value = (g.genres?.genre ?? [])
      .filter((x) => x.albumCount > 0)
      .sort((a, b) => b.albumCount - a.albumCount);
  } catch { /* genres optional */ }
  loadMore();
});

const sentinel = useTemplateRef<HTMLElement>('sentinel');
useIntersectionObserver(sentinel, ([entry]) => {
  if (entry?.isIntersecting) loadMore();
});
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <div class="mb-6">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        Albums
      </h1>
    </div>

    <!-- controls -->
    <div class="mb-6 flex flex-col gap-3">
      <div class="flex flex-wrap gap-2">
        <button
          v-for="s in SORTS"
          :key="s.key"
          type="button"
          class="rounded-control border px-3 py-1.5 text-sm font-medium transition-colors"
          :class="sort === s.key
            ? 'border-transparent bg-accent text-accent-ink'
            : 'border-border text-muted hover:border-faint hover:text-text'"
          @click="sort = s.key"
        >
          {{ s.label }}
        </button>
      </div>
      <div v-if="genres.length" class="no-scrollbar flex gap-2 overflow-x-auto pb-1">
        <button
          type="button"
          class="shrink-0 rounded-control border px-3 py-1.5 text-sm font-medium transition-colors"
          :class="!genre ? 'border-transparent bg-surface-2 text-text' : 'border-border text-muted hover:text-text'"
          @click="genre = ''"
        >
          All genres
        </button>
        <button
          v-for="g in genres"
          :key="g.value"
          type="button"
          class="shrink-0 rounded-control border px-3 py-1.5 text-sm font-medium transition-colors"
          :class="genre === g.value ? 'border-transparent bg-surface-2 text-text' : 'border-border text-muted hover:text-text'"
          @click="genre = genre === g.value ? '' : g.value"
        >
          {{ g.value }}
        </button>
      </div>
    </div>

    <!-- grid -->
    <div
      v-if="albums.length"
      class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
    >
      <AlbumCard v-for="album in albums" :key="album.id" :album="album" />
    </div>

    <SkeletonGrid
      v-else-if="showFirstPageSkeleton"
      class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
      :count="18"
    />

    <EmptyState
      v-else-if="!loading"
      :icon="Disc3"
      title="No albums"
      description="Nothing matches this filter yet."
    />

    <!-- infinite-scroll sentinel, then the next page's worth of cards -->
    <div ref="sentinel" class="h-12" />
    <SkeletonGrid
      v-if="showMoreSkeleton"
      class="grid grid-cols-2 gap-x-4 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
      :count="6"
    />
  </div>
</template>
