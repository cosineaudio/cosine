<script setup lang="ts">
/**
 * Shimmer loading placeholder. Use it art-shaped (square for covers, a bar for
 * text) instead of a spinner. Compose several to sketch a page's layout.
 */
withDefaults(
  defineProps<{
    /** Tailwind rounding: 'control' (4px), 'full' (circle/pill), or 'none'. */
    rounded?: 'control' | 'full' | 'none';
  }>(),
  { rounded: 'control' },
);
</script>

<template>
  <div
    class="skeleton"
    :class="{
      'rounded-control': rounded === 'control',
      'rounded-full': rounded === 'full',
    }"
    aria-hidden="true"
  />
</template>
