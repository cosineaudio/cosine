<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import {
  DialogRoot, DialogPortal, DialogOverlay, DialogContent, DialogTitle, DialogDescription,
} from 'reka-ui';
import {
  AudioLines, Headphones, ListMusic, Mic2, Music2, Pause, Plus, Search, Users,
  Volume2, VolumeX,
} from 'lucide-vue-next';
import { toast } from 'vue-sonner';
import { usePartyGuestStore } from '@/stores/partyGuest';
import {
  getPartyLyrics, partyArtworkUrl, type PartySearchTrack,
} from '@/api/party';
import type { StructuredLyrics } from '@/api/lyrics';
import Spinner from '@/components/ui/Spinner.vue';
import NowPlayingBars from '@/components/ui/NowPlayingBars.vue';
import Visualizer from '@/components/Visualizer.vue';
import { useDominantColorFromUrl } from '@/composables/useDominantColor';
import { formatDuration } from '@/lib/format';
import { scrollCentre } from '@/lib/scrollCentre';

/**
 * The guest's whole world: this page is reachable from a share link with no account, so it
 * lives outside DefaultLayout and touches nothing that assumes a session. Everything on it
 * goes through the guest store - a minted ticket for anonymous visitors, the session cookie
 * for signed-in users (who skip the name prompt entirely).
 *
 * The active phase is deliberately the FullScreenPlayer's shape - art-tinted backdrop, big
 * cover, side panel with queue and lyrics, ambient visualizer - because to a guest this page
 * IS the player, not a status report about someone else's.
 */

const route = useRoute();
const guest = usePartyGuestStore();

const token = computed(() => String(route.params.token ?? ''));
const displayName = ref('');

// ---- position interpolation ----
// States arrive ~1 Hz; everything position-driven (bar, synced lyrics) animates between them
// from the moment each one landed.
const stateReceivedAt = ref(0);
const nowTick = ref(0);
let ticker: ReturnType<typeof setInterval> | null = null;

watch(() => guest.state, () => { stateReceivedAt.value = performance.now(); });

const positionSeconds = computed(() => {
  const s = guest.state;
  if (!s) return 0;
  void nowTick.value; // subscribe to the ticker
  const base = s.positionMs / 1000;
  if (!s.isPlaying) return base;
  return base + (performance.now() - stateReceivedAt.value) / 1000;
});

const durationSeconds = computed(() =>
  guest.currentItem?.durationMs != null ? guest.currentItem.durationMs / 1000 : 0);

const progressPercent = computed(() =>
  durationSeconds.value > 0
    ? Math.min(100, (positionSeconds.value / durationSeconds.value) * 100)
    : 0);

const hostName = computed(() => guest.members.find((m) => m.isOwner)?.displayName ?? null);
const partyPlaying = computed(() => guest.state?.isPlaying === true);
/** The host pressed pause mid-party - shown as a glyph over the art, not a status line. */
const hostPaused = computed(() => !!guest.currentItem && guest.state?.isPlaying === false);

const artworkUrl = computed(() => {
  const item = guest.currentItem;
  if (!item?.albumId) return null;
  return partyArtworkUrl(guest.partyIdRef, item.albumId, guest.guestTicket, 512);
});

// Art is the color (LIBERTAE): the current cover tints the backdrop and the visualizer.
const { tint } = useDominantColorFromUrl(() => {
  const item = guest.currentItem;
  if (!item?.albumId) return null;
  return partyArtworkUrl(guest.partyIdRef, item.albumId, guest.guestTicket, 128);
});

// ---- side panel ----
const tab = ref<'queue' | 'lyrics' | 'add'>('queue');
const panelScroll = ref<HTMLElement | null>(null);

// The invitation: land on the link, the party knocks, and "Join" is the whole ceremony -
// one click joins AND starts the audio (the click is the user gesture the browser needs).
const inviteOpen = ref(true);

async function joinAndListen() {
  await guest.join(token.value, displayName.value || 'Guest');
  if (guest.phase === 'active') {
    guest.startListening();
    inviteOpen.value = false;
  }
}

// Returning members resume mid-party with no gesture to ride, so the same takeover asks
// once whether to pick the audio back up. Never re-opened by "Stop listening".
const listenPromptOpen = ref(false);
let listenPromptAnswered = false;
watch(
  () => guest.phase,
  (p) => {
    if (p === 'active' && !guest.listening && !listenPromptAnswered) {
      listenPromptOpen.value = true;
      listenPromptAnswered = true;
    }
    if (p !== 'active') listenPromptOpen.value = false;
  },
  { immediate: true },
);
watch(() => guest.listening, (l) => { if (l) listenPromptOpen.value = false; });

