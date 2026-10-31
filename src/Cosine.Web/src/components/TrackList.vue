<script setup lang="ts">
import { computed, ref } from 'vue';
import { useVirtualizer } from '@tanstack/vue-virtual';
import TrackRow from '@/components/TrackRow.vue';
import type { Track } from '@/api/types';
import type { MenuItem } from '@/components/ui/ContextMenu.vue';
import type { QueueSource } from '@/stores/autoPlay';

/**
 * Renders a list of TrackRows. Short lists render plainly (identical to a raw
 * `v-for`); long lists (> threshold) switch to windowed rendering via
 * `@tanstack/vue-virtual` inside an internal scroll container so thousands of
 * rows stay smooth. Behaviour and markup per-row are otherwise identical.
 */
const props = withDefaults(
  defineProps<{
    tracks: Track[];
    /** Queue a row plays within; defaults to the full list. */
    queue?: Track[];
    /** Where that queue came from, when it is only part of a longer list - see `QueueSource`. */
    queueSource?: QueueSource | null;
    showArt?: boolean;
    showAlbum?: boolean;
    /** Show the row number. */
    numbered?: boolean;
    /**
     * Number by the album track number instead of list position. Only for lists that
     * *are* an album - anywhere else per-album track numbers reset and read as noise.
     */
    albumNumbers?: boolean;
    /** Extra context-menu items appended to every row. */
    menuExtra?: MenuItem[];
    /** Above this many rows, virtualize. */
    threshold?: number;
    /** Estimated row height (px) for the virtualizer. */
    rowHeight?: number;
  }>(),
  { showArt: false, showAlbum: false, numbered: true, albumNumbers: false, threshold: 60, rowHeight: 48 },
);

const list = computed(() => props.tracks);
const queueList = computed(() => props.queue ?? props.tracks);
const virtualize = computed(() => list.value.length > props.threshold);

function indexFor(track: Track, i: number): number | undefined {
  if (!props.numbered) return undefined;
  if (props.albumNumbers) return track.track ?? i + 1;
  return i + 1;
}

// ---- virtualized path ----
const scrollEl = ref<HTMLElement | null>(null);
const virtualizer = useVirtualizer(
  computed(() => ({
    count: list.value.length,
    getScrollElement: () => scrollEl.value,
    estimateSize: () => props.rowHeight,
    overscan: 10,
  })),
);
const virtualRows = computed(() => virtualizer.value.getVirtualItems());
const totalSize = computed(() => virtualizer.value.getTotalSize());
</script>

<template>
  <!-- short list: plain render, behaviour identical to a raw v-for -->
  <div v-if="!virtualize" class="grid gap-0.5">
    <TrackRow
      v-for="(track, i) in list"
      :key="track.id"
      :track="track"
      :queue="queueList"
      :queue-source="queueSource"
      :index="indexFor(track, i)"
      :show-art="showArt"
      :show-album="showAlbum"
      :menu-extra="menuExtra"
    />
  </div>

  <!-- long list: windowed -->
  <div
    v-else
    ref="scrollEl"
    class="overflow-y-auto"
    style="max-height: min(70vh, 720px)"
  >
    <div :style="{ height: `${totalSize}px`, position: 'relative', width: '100%' }">
      <div
        v-for="row in virtualRows"
        :key="list[row.index].id"
        :style="{
          position: 'absolute',
          top: 0,
          left: 0,
          width: '100%',
          transform: `translateY(${row.start}px)`,
        }"
      >
        <TrackRow
          :track="list[row.index]"
          :queue="queueList"
          :queue-source="queueSource"
          :index="indexFor(list[row.index], row.index)"
          :show-art="showArt"
          :show-album="showAlbum"
          :menu-extra="menuExtra"
        />
      </div>
    </div>
  </div>
</template>
