import { ref, watch, onUnmounted } from 'vue';
import * as signalR from '@microsoft/signalr';
import { useAuthStore } from '@/stores/auth';

interface ScanProgress {
  scanLogId: string;
  phase: string;
  current: number;
  total: number;
  currentFile?: string;
}

/**
 * Progress of a content-based audio analysis pass. Null when none is running.
 *
 * Pushed a couple of times a second for as long as a pass runs, and it covers both halves of
 * one - `analysing` decodes and extracts, `normalizing` z-scores the result against the library
 * afterwards. The second half has no per-track count for most of its duration, which is why
 * `step` exists and why `total` may be 0 while work is very much still happening.
 */
interface AudioAnalysisProgress {
  phase: 'analysing' | 'normalizing';
  step: string;
  processed: number;
  total: number;
  failed: number;
  tracksPerMinute: number;
  etaSeconds: number | null;
  currentTrack: string | null;
}

interface NowPlayingEvent {
  userId: string;
  username: string;
  trackId: string;
}

/**
 * Coerces a hub payload into a usable progress object, or null if it is not one at all.
 *
 * A hub payload is untrusted input in the one way that matters: the server it came from may be
 * a version this bundle has never seen. Reading a field straight off it and handing the result
 * to `toLocaleString()` in a template turns any mismatch into a TypeError during render, which
 * takes down the whole page rather than one stale number - which is exactly what a previous
 * change to this event's shape did. Numbers default, they never arrive undefined.
 */
export function toAudioAnalysisProgress(payload: unknown): AudioAnalysisProgress | null {
  if (!payload || typeof payload !== 'object') return null;

  const raw = payload as Record<string, unknown>;
  const count = (value: unknown) =>
    typeof value === 'number' && Number.isFinite(value) ? value : 0;

  return {
    phase: raw.phase === 'normalizing' ? 'normalizing' : 'analysing',
    step: typeof raw.step === 'string' && raw.step ? raw.step : 'working',
    processed: count(raw.processed),
    total: count(raw.total),
    failed: count(raw.failed),
    tracksPerMinute: count(raw.tracksPerMinute),
    etaSeconds: typeof raw.etaSeconds === 'number' && Number.isFinite(raw.etaSeconds)
      ? raw.etaSeconds
      : null,
    currentTrack: typeof raw.currentTrack === 'string' ? raw.currentTrack : null,
  };
}

/** Shared reconnect back-off: immediate, then easing out to every 30 s. */
const RECONNECT_DELAYS = [0, 1000, 2000, 4000, 8000, 15000, 30000];

/**
 * The playback hub is built here, with the same URL/reconnect setup as the hubs above, but its
 * connection is *not* owned by this composable: a remote session outlives any component, so
 * `stores/remotePlayer.ts` starts and stops it alongside the session it is watching. Everything
 * else about it - transport, retry policy, auth by cookie - is the established pattern.
 */
export function createPlaybackHubConnection(): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/playback')
    .withAutomaticReconnect(RECONNECT_DELAYS)
    .build();
}

/**
 * The listen-together hub, same setup and ownership rules as the playback hub: the party
 * stores own start/stop, because a party outlives any component. Unlike every other hub this
 * one is also used by *anonymous* guests - the server admits them by guest ticket passed to
 * its Subscribe method, not by cookie.
 */
export function createPartyHubConnection(): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/party')
    .withAutomaticReconnect(RECONNECT_DELAYS)
    .build();
}

export function useSignalR() {
  const scanProgress = ref<ScanProgress | null>(null);
  const audioAnalysisProgress = ref<AudioAnalysisProgress | null>(null);
  /** Bumped when an analysis pass ends, so watchers can refresh coverage once. */
  const audioAnalysisCompleted = ref(0);
  const nowPlaying = ref<NowPlayingEvent[]>([]);
  const libraryChanged = ref(0);

  let scanConnection: signalR.HubConnection | null = null;
  let activityConnection: signalR.HubConnection | null = null;

  const authStore = useAuthStore();

  async function connect() {
    if (!authStore.isAuthenticated) return;

    // Scan hub. Scan lifecycle events (Started/Progress/Completed/Error) are admin
    // telemetry and are only sent to admin connections; every client gets LibraryChanged.
    scanConnection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/scan')
      .withAutomaticReconnect(RECONNECT_DELAYS)
      .build();

    scanConnection.on('ScanStarted', (scanLogId: string) => {
      scanProgress.value = { scanLogId, phase: 'starting', current: 0, total: 0 };
    });

    scanConnection.on('ScanProgress', (scanLogId: string, phase: string, current: number, total: number, currentFile: string) => {
      scanProgress.value = { scanLogId, phase, current, total, currentFile };
    });

    scanConnection.on('ScanCompleted', (_scanLogId: string) => {
      scanProgress.value = null;
    });

    // Broadcast to every connection when a scan finishes, so non-admins also refresh.
    scanConnection.on('LibraryChanged', () => {
      libraryChanged.value++;
    });

    // Audio analysis rides the same admin group as scan telemetry - it is queued by the scan and
    // reported the same way, so it needs no polling to stay current.
    scanConnection.on('AudioAnalysisStarted', (total: number) => {
      audioAnalysisProgress.value = {
        phase: 'analysing', step: 'starting', processed: 0, total,
        failed: 0, tracksPerMinute: 0, etaSeconds: null, currentTrack: null,
      };
    });

    // Every push carries the whole state, so a client that joined mid-pass - or missed a frame -
    // is correct again on the next one rather than having to reconstruct anything.
    //
    // 'AudioAnalysisState', not 'AudioAnalysisProgress': this payload is one object where the
    // older event was two positional integers, and reusing the name meant an unreloaded tab
    // bound the object to `processed` and undefined to `total`. See ScanHub.AudioAnalysisState.
    scanConnection.on('AudioAnalysisState', (payload: unknown) => {
      const update = toAudioAnalysisProgress(payload);
      if (update) audioAnalysisProgress.value = update;
    });

    // Only ever sent once both analysis and normalization are done; see AudioAnalysisJob.
    scanConnection.on('AudioAnalysisCompleted', (_analyzed: number, _failed: number) => {
      audioAnalysisProgress.value = null;
      audioAnalysisCompleted.value++;
    });

    scanConnection.on('ScanError', (_scanLogId: string, message: string) => {
      console.error('Scan error:', message);
      scanProgress.value = null;
    });

    // Activity hub
    activityConnection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/activity')
      .withAutomaticReconnect(RECONNECT_DELAYS)
      .build();

    activityConnection.on('NowPlaying', (userId: string, username: string, trackId: string) => {
      nowPlaying.value.push({ userId, username, trackId });
      if (nowPlaying.value.length > 10) nowPlaying.value.shift();
    });

    activityConnection.on('LibraryChanged', () => {
      libraryChanged.value++;
    });

    try {
      await scanConnection.start();
      await activityConnection.start();
    } catch (err) {
      console.error('SignalR connection failed:', err);
    }
  }

  async function disconnect() {
    if (scanConnection) await scanConnection.stop();
    if (activityConnection) await activityConnection.stop();
    scanConnection = null;
    activityConnection = null;
  }

  watch(() => authStore.isAuthenticated, (authenticated) => {
    if (authenticated) connect();
    else disconnect();
  }, { immediate: true });

  onUnmounted(() => {
    disconnect();
  });

  return {
    scanProgress,
    audioAnalysisProgress,
    audioAnalysisCompleted,
    nowPlaying,
    libraryChanged,
  };
}