function acceptListenPrompt() {
  guest.startListening();
  listenPromptOpen.value = false;
}

// Ambient visualizer on/off - same persisted preference the full-screen player keeps.
const VISUALIZER_KEY = 'cosine_visualizer_on';
const visualizerOn = ref((() => {
  try { return localStorage.getItem(VISUALIZER_KEY) !== '0'; } catch { return true; }
})());
watch(visualizerOn, (on) => {
  try { localStorage.setItem(VISUALIZER_KEY, on ? '1' : '0'); } catch { /* ignore */ }
});

// ---- search (Add songs tab) ----
const query = ref('');
const results = ref<PartySearchTrack[]>([]);
const searching = ref(false);
let searchTimer: ReturnType<typeof setTimeout> | null = null;

watch(query, (q) => {
  if (searchTimer) clearTimeout(searchTimer);
  if (!q.trim()) {
    results.value = [];
    return;
  }
  searchTimer = setTimeout(async () => {
    searching.value = true;
    try {
      results.value = await guest.search(q.trim());
    } catch {
      results.value = [];
    } finally {
      searching.value = false;
    }
  }, 300);
});

async function add(track: PartySearchTrack) {
  const ok = await guest.addTrack(track.id);
  if (ok) toast.success(`Added "${track.title}" to the queue`);
  else toast.error('Could not add the track');
}

// ---- lyrics ----
const lyrics = ref<StructuredLyrics | null>(null);
const plainLyrics = ref<string | null>(null);
const lyricsLoading = ref(false);
const lyricsTrackId = ref<string | null>(null);
const lineEls = ref<HTMLElement[]>([]);

