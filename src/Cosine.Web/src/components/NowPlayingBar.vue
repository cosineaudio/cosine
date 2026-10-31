<script setup lang="ts">
import { computed, ref } from 'vue';
import { useRouter } from 'vue-router';
import {
  Shuffle, SkipBack, SkipForward, Repeat, Repeat1, Infinity as InfinityIcon,
  Volume2, Volume1, VolumeX, ListMusic, Maximize2, Check, MoreHorizontal, Play, Pause,
} from 'lucide-vue-next';
import { usePlaybackStore } from '@/stores/playback';
import { useUiStore } from '@/stores/ui';
import { useFavoritesStore } from '@/stores/favorites';
import CoverArt from '@/components/CoverArt.vue';
import ArtistCredit from '@/components/ArtistCredit.vue';
import PlayButton from '@/components/ui/PlayButton.vue';
import FavoriteButton from '@/components/ui/FavoriteButton.vue';
import ProgressScrubber from '@/components/ui/ProgressScrubber.vue';
import ContextMenu, { type MenuItem } from '@/components/ui/ContextMenu.vue';
import OutputPicker from '@/components/OutputPicker.vue';
import ListenTogetherPanel from '@/components/ListenTogetherPanel.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { useTrackMenu } from '@/composables/useTrackMenu';
import { formatDuration } from '@/lib/format';
import { getTranscodeFormats, type TranscodeFormat } from '@/api/transcoding';

const router = useRouter();
const player = usePlaybackStore();
const ui = useUiStore();
const favorites = useFavoritesStore();

/**
 * A stall or an unanswered device command, gated so that ordinary track starts -
 * which are busy for a frame or two - never flicker the transport.
 */
const showBusy = useDeferredLoading(computed(() => player.isBusy), { delay: 300, minVisible: 300 });

const track = computed(() => player.currentTrack);
const artId = computed(() => track.value?.coverArt ?? track.value?.albumId ?? track.value?.id ?? null);
const isFav = computed(() => (track.value ? favorites.isSong(track.value.id) : false));

// No "Play" item: this is the track that is already playing.
const trackMenu = useTrackMenu(() => track.value);

// Source file format on the server (what the file IS, vs. the stream format selector below)
const sourceFormat = computed(() => track.value?.suffix?.toUpperCase() ?? null);
const sourceFormatTitle = computed(() => {
  if (!track.value?.suffix) return '';
  const bitRate = track.value.bitRate ? ` · ${track.value.bitRate} kbps` : '';
  return `Source file: ${track.value.suffix.toUpperCase()}${bitRate}`;
});

// ---- format selector ----
const formatOptions = ref<TranscodeFormat[]>([]);
let formatsLoaded = false;

async function ensureFormats() {
  if (formatsLoaded) return;
  try {
    formatOptions.value = await getTranscodeFormats();
    formatsLoaded = true;
  } catch {
    formatOptions.value = [];
  }
}

const selectedFormatLabel = computed(() => {
  if (!player.isTranscodingEnabled) return 'Original';
  const fmt = player.preferredFormat.toUpperCase();
  return player.preferredBitRate > 0 ? `${fmt} ${player.preferredBitRate}k` : fmt;
});

const formatMenuItems = computed<MenuItem[]>(() => {
  // A speaker fetches its own bytes, so the format is a property of the device - its transcode
  // profile, set per device in admin - and not of this browser. The menu shows what the device
  // is being sent rather than offering a choice that would go nowhere (plan §4.7).
  if (player.isRemote) {
    return [
      { label: `Stream format for ${player.outputName}`, disabled: true },
      { separator: true, label: '' },
      { label: `${selectedFormatLabel.value} · set per device in admin`, disabled: true },
    ];
  }

  const isOriginal = !player.isTranscodingEnabled;
  const items: MenuItem[] = [
    { label: 'Stream format', disabled: true },
    { separator: true, label: '' },
    {
      label: 'Original',
      icon: isOriginal ? Check : undefined,
      onSelect: () => player.setFormatPreference('', 0),
    },
  ];
  for (const fmt of formatOptions.value) {
    if (fmt.bitRates.length > 0) {
      items.push({
        label: fmt.name.toUpperCase(),
        children: fmt.bitRates.map((br) => ({
          label: `${br} kbps`,
          icon: player.preferredFormat === fmt.name && player.preferredBitRate === br ? Check : undefined,
          onSelect: () => player.setFormatPreference(fmt.name, br),
        })),
      });
    } else {
      items.push({
        label: `${fmt.name.toUpperCase()} (lossless)`,
        icon: player.preferredFormat === fmt.name ? Check : undefined,
        onSelect: () => player.setFormatPreference(fmt.name, 0),
      });
    }
  }
  return items;
});

function goToAlbum() {
  if (track.value?.albumId) router.push({ name: 'album', params: { id: track.value.albumId } });
}
const repeatActive = computed(() => player.repeatMode !== 'off');

