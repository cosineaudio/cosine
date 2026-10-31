<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { useVirtualizer } from '@tanstack/vue-virtual';
import { X, Infinity as InfinityIcon } from 'lucide-vue-next';
import { usePlaybackStore } from '@/stores/playback';
import { useUiStore } from '@/stores/ui';
import QueueRow from '@/components/QueueRow.vue';
import { useQueueDrag } from '@/composables/useQueueDrag';
import { formatTotalTime, countLabel } from '@/lib/format';
import { scrollCentre } from '@/lib/scrollCentre';

const player = usePlaybackStore();
const ui = useUiStore();

const { dragIndex, overIndex, onDragStart, onDragOver, onDrop, onDragEnd } =
  useQueueDrag((from, to) => player.moveInQueue(from, to));

/** Entries behind the playing one. They stay in the list, dimmed, rather than being hidden. */
const playedCount = computed(() => Math.min(player.queueIndex, player.queue.length));
const upNextCount = computed(() => Math.max(0, player.queue.length - player.queueIndex - 1));

/**
 * Marked per row rather than with a divider: the long queue renders windowed, and a heading that
 * only exists between two particular rows is exactly the thing a virtualizer measures wrongly.
 * The badge travels with the track, so it survives a drag as well.
 */
const isAutoPlayed = (trackId: string) => player.isAutoPlayTrack(trackId);
const totalRemaining = computed(() =>
  player.queue
    .slice(player.queueIndex)
    .reduce((sum, t) => sum + (t.duration ?? 0), 0),
);

// Windowed rendering once the queue gets long; short queues render plainly.
const VIRTUALIZE_THRESHOLD = 60;
const virtualize = computed(() => player.queue.length > VIRTUALIZE_THRESHOLD);
const scrollEl = ref<HTMLElement | null>(null);
const virtualizer = useVirtualizer(
  computed(() => ({
    count: player.queue.length,
    getScrollElement: () => scrollEl.value,
    estimateSize: () => 52,
    overscan: 12,
  })),
);
const virtualRows = computed(() => virtualizer.value.getVirtualItems());
const totalSize = computed(() => virtualizer.value.getTotalSize());

/**
 * Centre the playing entry. The queue holds what has already played as well as what is next, so
 * a drawer that opened at the top of a long queue would open somewhere the listener isn't.
 *
 * The row is found by data attribute rather than by a template ref: the windowed branch mounts
 * and unmounts rows as it scrolls, and a ref to one of them is only as good as its last render.
 */
async function revealCurrent(behavior: ScrollBehavior = 'auto'): Promise<void> {
  const index = player.queueIndex;
  if (index < 0 || index >= player.queue.length) return;
  await nextTick();
  // The drawer mounts its scroller in the tick it opens, and the virtualizer can only scroll to
  // a row once that element has been measured - which happens a frame later.
  if (typeof requestAnimationFrame === 'function') {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  }
  if (virtualize.value) {
    virtualizer.value.scrollToIndex(index, { align: 'center', behavior });
    return;
  }
  const container = scrollEl.value;
  const el = container?.querySelector<HTMLElement>('[data-current="true"]');
  if (container && el) scrollCentre(container, el, behavior);
}

watch(() => ui.queueOpen, (open) => { if (open) void revealCurrent(); });
// Following the queue as it advances, but only while the drawer is actually showing it.
watch(() => player.queueIndex, () => { if (ui.queueOpen) void revealCurrent('smooth'); });
</script>

