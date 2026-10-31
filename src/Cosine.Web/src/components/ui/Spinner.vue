<script setup lang="ts">
import { computed } from 'vue';

/**
 * The app's only spinner: a bare 270° arc in `currentColor`.
 *
 * Skeletons say "content is on its way, here is its shape"; this says "I heard
 * you, I am working". Use it for actions - buttons, form submits, a device
 * handshake - never for page load. It is deliberately unlike NowPlayingBars so
 * "loading" can never be misread as "playing".
 */
const props = withDefaults(
  defineProps<{
    /** sm sits inline in a button; md/lg stand alone. */
    size?: 'sm' | 'md' | 'lg';
    /** Announced to assistive tech; omit inside a button that already says it. */
    label?: string;
  }>(),
  { size: 'sm' },
);

const px = computed(() => ({ sm: 14, md: 20, lg: 28 })[props.size]);
</script>

<template>
  <svg
    class="spinner-arc shrink-0"
    :width="px"
    :height="px"
    viewBox="0 0 16 16"
    fill="none"
    :role="label ? 'status' : undefined"
    :aria-label="label"
    :aria-hidden="label ? undefined : 'true'"
  >
    <!-- 270° of a r=6.5 circle: stroke-dasharray is the arc, then the gap. -->
    <circle
      cx="8"
      cy="8"
      r="6.5"
      stroke="currentColor"
      stroke-width="1.5"
      stroke-linecap="round"
      stroke-dasharray="30.6 10.2"
    />
  </svg>
</template>

<style scoped>
.spinner-arc {
  animation: spin 0.7s linear infinite;
  transform-origin: center;
}
</style>