async function loadLyrics() {
  const trackId = guest.state?.trackId;
  if (!trackId || lyricsTrackId.value === trackId) return;
  lyricsTrackId.value = trackId;
  lyrics.value = null;
  plainLyrics.value = null;
  lineEls.value = [];
  lyricsLoading.value = true;
  try {
    const { structured, plain } = await getPartyLyrics(
      guest.partyIdRef, trackId, guest.guestTicket);
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

// The synced highlight rides the same interpolated position as the progress bar, so guests
// see lines land where the host's playback actually is, not where the last state tick was.
const activeLine = computed(() => {
  if (!lyrics.value?.synced) return -1;
  const ms = positionSeconds.value * 1000;
  const lines = lyrics.value.line;
  let idx = -1;
  for (let i = 0; i < lines.length; i++) {
    if ((lines[i].start ?? 0) <= ms) idx = i;
    else break;
  }
  return idx;
});

const hasLyrics = computed(() => !!lyrics.value || !!plainLyrics.value);

async function revealActiveLine(behavior: ScrollBehavior = 'smooth'): Promise<void> {
  if (tab.value !== 'lyrics' || activeLine.value < 0) return;
  await nextTick();
  const el = lineEls.value[activeLine.value];
  const container = panelScroll.value;
  if (!el || !container) return;
  scrollCentre(container, el, behavior);
}

watch(activeLine, () => { void revealActiveLine(); });
watch(() => [tab.value, guest.state?.trackId] as const, ([activeTab]) => {
  if (activeTab !== 'lyrics') return;
  void loadLyrics();
  void revealActiveLine('auto');
});

onMounted(() => {
  void guest.init(token.value);
  ticker = setInterval(() => { nowTick.value++; }, 500);
});

onUnmounted(() => {
  if (ticker) clearInterval(ticker);
  if (searchTimer) clearTimeout(searchTimer);
  guest.teardown();
});
</script>

<template>
  <div class="bg-bg text-text">
    <!-- ===== Resolving who's calling ===== -->
    <div v-if="guest.phase === 'loading'" class="grid min-h-viewport place-items-center">
      <Spinner size="md" />
    </div>

    <!-- ===== Invite: the party knocks ===== -->
    <div
      v-else-if="guest.phase === 'invite'"
      class="relative flex min-h-viewport flex-col items-center justify-center overflow-hidden px-6 text-center"
    >
      <span class="label">cosine · listening party</span>
      <h1
        class="mt-5 font-display font-bold uppercase leading-[0.9] tracking-tight text-text"
        style="font-size: clamp(3rem, 8vw, 7rem)"
      >
        You're<br />invited
      </h1>
      <!-- reconsider after "Not now" -->
      <button
        v-if="!inviteOpen"
        type="button"
        class="mt-10 flex items-center gap-2 rounded-control bg-accent px-6 py-3 font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
        @click="inviteOpen = true"
      >
        <Headphones :size="18" :stroke-width="2" /> Join the party
      </button>
      <p class="absolute bottom-10 font-mono text-xs text-faint">// no account needed - just ears</p>

      <DialogRoot :open="inviteOpen" @update:open="(v) => (inviteOpen = v)">
        <DialogPortal>
          <DialogOverlay class="fixed inset-0 z-[60] bg-black/70 backdrop-blur-sm" />
          <DialogContent
            class="fixed left-1/2 top-1/2 z-[60] w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-control border border-border bg-surface p-6 text-center shadow-2xl focus:outline-none"
            @open-auto-focus.prevent
          >
            <span class="label">Listening party</span>
            <DialogTitle class="mt-2 font-display text-2xl font-bold tracking-tight text-text">
              {{ guest.preview?.name }}
            </DialogTitle>
            <DialogDescription class="mt-1 text-sm text-muted">
              <template v-if="guest.preview?.hostName">Hosted by {{ guest.preview.hostName }} · </template>
              there's a party going on right now. Jump in and hear it live.
            </DialogDescription>

            <input
              v-if="!guest.isSignedIn"
              v-model="displayName"
              type="text"
              maxlength="64"
              autocomplete="nickname"
              aria-label="Your name"
              class="mt-5 w-full rounded-control border border-border bg-surface-2 px-3 py-2.5 text-center text-sm text-text placeholder:text-faint focus:border-transparent"
              placeholder="Your name (optional)"
              @keydown.enter="joinAndListen"
            />

            <div
              v-if="guest.joinError"
              class="mt-3 rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger"
              role="alert"
            >
              {{ guest.joinError }}
            </div>

            <button
              type="button"
              :disabled="guest.joining"
              class="mt-5 flex w-full items-center justify-center gap-2 rounded-control bg-accent py-2.5 font-medium text-accent-ink transition-transform hover:scale-[1.01] active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-50"
              @click="joinAndListen"
            >
              <Spinner v-if="guest.joining" size="md" />
              <Headphones v-else :size="18" :stroke-width="2" />
              {{ guest.joining ? 'Joining…' : 'Join the party' }}
            </button>
            <button
              type="button"
              class="mt-2 w-full rounded-control px-4 py-2 text-sm text-muted transition-colors hover:text-text"
              @click="inviteOpen = false"
            >
              Not now
            </button>
          </DialogContent>
        </DialogPortal>
      </DialogRoot>
    </div>

    <!-- ===== Ended ===== -->
    <div
      v-else-if="guest.phase === 'ended'"
      class="flex min-h-viewport flex-col items-center justify-center px-6 text-center"
    >
      <span class="label">Listening party</span>
      <h2
        class="mt-4 font-display font-bold uppercase leading-[0.95] tracking-tight text-text"
        style="font-size: var(--text-display-xl)"
      >
        That's a wrap
      </h2>
      <p class="mt-5 max-w-sm text-muted">The party has ended. Thanks for listening.</p>
      <p class="mt-10 font-mono text-xs text-faint">// ask the host for a fresh link any time</p>
    </div>

    <!-- ===== Active: the broadcast booth, full-screen-player shape ===== -->
    <div v-else class="relative h-viewport overflow-hidden">
      <!-- join-the-jam takeover: shown on arrival, and again any time listening is off -->
      <DialogRoot :open="listenPromptOpen" @update:open="(v) => (listenPromptOpen = v)">
      <DialogPortal>
        <DialogOverlay class="fixed inset-0 z-[60] bg-black/70 backdrop-blur-sm" />
        <DialogContent
          class="fixed left-1/2 top-1/2 z-[60] w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-control border border-border bg-surface p-6 text-center shadow-2xl focus:outline-none"
          @open-auto-focus.prevent
        >
          <div class="mx-auto mb-5 w-36 overflow-hidden rounded-control shadow-2xl ring-1 ring-border">
            <img
              v-if="artworkUrl"
              :src="artworkUrl"
              :alt="guest.currentItem?.album ?? ''"
              class="aspect-square w-full object-cover"
            />
            <div v-else class="grid aspect-square w-full place-items-center bg-surface-2">
              <Music2 :size="32" :stroke-width="1.5" class="text-faint" />
            </div>
          </div>
          <span class="label">Listening party</span>
          <DialogTitle class="mt-2 font-display text-2xl font-bold tracking-tight text-text">
            {{ guest.party?.name }}
          </DialogTitle>
          <DialogDescription class="mt-1 text-sm text-muted">
            <template v-if="hostName">Hosted by {{ hostName }} · </template>hear it live, together
          </DialogDescription>
          <button
            type="button"
            class="mt-6 flex w-full items-center justify-center gap-2 rounded-control bg-accent py-2.5 font-medium text-accent-ink transition-transform hover:scale-[1.01] active:scale-[0.99]"
            @click="acceptListenPrompt"
          >
            <Headphones :size="18" :stroke-width="2" /> Listen along
          </button>
          <button
            type="button"
            class="mt-2 w-full rounded-control px-4 py-2 text-sm text-muted transition-colors hover:text-text"
            @click="listenPromptOpen = false"
          >
            Just browsing
          </button>
        </DialogContent>
        </DialogPortal>
      </DialogRoot>
      <!-- art-derived backdrop: dominant-colour wash over the blurred cover -->
      <div class="absolute inset-0 -z-10">
        <img
          v-if="artworkUrl"
          :src="artworkUrl"
          alt=""
          class="h-full w-full scale-125 object-cover opacity-30 blur-3xl"
        />
        <div
          v-if="tint"
          class="absolute inset-0 transition-opacity duration-500"
          :style="{ background: `radial-gradient(120% 80% at 50% 0%, color-mix(in srgb, ${tint} 45%, transparent) 0%, transparent 70%)` }"
        />
        <div class="absolute inset-0 bg-gradient-to-b from-bg/40 via-bg/80 to-bg" />
      </div>

      <!-- ambient visualizer: the guest's own analyser tap, only while listening along -->
      <div
        v-if="guest.listening && visualizerOn"
        class="pointer-events-none absolute inset-x-0 bottom-0 z-0 h-[45%] opacity-60"
        :style="{
          maskImage: 'linear-gradient(to top, black 30%, transparent)',
          WebkitMaskImage: 'linear-gradient(to top, black 30%, transparent)',
        }"
      >
        <Visualizer
          :tint="tint"
          :active="guest.listening"
          :analyser="guest.ensureAnalyser"
          :playing="partyPlaying"
        />
      </div>

      <div class="relative z-10 mx-auto flex h-full max-w-6xl flex-col px-4 pb-6 pt-5 sm:px-8">
        <!-- header -->
        <header class="flex items-start justify-between gap-4">
          <div class="min-w-0">
            <div class="flex items-center gap-3">
              <span class="label">Listening party</span>
              <span class="flex items-center gap-1.5 font-mono text-[10px] uppercase tracking-widest text-accent">
                <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-accent" />
                live
              </span>
            </div>
            <h1 class="mt-1 truncate font-display text-2xl font-bold tracking-tight text-text">
              {{ guest.party?.name }}
            </h1>
            <p v-if="hostName" class="mt-0.5 text-xs text-muted">Hosted by {{ hostName }}</p>
          </div>
          <div class="flex shrink-0 items-center gap-2">
            <!-- listen-along lives up here: it is the page's one decision, not a transport -->
            <button
              v-if="!guest.listening"
              type="button"
              class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95"
              @click="guest.startListening()"
            >
              <Headphones :size="16" :stroke-width="2" /> Listen along
            </button>
            <button
              v-else
              type="button"
              class="flex items-center gap-2 rounded-control border border-border px-4 py-2 text-sm font-medium text-text transition-colors hover:bg-surface-2"
              @click="guest.stopListening()"
            >
              <Pause :size="16" :stroke-width="2" /> Stop listening
            </button>
            <div
              class="flex items-center gap-1.5 rounded-control border border-border px-2.5 py-2 font-mono text-xs text-muted"
              :title="guest.members.map((m) => m.displayName).join(', ')"
            >
              <Users :size="14" :stroke-width="2" />
              {{ guest.members.length }}
            </div>
          </div>
        </header>

        <!-- body -->
        <div
          class="grid min-h-0 flex-1 grid-cols-1 grid-rows-[minmax(0,auto)_minmax(0,1fr)] gap-8 py-4 lg:grid-cols-2 lg:grid-rows-[minmax(0,1fr)] lg:items-center lg:gap-12"
        >
          <!-- left: cover + listen-along -->
          <div class="flex min-h-0 flex-col items-center justify-center gap-6">
            <div class="flex w-full max-w-md justify-center">
              <div class="cover-fit relative aspect-square w-full overflow-hidden rounded-control shadow-2xl ring-1 ring-border">
                <img
                  v-if="artworkUrl"
                  :src="artworkUrl"
                  :alt="guest.currentItem?.album ?? ''"
                  class="h-full w-full object-cover transition-opacity duration-150 ease-out"
                  :class="hostPaused ? 'opacity-35' : 'opacity-100'"
                />
                <div v-else class="grid h-full w-full place-items-center bg-surface-2">
                  <Music2 :size="40" :stroke-width="1.5" class="text-faint" />
                </div>
                <!-- the host pressed pause: the art itself holds its breath -->
                <Transition name="pause-pop">
                  <div v-if="hostPaused" class="pointer-events-none absolute inset-0 grid place-items-center">
                    <Pause
                      :size="72"
                      :stroke-width="1.5"
                      fill="currentColor"
                      class="text-text [filter:drop-shadow(0_2px_12px_rgb(0_0_0_/_0.7))]"
                    />
                  </div>
                </Transition>
              </div>
            </div>

            <div class="w-full max-w-md">
              <!-- title -->
              <template v-if="guest.currentItem">
                <p class="truncate font-display text-2xl font-semibold text-text sm:text-3xl">
                  {{ guest.currentItem.title }}
                </p>
                <p class="mt-1 truncate text-muted">
                  {{ guest.currentItem.artist }} · {{ guest.currentItem.album }}
                </p>
              </template>
              <p v-else class="text-muted">Waiting for the host to play something…</p>

              <!-- progress (read-only: the host owns the transport) -->
              <div v-if="guest.currentItem" class="mt-5">
                <div class="h-1 w-full overflow-hidden rounded-full bg-surface-2">
                  <div
                    class="h-full bg-accent transition-[width] duration-500 ease-linear"
                    :style="{ width: progressPercent + '%' }"
                  />
                </div>
                <div class="mt-1 flex justify-between font-mono text-[11px] tabular-nums text-muted">
                  <span>{{ formatDuration(positionSeconds) }}</span>
                  <span>{{ formatDuration(durationSeconds) }}</span>
                </div>
              </div>

              <!-- volume: mute (left), centered slider, visualizer toggle (right) - the
                   full-screen player's row, for the guest's own ears -->
              <div
                v-if="guest.listening"
                class="mt-5 grid grid-cols-[1fr_auto_1fr] items-center [filter:drop-shadow(0_1px_4px_rgb(0_0_0_/_0.6))]"
              >
                <button
                  type="button"
                  class="justify-self-start text-muted transition-colors hover:text-text"
                  aria-label="Mute"
                  @click="guest.setVolume(guest.volume === 0 ? 1 : 0)"
                >
                  <VolumeX v-if="guest.volume === 0" :size="18" :stroke-width="2" />
                  <Volume2 v-else :size="18" :stroke-width="2" />
                </button>
                <input
                  type="range" min="0" max="1" step="0.01"
                  :value="guest.volume"
                  aria-label="Volume"
                  class="volume-slider h-1 w-40 cursor-pointer appearance-none rounded-full bg-surface-2"
                  @input="guest.setVolume(parseFloat(($event.target as HTMLInputElement).value))"
                />
                <button
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

          <!-- right: tabs (queue / lyrics / add songs) -->
          <div class="flex min-h-0 flex-col lg:self-stretch">
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
              <button
                type="button"
                class="flex items-center gap-2 border-b-2 px-3 py-2 text-sm font-medium transition-colors"
                :class="tab === 'add' ? 'border-accent text-text' : 'border-transparent text-muted hover:text-text'"
                @click="tab = 'add'"
              >
                <Plus :size="16" :stroke-width="2" /> Add songs
              </button>
            </div>

            <!-- the search box stays put while its results scroll -->
            <div v-if="tab === 'add'" class="relative mb-3">
              <Search :size="15" :stroke-width="2" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-faint" />
              <input
                v-model="query"
                type="search"
                class="w-full rounded-control border border-border bg-surface py-2.5 pl-9 pr-3 text-sm text-text placeholder:text-faint focus:border-transparent"
                placeholder="Search the host's library…"
              />
            </div>

            <div ref="panelScroll" class="no-scrollbar min-h-0 flex-1 overflow-y-auto pr-1">
              <!-- queue -->
              <ul v-if="tab === 'queue'" class="flex flex-col">
                <li v-if="guest.queue.length === 0" class="py-8 text-center text-sm text-muted">
                  Nothing queued yet. Add the first song.
                </li>
                <li
                  v-for="item in guest.queue"
                  :key="item.entryId"
                  class="flex items-center gap-3 rounded-control px-2 py-1.5 transition-colors hover:bg-surface-2"
                  :class="item.trackId === guest.state?.trackId ? 'bg-surface-2/60' : ''"
                >
                  <div class="grid w-7 shrink-0 place-items-center">
                    <NowPlayingBars
                      v-if="item.trackId === guest.state?.trackId && partyPlaying"
                      class="text-accent"
                    />
                    <span
                      v-else
                      class="font-mono text-sm"
                      :class="item.trackId === guest.state?.trackId ? 'text-accent' : 'text-muted'"
                    >{{ item.position + 1 }}</span>
                  </div>
                  <div class="flex min-w-0 flex-1 flex-col items-start">
                    <span
                      class="max-w-full truncate font-medium"
                      :class="item.trackId === guest.state?.trackId ? 'text-accent' : 'text-text'"
                    >{{ item.title }}</span>
                    <span class="max-w-full truncate text-sm text-muted">{{ item.artist }}</span>
                  </div>
                  <span v-if="item.addedBy" class="shrink-0 font-mono text-xs text-faint">
                    {{ item.addedBy }}
                  </span>
                  <span class="w-12 shrink-0 text-right font-mono text-sm text-muted">
                    {{ formatDuration(item.durationMs != null ? item.durationMs / 1000 : null) }}
                  </span>
                </li>
              </ul>

              <!-- lyrics -->
              <div v-else-if="tab === 'lyrics'" class="pb-8">
                <p v-if="lyricsLoading" class="py-8 text-center text-sm text-muted">Loading lyrics…</p>
                <p v-else-if="!guest.currentItem" class="py-8 text-center text-sm text-muted">
                  Waiting for the host to play something…
                </p>
                <p v-else-if="!hasLyrics" class="py-8 text-center text-sm text-muted">No lyrics available.</p>
                <!-- synced / structured -->
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

              <!-- add songs -->
              <div v-else>
                <div v-if="searching" class="mt-4 flex justify-center"><Spinner size="sm" /></div>
                <ul v-else-if="results.length > 0" class="flex flex-col">
                  <li
                    v-for="result in results"
                    :key="result.id"
                    class="flex items-center gap-3 rounded-control px-2 py-1.5 transition-colors hover:bg-surface-2"
                  >
                    <div class="flex min-w-0 flex-1 flex-col items-start">
                      <span class="max-w-full truncate font-medium text-text">{{ result.title }}</span>
                      <span class="max-w-full truncate text-sm text-muted">{{ result.artist }} · {{ result.album }}</span>
                    </div>
                    <span class="w-12 shrink-0 text-right font-mono text-sm text-muted">
                      {{ formatDuration(result.durationMs != null ? result.durationMs / 1000 : null) }}
                    </span>
                    <button
                      type="button"
                      class="grid h-8 w-8 shrink-0 place-items-center rounded-control border border-border text-muted transition-colors hover:border-faint hover:text-text"
                      :aria-label="`Add ${result.title} to the queue`"
                      @click="add(result)"
                    >
                      <Plus :size="15" :stroke-width="2" />
                    </button>
                  </li>
                </ul>
                <p v-else-if="query.trim()" class="mt-4 text-center text-sm text-muted">No matches.</p>
                <p v-else class="mt-4 text-center text-sm text-muted">
                  Search the host's library and add songs to the party queue.
                </p>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
/* Fast, physical pause pop - LIBERTAE motion is feedback, never decoration. */
.pause-pop-enter-active,
.pause-pop-leave-active {
  transition: opacity 0.15s var(--ease-out), transform 0.15s var(--ease-out);
}
.pause-pop-enter-from,
.pause-pop-leave-to {
  opacity: 0;
  transform: scale(0.85);
}

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
