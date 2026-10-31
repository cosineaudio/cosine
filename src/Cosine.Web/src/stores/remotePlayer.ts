import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import type { HubConnection } from '@microsoft/signalr';
import type { Track } from '@/api/types';
import type { RepeatMode } from '@/stores/player';
import * as api from '@/api/playback';
import type {
  PlaybackDevice, PlaybackQueueSnapshot, PlaybackRepeatMode, PlaybackSessionState,
  PlaybackStateSnapshot,
} from '@/api/playback';

/**
 * The mirror of a server-owned playback session: the queue, index, shuffle and repeat all live
 * on the server (plan §4.5), so this holds a snapshot of them and sends commands. It deliberately
 * does not reimplement `player.ts` - there is no advance logic here, because the server does it.
 *
 * The three things that make a 1 Hz-polled speaker feel like local playback (plan §4.8) are all
 * here rather than in the components:
 *
 * 1. **Optimistic commands.** Every transport command moves the mirrored state immediately and
 *    reconciles from the command's own response, so no button waits for a LAN round-trip plus up
 *    to a full poll interval before the UI moves.
 * 2. **Position interpolation.** A 250 ms ticker advances `currentTime` by wall-clock delta while
 *    playing, and hub messages only pull it back when they disagree by more than a second - so
 *    the scrubber sweeps rather than stepping once a second.
 * 3. **Queue events are separate from state events**, so a queue that hasn't changed isn't
 *    re-diffed every second.
 *
 * Conformance to `PlaybackOutput` is asserted in `stores/playback.ts`, where both outputs are
 * held to the interface at once - a member that drifts here is a compile error there.
 */

/** How often the interpolated position advances between hub messages. */
const TICK_MS = 250;

/**
 * How far the server's reported position may differ from the interpolated one before we snap to
 * it. Below this the interpolation is closer to the truth than a 1 Hz sample is, and snapping
 * would only make the scrubber twitch.
 */
const RESYNC_DRIFT_SECONDS = 1;

/**
 * How long the server's position is ignored after a seek. The device needs a moment to actually
 * move, and the polls that land in between still report where it was - without this, dragging the
 * scrubber snaps back before it takes effect.
 */
const SEEK_SETTLE_MS = 2000;

const REPEAT_TO_SERVER: Record<RepeatMode, PlaybackRepeatMode> = {
  off: 'Off',
  all: 'All',
  one: 'One',
};
const REPEAT_FROM_SERVER: Record<PlaybackRepeatMode, RepeatMode> = {
  Off: 'off',
  All: 'all',
  One: 'one',
};

/** Device transcode profiles look like `"flac"` or `"mp3:320"`; null means "send it the file". */
function parseProfile(profile: string | null): { format: string; bitRate: number } {
  if (!profile) return { format: '', bitRate: 0 };
  const [format, bitRate] = profile.split(':', 2);
  return { format: format.trim().toLowerCase(), bitRate: Number(bitRate) || 0 };
}

