<script setup lang="ts">
import { computed } from 'vue';
import { useRouter } from 'vue-router';
import { creditSegments } from '../lib/artistCredit';
import type { Track } from '../api/types';

/**
 * A track's artist line, with every credited artist linkable. Replaces the single
 * `{{ track.artist }}` button the players used to render, which could only ever name one
 * artist however many were on the track.
 *
 * Renders as inline content, so the caller keeps control of size and colour.
 *
 * Two things about the template are load-bearing and look like they are not:
 *
 * - **Exactly one root node, and no comment beside it.** Anything else - a `v-if`/`v-else` pair
 *   of roots, or a stray template comment - makes this a fragment, and Vue silently stops
 *   inheriting the caller's `class`. Every row's truncation and colour goes with it.
 * - **No display utility of its own.** This used to carry `inline-block`, which quietly fought
 *   every caller that passed `block`: two Tailwind display classes on one element are resolved
 *   by their order in the stylesheet, not in the attribute, so the caller does not reliably
 *   win. In the full-screen player that turned the artist line inline and pulled the
 *   "FLAC · 856 kbps" badge up beside it. Callers pass the display they need - and must pass a
 *   block-level one alongside `truncate`, since overflow does nothing to an inline box.
 */
const props = withDefaults(
  defineProps<{
    track?: Track | null;
    /** Shown when the track has no artist at all. */
    placeholder?: string;
    /**
     * How to open an artist. Callers with something to tear down first - the full-screen
     * player has to close itself before routing - pass their own; everyone else gets a
     * plain push.
     */
    navigate?: (id: string) => void;
  }>(),
  { track: null, placeholder: 'Unknown artist', navigate: undefined },
);

const router = useRouter();

const segments = computed(() => creditSegments(props.track));

function open(id: string) {
  if (props.navigate) {
    props.navigate(id);
    return;
  }
  router.push({ name: 'artist', params: { id } });
}
</script>

<template>
  <span>
    <template v-if="segments.length === 0">{{ placeholder }}</template>
    <template v-else>
      <template v-for="(segment, i) in segments" :key="i">
        <button
          v-if="segment.id"
          type="button"
          class="hover:underline"
          @click.stop="open(segment.id)"
        >{{ segment.text }}</button>
        <span v-else>{{ segment.text }}</span>
      </template>
    </template>
  </span>
</template>