// Repeat already means the queue never ends, so auto-play has nothing left to do - saying so
// beats a toggle that looks on and quietly does nothing.
const autoPlayTitle = computed(() => {
  if (player.repeatMode !== 'off') return 'Auto-play - inactive while repeat is on';
  if (!player.autoPlayEnabled) return 'Auto-play: keep the queue going with similar music';
  return player.isRemote
    ? `Auto-play is on - ${player.outputName} keeps going on its own`
    : 'Auto-play is on';
});
</script>

<template>
  <!-- data attr: style.css offsets toasts above this bar via :has() -->
  <div
    v-if="player.hasTrack"
    data-now-playing-bar
    class="shrink-0 border-t border-border bg-surface"
  >
    <!-- ===== Desktop / tablet bar ===== -->
    <div class="hidden h-20 items-center gap-4 px-4 sm:flex">
      <!-- left: track (right-click for the same menu as the ⋯ button) -->
      <ContextMenu :items="trackMenu" context-menu>
      <div class="flex min-w-0 flex-1 items-center gap-3">
        <button
          type="button"
          class="h-14 w-14 shrink-0 overflow-hidden rounded-control ring-1 ring-border transition hover:opacity-80"
          aria-label="Open full-screen player"
          @click="ui.openFullScreen()"
        >
          <CoverArt :cover-art="artId" :alt="track?.album ?? track?.title" :size="120" />
        </button>
        <div class="min-w-0">
          <button
            type="button"
            class="block max-w-full truncate text-left text-sm font-semibold text-text hover:underline"
            @click="goToAlbum"
          >{{ track?.title }}</button>
          <ArtistCredit
            :track="track"
            class="block max-w-full truncate text-left text-xs text-muted hover:text-text"
          />
        </div>
        <span
          v-if="sourceFormat"
          class="hidden shrink-0 cursor-default rounded-control border border-border px-1.5 py-0.5 font-mono text-[10px] tracking-wide text-muted lg:inline"
          :title="sourceFormatTitle"
        >{{ sourceFormat }}</span>
        <FavoriteButton
          v-if="track"
          :active="isFav"
          class="ml-1 shrink-0"
          @toggle="favorites.toggle('song', track.id, track.title)"
        />
      </div>
      </ContextMenu>

      <!-- center: transport + scrubber -->
      <div class="flex max-w-xl flex-1 flex-col items-center gap-1.5">
        <div class="flex items-center gap-4">
          <button
            type="button"
            class="transition-colors"
            :class="player.isShuffled ? 'text-accent' : 'text-muted hover:text-text'"
            aria-label="Shuffle"
            :aria-pressed="player.isShuffled"
            @click="player.toggleShuffle()"
          >
            <Shuffle :size="17" :stroke-width="2" />
          </button>
          <button
            type="button"
            class="text-muted transition-colors hover:text-text"
            aria-label="Previous"
            @click="player.previous()"
          >
            <SkipBack :size="20" :stroke-width="2" fill="currentColor" />
          </button>
          <PlayButton
            size="sm"
            :playing="player.isPlaying"
            :busy="showBusy"
            @click="player.togglePlay()"
          />
          <button
            type="button"
            class="text-muted transition-colors hover:text-text"
            aria-label="Next"
            @click="player.next()"
          >
            <SkipForward :size="20" :stroke-width="2" fill="currentColor" />
          </button>
          <button
            type="button"
            class="transition-colors"
            :class="repeatActive ? 'text-accent' : 'text-muted hover:text-text'"
            aria-label="Repeat"
            @click="player.cycleRepeatMode()"
          >
            <Repeat1 v-if="player.repeatMode === 'one'" :size="17" :stroke-width="2" />
            <Repeat v-else :size="17" :stroke-width="2" />
          </button>
          <button
            type="button"
            class="transition-colors"
            :class="[
              player.autoPlayEnabled ? 'text-accent' : 'text-muted hover:text-text',
              player.repeatMode !== 'off' ? 'opacity-40' : '',
            ]"
            aria-label="Auto-play"
            :aria-pressed="player.autoPlayEnabled"
            :title="autoPlayTitle"
            @click="player.toggleAutoPlay()"
          >
            <InfinityIcon :size="18" :stroke-width="2" />
          </button>
        </div>
        <div class="flex w-full items-center gap-2">
          <span class="w-10 text-right font-mono text-[11px] tabular-nums text-muted">{{ formatDuration(player.currentTime) }}</span>
          <ProgressScrubber
            class="flex-1"
            :current="player.currentTime"
            :duration="player.duration"
            :buffered="player.bufferedProgress"
            @seek="player.seek($event)"
          />
          <span class="w-10 font-mono text-[11px] tabular-nums text-muted">{{ formatDuration(player.duration) }}</span>
        </div>
      </div>

      <!-- right: volume + actions -->
      <div class="flex flex-1 items-center justify-end gap-1">
        <div class="group/vol flex items-center gap-2">
          <button
            type="button"
            class="text-muted transition-colors hover:text-text"
            aria-label="Mute"
            @click="player.toggleMute()"
          >
            <VolumeX v-if="player.volume === 0" :size="18" :stroke-width="2" />
            <Volume1 v-else-if="player.volume < 0.5" :size="18" :stroke-width="2" />
            <Volume2 v-else :size="18" :stroke-width="2" />
          </button>
          <input
            type="range" min="0" max="1" step="0.01"
            :value="player.volume"
            aria-label="Volume"
            class="volume-slider h-1 w-24 cursor-pointer appearance-none rounded-full bg-surface-2"
            @input="player.setVolume(parseFloat(($event.target as HTMLInputElement).value))"
          />
        </div>

        <OutputPicker />

        <ListenTogetherPanel />

        <button
          type="button"
          class="grid h-9 w-9 place-items-center rounded-control transition-colors"
          :class="ui.queueOpen ? 'text-accent' : 'text-muted hover:text-text'"
          aria-label="Queue"
          :aria-pressed="ui.queueOpen"
          @click="ui.toggleQueue()"
        >
          <ListMusic :size="18" :stroke-width="2" />
        </button>

        <ContextMenu :items="formatMenuItems">
          <button
            type="button"
            class="rounded-control border border-border px-2 py-1 font-mono text-[11px] text-muted transition-colors hover:border-faint hover:text-text"
            :title="selectedFormatLabel"
            @pointerdown="ensureFormats"
          >
            {{ selectedFormatLabel }}
          </button>
        </ContextMenu>

        <ContextMenu :items="trackMenu">
          <button
            type="button"
            class="grid h-9 w-9 place-items-center rounded-control text-muted transition-colors hover:text-text"
            aria-label="More options"
          >
            <MoreHorizontal :size="18" :stroke-width="2" />
          </button>
        </ContextMenu>

        <button
          type="button"
          class="grid h-9 w-9 place-items-center rounded-control text-muted transition-colors hover:text-text"
          aria-label="Full-screen player"
          @click="ui.openFullScreen()"
        >
          <Maximize2 :size="17" :stroke-width="2" />
        </button>
      </div>
    </div>

    <!-- ===== Mobile mini-bar ===== -->
    <div class="sm:hidden">
      <div class="h-0.5 w-full bg-surface-2">
        <div class="h-full bg-accent" :style="{ width: (player.duration > 0 ? (player.currentTime / player.duration) * 100 : 0) + '%' }" />
      </div>
      <div class="flex items-center gap-3 px-3 py-2">
        <button
          type="button"
          class="flex min-w-0 flex-1 items-center gap-3 text-left"
          aria-label="Open full-screen player"
          @click="ui.openFullScreen()"
        >
          <div class="h-11 w-11 shrink-0 overflow-hidden rounded-control ring-1 ring-border">
            <CoverArt :cover-art="artId" :alt="track?.album ?? track?.title" :size="96" />
          </div>
          <div class="min-w-0">
            <div class="truncate text-sm font-semibold text-text">{{ track?.title }}</div>
            <div class="truncate text-xs text-muted">{{ track?.artist ?? 'Unknown artist' }}</div>
          </div>
        </button>
        <FavoriteButton
          v-if="track"
          :active="isFav"
          @toggle="favorites.toggle('song', track.id, track.title)"
        />
        <button
          type="button"
          class="grid h-10 w-10 place-items-center text-text"
          :aria-label="player.isPlaying ? 'Pause' : 'Play'"
          @click="player.togglePlay()"
        >
          <Pause v-if="player.isPlaying" :size="24" :stroke-width="2" fill="currentColor" />
          <Play v-else :size="24" :stroke-width="2" fill="currentColor" class="translate-x-px" />
        </button>
        <button
          type="button"
          class="grid h-10 w-10 place-items-center text-muted"
          aria-label="Next"
          @click="player.next()"
        >
          <SkipForward :size="22" :stroke-width="2" fill="currentColor" />
        </button>
        <!--
          Where the "open full-screen" chevron used to be. The art-and-title block to
          the left already does that, so the slot is better spent on the track menu -
          otherwise the only way to reach it on a phone is through the overlay.
        -->
        <ContextMenu :items="trackMenu">
          <button
            type="button"
            class="grid h-10 w-9 place-items-center text-muted"
            aria-label="More options"
          >
            <MoreHorizontal :size="20" :stroke-width="2" />
          </button>
        </ContextMenu>
      </div>
    </div>
  </div>
</template>

<style scoped>
.volume-slider::-webkit-slider-thumb {
  -webkit-appearance: none;
  appearance: none;
  height: 0.75rem;
  width: 0.75rem;
  border-radius: 9999px;
  background: var(--color-accent);
}
.volume-slider::-moz-range-thumb {
  height: 0.75rem;
  width: 0.75rem;
  border: none;
  border-radius: 9999px;
  background: var(--color-accent);
}
</style>
