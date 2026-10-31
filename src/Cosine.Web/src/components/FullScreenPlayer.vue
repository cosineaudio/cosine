<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue';
import { useRouter } from 'vue-router';
import {
  ChevronDown, Shuffle, SkipBack, SkipForward, Repeat, Repeat1,
  ListMusic, Mic2, AudioLines, Volume2, Volume1, VolumeX,
  PanelRightClose, PanelRightOpen, Infinity as InfinityIcon, MoreHorizontal,
} from 'lucide-vue-next';
import { usePlaybackStore } from '@/stores/playback';
import { useUiStore } from '@/stores/ui';
import { useFavoritesStore } from '@/stores/favorites';
import CoverArt from '@/components/CoverArt.vue';
import ArtistCredit from '@/components/ArtistCredit.vue';
import PlayButton from '@/components/ui/PlayButton.vue';
import FavoriteButton from '@/components/ui/FavoriteButton.vue';
import ProgressScrubber from '@/components/ui/ProgressScrubber.vue';
import NowPlayingBars from '@/components/ui/NowPlayingBars.vue';
import Visualizer from '@/components/Visualizer.vue';
import OutputPicker from '@/components/OutputPicker.vue';
import QueueRow from '@/components/QueueRow.vue';
import ContextMenu from '@/components/ui/ContextMenu.vue';
import { useQueueDrag } from '@/composables/useQueueDrag';
import { useTrackMenu } from '@/composables/useTrackMenu';
import { useDeferredLoading } from '@/composables/useDeferredLoading';
import { formatDuration } from '@/lib/format';
import { scrollCentre } from '@/lib/scrollCentre';
import { coverArtUrl } from '@/api/browsing';
import { useDominantColor } from '@/composables/useDominantColor';
import { getTrackLyrics, type StructuredLyrics } from '@/api/lyrics';

const router = useRouter();
const player = usePlaybackStore();
const ui = useUiStore();
const favorites = useFavoritesStore();

const track = computed(() => player.currentTrack);
const artId = computed(() => track.value?.coverArt ?? track.value?.albumId ?? track.value?.id ?? null);
const backdropUrl = computed(() => (artId.value ? coverArtUrl(artId.value, 512) : null));
// Only extract while the overlay is open, so background browsing stays cheap.
const { tint } = useDominantColor(() => (ui.fullScreenOpen ? artId.value : null));
const isFav = computed(() => (track.value ? favorites.isSong(track.value.id) : false));
const repeatActive = computed(() => player.repeatMode !== 'off');

// No "Play" item - this is the track already playing. Navigation goes through `navTo`
// so the overlay closes before the page underneath it changes.
const trackMenu = useTrackMenu(() => track.value, { navigate: navTo });

/**
 * A stall or an unanswered device command, gated so that ordinary track starts -
 * which are busy for a frame or two - never flicker the transport.
 */
const showBusy = useDeferredLoading(computed(() => player.isBusy), { delay: 300, minVisible: 300 });

// Source file format on the server, e.g. "FLAC · 939 kbps"
const sourceFormat = computed(() => {
  const t = track.value;
  if (!t?.suffix) return null;
  return t.bitRate ? `${t.suffix.toUpperCase()} · ${t.bitRate} kbps` : t.suffix.toUpperCase();
});

const tab = ref<'queue' | 'lyrics'>('queue');

// Internet-radio streams are cross-origin, so the analyser can't tap them.
// Artist radio is ordinary library tracks (no streamUrl) and works fine.
// A remote session has no local element to tap at all - the speaker has the audio - so the
// visualizer is hidden rather than left drawing a flat line (plan §4.7).
const visualizerAvailable = computed(() =>
  !!track.value && !track.value.streamUrl && player.canAnalyse);

// Ambient visualizer is a persisted on/off preference, not a tab - it renders
// as a backdrop behind whichever tab (queue/lyrics) is showing.
const VISUALIZER_KEY = 'cosine_visualizer_on';
const visualizerOn = ref((() => {
  try { return localStorage.getItem(VISUALIZER_KEY) !== '0'; } catch { return true; }
})());
watch(visualizerOn, (on) => {
  try { localStorage.setItem(VISUALIZER_KEY, on ? '1' : '0'); } catch { /* ignore */ }
});
const showVisualizer = computed(() => visualizerOn.value && visualizerAvailable.value);

