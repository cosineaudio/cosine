<script setup lang="ts">
import { computed } from 'vue';
import { Play, MoreHorizontal } from 'lucide-vue-next';
import CoverArt from '@/components/CoverArt.vue';
import ArtistCredit from '@/components/ArtistCredit.vue';
import FavoriteButton from '@/components/ui/FavoriteButton.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import NowPlayingBars from '@/components/ui/NowPlayingBars.vue';
import { usePlaybackStore } from '@/stores/playback';
import { useFavoritesStore } from '@/stores/favorites';
import { useTrackMenu } from '@/composables/useTrackMenu';
import { formatDuration } from '@/lib/format';
import type { Track } from '@/api/types';
import type { QueueSource } from '@/stores/autoPlay';

const props = withDefaults(
  defineProps<{
    track: Track;
    /** 1-based number to show at left; omit to show only the play affordance. */
    index?: number;
    /** The list this track plays within (defaults to just this track). */
    queue?: Track[];
    /** Where `queue` came from, when it is only part of a longer list - see `QueueSource`. */
    queueSource?: QueueSource | null;
    showArt?: boolean;
    showAlbum?: boolean;
    /** Extra menu items appended below the defaults (e.g. Add to playlist). */
    menuExtra?: MenuItem[];
  }>(),
  { showArt: false, showAlbum: false },
);

const player = usePlaybackStore();
const favorites = useFavoritesStore();

const isCurrent = computed(() => player.currentTrackId === props.track.id);
const isPlaying = computed(() => isCurrent.value && player.isPlaying);
const isFav = computed(() => favorites.isSong(props.track.id));

function play() {
  const list = props.queue ?? [props.track];
  player.play(props.track, list, props.queueSource ?? null);
}

const menuItems = useTrackMenu(() => props.track, {
  play,
  extra: () => props.menuExtra ?? [],
});
</script>

<template>
  <ContextMenu :items="menuItems" context-menu>
  <div
    class="group/row flex items-center gap-3 rounded-control px-2 py-1.5 transition-colors hover:bg-surface-2"
    :class="isCurrent ? 'bg-surface-2/60' : ''"
    @dblclick="play"
  >
    <!-- index / now-playing indicator / hover play -->
    <div class="grid w-7 shrink-0 place-items-center">
      <NowPlayingBars v-if="isPlaying" class="text-accent" />
      <button
        v-else
        type="button"
        class="grid h-7 w-7 place-items-center"
        :aria-label="`Play ${track.title}`"
        @click="play"
      >
        <span
          class="font-mono text-sm text-muted group-hover/row:hidden"
          :class="isCurrent ? 'text-accent' : ''"
        >{{ index != null ? index : '' }}</span>
        <Play
          :size="15"
          :stroke-width="2"
          fill="currentColor"
          class="hidden translate-x-px text-text group-hover/row:block"
        />
      </button>
    </div>

    <!-- optional art -->
    <div v-if="showArt" class="h-10 w-10 shrink-0 overflow-hidden rounded-[3px]">
      <CoverArt :cover-art="track.coverArt ?? track.albumId ?? track.id" :alt="track.album ?? track.title" :size="80" />
    </div>

    <!-- title / artist -->
    <div class="flex min-w-0 flex-1 flex-col items-start">
      <button
        type="button"
        class="max-w-full truncate text-left font-medium"
        :class="isCurrent ? 'text-accent' : 'text-text'"
        @click="play"
      >{{ track.title }}</button>
      <ArtistCredit
        :track="track"
        class="block max-w-full truncate text-left text-sm text-muted transition-colors hover:text-text"
      />
    </div>

    <!-- album (wide screens) -->
    <RouterLink
      v-if="showAlbum && track.albumId"
      :to="{ name: 'album', params: { id: track.albumId } }"
      class="hidden min-w-0 flex-1 truncate text-sm text-muted transition-colors hover:text-text md:block"
    >
      {{ track.album }}
    </RouterLink>

    <!-- favorite -->
    <div class="shrink-0 opacity-0 transition-opacity group-hover/row:opacity-100" :class="isFav ? '!opacity-100' : ''">
      <FavoriteButton :active="isFav" @toggle="favorites.toggle('song', track.id, track.title)" />
    </div>

    <!-- duration -->
    <span class="w-12 shrink-0 text-right font-mono text-sm text-muted">
      {{ formatDuration(track.duration) }}
    </span>

    <!-- context menu -->
    <ContextMenu :items="menuItems">
      <button
        type="button"
        class="grid h-7 w-7 shrink-0 place-items-center rounded-control text-muted opacity-0 transition-all hover:text-text group-hover/row:opacity-100"
        aria-label="More options"
      >
        <MoreHorizontal :size="18" :stroke-width="2" />
      </button>
    </ContextMenu>
  </div>
  </ContextMenu>
</template>
