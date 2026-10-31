<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { Disc3 } from 'lucide-vue-next';
import { coverArtUrl } from '@/api/browsing';

const props = withDefaults(
  defineProps<{
    coverArt?: string | null;
    alt?: string;
    /** Requested pixel size for the cover art fetch. */
    size?: number;
    /** 'circle' rounds fully (artists); 'square' keeps art edges sharp. */
    shape?: 'square' | 'circle';
  }>(),
  { size: 300, shape: 'square' },
);

const loaded = ref(false);
const errored = ref(false);

const src = computed(() =>
  props.coverArt ? coverArtUrl(props.coverArt, props.size) : null,
);

// Reset load state when the source changes (list recycling, route change).
watch(src, () => {
  loaded.value = false;
  errored.value = false;
});

/**
 * The initials/disc glyph means "there is no art", so it is shown only when
 * that is actually true.
 *
 * It used to cover the loading state as well, which meant every tile in a grid
 * flashed its initials and then cross-faded to the real cover - reading as a
 * page full of missing artwork that then corrected itself. While an image is on
 * its way the tile is simply the empty `surface-2` well it already sits in.
 *
 * Deliberately no shimmer here: images are `loading="lazy"`, so anything below
 * the fold would shimmer indefinitely without ever having been requested.
 */
const showFallback = computed(() => !src.value || errored.value);

const initials = computed(() => {
  const name = (props.alt ?? '').trim();
  if (!name) return '';
  return name
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w[0])
    .join('')
    .toUpperCase();
});
</script>

<template>
  <div
    class="relative w-full aspect-square overflow-hidden bg-surface-2 select-none [container-type:inline-size]"
    :class="shape === 'circle' ? 'rounded-full' : ''"
  >
    <img
      v-if="src && !errored"
      :src="src"
      :alt="alt ?? ''"
      loading="lazy"
      decoding="async"
      class="absolute inset-0 h-full w-full object-cover transition-opacity duration-300"
      :class="loaded ? 'opacity-100' : 'opacity-0'"
      @load="loaded = true"
      @error="errored = true"
    />
    <div
      v-if="showFallback"
      class="absolute inset-0 flex items-center justify-center text-faint"
    >
      <span
        v-if="initials"
        class="font-display font-semibold tracking-tight text-text/25"
        style="font-size: clamp(0.9rem, 28cqw, 2.5rem)"
      >
        {{ initials }}
      </span>
      <Disc3 v-else :size="32" :stroke-width="1.5" />
    </div>
  </div>
</template>