// Focus mode: collapse the right column (queue / lyrics) so the player centres.
const SIDE_PANEL_KEY = 'cosine_fs_side_panel';
const sidePanelOpen = ref((() => {
  try { return localStorage.getItem(SIDE_PANEL_KEY) !== '0'; } catch { return true; }
})());
watch(sidePanelOpen, (open) => {
  try { localStorage.setItem(SIDE_PANEL_KEY, open ? '1' : '0'); } catch { /* ignore */ }
});

/** The side panel's scroller, shared by both of its tabs - each autoscrolls only while showing. */
const sidePanelScroll = ref<HTMLElement | null>(null);

// ---- queue ----
//
// The whole queue, not just what is ahead: the tab used to filter to `i > queueIndex`, which left
// no way to see - or get back to - the track that just played.

const { dragIndex, overIndex, onDragStart, onDragOver, onDrop, onDragEnd } =
  useQueueDrag((from, to) => player.moveInQueue(from, to));

/**
 * Centre the playing entry in the side panel. Shares its scroller with the lyrics tab, so this
 * and the synced-lyrics autoscroll below each stand down unless their own tab is showing.
 */
async function revealCurrentTrack(behavior: ScrollBehavior = 'auto'): Promise<void> {
  if (tab.value !== 'queue') return;
  await nextTick();
  const container = sidePanelScroll.value;
  const el = container?.querySelector<HTMLElement>('[data-current="true"]');
  if (!container || !el) return;
  scrollCentre(container, el, behavior);
}

watch(() => [ui.fullScreenOpen, tab.value] as const, ([open]) => {
  if (open) void revealCurrentTrack();
});
watch(() => player.queueIndex, () => {
  if (ui.fullScreenOpen) void revealCurrentTrack('smooth');
});

// ---- lyrics ----
const lyrics = ref<StructuredLyrics | null>(null);
const plainLyrics = ref<string | null>(null);
const lyricsLoading = ref(false);
const lyricsTrackId = ref<string | null>(null);

async function loadLyrics() {
  const t = track.value;
  if (!t) return;
  if (lyricsTrackId.value === t.id) return;
  lyricsTrackId.value = t.id;
  lyrics.value = null;
  plainLyrics.value = null;
  lineEls.value = [];
  resumeAutoScroll();
  lyricsLoading.value = true;
  try {
    const { structured, plain } = await getTrackLyrics(t.id);
    if (structured.length > 0) {
      // prefer a synced set if present
      lyrics.value = structured.find((s) => s.synced) ?? structured[0];
    } else {
      plainLyrics.value = plain?.value ?? null;
    }
  } catch {
    /* no lyrics */
  } finally {
    lyricsLoading.value = false;
  }
}

// synced highlight
//
// The position is corrected by the output's lyric offset before it is compared to a line's
// timestamp. A speaker buffers before it makes a sound, so the position cosine reports runs ahead
// of the room and unshifted lyrics arrive early; subtracting the offset holds each line back by
// however long that device is behind. Zero for the browser, and for any device left untuned.
const activeLine = computed(() => {
  if (!lyrics.value?.synced) return -1;
  const ms = player.currentTime * 1000 - player.lyricOffsetMs;
  const lines = lyrics.value.line;
  let idx = -1;
  for (let i = 0; i < lines.length; i++) {
    if ((lines[i].start ?? 0) <= ms) idx = i;
    else break;
  }
  return idx;
});

const hasLyrics = computed(() => !!lyrics.value || !!plainLyrics.value);

// autoscroll the highlighted line into view
//
// Re-centring every line yanks the view out from under a reader who is scrolling or selecting, so
// any hands-on input pauses the autoscroll. It resumes - and catches back up to the current line -
// once the reader has been idle for the grace period.
const lineEls = ref<HTMLElement[]>([]);
/** How long after the reader's last input the autoscroll leaves the view alone. */
const MANUAL_INPUT_GRACE_MS = 5000;
/** Set while the reader owns the view; cleared when the grace period lapses. */
const autoScrollPaused = ref(false);
let resumeTimer: ReturnType<typeof setTimeout> | undefined;
/** Our own smooth scrolls emit scroll events for a while; ignore those as manual input. */
let autoScrollGuardUntil = 0;

