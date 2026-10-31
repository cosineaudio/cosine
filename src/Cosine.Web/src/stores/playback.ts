import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import type { Track } from '@/api/types';
import type { PlaybackDevice } from '@/api/playback';
import { getPlaybackDevices } from '@/api/playback';
import type { PlaybackOutput } from '@/stores/playbackOutput';
import { usePlayerStore } from '@/stores/player';
import { useRemotePlayerStore } from '@/stores/remotePlayer';
import { useAutoPlayStore, type QueueSource } from '@/stores/autoPlay';
import { useAuthStore } from '@/stores/auth';

/**
 * The only playback store the UI talks to (plan §4.7). It is a *selector* over two
 * `PlaybackOutput` implementations - the browser and a speaker on the LAN - not a bag of
 * hand-copied fields: its members are exactly the interface's, and the compiler holds both
 * implementations to that.
 *
 * `stores/player.ts` is not edited by any of this. Local playback cannot regress, because its
 * code is not touched - not "carefully refactored": not touched. Remote is purely additive: with
 * no device selected, every call below lands on the same store method it always did.
 */

/**
 * Type-level conformance assertion (plan §4.7). A member that drifts or goes missing on either
 * store is a compile error here rather than a component silently reading `undefined`.
 */
function asOutput<T extends PlaybackOutput>(store: T): T {
  return store;
}

/** How long to keep retrying the local element after handing a queue back to the browser. */
const LOCAL_HANDOFF_TIMEOUT_MS = 8000;

