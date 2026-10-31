<script setup lang="ts">
import Skeleton from '@/components/ui/Skeleton.vue';
import { shimmerDelay, barWidth } from '@/lib/skeleton';

/**
 * Horizontal card-rail placeholder, matching Shelf's `gap-4` track and the
 * `w-44 sm:w-48` cards every shelf on the home page puts in it.
 */
withDefaults(
  defineProps<{
    count?: number;
    shape?: 'square' | 'circle';
    /** Renders the uppercase section-title bar above the rail. */
    heading?: boolean;
  }>(),
  { count: 8, shape: 'square', heading: true },
);
</script>

<template>
  <section>
    <!--
      Reserves SectionHeader's exact line box (text-title at 1.1 leading) so the
      rail below does not shift when the real heading replaces it. The bar
      itself is shorter than the box, the way the glyphs would be.
    -->
    <div
      v-if="heading"
      class="mb-4 flex items-center"
      :style="{ height: 'calc(var(--text-title) * 1.1)' }"
    >
      <Skeleton class="h-5 w-40" />
    </div>
    <div class="no-scrollbar flex gap-4 overflow-hidden pb-1">
      <div
        v-for="i in count"
        :key="i"
        class="w-44 shrink-0 sm:w-48"
        :style="shimmerDelay(i)"
      >
        <Skeleton
          class="w-full aspect-square"
          :rounded="shape === 'circle' ? 'full' : 'control'"
        />
        <Skeleton class="mt-3 h-4" :style="{ width: barWidth(i, 60, 90) }" />
        <Skeleton class="mt-2 h-3" :style="{ width: barWidth(i + 3, 35, 60) }" />
      </div>
    </div>
  </section>
</template>
