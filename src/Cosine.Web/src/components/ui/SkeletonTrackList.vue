<script setup lang="ts">
import Skeleton from '@/components/ui/Skeleton.vue';
import { shimmerDelay, barWidth } from '@/lib/skeleton';

/**
 * Track-row placeholder. The padding, gaps and column widths are TrackRow's, so
 * a list swapping from skeleton to real rows does not jump.
 */
withDefaults(
  defineProps<{
    count?: number;
    /** Match the `show-art` prop on the TrackRows this stands in for. */
    showArt?: boolean;
    /** 2 mirrors the `sm:grid-cols-2` track blocks on the home page. */
    columns?: 1 | 2;
  }>(),
  { count: 8, showArt: false, columns: 1 },
);
</script>

<template>
  <div class="grid gap-0.5" :class="columns === 2 ? 'sm:grid-cols-2' : ''">
    <div
      v-for="i in count"
      :key="i"
      class="flex items-center gap-3 px-2 py-1.5"
      :style="shimmerDelay(i)"
    >
      <!-- index column -->
      <div class="grid w-7 shrink-0 place-items-center">
        <Skeleton class="h-3 w-3" />
      </div>

      <Skeleton v-if="showArt" class="h-10 w-10 shrink-0 rounded-[3px]" rounded="none" />

      <!-- title / artist -->
      <div class="flex min-w-0 flex-1 flex-col gap-2">
        <Skeleton class="h-4" :style="{ width: barWidth(i, 40, 75) }" />
        <Skeleton class="h-3" :style="{ width: barWidth(i + 5, 25, 45) }" />
      </div>

      <!-- duration -->
      <Skeleton class="h-3 w-9 shrink-0" />
    </div>
  </div>
</template>