export const useRemotePlayerStore = defineStore('remotePlayer', () => {
  // ---- Session ----

  const sessionId = ref<string | null>(null);
  const device = ref<PlaybackDevice | null>(null);
  const sessionState = ref<PlaybackSessionState>('Idle');
  const ownerUsername = ref('');

  /** True from the moment a device is picked until the session ends. Selects the active output. */
  const isActive = computed(() => sessionId.value !== null);

  const deviceName = computed(() => device.value?.displayName ?? '');

  /**
   * Set when the session stops being ours to drive - someone took the device, or it stopped
   * answering. The session is kept (it is still resumable), so the UI needs to say why the
   * buttons aren't doing anything.
   */
  const interruption = computed<'hijacked' | 'offline' | null>(() => {
    if (sessionState.value === 'Hijacked') return 'hijacked';
    if (sessionState.value === 'Offline') return 'offline';
    return null;
  });

  // ---- Mirrored state ----

  const queue = ref<Track[]>([]);
  const queueVersion = ref(-1);
  const queueIndex = ref(0);
  const currentTime = ref(0);
  const reportedDuration = ref(0);
  const volume = ref(1);
  const isShuffled = ref(false);
  const repeatMode = ref<RepeatMode>('off');
  /**
   * Whether the *session* keeps its own queue going. Mirrored rather than held locally because
   * the server owns it: it has to keep fetching with no tab open, which is the entire reason
   * remote auto-play is not the browser's job.
   */
  const autoPlay = ref(false);
  /** Track ids the session queued itself, from the queue snapshot. */
  const autoPlayIds = ref<Set<string>>(new Set());

  const isPlaying = computed(() => sessionState.value === 'Playing');
  const currentTrack = computed(() => queue.value[queueIndex.value] ?? null);
  const hasTrack = computed(() => currentTrack.value !== null);
  const currentTrackId = computed(() => currentTrack.value?.id ?? null);

  /**
   * The device's own duration report wins where it has one; renderers occasionally report zero
   * while they open a stream, and a scrubber whose length flickers to nothing is worse than one
   * showing the scanned length.
   */
  const duration = computed(() =>
    reportedDuration.value > 0 ? reportedDuration.value : (currentTrack.value?.duration ?? 0));

  /**
   * The device buffers, and no pull driver reports how much. Claiming a buffered range we cannot
   * observe would be a lie drawn on the scrubber, so the range is simply absent.
   */
  const bufferedProgress = computed(() => 0);

  /**
   * `PlaybackOutput.isBusy` - a command has been sent and the device has not answered.
   *
   * Commands are optimistic, so the buttons have already moved by the time anyone sees this; what
   * it adds is the difference between "the speaker took it" and "we are still asking". On a healthy
   * LAN that is a few tens of milliseconds and the UI's delay gate means it is never drawn at all.
   * It shows up exactly when it should: when a speaker has gone slow or flaky.
   */
  const isBusy = computed(() => inFlight.value > 0);

  // The stream format is a property of the device (its TranscodeProfile), not of this browser:
  // a speaker fetches its own bytes, so the listener's local preference has nothing to do with
  // it. Setting it is per-device and lives in admin (plan §4.7).
  const profile = computed(() => parseProfile(device.value?.transcodeProfile ?? null));
  const preferredFormat = computed(() => profile.value.format);
  const preferredBitRate = computed(() => profile.value.bitRate);
  const isTranscodingEnabled = computed(() => profile.value.format !== '');

  // ---- Command plumbing ----

  /**
   * Number of commands awaiting their response. Hub messages are ignored while one is in flight:
   * they were sampled before the command landed, and the command's own response is both newer and
   * authoritative, so applying them would visibly undo the button that was just pressed.
   *
   * A ref rather than a plain counter because the transport reads it too: a speaker command is a
   * network round trip, so pressing play used to sit visibly dead until the response came back.
   */
  const inFlight = ref(0);

  /** Wall-clock instant until which the server's position is ignored. See SEEK_SETTLE_MS. */
  let positionHoldUntil = 0;

  /** Last error, for the UI to surface. Cleared by the next successful command. */
  const error = ref<string | null>(null);

  function send(call: (id: string) => Promise<PlaybackStateSnapshot>): void {
    const id = sessionId.value;
    if (!id) return;

    inFlight.value++;
    call(id)
      .then((snapshot) => {
        error.value = null;
        applyState(snapshot, true);
      })
      .catch(async (err: unknown) => {
        error.value = err instanceof Error ? err.message : 'The device did not accept that';
        // The optimistic update was a guess and the guess was wrong; go and find out the truth
        // rather than leaving the UI showing something that never happened.
        await refresh();
      })
      // Floored, so a response that lands after the session was torn down cannot leave the
      // counter negative and quietly disable the guard for the next session.
      .finally(() => { inFlight.value = Math.max(0, inFlight.value - 1); });
  }

  /** Re-read state and queue from the server, e.g. after a failed command or a reconnect. */
  async function refresh(): Promise<void> {
    const id = sessionId.value;
    if (!id) return;
    try {
      applyState(await api.getSession(id), true);
      applyQueue(await api.getSessionQueue(id));
    } catch {
      // The session is gone (ended elsewhere, or the server restarted). Fall back to the browser.
      reset();
    }
  }

  // ---- Reconciliation ----

  function applyState(s: PlaybackStateSnapshot, authoritative = false): void {
    if (s.sessionId !== sessionId.value) return;
    if (!authoritative && inFlight.value > 0) return;

    // Keyed on the track rather than the index: reordering the queue moves the index without
    // changing what is playing, and yanking the scrubber back to zero for a drag-and-drop would
    // be wrong.
    const trackChanged = s.currentTrackId !== currentTrackId.value;

    sessionState.value = s.state;
    queueIndex.value = s.queueIndex;
    ownerUsername.value = s.ownerUsername;
    reportedDuration.value = s.durationMs != null ? s.durationMs / 1000 : 0;
    volume.value = s.volumePercent / 100;
    isShuffled.value = s.shuffle;
    repeatMode.value = REPEAT_FROM_SERVER[s.repeatMode] ?? 'off';
    autoPlay.value = s.autoPlay;

    const position = s.positionMs / 1000;
    // A new track always resets the scrubber: interpolation from the previous track's position
    // is meaningless, and "drift" is the wrong question to ask about it.
    if (trackChanged) {
      currentTime.value = position;
      positionHoldUntil = 0;
    } else if (authoritative || Date.now() >= positionHoldUntil) {
      if (Math.abs(position - currentTime.value) > RESYNC_DRIFT_SECONDS) currentTime.value = position;
    }
    lastTickAt = Date.now();

    // The queue moved and we haven't seen the event for it yet - ask, rather than render a list
    // that no longer matches the index pointing into it.
    if (s.queueVersion !== queueVersion.value) void fetchQueue(s.queueVersion);
  }

  function applyQueue(q: PlaybackQueueSnapshot): void {
    if (q.sessionId !== sessionId.value) return;
    // Out-of-order delivery: a fetch answering an older version must not overwrite a newer event.
    if (q.queueVersion < queueVersion.value) return;
    queue.value = q.items.map(api.queueItemToTrack);
    autoPlayIds.value = new Set(q.items.filter((i) => i.autoPlay).map((i) => i.trackId));
    queueVersion.value = q.queueVersion;
  }

  /** In-flight queue fetch, so a burst of state messages asks once rather than once per message. */
  let fetchingQueueVersion: number | null = null;

  async function fetchQueue(version: number): Promise<void> {
    const id = sessionId.value;
    if (!id || fetchingQueueVersion === version) return;
    fetchingQueueVersion = version;
    try {
      applyQueue(await api.getSessionQueue(id));
    } catch {
      /* a state message or the hub's own queue event will bring it round */
    } finally {
      if (fetchingQueueVersion === version) fetchingQueueVersion = null;
    }
  }

  // ---- Position interpolation ----

  let ticker: ReturnType<typeof setInterval> | null = null;
  let lastTickAt = Date.now();

  function startTicker(): void {
    if (ticker) return;
    lastTickAt = Date.now();
    ticker = setInterval(() => {
      const now = Date.now();
      const elapsed = (now - lastTickAt) / 1000;
      lastTickAt = now;
      if (!isPlaying.value) return;
      const total = duration.value;
      const next = currentTime.value + elapsed;
      currentTime.value = total > 0 ? Math.min(next, total) : next;
    }, TICK_MS);
  }

  function stopTicker(): void {
    if (!ticker) return;
    clearInterval(ticker);
    ticker = null;
  }

  // ---- Hub ----

  let hub: HubConnection | null = null;

  async function connectHub(id: string): Promise<void> {
    // Imported on demand rather than at the top of the file. This store is reachable from the app
    // shell, and the SignalR client is a sizeable dependency that until now only admin pages
    // pulled in - nobody who never picks a speaker should pay for it on every page load (§1.3).
    const { createPlaybackHubConnection } = await import('@/composables/useSignalR');
    const connection = createPlaybackHubConnection();
    hub = connection;

    connection.on('PlaybackState', (s: PlaybackStateSnapshot) => applyState(s));
    connection.on('QueueChanged', (q: PlaybackQueueSnapshot) => applyQueue(q));
    connection.on('PlaybackSessionEnded', (endedId: string) => {
      if (endedId === sessionId.value) reset();
    });
    // A reconnect rejoins a group the server has forgotten us from, and we missed everything that
    // happened while we were away.
    connection.onreconnected(async () => {
      if (sessionId.value !== id) return;
      try {
        await connection.invoke('Subscribe', id);
        await refresh();
      } catch { /* the next command's response will resync */ }
    });

    await connection.start();
    await connection.invoke('Subscribe', id);
  }

  async function disconnectHub(): Promise<void> {
    const connection = hub;
    hub = null;
    if (!connection) return;
    try {
      await connection.stop();
    } catch { /* already gone */ }
  }

  // ---- Session lifecycle ----

  /**
   * Claim a device and start mirroring it. The hub connection and the interpolation ticker live
   * exactly as long as the session does.
   *
   * @param takeOver Ends another user's session. Admins only; anyone else gets a clear error.
   */
  async function activate(target: PlaybackDevice, takeOver = false): Promise<void> {
    if (sessionId.value) await deactivate();

    const snapshot = await api.startSession(target.id, takeOver);
    device.value = target;
    sessionId.value = snapshot.sessionId;
    queue.value = [];
    queueVersion.value = -1;
    error.value = null;
    applyState(snapshot, true);

    startTicker();
    try {
      await connectHub(snapshot.sessionId);
    } catch {
      // Polling the REST endpoint is not worth building as a fallback: without the hub the UI is
      // still correct after every command, just not while the *device* changes something.
      error.value = 'Live updates are unavailable; state will refresh as you use the controls';
    }
  }

  /**
   * Take over a queue from another output - the browser handing its queue to a speaker. Unlike
   * the transport commands this awaits each step in order: `setQueue`, `seek` and `pause` are
   * three separate requests, and a seek that overtook the queue it was meant to seek within would
   * land on whatever the device was playing before.
   */
  async function adopt(
    tracks: Track[], index: number, positionSeconds: number, playing: boolean,
  ): Promise<void> {
    const id = sessionId.value;
    if (!id || tracks.length === 0) return;

    const startIndex = Math.max(0, Math.min(index, tracks.length - 1));
    queue.value = [...tracks];
    queueIndex.value = startIndex;
    currentTime.value = positionSeconds;
    sessionState.value = playing ? 'Playing' : 'Paused';

    inFlight.value++;
    try {
      applyState(await api.setQueue(id, tracks.map((t) => t.id), startIndex), true);
      if (positionSeconds > 1) {
        positionHoldUntil = Date.now() + SEEK_SETTLE_MS;
        applyState(await api.seek(id, positionSeconds), true);
      }
      if (!playing) applyState(await api.pause(id), true);
      error.value = null;
    } catch (err: unknown) {
      error.value = err instanceof Error ? err.message : 'The device would not take the queue';
      await refresh();
    } finally {
      inFlight.value = Math.max(0, inFlight.value - 1);
    }
  }

  /** End the session, stop the device, and hand the output back to the browser. */
  async function deactivate(): Promise<void> {
    const id = sessionId.value;
    reset();
    if (!id) return;
    try {
      await api.endSession(id);
    } catch { /* already ended, or the server is gone; the local output takes over either way */ }
  }

  /** Drop every trace of a session without touching the server. */
  function reset(): void {
    void disconnectHub();
    stopTicker();
    sessionId.value = null;
    device.value = null;
    sessionState.value = 'Idle';
    queue.value = [];
    queueVersion.value = -1;
    queueIndex.value = 0;
    currentTime.value = 0;
    reportedDuration.value = 0;
    autoPlay.value = false;
    autoPlayIds.value = new Set();
    inFlight.value = 0;
    positionHoldUntil = 0;
  }

  // ---- Commands (PlaybackOutput) ----
  //
  // Each one moves the mirrored state first and sends second. The response reconciles it; a
  // failure resyncs from the server (see `send`).

  /**
   * Internet radio is a direct cross-origin URL with no track row behind it, so there is nothing
   * to hand a speaker: the queue the server holds is a list of library track ids. Refusing with a
   * reason beats sending an id the server will not find.
   */
  function rejectRadio(tracks: Track[]): boolean {
    if (!tracks.some((t) => t.streamUrl)) return false;
    error.value = `Internet radio can only play in this browser, not on ${deviceName.value}`;
    return true;
  }

  function play(track: Track, newQueue?: Track[]): void {
    if (rejectRadio(newQueue ?? [track])) return;
    const list = newQueue ?? queue.value;
    const index = Math.max(0, list.findIndex((t) => t.id === track.id));

    if (newQueue) {
      queue.value = [...newQueue];
      queueIndex.value = index;
      currentTime.value = 0;
      sessionState.value = 'Playing';
      send((id) => api.setQueue(id, newQueue.map((t) => t.id), index));
      return;
    }

    playAt(index);
  }

  function playAt(index: number): void {
    if (index < 0 || index >= queue.value.length) return;
    queueIndex.value = index;
    currentTime.value = 0;
    sessionState.value = 'Playing';
    send((id) => api.playAt(id, index));
  }

  function pause(): void {
    sessionState.value = 'Paused';
    send(api.pause);
  }

  function resume(): void {
    sessionState.value = 'Playing';
    send(api.play);
  }

  function togglePlay(): void {
    if (isPlaying.value) pause();
    else resume();
  }

  function next(): void {
    // Which track comes next is the server's decision (shuffle lives there), so unlike the other
    // commands this one has nothing to predict beyond restarting the scrubber.
    currentTime.value = 0;
    send(api.next);
  }

  function previous(): void {
    currentTime.value = 0;
    send(api.previous);
  }

  function seek(seconds: number): void {
    const clamped = Math.max(0, duration.value > 0 ? Math.min(seconds, duration.value) : seconds);
    currentTime.value = clamped;
    positionHoldUntil = Date.now() + SEEK_SETTLE_MS;
    send((id) => api.seek(id, clamped));
  }

  function setVolume(level: number): void {
    const clamped = Math.max(0, Math.min(1, level));
    volume.value = clamped;
    send((id) => api.setVolume(id, Math.round(clamped * 100)));
  }

  /** Volume before the last mute, so unmuting restores it - as the local output does. */
  const volumeBeforeMute = ref(1);

  function toggleMute(): void {
    if (volume.value === 0) {
      setVolume(volumeBeforeMute.value > 0 ? volumeBeforeMute.value : 1);
    } else {
      volumeBeforeMute.value = volume.value;
      setVolume(0);
    }
  }

  function addToQueue(tracks: Track[], position: 'next' | 'end' = 'end'): void {
    if (tracks.length === 0 || rejectRadio(tracks)) return;
    const insertAt = position === 'next' ? queueIndex.value + 1 : null;
    const ids = tracks.map((t) => t.id);

    if (insertAt === null) queue.value = [...queue.value, ...tracks];
    else queue.value = [...queue.value.slice(0, insertAt), ...tracks, ...queue.value.slice(insertAt)];

    send((id) => api.addToQueue(id, ids, insertAt));
  }

  function removeFromQueue(index: number): void {
    if (index < 0 || index >= queue.value.length) return;
    queue.value = queue.value.filter((_, i) => i !== index);
    if (index < queueIndex.value) queueIndex.value--;
    send((id) => api.removeFromQueue(id, index));
  }

  function moveInQueue(from: number, to: number): void {
    const len = queue.value.length;
    if (from < 0 || from >= len || to < 0 || to >= len || from === to) return;
    const currentId = currentTrackId.value;
    const next = [...queue.value];
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    queue.value = next;
    if (currentId) {
      const at = next.findIndex((t) => t.id === currentId);
      if (at !== -1) queueIndex.value = at;
    }
    send((id) => api.moveInQueue(id, from, to));
  }

  function clearQueue(): void {
    queue.value = [];
    queueIndex.value = 0;
    currentTime.value = 0;
    sessionState.value = 'Stopped';
    send((id) => api.setQueue(id, [], 0));
  }

  function toggleShuffle(): void {
    const shuffled = !isShuffled.value;
    isShuffled.value = shuffled;
    send((id) => api.setShuffle(id, shuffled));
  }

  function cycleRepeatMode(): void {
    const modes: RepeatMode[] = ['off', 'one', 'all'];
    const mode = modes[(modes.indexOf(repeatMode.value) + 1) % modes.length];
    repeatMode.value = mode;
    send((id) => api.setRepeat(id, REPEAT_TO_SERVER[mode]));
  }

  function setAutoPlay(enabled: boolean): void {
    autoPlay.value = enabled;
    send((id) => api.setAutoPlay(id, enabled));
  }

  function isAutoPlayTrack(trackId: string): boolean {
    return autoPlayIds.value.has(trackId);
  }

  /**
   * No-op by design. The stream format for a remote session is the device's `TranscodeProfile`,
   * set per device in admin, because the speaker fetches its own bytes - this browser's
   * preference has nothing to do with what a speaker on the other side of the house is sent.
   * The menu that would call this is rendered inert while remote (`NowPlayingBar.vue`).
   */
  function setFormatPreference(_format: string, _bitRate: number): void {
    /* the device decides; see the doc comment */
  }

  const store = {
    // session
    isActive,
    sessionId,
    device,
    deviceName,
    sessionState,
    ownerUsername,
    interruption,
    error,
    activate,
    adopt,
    deactivate,
    refresh,

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

    // auto-play (session-owned; not part of PlaybackOutput - the browser's own is a store)
    autoPlay,
    setAutoPlay,
    isAutoPlayTrack,

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
  };

  return store;
});