export const usePlaybackStore = defineStore('playback', () => {
  const local = asOutput(usePlayerStore());
  const remote = asOutput(useRemotePlayerStore());
  const autoPlay = useAutoPlayStore();

  /** Whichever output is currently playing. Everything below reads or commands through it. */
  const active = computed<PlaybackOutput>(() => (remote.isActive ? remote : local));

  // ---- State: read straight through ----

  const currentTrack = computed(() => active.value.currentTrack);
  const hasTrack = computed(() => active.value.hasTrack);
  const currentTrackId = computed(() => active.value.currentTrackId);
  const queue = computed(() => active.value.queue);
  const queueIndex = computed(() => active.value.queueIndex);
  const isPlaying = computed(() => active.value.isPlaying);
  const currentTime = computed(() => active.value.currentTime);
  const duration = computed(() => active.value.duration);
  const bufferedProgress = computed(() => active.value.bufferedProgress);
  const isBusy = computed(() => active.value.isBusy);
  const volume = computed(() => active.value.volume);
  const isShuffled = computed(() => active.value.isShuffled);
  const repeatMode = computed(() => active.value.repeatMode);
  const preferredFormat = computed(() => active.value.preferredFormat);
  const preferredBitRate = computed(() => active.value.preferredBitRate);
  const isTranscodingEnabled = computed(() => active.value.isTranscodingEnabled);

  // ---- Commands: forward ----

  /**
   * @param source Where `newQueue` came from, when the page holds only part of it - see
   * `QueueSource`. Auto-play reads on through it before blending anything in. Left off, a new
   * queue has nothing behind it and the blend is all there is. Ignored without a new queue:
   * playing a track already in the queue does not change where the queue came from.
   */
  const play = (track: Track, newQueue?: Track[], source: QueueSource | null = null) => {
    if (newQueue) autoPlay.setQueueSource(source);
    active.value.play(track, newQueue);
  };
  const pause = () => active.value.pause();
  const resume = () => active.value.resume();
  const togglePlay = () => active.value.togglePlay();
  const next = () => active.value.next();
  const previous = () => active.value.previous();
  const seek = (seconds: number) => active.value.seek(seconds);
  const setVolume = (level: number) => active.value.setVolume(level);
  const toggleMute = () => active.value.toggleMute();
  const addToQueue = (tracks: Track[], position: 'next' | 'end' = 'end') =>
    active.value.addToQueue(tracks, position);
  const removeFromQueue = (index: number) => active.value.removeFromQueue(index);
  const moveInQueue = (from: number, to: number) => active.value.moveInQueue(from, to);
  const playAt = (index: number) => active.value.playAt(index);
  const clearQueue = () => {
    autoPlay.setQueueSource(null);
    active.value.clearQueue();
  };
  const toggleShuffle = () => active.value.toggleShuffle();
  const cycleRepeatMode = () => active.value.cycleRepeatMode();
  const setFormatPreference = (format: string, bitRate: number) =>
    active.value.setFormatPreference(format, bitRate);

  // ---- Local-only surface ----
  //
  // Neither of these belongs to an output. The analyser taps the browser's own audio element, and
  // queue persistence is this browser's cross-device resume - it runs whichever output is
  // selected, and follows the local queue because that is the queue it restores into.

  const ensureAnalyser = () => local.ensureAnalyser();
  const initPersistence = () => local.initPersistence();

  // ---- Auto-play ----
  //
  // Not a `PlaybackOutput` member, because the two outputs do not implement the same thing. The
  // browser's version is a store above `player.ts`; a speaker's is a flag on the server-owned
  // session, so it keeps fetching after this tab is closed. What they share is this switch.

  const autoPlayEnabled = computed(() => (remote.isActive ? remote.autoPlay : autoPlay.enabled));

  function setAutoPlay(enabled: boolean): void {
    if (remote.isActive) remote.setAutoPlay(enabled);
    else autoPlay.setEnabled(enabled);
  }

  function toggleAutoPlay(): void {
    setAutoPlay(!autoPlayEnabled.value);
  }

  /** Whether a queue entry was put there by auto-play rather than by the listener. */
  const isAutoPlayTrack = (trackId: string): boolean =>
    (remote.isActive ? remote.isAutoPlayTrack(trackId) : autoPlay.isAutoPlayTrack(trackId));

  /** Only the browser's own auto-play reports a failure; a session's happens out of sight. */
  const autoPlayError = computed(() => (remote.isActive ? null : autoPlay.error));

  // ---- Remote surface ----

  const isRemote = computed(() => remote.isActive);
  /** Gates the visualizer: there is no local element to tap while a speaker is playing. */
  const canAnalyse = computed(() => !remote.isActive);
  const outputName = computed(() => (remote.isActive ? remote.deviceName : 'This browser'));
  /** Id of the device currently playing, or null when the browser is the output. */
  const outputDeviceId = computed(() => (remote.isActive ? remote.device?.id ?? null : null));
  /**
   * Milliseconds to shift synced lyrics by, from the active device's pinned setting. Zero for the
   * browser: the audio element's `currentTime` *is* the playhead, so there is nothing to correct.
   */
  const lyricOffsetMs = computed(() =>
    (remote.isActive ? remote.device?.lyricOffsetMs ?? 0 : 0));
  /** Non-null when a remote session has been taken over or has gone quiet. */
  const remoteInterruption = computed(() => remote.interruption);
  const remoteError = computed(() => remote.error);

  // ---- Device picker ----

  const devices = ref<PlaybackDevice[]>([]);
  const devicesLoading = ref(false);
  const devicesError = ref<string | null>(null);

  /**
   * Refresh the device list. The picker renders whatever it already holds and calls this in the
   * background: SSDP's `M-SEARCH` has a multi-second wait built into the protocol, and a popover
   * that blocks on it is a popover that feels broken (plan §4.8 item 4).
   */
  async function loadDevices(): Promise<void> {
    if (devicesLoading.value) return;
    devicesLoading.value = true;
    try {
      devices.value = await getPlaybackDevices();
      devicesError.value = null;
    } catch (err: unknown) {
      devicesError.value = err instanceof Error ? err.message : 'Could not list devices';
    } finally {
      devicesLoading.value = false;
    }
  }

  // ---- Switching output ----

  /**
   * Reattach to a session this user already has running, so closing the tab and reopening it
   * shows live state instead of an idle browser while the speaker plays on. This is the whole
   * point of a session living on the server rather than in a tab.
   *
   * Claiming a device you already own is a no-op on the server - `StartSessionAsync` hands back
   * the existing session - so the reattach cannot disturb what is playing.
   */
  async function restoreSession(): Promise<void> {
    if (remote.isActive) return;
    await loadDevices();

    const auth = useAuthStore();
    const username = auth.user?.username;
    if (!username) return;

    // The device list carries the holder's name rather than their id, and usernames are unique.
    const mine = devices.value.find(
      (d) => d.activeSessionId !== null && d.activeSessionOwner === username);
    if (!mine) return;

    try {
      await remote.activate(mine);
    } catch {
      // It ended between listing and claiming. The browser is the output; nothing to report.
    }
  }

  /**
   * Move playback to a speaker, taking the queue and position with it. The browser is paused
   * rather than cleared, so switching back is a handover rather than a restart.
   *
   * @param takeOver Claim a device another user is holding. Admins only.
   */
  async function selectDevice(device: PlaybackDevice, takeOver = false): Promise<void> {
    if (remote.isActive && remote.device?.id === device.id) return;

    const tracks = [...local.queue];
    const index = local.queueIndex;
    const position = local.currentTime;
    const wasPlaying = local.isPlaying;

    if (local.isPlaying) local.pause();

    await remote.activate(device, takeOver);
    if (tracks.length > 0) await remote.adopt(tracks, index, position, wasPlaying);
    // The queue moved to the speaker; endless play is part of the queue as far as the listener is
    // concerned, so it moves with it rather than switching itself off at the handover.
    if (autoPlay.enabled && !remote.autoPlay) remote.setAutoPlay(true);
    void loadDevices();
  }

  /**
   * Bring playback back into the browser, carrying the remote queue and position across, then end
   * the session so the speaker is free.
   *
   * The handover is audible for a moment: `player.ts` seeks by assigning to the audio element,
   * which the element ignores until it has metadata, so playback starts at zero and jumps to the
   * position once the first `timeupdate` proves it is live. Fixing that properly means splitting
   * `player.ts` into a queue model and a transport, which is plan §9's accepted debt, deliberately
   * not folded into this phase.
   */
  async function selectLocal(): Promise<void> {
    if (!remote.isActive) return;

    const tracks = [...remote.queue];
    const index = remote.queueIndex;
    const position = remote.currentTime;
    const wasPlaying = remote.isPlaying;
    const wasAutoPlaying = remote.autoPlay;

    await remote.deactivate();

    // …and it comes back with the queue, so a session that was playing endlessly does not quietly
    // stop doing so on the way into the browser.
    if (wasAutoPlaying) autoPlay.setEnabled(true);

    const resume = tracks[index] ?? tracks[0];
    if (!resume) return;

    local.play(resume, tracks);
    whenLocalElementLive(() => {
      if (position > 1) local.seek(position);
      if (!wasPlaying) local.pause();
    });
    void loadDevices();
  }

  /**
   * Run `action` once the local audio element is actually playing. A `timeupdate` having moved the
   * position is the observable proof that the element has metadata and will accept a seek; before
   * that it silently ignores one.
   */
  function whenLocalElementLive(action: () => void): void {
    const startedAt = Date.now();
    const timer = setInterval(() => {
      const live = local.currentTime > 0;
      if (!live && Date.now() - startedAt < LOCAL_HANDOFF_TIMEOUT_MS) return;
      clearInterval(timer);
      if (live) action();
    }, 100);
  }

  return {
    // PlaybackOutput state
    currentTrack,
    hasTrack,
    currentTrackId,
    queue,
    queueIndex,
    isPlaying,
    currentTime,
    duration,
    bufferedProgress,
    isBusy,
    volume,
    isShuffled,
    repeatMode,
    preferredFormat,
    preferredBitRate,
    isTranscodingEnabled,

    // PlaybackOutput commands
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

    // local-only
    ensureAnalyser,
    initPersistence,

    // auto-play
    autoPlayEnabled,
    autoPlayError,
    setAutoPlay,
    toggleAutoPlay,
    isAutoPlayTrack,

    // remote
    isRemote,
    canAnalyse,
    outputName,
    outputDeviceId,
    lyricOffsetMs,
    remoteInterruption,
    remoteError,
    devices,
    devicesLoading,
    devicesError,
    loadDevices,
    restoreSession,
    selectDevice,
    selectLocal,
  };
});
