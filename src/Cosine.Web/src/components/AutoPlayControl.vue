<script setup lang="ts">
import { computed } from 'vue';
import { toast } from 'vue-sonner';
import { Infinity as InfinityIcon, Minus, Plus } from 'lucide-vue-next';
import { usePlaybackStore } from '@/stores/playback';

/**
 * The auto-play toggle, and - while auto-play is running - the "−" / "+" either side of it that
 * steer it: this track does not fit, this track fits. They sit on the toggle rather than by the
 * track title because they are about the station, not the song: the heart by the title is
 * "favourite", which is a different thing, and the two should not be mistaken for each other.
 */
const props = withDefaults(defineProps<{ size?: 'sm' | 'lg' }>(), { size: 'sm' });

const player = usePlaybackStore();

const iconSize = computed(() => (props.size === 'lg' ? 21 : 18));
const voteSize = computed(() => (props.size === 'lg' ? 18 : 15));

/** Repeat means the queue never ends, so auto-play - and steering it - has nothing to do. */
const repeatBlocks = computed(() => player.repeatMode !== 'off');
const steering = computed(() => player.autoPlayEnabled && !repeatBlocks.value);
/** Internet radio has no track row; there is nothing to vote on. */
const canVote = computed(() => !!player.currentTrack && !player.currentTrack.streamUrl);

const title = computed(() => {
  if (repeatBlocks.value) return 'Auto-play - inactive while repeat is on';
  if (!player.autoPlayEnabled) return 'Auto-play: keep the queue going with similar music';
  return player.isRemote
    ? `Auto-play is on - ${player.outputName} keeps going on its own`
    : 'Auto-play is on';
});

function fits(): void {
  player.steer(1);
}

function doesNotFit(): void {
  const track = player.currentTrack;
  if (!track) return;
  const undoing = player.currentVote === -1;
  player.steer(-1);
  if (undoing) return;
  toast('Skipped - steering away from this', {
    description: track.title,
    action: { label: 'Undo', onClick: () => void player.undoDoesNotFit(track.id) },
  });
}
</script>

<template>
  <!-- The pill is always there; while auto-play is off its border is transparent and the vote
       buttons are collapsed to nothing, so switching it on grows them out of the ∞ rather than
       popping them in - and the transport row slides instead of jumping. -->
  <div
    class="flex items-center rounded-full border transition-[border-color,padding,gap] duration-200 ease-out motion-reduce:transition-none"
    :class="steering ? 'gap-1 border-border px-1 py-0.5' : 'gap-0 border-transparent px-0 py-0.5'"
  >
    <button
      type="button"
      class="grid shrink-0 place-items-center overflow-hidden rounded-full transition-[max-width,opacity,transform,background-color,color] duration-200 ease-out motion-reduce:transition-none"
      :class="[
        size === 'lg' ? 'h-8' : 'h-6',
        steering
          ? (size === 'lg' ? 'w-8 max-w-8' : 'w-6 max-w-6') + ' scale-100 opacity-100 disabled:opacity-30'
          : 'pointer-events-none max-w-0 scale-50 opacity-0',
        player.currentVote === -1 ? 'bg-text text-surface' : 'text-muted hover:text-text',
      ]"
      :disabled="!steering || !canVote"
      :tabindex="steering ? 0 : -1"
      :aria-hidden="!steering"
      aria-label="Doesn't fit - steer auto-play away and skip"
      :aria-pressed="player.currentVote === -1"
      title="Doesn't fit - skip it and steer away from it"
      @click="doesNotFit"
    >
      <Minus :size="voteSize" :stroke-width="2.5" class="shrink-0" />
    </button>
    <button
      type="button"
      class="grid place-items-center transition-colors"
      :class="[
        player.autoPlayEnabled ? 'text-accent' : 'text-muted hover:text-text',
        repeatBlocks ? 'opacity-40' : '',
      ]"
      aria-label="Auto-play"
      :aria-pressed="player.autoPlayEnabled"
      :title="title"
      @click="player.toggleAutoPlay()"
    >
      <InfinityIcon :size="iconSize" :stroke-width="2" />
    </button>
    <button
      type="button"
      class="grid shrink-0 place-items-center overflow-hidden rounded-full transition-[max-width,opacity,transform,background-color,color] duration-200 ease-out motion-reduce:transition-none"
      :class="[
        size === 'lg' ? 'h-8' : 'h-6',
        steering
          ? (size === 'lg' ? 'w-8 max-w-8' : 'w-6 max-w-6') + ' scale-100 opacity-100 disabled:opacity-30'
          : 'pointer-events-none max-w-0 scale-50 opacity-0',
        player.currentVote === 1 ? 'bg-accent text-accent-ink' : 'text-muted hover:text-text',
      ]"
      :disabled="!steering || !canVote"
      :tabindex="steering ? 0 : -1"
      :aria-hidden="!steering"
      aria-label="Fits - steer auto-play toward this"
      :aria-pressed="player.currentVote === 1"
      title="Fits - more like this"
      @click="fits"
    >
      <Plus :size="voteSize" :stroke-width="2.5" class="shrink-0" />
    </button>
  </div>
</template>