/** Any hands-on input over the lyrics: scrolling, dragging out a selection, arrow keys. */
function noteManualLyricsInput(): void {
  if (tab.value !== 'lyrics') return;
  autoScrollPaused.value = true;
  clearTimeout(resumeTimer);
  resumeTimer = setTimeout(() => {
    // A selection left standing means the reader is still working with the text; wait them out.
    if (hasLyricsSelection()) { noteManualLyricsInput(); return; }
    autoScrollPaused.value = false;
    void revealActiveLine();
  }, MANUAL_INPUT_GRACE_MS);
}

function onSidePanelScroll(): void {
  // Wheel/touch/pointer are handled directly (they can interrupt one of our smooth scrolls); this
  // catches the rest - scrollbar drags, keyboard paging - outside our own scroll window.
  if (performance.now() < autoScrollGuardUntil) return;
  noteManualLyricsInput();
}

/** Whether text inside the lyrics panel is currently selected. */
function hasLyricsSelection(): boolean {
  const container = sidePanelScroll.value;
  const sel = typeof window !== 'undefined' ? window.getSelection() : null;
  if (!container || !sel || sel.isCollapsed || sel.rangeCount === 0) return false;
  return container.contains(sel.getRangeAt(0).commonAncestorContainer);
}

async function revealActiveLine(behavior: ScrollBehavior = 'smooth', force = false): Promise<void> {
  // The queue tab scrolls the same container, so a line arriving while the queue is showing must
  // not drag it - but coming back to the lyrics has to land on the current line rather than at
  // the top, which is why this is callable rather than only watched.
  if (tab.value !== 'lyrics' || activeLine.value < 0) return;
  if (autoScrollPaused.value && !force) return;
  await nextTick();
  const el = lineEls.value[activeLine.value];
  const container = sidePanelScroll.value;
  if (!el || !container) return;
  autoScrollGuardUntil = performance.now() + (behavior === 'smooth' ? 900 : 200);
  scrollCentre(container, el, behavior);
}
watch(activeLine, () => { void revealActiveLine(); });

/** Hand the view back to the autoscroll: a new track, or the tab being (re)opened. */
function resumeAutoScroll(): void {
  clearTimeout(resumeTimer);
  autoScrollPaused.value = false;
}
onUnmounted(() => clearTimeout(resumeTimer));

watch(
  () => [ui.fullScreenOpen, tab.value, player.currentTrackId] as const,
  ([open, activeTab]) => {
    if (!open || activeTab !== 'lyrics') return;
    loadLyrics();
    // Already-loaded lyrics render wherever they were left; jump straight to the current line.
    // Opening the tab (or a new track) is a fresh start, so any earlier pause is void.
    resumeAutoScroll();
    void revealActiveLine('auto', true);
  },
);

/**
 * The dialog root fills the viewport and must never scroll, but `overflow: hidden` only stops the
 * user - the browser still scrolls it to reveal a focused control, and that shifts the whole
 * overlay (header off the top, bottom-anchored visualizer up off the bottom edge). Pin it back.
 */
function keepDialogPinned(e: Event): void {
  const root = e.currentTarget as HTMLElement | null;
  if (!root) return;
  if (root.scrollTop !== 0) root.scrollTop = 0;
  if (root.scrollLeft !== 0) root.scrollLeft = 0;
}

// Esc / swipe-down to dismiss
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape') ui.closeFullScreen();
}
watch(
  () => ui.fullScreenOpen,
  (open) => {
    if (typeof window === 'undefined') return;
    if (open) window.addEventListener('keydown', onKeydown);
    else window.removeEventListener('keydown', onKeydown);
  },
);

let touchStartY = 0;
function onTouchStart(e: TouchEvent) { touchStartY = e.touches[0]?.clientY ?? 0; }
function onTouchEnd(e: TouchEvent) {
  const endY = e.changedTouches[0]?.clientY ?? 0;
  if (endY - touchStartY > 90) ui.closeFullScreen();
}

function navTo(name: string, id?: string) {
  if (!id) return;
  ui.closeFullScreen();
  router.push({ name, params: { id } });
}
</script>

