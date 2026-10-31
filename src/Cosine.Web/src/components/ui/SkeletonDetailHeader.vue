<script setup lang="ts">
import Skeleton from '@/components/ui/Skeleton.vue';

/**
 * Stands in for PageHeader on the album / artist / playlist detail pages.
 * Same paddings, same art width, and the title bar takes its height straight
 * from `--text-display-l` so the real heading lands on the same baseline.
 */
withDefaults(
  defineProps<{
    shape?: 'square' | 'circle';
    /** Number of action-button placeholders under the title. */
    actions?: number;
  }>(),
  { shape: 'square', actions: 3 },
);
</script>

<template>
  <header class="relative isolate">
    <div class="flex flex-col gap-6 px-6 pb-6 pt-8 sm:flex-row sm:items-end sm:gap-8 sm:px-8 sm:pt-12">
      <Skeleton
        class="w-40 shrink-0 self-center aspect-square sm:w-52 sm:self-end"
        :rounded="shape === 'circle' ? 'full' : 'control'"
      />
      <div class="flex min-w-0 flex-1 flex-col items-center gap-3 sm:items-start">
        <Skeleton class="h-3 w-16" />
        <Skeleton class="w-2/3 max-w-md" :style="{ height: 'var(--text-display-l)' }" />
        <Skeleton class="h-3.5 w-40" />
        <div class="mt-2 flex flex-wrap items-center justify-center gap-3 sm:justify-start">
          <Skeleton v-for="i in actions" :key="i" class="h-11" :class="i === 1 ? 'w-28' : 'w-32'" />
        </div>
      </div>
    </div>
  </header>
</template>
