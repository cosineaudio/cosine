<script setup lang="ts">
import { computed } from 'vue';
import { Play, Pause } from 'lucide-vue-next';
import Spinner from '@/components/ui/Spinner.vue';

const props = withDefaults(
  defineProps<{
    playing?: boolean;
    size?: 'sm' | 'md' | 'lg';
    /** Filled white block (primary) vs. bare icon. */
    variant?: 'solid' | 'ghost';
    label?: string;
    /**
     * Waiting on sound - a local buffer stall or an unanswered device command.
     * Takes over the icon, because in both cases the transport already claims
     * to be playing and the glyph would otherwise be the only thing insisting
     * that music is coming out when it is not.
     */
    busy?: boolean;
  }>(),
  { playing: false, size: 'md', variant: 'solid', busy: false },
);

const dims = computed(
  () => ({ sm: 'h-8 w-8', md: 'h-11 w-11', lg: 'h-14 w-14' })[props.size],
);
const iconSize = computed(() => ({ sm: 15, md: 20, lg: 26 })[props.size]);
</script>

<template>
  <button
    type="button"
    :aria-label="label ?? (playing ? 'Pause' : 'Play')"
    :class="[
      'grid place-items-center rounded-full transition-transform duration-150 active:scale-95',
      dims,
      variant === 'solid'
        ? 'bg-accent text-accent-ink shadow-lg hover:scale-105'
        : 'text-text hover:text-accent',
    ]"
  >
    <Spinner v-if="busy" :size="size" />
    <Pause v-else-if="playing" :size="iconSize" :stroke-width="2" fill="currentColor" />
    <!-- nudge the play triangle to feel optically centered -->
    <Play v-else :size="iconSize" :stroke-width="2" fill="currentColor" class="translate-x-[1px]" />
  </button>
</template>
