import { defineStore } from 'pinia';
import { ref, computed, watch } from 'vue';
import type { Track } from '@/api/types';
import { scrobble as scrobbleTrack } from '@/api/annotation';
import { coverArtUrl } from '@/api/browsing';
import { getPlayQueue, savePlayQueue } from '@/api/queue';
import { pushRecentTrack } from '@/lib/recentTracks';

const FORMAT_PREF_KEY = 'cosine_preferred_format';
const BITRATE_PREF_KEY = 'cosine_preferred_bitrate';
const VOLUME_KEY = 'cosine_volume';

/** How close (seconds) to the end before we warm the next track's stream. */
const PRELOAD_THRESHOLD = 15;

/**
 * A play is submitted once the listener reaches half the track, or four minutes of it -
 * the long-standing last.fm rule. Scrobbling from `play()` meant skipping through twenty
 * tracks recorded twenty plays; the backend treats scrobble as the deliberate signal.
 */
const SCROBBLE_FRACTION = 0.5;
const SCROBBLE_SECONDS = 240;

/** How much playback movement between saves of the cross-device resume position. */
const POSITION_SAVE_SECONDS = 10;

function loadStored<T>(key: string, fallback: T): T {
  try {
    const stored = localStorage.getItem(key);
    if (stored !== null) return JSON.parse(stored) as T;
  } catch { /* ignore */ }
  return fallback;
}

function saveStored<T>(key: string, value: T): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch { /* ignore */ }
}

export type RepeatMode = 'off' | 'one' | 'all';

