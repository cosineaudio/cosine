<script setup lang="ts">
import { computed } from 'vue';
import CoverArt from '@/components/CoverArt.vue';

const props = withDefaults(
  defineProps<{
    /** Small uppercase label above the title, e.g. "ALBUM". */
    kicker?: string;
    title: string;
    coverArt?: string | null;
    shape?: 'square' | 'circle';
    /** Extracted dominant color (rgb string) to wash the backdrop. */
    tint?: string | null;
  }>(),
  { shape: 'square' },
);

// Art-tinted backdrop: the extracted color bleeds down into the canvas as a
// wash. Controls & text stay mono; only the backdrop takes color, and it starts
// at reduced strength so a vivid swatch reads as atmosphere, not a solid band.
const bg = computed(() => {
  const c = props.tint ?? 'rgb(38,38,38)';
  return (
    `linear-gradient(to bottom, ` +
    `color-mix(in srgb, ${c} 55%, transparent) 0%, ` +
    `color-mix(in srgb, ${c} 22%, transparent) 42%, ` +
    `transparent 100%)`
  );
});
</script>

<template>
  <header class="relative isolate">
    <div class="pointer-events-none absolute inset-x-0 top-0 -z-10 h-[130%]" :style="{ background: bg }" />
    <div class="flex flex-col gap-6 px-6 pb-6 pt-8 sm:flex-row sm:items-end sm:gap-8 sm:px-8 sm:pt-12">
      <div
        class="w-40 shrink-0 self-center overflow-hidden shadow-2xl shadow-black/50 sm:w-52 sm:self-end"
        :class="shape === 'circle' ? 'rounded-full' : 'rounded-control'"
      >
        <CoverArt :cover-art="coverArt" :alt="title" :size="500" :shape="shape" />
      </div>
      <div class="flex min-w-0 flex-1 flex-col gap-3 text-center sm:text-left">
        <span v-if="kicker" class="label">{{ kicker }}</span>
        <h1
          class="font-display font-bold tracking-tight text-text"
          :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1.02' }"
        >
          {{ title }}
        </h1>
        <div class="flex flex-wrap items-center justify-center gap-x-2 gap-y-1 text-sm text-muted sm:justify-start">
          <slot name="meta" />
        </div>
        <div class="mt-2 flex flex-wrap items-center justify-center gap-3 sm:justify-start">
          <slot name="actions" />
        </div>
      </div>
    </div>
  </header>
</template>