<template>
  <Transition name="fs-fade">
    <!-- data attr: style.css drops the toast offset back to the edge via :has() -->
    <div
      v-if="ui.fullScreenOpen && track"
      data-fullscreen-player
      class="fixed inset-0 z-50 overflow-hidden bg-bg text-text"
      role="dialog"
      aria-label="Now playing"
      @scroll.passive="keepDialogPinned"
      @touchstart="onTouchStart"
      @touchend="onTouchEnd"
    >
      <!-- art-derived backdrop: dominant-colour wash over the blurred cover -->
      <div class="absolute inset-0 -z-10">
        <img v-if="backdropUrl" :src="backdropUrl" alt="" class="h-full w-full scale-125 object-cover opacity-30 blur-3xl" />
        <div
          v-if="tint"
          class="absolute inset-0 transition-opacity duration-500"
          :style="{ background: `radial-gradient(120% 80% at 50% 0%, color-mix(in srgb, ${tint} 45%, transparent) 0%, transparent 70%)` }"
        />
        <div class="absolute inset-0 bg-gradient-to-b from-bg/40 via-bg/80 to-bg" />
      </div>

      <!-- ambient visualizer: a faint, full-bleed spectrum floor behind the player -->
      <div
        v-if="showVisualizer"
        class="pointer-events-none absolute inset-x-0 bottom-0 z-0 h-[45%] opacity-60"
        :style="{
          maskImage: 'linear-gradient(to top, black 30%, transparent)',
          WebkitMaskImage: 'linear-gradient(to top, black 30%, transparent)',
        }"
      >
        <Visualizer :tint="tint" :active="ui.fullScreenOpen && showVisualizer" />
      </div>

      <div class="relative z-10 mx-auto flex h-full max-w-6xl flex-col px-4 pb-6 pt-4 sm:px-8">
        <!-- header -->
        <header class="flex items-center justify-between">
          <button
            type="button"
            class="grid h-10 w-10 place-items-center rounded-control text-muted transition-colors hover:text-text"
            aria-label="Close full-screen player"
            @click="ui.closeFullScreen()"
          >
            <ChevronDown :size="24" :stroke-width="2" />
          </button>
          <div class="text-center">
            <div class="label">Now playing</div>
            <div v-if="player.isRemote" class="font-mono text-[10px] text-accent">
              on {{ player.outputName }}
            </div>
          </div>
          <div class="flex items-center gap-1">
            <OutputPicker size="md" />
            <button
              type="button"
              class="grid h-10 w-10 place-items-center rounded-control text-muted transition-colors hover:text-text"
              :aria-pressed="sidePanelOpen"
              :aria-label="sidePanelOpen ? 'Collapse side panel' : 'Show side panel'"
              @click="sidePanelOpen = !sidePanelOpen"
            >
              <PanelRightClose v-if="sidePanelOpen" :size="20" :stroke-width="2" />
              <PanelRightOpen v-else :size="20" :stroke-width="2" />
            </button>
          </div>
        </header>

        <!-- body -->
        <div
          class="grid min-h-0 flex-1 grid-cols-1 gap-8 py-4"
          :class="sidePanelOpen
            ? 'grid-rows-[minmax(0,auto)_minmax(0,1fr)] lg:grid-cols-2 lg:grid-rows-[minmax(0,1fr)] lg:items-center lg:gap-12'
            : 'grid-rows-[minmax(0,1fr)] items-center'"
        >
          <!-- left: cover + controls -->
          <div class="flex min-h-0 flex-col items-center justify-center gap-6">
            <!--
              The art is the only thing in this column that can give, so a short window has to
              shrink it rather than clip the header off the top. That cap lives in `cover-fit`
              (style.css) and is measured off the viewport, deliberately: this column is a
              *centred* grid item, so its height is its content's height, and sizing the art as
              a fraction of that is circular - `flex-1` + `h-full` here resolved to zero and the
              cover disappeared entirely.
            -->
            <div class="flex w-full max-w-md justify-center">
              <div class="cover-fit aspect-square w-full overflow-hidden rounded-control shadow-2xl ring-1 ring-border">
                <CoverArt :cover-art="artId" :alt="track.album ?? track.title" :size="600" />
              </div>
            </div>

            <div class="w-full max-w-md">
              <!-- title / artist / fav -->
              <div class="flex items-start justify-between gap-3">
                <div class="min-w-0">
                  <button
                    type="button"
                    class="block max-w-full truncate text-left font-display text-2xl font-semibold text-text hover:underline sm:text-3xl"
                    @click="navTo('album', track.albumId)"
                  >{{ track.title }}</button>
                  <ArtistCredit
                    :track="track"
                    :navigate="(id: string) => navTo('artist', id)"
                    class="mt-1 block max-w-full truncate text-left text-muted transition-colors hover:text-text"
                  />
                  <span
                    v-if="sourceFormat"
                    class="mt-2 inline-block cursor-default rounded-control border border-border px-1.5 py-0.5 font-mono text-[10px] tracking-wide text-muted"
                  >{{ sourceFormat }}</span>
                </div>
                <div class="mt-1 flex shrink-0 items-center gap-1">
                  <FavoriteButton
                    :active="isFav"
                    :size="24"
                    @toggle="favorites.toggle('song', track.id, track.title)"
                  />
                  <ContextMenu :items="trackMenu">
                    <button
                      type="button"
                      class="grid h-9 w-9 place-items-center rounded-control text-muted transition-colors hover:text-text"
                      aria-label="More options"
                    >
                      <MoreHorizontal :size="22" :stroke-width="2" />
                    </button>
                  </ContextMenu>
                </div>
              </div>

              <!-- scrubber -->
              <div class="mt-5">
                <ProgressScrubber
                  size="md"
                  :current="player.currentTime"
                  :duration="player.duration"
                  :buffered="player.bufferedProgress"
                  @seek="player.seek($event)"
                />
                <div class="mt-1 flex justify-between font-mono text-[11px] tabular-nums text-muted">
                  <span>{{ formatDuration(player.currentTime) }}</span>
                  <span>{{ formatDuration(player.duration) }}</span>
                </div>
              </div>

              <!-- transport (drop-shadow keeps icons legible over the visualizer) -->
              <div class="mt-4 flex items-center justify-center gap-6 [filter:drop-shadow(0_1px_4px_rgb(0_0_0_/_0.6))]">
                <button
                  type="button"
                  class="transition-colors"
                  :class="player.isShuffled ? 'text-accent' : 'text-muted hover:text-text'"
                  aria-label="Shuffle"
                  @click="player.toggleShuffle()"
                >
                  <Shuffle :size="20" :stroke-width="2" />
                </button>
                <button type="button" class="text-text transition-colors hover:text-accent" aria-label="Previous" @click="player.previous()">
                  <SkipBack :size="26" :stroke-width="2" fill="currentColor" />
                </button>
                <PlayButton size="lg" :playing="player.isPlaying" :busy="showBusy" @click="player.togglePlay()" />
                <button type="button" class="text-text transition-colors hover:text-accent" aria-label="Next" @click="player.next()">
                  <SkipForward :size="26" :stroke-width="2" fill="currentColor" />
                </button>
                <button
                  type="button"
                  class="transition-colors"
                  :class="repeatActive ? 'text-accent' : 'text-muted hover:text-text'"
                  aria-label="Repeat"
                  @click="player.cycleRepeatMode()"
                >
                  <Repeat1 v-if="player.repeatMode === 'one'" :size="20" :stroke-width="2" />
                  <Repeat v-else :size="20" :stroke-width="2" />
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
                  :title="player.repeatMode !== 'off'
                    ? 'Auto-play - inactive while repeat is on'
                    : 'Auto-play: keep the queue going with similar music'"
                  @click="player.toggleAutoPlay()"
                >
                  <InfinityIcon :size="21" :stroke-width="2" />
                </button>
              </div>

              <!-- volume: mute (left), centered slider, visualizer toggle (right) -->
              <div class="mt-5 grid grid-cols-[1fr_auto_1fr] items-center [filter:drop-shadow(0_1px_4px_rgb(0_0_0_/_0.6))]">
                <button
                  type="button"
                  class="justify-self-start text-muted transition-colors hover:text-text"
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
                  class="volume-slider h-1 w-40 cursor-pointer appearance-none rounded-full bg-surface-2"
                  @input="player.setVolume(parseFloat(($event.target as HTMLInputElement).value))"
                />
                <button
                  v-if="visualizerAvailable"
                  type="button"
                  class="justify-self-end transition-colors"
                  :class="visualizerOn ? 'text-accent' : 'text-muted hover:text-text'"
                  :aria-pressed="visualizerOn"
                  aria-label="Toggle visualizer"
                  @click="visualizerOn = !visualizerOn"
                >
                  <AudioLines :size="18" :stroke-width="2" />
                </button>
              </div>
            </div>
          </div>

          <!-- right: tabs (queue / lyrics) -->
          <div v-if="sidePanelOpen" class="flex min-h-0 flex-col lg:self-stretch">
            <div class="mb-3 flex gap-1 border-b border-border">
              <button
                type="button"
                class="flex items-center gap-2 border-b-2 px-3 py-2 text-sm font-medium transition-colors"
                :class="tab === 'queue' ? 'border-accent text-text' : 'border-transparent text-muted hover:text-text'"
                @click="tab = 'queue'"
              >
                <ListMusic :size="16" :stroke-width="2" /> Queue
              </button>
              <button
                type="button"
                class="flex items-center gap-2 border-b-2 px-3 py-2 text-sm font-medium transition-colors"
                :class="tab === 'lyrics' ? 'border-accent text-text' : 'border-transparent text-muted hover:text-text'"
                @click="tab = 'lyrics'"
              >
                <Mic2 :size="16" :stroke-width="2" /> Lyrics
              </button>
            </div>

            <div
              ref="sidePanelScroll"
              class="no-scrollbar min-h-0 flex-1 overflow-y-auto pr-1"
              @scroll.passive="onSidePanelScroll"
              @wheel.passive="noteManualLyricsInput"
              @touchmove.passive="noteManualLyricsInput"
              @pointerdown.passive="noteManualLyricsInput"
              @selectstart.passive="noteManualLyricsInput"
            >
              <!-- queue: played, playing and up next, same row as the queue drawer -->
              <ul v-if="tab === 'queue'" class="flex flex-col">
                <li v-if="player.queue.length === 0" class="py-8 text-center text-sm text-muted">The queue is empty.</li>
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
                    :is-auto-played="player.isAutoPlayTrack(t.id)"
                    :is-dragging="dragIndex === i"
                    :is-drop-target="overIndex === i && dragIndex !== null"
                    :navigate="navTo"
                    @play="player.playAt(i)"
                    @remove="player.removeFromQueue(i)"
                    @dragstart="onDragStart(i, $event)"
                    @dragover="onDragOver(i, $event)"
                    @drop="onDrop(i)"
                    @dragend="onDragEnd"
                  />
                </li>
              </ul>

              <!-- lyrics -->
              <div v-else class="pb-8">
                <p v-if="lyricsLoading" class="py-8 text-center text-sm text-muted">Loading lyrics…</p>
                <p v-else-if="!hasLyrics" class="py-8 text-center text-sm text-muted">No lyrics available.</p>
                <!-- synced -->
                <div v-else-if="lyrics" class="flex flex-col gap-1.5">
                  <p
                    v-for="(line, li) in lyrics.line"
                    :key="li"
                    :ref="(el) => { if (el) lineEls[li] = el as HTMLElement; }"
                    class="relative text-lg leading-snug transition-colors"
                    :class="[
                      lyrics.synced ? 'pl-7' : '',
                      lyrics.synced
                        ? (li === activeLine ? 'font-semibold text-text' : 'text-faint')
                        : 'text-muted',
                    ]"
                  >
                    <NowPlayingBars
                      v-if="lyrics.synced && li === activeLine"
                      class="absolute left-0 top-[0.35em] inline-flex text-accent"
                    />
                    {{ line.value || '♪' }}
                  </p>
                </div>
                <!-- plain -->
                <pre v-else class="whitespace-pre-wrap font-sans text-base leading-relaxed text-muted">{{ plainLyrics }}</pre>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.fs-fade-enter-active,
.fs-fade-leave-active { transition: opacity 0.24s ease, transform 0.24s ease; }
.fs-fade-enter-from,
.fs-fade-leave-to { opacity: 0; transform: translateY(2%); }

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