export const usePlayerStore = defineStore('player', () => {
  const currentTrack = ref<Track | null>(null);
  const queue = ref<Track[]>([]);
  const queueIndex = ref(0);
  const isPlaying = ref(false);
  const currentTime = ref(0);
  const duration = ref(0);
  const bufferedEnd = ref(0);
  /**
   * The element wants to play but has run out of data - a stall, not a pause.
   *
   * Without this the two are indistinguishable in the UI: the transport goes on
   * showing a pause button while nothing comes out of the speakers, which reads
   * as a broken player rather than a slow network.
   */
  const isBuffering = ref(false);
  const volume = ref(Math.max(0, Math.min(1, loadStored(VOLUME_KEY, 1))));
  const isShuffled = ref(false);
  const repeatMode = ref<RepeatMode>('off');
  const preferredFormat = ref<string>(loadStored(FORMAT_PREF_KEY, ''));
  const preferredBitRate = ref<number>(loadStored(BITRATE_PREF_KEY, 0));

  const hasTrack = computed(() => currentTrack.value !== null);
  const currentTrackId = computed(() => currentTrack.value?.id ?? null);
  // bitRate 0 is valid: lossless formats (flac/wav) transcode without a bitrate.
  const isTranscodingEnabled = computed(() => preferredFormat.value !== '');
  /** Fraction (0–1) of the track already buffered - for the scrubber's buffered range. */
  const bufferedProgress = computed(() =>
    duration.value > 0 ? Math.min(1, bufferedEnd.value / duration.value) : 0,
  );

  // Persist format preferences to localStorage
  watch(preferredFormat, (v) => saveStored(FORMAT_PREF_KEY, v));
  watch(preferredBitRate, (v) => saveStored(BITRATE_PREF_KEY, v));
  watch(volume, (v) => saveStored(VOLUME_KEY, v));

  /** The currently active playback element (either libAudio or radioAudio). */
  const audio = ref<HTMLAudioElement | null>(null);
  /**
   * Same-origin library playback. This is the only element the visualizer's
   * analyser taps - a MediaElementSource silences any cross-origin resource
   * loaded afterwards, so radio must never touch it.
   */
  let libAudio: HTMLAudioElement | null = null;
  /** Internet-radio playback (direct cross-origin stream URLs). Never tapped. */
  let radioAudio: HTMLAudioElement | null = null;
  /** Hidden element used only to warm the next track's stream near track end. */
  let preloadAudio: HTMLAudioElement | null = null;
  let preloadedTrackId: string | null = null;

  // ---- Web Audio analyser (frequency data for the full-screen visualizer) ----
  let audioCtx: AudioContext | null = null;
  let analyser: AnalyserNode | null = null;
  let sourceNode: MediaElementAudioSourceNode | null = null;
  /** Volume for the library element once the graph exists, so the analyser can
   *  tap the signal *before* it - bar height stays independent of volume. */
  let libGain: GainNode | null = null;
  /** Guards scrobble against firing more than once per track start. Cleared by play(),
   *  so replaying or repeating a track submits it again. */
  let scrobbledTrackId: string | null = null;

  function streamUrlFor(track: Track): string {
    // Direct-URL sources (internet radio) stream as-is, no transcoding.
    if (track.streamUrl) return track.streamUrl;
    if (isTranscodingEnabled.value) {
      return `/api/stream/${encodeURIComponent(track.id)}?format=${encodeURIComponent(preferredFormat.value)}&bitRate=${preferredBitRate.value}`;
    }
    return `/api/stream/${encodeURIComponent(track.id)}`;
  }

  // ---- Shuffle ----
  //
  // Shuffle used to pick `Math.floor(Math.random() * length)` on every advance, which could
  // repeat the track that just played and replayed tracks long before the queue was exhausted.
  // Instead the queue is walked in a random permutation: every track plays once per pass.

  /** A permutation of queue indices, current track first. Empty while shuffle is off. */
  let shuffleOrder: number[] = [];
  /** Position of the playing track within `shuffleOrder`. */
  let shufflePos = 0;

  function buildShuffleOrder(startIndex: number): void {
    const order = queue.value.map((_, i) => i);
    for (let i = order.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [order[i], order[j]] = [order[j], order[i]];
    }
    // The playing track leads the permutation, so the first advance moves off it.
    const at = order.indexOf(startIndex);
    if (at > 0) [order[0], order[at]] = [order[at], order[0]];
    shuffleOrder = order;
    shufflePos = 0;
  }

  /**
   * Point `shufflePos` at the playing track, rebuilding the permutation if the queue changed
   * underneath it. Called from play(), so picking a track by hand (playAt, a click in the
   * queue) keeps the pass consistent instead of desyncing the pointer.
   */
  function syncShufflePointer(): void {
    if (shuffleOrder.length !== queue.value.length) {
      buildShuffleOrder(queueIndex.value);
      return;
    }
    const at = shuffleOrder.indexOf(queueIndex.value);
    if (at === -1) buildShuffleOrder(queueIndex.value);
    else shufflePos = at;
  }

  /**
   * Fold newly appended slots into the current permutation rather than redrawing it. A redraw puts
   * every track already heard this pass back in play, which is the one thing an endless queue must
   * not do - it is the difference between a radio and a loop.
   *
   * The new slots land at random positions *after* the pointer, so they belong to the rest of this
   * pass without displacing what has already been decided.
   */
  function extendShuffleOrder(firstIndex: number, addedCount: number): void {
    // The permutation described a different queue than the one being appended to; there is
    // nothing to preserve and a redraw is the honest repair.
    if (shuffleOrder.length !== firstIndex) {
      buildShuffleOrder(queueIndex.value);
      return;
    }
    for (let i = 0; i < addedCount; i++) {
      const at = shufflePos + 1 + Math.floor(Math.random() * (shuffleOrder.length - shufflePos));
      shuffleOrder.splice(at, 0, firstIndex + i);
    }
  }

  /** The next index in the permutation, or null when playback should stop. */
  function nextShuffleIndex(): number | null {
    syncShufflePointer();
    if (shufflePos + 1 < shuffleOrder.length) return shuffleOrder[shufflePos + 1];
    if (repeatMode.value === 'off') return null;
    // The pass is over: reshuffle and resume at the second entry, so the track that just
    // finished doesn't immediately repeat as the first of the new pass.
    buildShuffleOrder(queueIndex.value);
    return shuffleOrder.length > 1 ? shuffleOrder[1] : shuffleOrder[0];
  }

  /** The track that would play next in sequence, or null if playback would stop. */
  function upcomingTrack(): Track | null {
    if (queue.value.length === 0) return null;
    if (isShuffled.value) {
      syncShufflePointer();
      // Only within the current pass - the next permutation isn't drawn yet.
      const at = shufflePos + 1;
      return at < shuffleOrder.length ? queue.value[shuffleOrder[at]] ?? null : null;
    }
    const nextIdx = queueIndex.value + 1;
    if (nextIdx >= queue.value.length) {
      return repeatMode.value === 'all' ? queue.value[0] ?? null : null;
    }
    return queue.value[nextIdx] ?? null;
  }

  function updateBuffered(): void {
    const el = audio.value;
    if (!el) return;
    try {
      const ranges = el.buffered;
      let end = 0;
      const t = el.currentTime;
      for (let i = 0; i < ranges.length; i++) {
        if (ranges.start(i) <= t + 0.5 && ranges.end(i) > end) end = ranges.end(i);
      }
      bufferedEnd.value = end;
    } catch { /* buffered can throw before metadata */ }
  }

  /**
   * Warms the next track's stream into a hidden element shortly before the current one ends.
   * The element that eventually plays it is a *different* one, so this only pays off when the
   * browser can serve the second request out of its HTTP cache: `/rest/stream` now sends an
   * ETag + Cache-Control for that (#41). A live transcode has neither - it is chunked ffmpeg
   * output - so preloading one is a guaranteed second download and a second ffmpeg process
   * holding a transcode slot (#39). Radio is a direct cross-origin URL with no track to warm.
   */
  function maybePreloadNext(): void {
    if (isTranscodingEnabled.value) return;
    const el = audio.value;
    if (!el || !isFinite(el.duration) || el.duration === 0) return;
    if (el.duration - el.currentTime > PRELOAD_THRESHOLD) return;
    const next = upcomingTrack();
    if (!next || next.streamUrl || next.id === preloadedTrackId) return;
    preloadedTrackId = next.id;
    if (!preloadAudio) {
      preloadAudio = new Audio();
      preloadAudio.preload = 'auto';
    }
    preloadAudio.src = streamUrlFor(next);
    try { preloadAudio.load(); } catch { /* ignore */ }
  }

  /**
   * Wire an element's events to shared player state. Both the library and radio
   * elements share this; each listener no-ops unless its element is the active
   * one, so pausing the idle element during a switch can't clobber state.
   */
  function attachAudioListeners(el: HTMLAudioElement): void {
    el.addEventListener('timeupdate', () => {
      if (el !== audio.value) return;
      currentTime.value = el.currentTime;
      updatePositionState();
      maybePreloadNext();
    });
    el.addEventListener('durationchange', () => {
      if (el !== audio.value) return;
      const d = el.duration;
      // Live-transcoded streams are chunked and report Infinity/NaN; the track's
      // scanned length is the real duration regardless of stream format.
      duration.value = isFinite(d) && d > 0 ? d : (currentTrack.value?.duration ?? 0);
    });
    el.addEventListener('progress', () => { if (el === audio.value) updateBuffered(); });
    el.addEventListener('ended', () => { if (el === audio.value) onTrackEnded(); });
    el.addEventListener('play', () => {
      if (el !== audio.value) return;
      isPlaying.value = true;
      updateMediaSessionPlaybackState();
    });
    el.addEventListener('pause', () => {
      if (el !== audio.value) return;
      isPlaying.value = false;
      isBuffering.value = false;
      updateMediaSessionPlaybackState();
    });

    // Stalls. `waiting` fires when playback halts for data; `stalled` when no
    // data has arrived for a while even though it was expected. Either one ends
    // at `playing` (sound is actually coming out) or `canplay` (enough buffered
    // to resume), and both of those are cleared unconditionally so a stall can
    // never outlive the condition that set it.
    el.addEventListener('waiting', () => { if (el === audio.value) isBuffering.value = true; });
    el.addEventListener('stalled', () => { if (el === audio.value) isBuffering.value = true; });
    el.addEventListener('playing', () => { if (el === audio.value) isBuffering.value = false; });
    el.addEventListener('canplay', () => { if (el === audio.value) isBuffering.value = false; });
    el.addEventListener('emptied', () => { if (el === audio.value) isBuffering.value = false; });
  }

  let mediaSessionInited = false;
  function makeAudioElement(): HTMLAudioElement {
    const el = new Audio();
    el.volume = volume.value;
    attachAudioListeners(el);
    if (!mediaSessionInited) {
      setupMediaSessionHandlers();
      mediaSessionInited = true;
    }
    return el;
  }

  function libraryEl(): HTMLAudioElement {
    if (!libAudio) libAudio = makeAudioElement();
    return libAudio;
  }
  function radioElement(): HTMLAudioElement {
    if (!radioAudio) radioAudio = makeAudioElement();
    return radioAudio;
  }

  /** The element that should play a given track: radio streams stay off the tapped element. */
  function audioFor(track: Track): HTMLAudioElement {
    return track.streamUrl ? radioElement() : libraryEl();
  }

  /** Make `el` the active element, pausing whichever element was active before. */
  function setActiveAudio(el: HTMLAudioElement): void {
    const prev = audio.value;
    if (prev && prev !== el) prev.pause();
    audio.value = el;
  }

  function getAudio(): HTMLAudioElement {
    if (!audio.value) audio.value = libraryEl();
    return audio.value;
  }

  /**
   * Lazily build the Web Audio graph that feeds the visualizer, tapping the
   * library element. Returns the analyser, or null when Web Audio is
   * unavailable. Called on a user gesture (opening the visualizer tab).
   */
  function ensureAnalyser(): AnalyserNode | null {
    if (analyser) return analyser;
    if (typeof window === 'undefined') return null;
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return null;
    try {
      const el = libraryEl();
      audioCtx = new Ctx();
      sourceNode = audioCtx.createMediaElementSource(el);
      analyser = audioCtx.createAnalyser();
      analyser.fftSize = 2048; // 1024 bins - enough low-end resolution for log bands
      analyser.smoothingTimeConstant = 0.82;
      // source → analyser (full level) → gain (volume) → out. Tapping before the
      // gain keeps the visualizer independent of the volume slider.
      libGain = audioCtx.createGain();
      libGain.gain.value = volume.value;
      sourceNode.connect(analyser);
      analyser.connect(libGain);
      libGain.connect(audioCtx.destination);
      el.volume = 1; // the gain node owns level now
    } catch {
      analyser = null;
    }
    resumeAudioContext();
    return analyser;
  }

  function resumeAudioContext(): void {
    if (audioCtx && audioCtx.state === 'suspended') audioCtx.resume().catch(() => { /* ignore */ });
  }

  // ---- MediaSession (OS / lockscreen controls) ----

  function mediaSession(): MediaSession | null {
    if (typeof navigator === 'undefined' || !('mediaSession' in navigator)) return null;
    return navigator.mediaSession;
  }

  function setupMediaSessionHandlers(): void {
    const ms = mediaSession();
    if (!ms) return;
    const set = (action: MediaSessionAction, handler: (() => void) | null) => {
      try { ms.setActionHandler(action, handler as MediaSessionActionHandler | null); } catch { /* unsupported action */ }
    };
    set('play', () => resume());
    set('pause', () => pause());
    set('previoustrack', () => previous());
    set('nexttrack', () => next());
    try {
      ms.setActionHandler('seekto', (details) => {
        if (typeof details.seekTime === 'number') seek(details.seekTime);
      });
    } catch { /* unsupported */ }
  }

  function updateMediaSessionMetadata(track: Track): void {
    const ms = mediaSession();
    if (!ms || typeof MediaMetadata === 'undefined') return;
    const artId = track.coverArt ?? track.albumId ?? track.id;
    const origin = typeof location !== 'undefined' ? location.origin : '';
    const artwork = artId
      ? [96, 256, 512].map((size) => ({
          src: `${origin}${coverArtUrl(artId, size)}`,
          sizes: `${size}x${size}`,
          type: 'image/jpeg',
        }))
      : [];
    try {
      ms.metadata = new MediaMetadata({
        title: track.title,
        artist: track.artist ?? '',
        album: track.album ?? '',
        artwork,
      });
    } catch { /* ignore */ }
  }

  function updateMediaSessionPlaybackState(): void {
    const ms = mediaSession();
    if (!ms) return;
    try { ms.playbackState = isPlaying.value ? 'playing' : 'paused'; } catch { /* ignore */ }
  }

  function updatePositionState(): void {
    const ms = mediaSession();
    const el = audio.value;
    if (!ms || !el || typeof ms.setPositionState !== 'function') return;
    if (!isFinite(el.duration) || el.duration <= 0) return;
    try {
      ms.setPositionState({
        duration: el.duration,
        playbackRate: el.playbackRate || 1,
        position: Math.min(el.currentTime, el.duration),
      });
    } catch { /* ignore */ }
  }

  // ---- Playback ----

  /**
   * Load a track into its audio element and select it, without starting playback. Used when
   * restoring a saved queue (browsers block autoplay) and when the playing entry is removed
   * from the queue while paused.
   */
  function primeTrack(track: Track): HTMLAudioElement {
    currentTrack.value = track;
    bufferedEnd.value = 0;
    // A fresh source has nothing loaded, but priming alone does not ask for
    // sound - only play() does, so only play() claims to be waiting on any.
    isBuffering.value = false;
    currentTime.value = 0;
    preloadedTrackId = null;
    scrobbledTrackId = null;
    duration.value = track.duration ?? 0;

    const el = audioFor(track);
    setActiveAudio(el);
    el.src = streamUrlFor(track);
    updateMediaSessionMetadata(track);
    return el;
  }

  /**
   * The queue is copied, never adopted. Callers hand over a list they still own and render -
   * an album's `song` array, an artist's top songs, a genre page's songs - and every queue
   * operation here mutates in place, so keeping the reference would push auto-play's top-up
   * into the page the listener is looking at. `remotePlayer` copies for the same reason.
   */
  function play(track: Track, newQueue?: Track[]) {
    if (newQueue) {
      queue.value = [...newQueue];
      queueIndex.value = newQueue.findIndex(t => t.id === track.id);
      if (queueIndex.value === -1) queueIndex.value = 0;
    } else {
      const idx = queue.value.findIndex(t => t.id === track.id);
      if (idx !== -1) queueIndex.value = idx;
    }

    const audioEl = primeTrack(track);
    if (isShuffled.value) syncShufflePointer();
    resumeAudioContext();
    // Sound has been asked for and none is arriving yet. `canplay`/`playing`
    // clear this, usually within a frame or two - the UI defers showing
    // anything, so only a genuinely slow start is ever visible.
    isBuffering.value = true;
    audioEl.play().catch(() => { isBuffering.value = false; });

    // Local "recently played" is a log of what you started, so it is written here; the
    // server-side play count is not, and waits for maybeScrobble's threshold.
    pushRecentTrack(track);
  }

  /**
   * Submit the play once enough of the track has been heard. Driven by playback position
   * rather than by play(), so a skipped track is never counted.
   */
  function maybeScrobble(position: number): void {
    const track = currentTrack.value;
    if (!track || scrobbledTrackId === track.id) return;
    // Internet radio has no track row to scrobble against, and never reaches a threshold.
    if (track.streamUrl) return;

    const total = duration.value;
    if (!(total > 0)) return;
    if (position < Math.min(total * SCROBBLE_FRACTION, SCROBBLE_SECONDS)) return;

    scrobbledTrackId = track.id;
    scrobbleTrack(track.id).catch(() => { /* non-fatal */ });
  }

  // Synchronous so the submission is decided on the timeupdate that crossed the threshold.
  watch(currentTime, (position) => maybeScrobble(position), { flush: 'sync' });

  function pause() {
    getAudio().pause();
  }

  function resume() {
    getAudio().play().catch(() => {});
  }

  function togglePlay() {
    if (isPlaying.value) pause();
    else resume();
  }

  function next() {
    if (queue.value.length === 0) return;
    if (isShuffled.value) {
      const nextIdx = nextShuffleIndex();
      if (nextIdx === null) {
        pause();
        return;
      }
      play(queue.value[nextIdx]);
    } else {
      const nextIdx = (queueIndex.value + 1) % queue.value.length;
      if (nextIdx === 0 && repeatMode.value === 'off') {
        pause();
        return;
      }
      play(queue.value[nextIdx]);
    }
  }

  function previous() {
    if (queue.value.length === 0 || currentTime.value > 3) {
      seek(0);
      return;
    }
    if (isShuffled.value) {
      // Step back through the permutation, so "previous" undoes the last shuffled advance.
      syncShufflePointer();
      if (shufflePos <= 0) {
        seek(0);
        return;
      }
      play(queue.value[shuffleOrder[shufflePos - 1]]);
      return;
    }
    const prevIdx = queueIndex.value - 1;
    if (prevIdx < 0) {
      seek(0);
      return;
    }
    play(queue.value[prevIdx]);
  }

  function seek(seconds: number) {
    const audioEl = getAudio();
    audioEl.currentTime = seconds;
    currentTime.value = seconds;
    updatePositionState();
  }

  function setVolume(level: number) {
    volume.value = Math.max(0, Math.min(1, level));
    // Once the analyser graph exists, the gain node owns the library element's
    // level (its .volume is pinned to 1); otherwise fall back to element volume.
    if (libGain) libGain.gain.value = volume.value;
    else if (libAudio) libAudio.volume = volume.value;
    if (radioAudio) radioAudio.volume = volume.value;
  }

  /** Volume before the last mute, so unmuting restores it. */
  const volumeBeforeMute = ref(1);

  function toggleMute() {
    if (volume.value === 0) {
      setVolume(volumeBeforeMute.value > 0 ? volumeBeforeMute.value : 1);
    } else {
      volumeBeforeMute.value = volume.value;
      setVolume(0);
    }
  }

  function addToQueue(tracks: Track[], position: 'next' | 'end' = 'end') {
    const wasEmpty = queue.value.length === 0;
    // Where the pass has got to, read before the queue grows underneath the permutation.
    if (isShuffled.value && !wasEmpty) syncShufflePointer();
    const appendedAt = queue.value.length;

    if (position === 'next') {
      queue.value.splice(queueIndex.value + 1, 0, ...tracks);
    } else {
      queue.value.push(...tracks);
    }

    if (isShuffled.value && !wasEmpty && tracks.length > 0) {
      // An append only adds slots; inserting renumbers the ones the permutation refers to, so
      // only the first can be folded into the pass rather than redrawn.
      if (position === 'end') extendShuffleOrder(appendedAt, tracks.length);
      else buildShuffleOrder(queueIndex.value);
    }
    // Starting from empty: begin playing the first added track. The queue already holds it, so
    // `play` selects it in place rather than being handed a queue to copy.
    if (!currentTrack.value && tracks.length > 0) {
      play(tracks[0]);
    }
  }

  /**
   * Drop a queue entry. Removing the entry that is playing used to splice it out and leave
   * `currentTrack`/`queueIndex` pointing at it, so the store believed it was playing something
   * the queue no longer contained; the track that shifted into the slot takes over instead.
   */
  function removeFromQueue(index: number) {
    if (index < 0 || index >= queue.value.length) return;

    const wasCurrent = index === queueIndex.value;
    const wasPlaying = isPlaying.value;
    queue.value.splice(index, 1);

    if (index < queueIndex.value) {
      queueIndex.value--;
      return;
    }
    if (!wasCurrent) return;

    if (queue.value.length === 0) {
      clearQueue();
      return;
    }

    // The following track shifted into this slot - unless the removed one was last, in which
    // case the queue wraps back to its new end.
    const nextIndex = Math.min(index, queue.value.length - 1);
    if (wasPlaying) {
      play(queue.value[nextIndex]);
    } else {
      queueIndex.value = nextIndex;
      primeTrack(queue.value[nextIndex]);
      if (isShuffled.value) syncShufflePointer();
    }
  }

  /** Reorder the queue, keeping the currently-playing track selected. */
  function moveInQueue(from: number, to: number) {
    const len = queue.value.length;
    if (from < 0 || from >= len || to < 0 || to >= len || from === to) return;
    const currentId = currentTrack.value?.id;
    const [moved] = queue.value.splice(from, 1);
    queue.value.splice(to, 0, moved);
    if (currentId) {
      const idx = queue.value.findIndex(t => t.id === currentId);
      if (idx !== -1) queueIndex.value = idx;
    }
  }

  /** Jump to a specific queue index and play it. */
  function playAt(index: number) {
    if (index < 0 || index >= queue.value.length) return;
    play(queue.value[index]);
  }

  function clearQueue() {
    pause();
    queue.value = [];
    queueIndex.value = 0;
    currentTrack.value = null;
    scrobbledTrackId = null;
    preloadedTrackId = null;
    shuffleOrder = [];
  }

  function toggleShuffle() {
    isShuffled.value = !isShuffled.value;
    if (isShuffled.value) buildShuffleOrder(queueIndex.value);
    else shuffleOrder = [];
  }

  function cycleRepeatMode() {
    const modes: RepeatMode[] = ['off', 'one', 'all'];
    const idx = modes.indexOf(repeatMode.value);
    repeatMode.value = modes[(idx + 1) % modes.length];
  }

  function onTrackEnded() {
    if (repeatMode.value === 'one') {
      scrobbledTrackId = null; // count the replay
      seek(0);
      resume();
      return;
    }
    next();
  }

  function setFormatPreference(format: string, bitRate: number) {
    if (preferredFormat.value === format && preferredBitRate.value === bitRate) return;
    preferredFormat.value = format;
    preferredBitRate.value = bitRate;
    reloadCurrentStream();
  }

  /** Swap the playing track over to the newly selected format, keeping position and play state. */
  function reloadCurrentStream() {
    const track = currentTrack.value;
    const el = audio.value;
    // Radio streams (direct URLs) have no format variants; nothing loaded means nothing to swap.
    if (!track || !el || track.streamUrl || !el.currentSrc) return;

    const pos = el.currentTime;
    const wasPlaying = !el.paused;
    bufferedEnd.value = 0;
    // Swapping the source mid-play drops the buffer and has to refill it.
    isBuffering.value = wasPlaying;
    preloadedTrackId = null; // a preloaded next track would be in the old format

    el.src = streamUrlFor(track);
    if (pos > 0) {
      const applySeek = () => {
        try { el.currentTime = pos; } catch { /* stream not seekable */ }
        el.removeEventListener('loadedmetadata', applySeek);
      };
      el.addEventListener('loadedmetadata', applySeek);
    }
    if (wasPlaying) el.play().catch(() => {});
  }

  // ---- Cross-device queue persistence (savePlayQueue / getPlayQueue) ----

  let persistenceInited = false;

  /** Restore the saved queue on load, without auto-playing (browsers block that). */
  async function restoreQueue(): Promise<void> {
    if (queue.value.length > 0 || currentTrack.value) return; // don't clobber active playback
    const state = await getPlayQueue();
    if (!state || state.entry.length === 0) return;
    queue.value = [...state.entry];
    const idx = state.current ? state.entry.findIndex((t) => t.id === state.current) : 0;
    queueIndex.value = idx >= 0 ? idx : 0;
    const restored = queue.value[queueIndex.value] ?? null;
    if (!restored) return;

    // Prime the audio element paused at the saved position; playback starts on user input.
    const el = primeTrack(restored);
    const pos = (state.position ?? 0) / 1000;
    if (pos > 0) {
      const applySeek = () => {
        try {
          el.currentTime = pos;
          currentTime.value = pos;
        } catch { /* metadata not ready */ }
        el.removeEventListener('loadedmetadata', applySeek);
      };
      el.addEventListener('loadedmetadata', applySeek);
    }
  }

  /**
   * Wire up cross-device persistence: restore once on app load, then save the
   * queue (debounced) whenever its composition or the current track changes.
   * Not run inside store setup so unit tests stay side-effect free - the app
   * shell calls this once on mount.
   */
  async function initPersistence(): Promise<void> {
    if (persistenceInited) return;
    persistenceInited = true;
    try {
      await restoreQueue();
    } catch { /* server may not implement play queues / be offline */ }

    let timer: ReturnType<typeof setTimeout> | null = null;
    const save = () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        if (queue.value.length === 0) return;
        savePlayQueue({
          ids: queue.value.map((t) => t.id),
          current: currentTrack.value?.id,
          position: Math.round(currentTime.value * 1000),
        }).catch(() => { /* non-fatal */ });
      }, 1500);
    };

    watch([() => queue.value.map((t) => t.id).join('|'), currentTrackId], save);

    // Position has to be saved *during* playback too. Watching the queue and current track
    // alone meant one save ≈1.5 s after each track change and never again, so cross-device
    // resume faithfully restored a position of ≈0. timeupdate fires several times a second,
    // so this samples every POSITION_SAVE_SECONDS of movement - often enough to be useful,
    // rare enough that the 1.5 s debounce still settles.
    let lastSavedPosition = 0;
    watch(currentTime, (position) => {
      if (Math.abs(position - lastSavedPosition) < POSITION_SAVE_SECONDS) return;
      lastSavedPosition = position;
      save();
    });

    // A pause is a natural stopping point, and the last moment we reliably get before a
    // tab is closed.
    watch(isPlaying, (playing) => {
      if (!playing) save();
    });
  }

  return {
    currentTrack,
    queue,
    queueIndex,
    isPlaying,
    currentTime,
    duration,
    bufferedEnd,
    bufferedProgress,
    // `PlaybackOutput.isBusy`. For the browser, busy means the audio element
    // stalled; the remote output satisfies the same member with its in-flight
    // command count.
    isBusy: isBuffering,
    volume,
    isShuffled,
    repeatMode,
    preferredFormat,
    preferredBitRate,
    isTranscodingEnabled,
    hasTrack,
    currentTrackId,
    play,
    pause,
    resume,
    togglePlay,
    next,
    previous,
    seek,
    setVolume,
    toggleMute,
    addToQueue,
    removeFromQueue,
    moveInQueue,
    playAt,
    clearQueue,
    toggleShuffle,
    cycleRepeatMode,
    setFormatPreference,
    initPersistence,
    ensureAnalyser,
  };
});
