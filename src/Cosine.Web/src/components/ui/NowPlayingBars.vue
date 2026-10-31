<script setup lang="ts">
// Tiny animated equalizer shown next to the currently playing track.
</script>

<template>
  <span class="flex h-4 items-end gap-[2px]" aria-hidden="true">
    <span class="npb-bar" style="animation-delay: -0.4s" />
    <span class="npb-bar" style="animation-delay: -0.15s" />
    <span class="npb-bar" style="animation-delay: -0.6s" />
  </span>
</template>

<style scoped>
.npb-bar {
  width: 2px;
  height: 100%;
  background: currentColor;
  transform-origin: bottom;
  animation: npb 0.9s ease-in-out infinite alternate;
}
@keyframes npb {
  0% {
    transform: scaleY(0.25);
  }
  100% {
    transform: scaleY(1);
  }
}
@media (prefers-reduced-motion: reduce) {
  .npb-bar {
    animation: none;
    transform: scaleY(0.6);
  }
}
</style>
