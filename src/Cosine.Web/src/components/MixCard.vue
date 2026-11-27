<script setup lang="ts">
import { computed } from 'vue';
import { useRouter } from 'vue-router';
import { Radio, User } from 'lucide-vue-next';
import CoverArt from '@/components/CoverArt.vue';
import PlayButton from '@/components/ui/PlayButton.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import { useDominantColor } from '@/composables/useDominantColor';
import { useRadio } from '@/composables/useRadio';
import { getArtistSongs } from '@/api/library';
import type { Artist } from '@/api/types';

/**
 * One home-page mix: an auto-play station seeded from an artist. The cover is the artist's own
 * image with the mix's name set over it, washed in the colour the image already has - so each
 * mix looks like its artist rather than like a template.
 */
const props = defineProps<{ artist: Artist }>();

const router = useRouter();
const radio = useRadio();
const { tint } = useDominantColor(() => props.artist.coverArt ?? props.artist.id);

const title = computed(() => `${props.artist.name} Mix`);
const wash = computed(() => tint.value ?? 'rgb(0 0 0)');

function play(): void {
  const a = props.artist;
  void radio.fromTracks(() => getArtistSongs(a.id), title.value);
}

const menuItems = computed<MenuItem[]>(() => [
  { label: 'Play mix', icon: Radio, onSelect: play },
  { label: 'Go to artist', icon: User, onSelect: () => router.push({ name: 'artist', params: { id: props.artist.id } }) },
]);
</script>

<template>
  <ContextMenu :items="menuItems" context-menu>
  <div class="group/card w-full">
    <div class="relative aspect-square overflow-hidden rounded-control ring-1 ring-border">
      <button type="button" class="block h-full w-full" :aria-label="`Play ${title}`" @click="play">
        <CoverArt :cover-art="artist.coverArt ?? artist.id" :alt="artist.name" :size="400" />
        <!-- colour wash, strongest where the name sits -->
        <span
          class="pointer-events-none absolute inset-0"
          :style="{ background: `linear-gradient(to top, ${wash} 0%, color-mix(in srgb, ${wash} 55%, transparent) 38%, transparent 70%)` }"
        />
        <span class="pointer-events-none absolute inset-x-0 bottom-0 h-1.5" :style="{ background: wash }" />
        <span class="pointer-events-none absolute left-3 top-3 flex items-center gap-1 rounded-full bg-black/45 px-2 py-0.5 font-mono text-[10px] uppercase tracking-widest text-white backdrop-blur">
          <Radio :size="11" :stroke-width="2.5" /> Mix
        </span>
        <span class="pointer-events-none absolute inset-x-3 bottom-4 text-left font-display font-bold leading-[0.95] tracking-tight text-white [text-shadow:0_1px_12px_rgb(0_0_0_/_0.45)]">
          <span class="line-clamp-2 block text-2xl">{{ artist.name }}</span>
          <span class="block text-2xl opacity-80">Mix</span>
        </span>
      </button>
      <!-- hover play -->
      <div class="pointer-events-none absolute bottom-3 right-3 translate-y-1 opacity-0 transition-all duration-150 group-hover/card:pointer-events-auto group-hover/card:translate-y-0 group-hover/card:opacity-100">
        <PlayButton size="md" :busy="radio.pending.value" @click.stop="play" />
      </div>
    </div>
    <button type="button" class="mt-3 block w-full text-left" @click="play">
      <div class="truncate font-medium text-text">{{ title }}</div>
      <div class="truncate text-sm text-muted">{{ artist.name }} and more like them</div>
    </button>
  </div>
  </ContextMenu>
</template>
