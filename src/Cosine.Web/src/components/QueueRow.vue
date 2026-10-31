<script setup lang="ts">
import { GripVertical, Trash2, Play, MoreHorizontal, Infinity as InfinityIcon } from 'lucide-vue-next';
import type { Track } from '@/api/types';
import CoverArt from '@/components/CoverArt.vue';
import ArtistCredit from '@/components/ArtistCredit.vue';
import ContextMenu from '@/components/ui/ContextMenu.vue';
import NowPlayingBars from '@/components/ui/NowPlayingBars.vue';
import { useTrackMenu } from '@/composables/useTrackMenu';
import { formatDuration } from '@/lib/format';

/**
 * One entry in the play queue. The drawer and the full-screen player show the same queue, so they
 * show it with the same row: what a queue entry can do - play, reorder, be removed - does not
 * depend on which panel it is being read in.
 *
 * The drag events belong to whichever list is holding the row (only the list knows the indices),
 * so they arrive as fallthrough attributes. They have to be placed by hand: the root is now the
 * context-menu wrapper, which renders no element of its own, and Vue drops fallthrough listeners
 * on a component with no single root element rather than guessing where they were meant to go.
 */
const props = withDefaults(
  defineProps<{
    track: Track;
    /** The entry being played. The artwork carries that state, so no separate marker is needed. */
    isCurrent?: boolean;
    /** Playing rather than paused - only read when `isCurrent`. */
    isPlaying?: boolean;
    /**
     * Already played. Dimmed in place rather than dropped, so the queue can answer "what was
     * that last track" and be clicked to go back to it.
     */
    isPast?: boolean;
    /** Queued by auto-play rather than by the listener. */
    isAutoPlayed?: boolean;
    /** This row is the one being dragged. */
    isDragging?: boolean;
    /** A drop would land on this row. */
    isDropTarget?: boolean;
    /** How to open an album or artist - the full-screen player has to close itself first. */
    navigate?: (name: 'album' | 'artist', id: string) => void;
  }>(),
  {
    isCurrent: false,
    isPlaying: false,
    isPast: false,
    isAutoPlayed: false,
    isDragging: false,
    isDropTarget: false,
    navigate: undefined,
  },
);

const emit = defineEmits<{
  play: [];
  remove: [];
}>();

// Built here rather than handed down by the list, so it rebuilds only when this row's track
// changes - a list-built menu would be a fresh array on every render of a queue that can run to
// hundreds of entries. "Remove from queue" can come along because the index it needs is already
// bound to the `remove` emit by whichever list is holding the row.
const menuItems = useTrackMenu(() => props.track, {
  play: () => emit('play'),
  navigate: props.navigate,
  extra: () => [
    { label: 'Remove from queue', icon: Trash2, danger: true, onSelect: () => emit('remove') },
  ],
});

/** The artist line takes the one-argument form; the menu needs albums as well. */
const navigateArtist = props.navigate ? (id: string) => props.navigate!('artist', id) : undefined;

// The drag handlers arrive as fallthrough attributes and are placed by hand - see above.
defineOptions({ inheritAttrs: false });
</script>

<template>
  <ContextMenu :items="menuItems" context-menu>
  <div
    v-bind="$attrs"
    draggable="true"
    class="group/qrow flex items-center gap-2 rounded-control px-2 py-1.5 transition-colors"
    :class="[
      isCurrent ? 'bg-surface-2/70' : 'hover:bg-surface-2',
      isDropTarget ? 'ring-1 ring-accent/60' : '',
      // One opacity class at a time: two of them on an element are resolved by their order in
      // the stylesheet rather than in the attribute, so the row would dim unpredictably.
      isDragging ? 'opacity-40' : isPast ? 'opacity-45 hover:opacity-100' : '',
    ]"
  >
    <span class="cursor-grab text-faint opacity-0 transition-opacity group-hover/qrow:opacity-100 active:cursor-grabbing">
      <GripVertical :size="16" :stroke-width="2" />
    </span>
    <button
      type="button"
      class="relative h-9 w-9 shrink-0 overflow-hidden rounded-[3px] ring-1 ring-border"
      :aria-label="`Play ${track.title}`"
      @click="$emit('play')"
    >
      <CoverArt :cover-art="track.coverArt ?? track.albumId ?? track.id" :alt="track.album ?? track.title" :size="72" />
      <span v-if="isCurrent" class="absolute inset-0 grid place-items-center bg-black/50">
        <NowPlayingBars v-if="isPlaying" class="text-accent" />
        <Play v-else :size="14" :stroke-width="2" fill="currentColor" class="translate-x-px text-accent" />
      </span>
    </button>
    <!-- Not one button around both lines: the artist names are their own links,
         and a button inside a button is invalid markup the browser un-nests. -->
    <div class="flex min-w-0 flex-1 flex-col items-start text-left">
      <button
        type="button"
        class="max-w-full truncate text-sm font-medium"
        :class="isCurrent ? 'text-accent' : 'text-text'"
        @click="$emit('play')"
      >{{ track.title }}</button>
      <span class="flex w-full items-center gap-1 text-xs text-muted">
        <InfinityIcon
          v-if="isAutoPlayed"
          :size="12"
          :stroke-width="2"
          class="shrink-0 text-accent/80"
          aria-label="Added by auto-play"
        />
        <ArtistCredit :track="track" :navigate="navigateArtist" class="block min-w-0 truncate hover:text-text" />
      </span>
    </div>
    <span class="shrink-0 font-mono text-[11px] tabular-nums text-muted group-hover/qrow:hidden">{{ formatDuration(track.duration) }}</span>
    <ContextMenu :items="menuItems">
      <button
        type="button"
        class="hidden h-7 w-7 shrink-0 place-items-center rounded-control text-muted transition-colors hover:text-text group-hover/qrow:grid"
        :aria-label="`More options for ${track.title}`"
      >
        <MoreHorizontal :size="16" :stroke-width="2" />
      </button>
    </ContextMenu>
    <button
      type="button"
      class="hidden h-7 w-7 shrink-0 place-items-center rounded-control text-muted transition-colors hover:text-danger group-hover/qrow:grid"
      :aria-label="`Remove ${track.title} from queue`"
      @click="$emit('remove')"
    >
      <Trash2 :size="15" :stroke-width="2" />
    </button>
  </div>
  </ContextMenu>
</template>
