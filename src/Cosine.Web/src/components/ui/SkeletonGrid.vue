<script setup lang="ts">
import Skeleton from '@/components/ui/Skeleton.vue';
import { shimmerDelay, barWidth } from '@/lib/skeleton';

/**
 * Card-grid placeholder. Mirrors AlbumCard (square) and ArtistCard (circle)
 * exactly - art, then a title line, then a subtitle line.
 *
 * Pass the *same* grid column classes the real grid uses, so nothing reflows
 * when the data lands:
 *
 *     <SkeletonGrid class="grid grid-cols-2 gap-4 sm:grid-cols-4" :count="12" />
 */
withDefaults(
  defineProps<{
    count?: number;
    shape?: 'square' | 'circle';
    /** Title/subtitle bars below the art. Off for bare art tiles. */
    meta?: boolean;
  }>(),
  { count: 12, shape: 'square', meta: true },
);
</script>

<template>
  <div>
    <div v-for="i in count" :key="i" :style="shimmerDelay(i)">
      <Skeleton
        class="w-full aspect-square"
        :rounded="shape === 'circle' ? 'full' : 'control'"
      />
      <template v-if="meta">
        <Skeleton
          class="mt-3 h-4"
          :class="shape === 'circle' ? 'mx-auto' : ''"
          :style="{ width: barWidth(i, 60, 90) }"
        />
        <Skeleton
          class="mt-2 h-3"
          :class="shape === 'circle' ? 'mx-auto' : ''"
          :style="{ width: barWidth(i + 3, 35, 60) }"
        />
      </template>
    </div>
  </div>
</template>