<template>
  <Teleport to="body">
    <Transition name="queue-fade">
      <div v-if="ui.queueOpen" class="fixed inset-0 z-40" role="dialog" aria-label="Play queue">
        <div class="absolute inset-0 bg-black/50" @click="ui.toggleQueue()" />
        <Transition name="queue-slide" appear>
          <aside
            v-if="ui.queueOpen"
            class="absolute right-0 top-0 flex h-full w-full max-w-sm flex-col border-l border-border bg-surface shadow-2xl"
          >
            <!-- header -->
            <header class="flex items-center justify-between border-b border-border px-4 py-3">
              <div>
                <h2 class="font-display text-lg font-semibold text-text">Queue</h2>
                <p class="font-mono text-[11px] text-muted">
                  <span v-if="playedCount > 0">{{ playedCount }} played · </span>
                  {{ countLabel(upNextCount, 'track') }} up next · {{ formatTotalTime(totalRemaining) }}
                </p>
                <p
                  v-if="player.autoPlayEnabled && player.repeatMode === 'off'"
                  class="mt-0.5 flex items-center gap-1 font-mono text-[11px] text-accent"
                >
                  <InfinityIcon :size="12" :stroke-width="2" />
                  <span>auto-play on</span>
                </p>
              </div>
              <div class="flex items-center gap-1">
                <button
                  type="button"
                  class="rounded-control px-2 py-1 text-xs text-muted transition-colors hover:text-danger disabled:opacity-30"
                  :disabled="player.queue.length === 0"
                  @click="player.clearQueue()"
                >Clear</button>
                <button
                  type="button"
                  class="grid h-8 w-8 place-items-center rounded-control text-muted transition-colors hover:text-text"
                  aria-label="Close queue"
                  @click="ui.toggleQueue()"
                >
                  <X :size="18" :stroke-width="2" />
                </button>
              </div>
            </header>

            <!-- list -->
            <div ref="scrollEl" class="no-scrollbar flex-1 overflow-y-auto p-2">
              <p v-if="player.queue.length === 0" class="px-2 py-8 text-center text-sm text-muted">
                The queue is empty.
              </p>

              <!-- short queue: plain list -->
              <ul v-else-if="!virtualize" class="flex flex-col">
                <li
                  v-for="(t, i) in player.queue"
                  :key="`${t.id}-${i}`"
                  :data-current="i === player.queueIndex"
                >
                  <QueueRow
                    :track="t"
                    :is-current="i === player.queueIndex"
                    :is-playing="player.isPlaying"
                    :is-past="i < player.queueIndex"
                    :is-auto-played="isAutoPlayed(t.id)"
                    :is-dragging="dragIndex === i"
                    :is-drop-target="overIndex === i && dragIndex !== null"
                    @play="player.playAt(i)"
                    @remove="player.removeFromQueue(i)"
                    @dragstart="onDragStart(i, $event)"
                    @dragover="onDragOver(i, $event)"
                    @drop="onDrop(i)"
                    @dragend="onDragEnd"
                  />
                </li>
              </ul>

              <!-- long queue: windowed -->
              <div v-else :style="{ height: `${totalSize}px`, position: 'relative', width: '100%' }">
                <div
                  v-for="row in virtualRows"
                  :key="`${player.queue[row.index].id}-${row.index}`"
                  :style="{ position: 'absolute', top: 0, left: 0, width: '100%', transform: `translateY(${row.start}px)` }"
                >
                  <QueueRow
                    :track="player.queue[row.index]"
                    :is-current="row.index === player.queueIndex"
                    :is-playing="player.isPlaying"
                    :is-past="row.index < player.queueIndex"
                    :is-auto-played="isAutoPlayed(player.queue[row.index].id)"
                    :is-dragging="dragIndex === row.index"
                    :is-drop-target="overIndex === row.index && dragIndex !== null"
                    @play="player.playAt(row.index)"
                    @remove="player.removeFromQueue(row.index)"
                    @dragstart="onDragStart(row.index, $event)"
                    @dragover="onDragOver(row.index, $event)"
                    @drop="onDrop(row.index)"
                    @dragend="onDragEnd"
                  />
                </div>
              </div>
            </div>
          </aside>
        </Transition>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.queue-fade-enter-active,
.queue-fade-leave-active { transition: opacity 0.2s ease; }
.queue-fade-enter-from,
.queue-fade-leave-to { opacity: 0; }
.queue-slide-enter-active,
.queue-slide-leave-active { transition: transform 0.24s ease; }
.queue-slide-enter-from,
.queue-slide-leave-to { transform: translateX(100%); }
</style>
