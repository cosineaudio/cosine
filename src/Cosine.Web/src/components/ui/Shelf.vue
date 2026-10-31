<script setup lang="ts">
import { ref } from 'vue';
import { ChevronLeft, ChevronRight } from 'lucide-vue-next';

const track = ref<HTMLElement | null>(null);

function scrollBy(dir: 1 | -1) {
  const el = track.value;
  if (!el) return;
  el.scrollBy({ left: dir * Math.round(el.clientWidth * 0.8), behavior: 'smooth' });
}
</script>

<template>
  <div class="group/shelf relative">
    <!-- horizontal card rail -->
    <div
      ref="track"
      class="no-scrollbar flex snap-x snap-mandatory gap-4 overflow-x-auto scroll-smooth pb-1"
    >
      <slot />
    </div>

    <!-- desktop scroll affordances -->
    <button
      type="button"
      aria-label="Scroll left"
      class="absolute -left-3 top-1/2 hidden -translate-y-1/2 rounded-full border border-border bg-surface/90 p-2 text-text opacity-0 backdrop-blur transition-opacity group-hover/shelf:opacity-100 hover:bg-surface-2 lg:grid"
      @click="scrollBy(-1)"
    >
      <ChevronLeft :size="18" :stroke-width="2" />
    </button>
    <button
      type="button"
      aria-label="Scroll right"
      class="absolute -right-3 top-1/2 hidden -translate-y-1/2 rounded-full border border-border bg-surface/90 p-2 text-text opacity-0 backdrop-blur transition-opacity group-hover/shelf:opacity-100 hover:bg-surface-2 lg:grid"
      @click="scrollBy(1)"
    >
      <ChevronRight :size="18" :stroke-width="2" />
    </button>
  </div>
</template>
