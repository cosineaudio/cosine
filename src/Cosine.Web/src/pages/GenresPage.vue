<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { Tags } from 'lucide-vue-next';
import { getGenres } from '@/api/browsing';
import EmptyState from '@/components/ui/EmptyState.vue';
import Skeleton from '@/components/ui/Skeleton.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { shimmerDelay } from '@/lib/skeleton';
import { countLabel } from '@/lib/format';

interface Genre {
  value: string;
  songCount: number;
  albumCount: number;
}

const genres = ref<Genre[]>([]);
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);

const sorted = computed(() =>
  [...genres.value].filter((g) => g.value).sort((a, b) => b.songCount - a.songCount),
);

onMounted(async () => {
  try {
    const res = await getGenres();
    genres.value = res.genres?.genre ?? [];
  } catch { /* leave empty */ } finally {
    loading.value = false;
  }
});
</script>

<template>
  <div class="mx-auto max-w-screen-2xl px-6 pb-16 pt-8">
    <h1 class="mb-6 font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
      Genres
    </h1>

    <!-- genre tiles: a title line pinned top, a count line pinned bottom -->
    <div v-if="showSkeleton" class="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
      <div
        v-for="i in 10"
        :key="i"
        class="flex h-24 flex-col justify-between rounded-control border border-border bg-surface p-4"
        :style="shimmerDelay(i)"
      >
        <Skeleton class="h-4 w-2/3" />
        <Skeleton class="h-3 w-4/5" />
      </div>
    </div>

    <div
      v-else-if="sorted.length"
      class="reveal grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5"
    >
      <RouterLink
        v-for="g in sorted"
        :key="g.value"
        :to="{ name: 'genre', params: { name: g.value } }"
        class="group/genre flex flex-col justify-between rounded-control border border-border bg-surface p-4 transition-colors hover:border-faint hover:bg-surface-2"
      >
        <span class="font-display text-lg font-semibold leading-tight tracking-tight text-text">{{ g.value }}</span>
        <span class="mt-3 font-mono text-xs text-muted">
          {{ countLabel(g.songCount, 'song') }} · {{ countLabel(g.albumCount, 'album') }}
        </span>
      </RouterLink>
    </div>

    <!-- `!loading` so the pre-skeleton gap stays blank instead of flashing "No genres" -->
    <EmptyState
      v-else-if="!loading"
      :icon="Tags"
      title="No genres"
      description="Genres appear once your library is tagged and scanned."
    />
  </div>
</template>
