import type {
  PlaybackDevice, PlaybackQueueItem, PlaybackQueueSnapshot,
  PlaybackRepeatMode, PlaybackSessionState, PlaybackStateSnapshot,
} from '@/api/playback';

/**
 * An in-memory stand-in for the `/playback` endpoints: enough of `PlaybackSessionManager` to
 * drive `stores/remotePlayer.ts` end to end without a server.
 *
 * It is a *server*, not a pile of stubbed return values, because what is being tested is a
 * mirror - optimistic state has to be reconciled against something that answers coherently, and
 * a stub that returns whatever the assertion wants would prove nothing about the round trip.
 * Only the sequential queue rules are modelled; shuffle is a flag here, since choosing the next
 * track under shuffle is the server's business and is tested in `PlaybackQueueModelTests`.
 */

export const FAKE_DEVICE: PlaybackDevice = {
  id: 'device-1',
  driverId: 'loopback',
  deviceKey: 'fake-speaker',
  displayName: 'Kitchen',
  advertisedName: 'Kitchen',
  online: true,
  address: '192.168.1.50',
  transcodeProfile: null,
  maxVolumePercent: 100,
  lyricOffsetMs: 0,
  enabled: true,
  lastSeenAt: null,
  activeSessionId: null,
  activeSessionOwner: null,
};

const SESSION_ID = 'session-1';
/** Who holds the session, as the device list and the state snapshot both report it. */
export const SESSION_OWNER = 'tester';
/** Every fake track is this long, so half-way thresholds and seeks have a scale to work against. */
const TRACK_MS = 180_000;

class FakePlaybackServer {
  live = false;
  state: PlaybackSessionState = 'Idle';
  tracks: string[] = [];
  index = 0;
  positionMs = 0;
  volumePercent = 100;
  repeatMode: PlaybackRepeatMode = 'Off';
  shuffle = false;
  autoPlay = false;
  /** Ids the session queued itself, as the real one reports them on the queue snapshot. */
  autoPlayed = new Set<string>();
  queueVersion = 0;

  /** Every request that has been made, so tests can assert what the store actually sent. */
  calls: string[] = [];

  /**
   * When set, responses are withheld until {@link flush}. This is how "the UI moved before the
   * server answered" is asserted rather than assumed.
   */
  deferred: (() => void)[] | null = null;

  /** Name of the next request to reject, for the "the optimistic guess was wrong" path. */
  failNext: string | null = null;

  reset(): void {
    this.live = false;
    this.state = 'Idle';
    this.tracks = [];
    this.index = 0;
    this.positionMs = 0;
    this.volumePercent = 100;
    this.repeatMode = 'Off';
    this.shuffle = false;
    this.autoPlay = false;
    this.autoPlayed = new Set();
    this.queueVersion = 0;
    this.calls = [];
    this.deferred = null;
    this.failNext = null;
  }

  /** Hold responses until `flush()`. */
  defer(): void {
    this.deferred = [];
  }

  /** Release every held response. */
  flush(): void {
    const held = this.deferred ?? [];
    this.deferred = null;
    for (const release of held) release();
  }

  respond<T>(name: string, produce: () => T): Promise<T> {
    this.calls.push(name);
    if (this.failNext === name) {
      this.failNext = null;
      return Promise.reject(new Error(`${name} refused`));
    }
    if (!this.deferred) return Promise.resolve(produce());
    return new Promise<T>((resolve) => {
      this.deferred!.push(() => resolve(produce()));
    });
  }

  snapshot(): PlaybackStateSnapshot {
    return {
      sessionId: SESSION_ID,
      deviceId: FAKE_DEVICE.id,
      deviceName: FAKE_DEVICE.displayName,
      state: this.state,
      queueIndex: this.index,
      currentTrackId: this.tracks[this.index] ?? null,
      positionMs: this.positionMs,
      durationMs: this.tracks.length > 0 ? TRACK_MS : null,
      volumePercent: this.volumePercent,
      repeatMode: this.repeatMode,
      shuffle: this.shuffle,
      queueVersion: this.queueVersion,
      ownerUserId: 'user-1',
      ownerUsername: SESSION_OWNER,
      serverTimestamp: new Date(0).toISOString(),
      autoPlay: this.autoPlay,
    };
  }

  queueSnapshot(): PlaybackQueueSnapshot {
    return {
      sessionId: SESSION_ID,
      queueVersion: this.queueVersion,
      items: this.tracks.map<PlaybackQueueItem>((trackId, position) => ({
        position,
        trackId,
        title: `Track ${trackId}`,
        artist: 'Artist',
        artistId: 'artist-1',
        album: 'Album',
        albumId: 'album-1',
        durationMs: TRACK_MS,
        suffix: 'flac',
        bitRate: 939,
        autoPlay: this.autoPlayed.has(trackId),
      })),
    };
  }
}

export const server = new FakePlaybackServer();

/** The subset of `@/api/playback` the stores call, backed by {@link server}. */
export const fakeApi = {
  getPlaybackDevices: () =>
    server.respond('getPlaybackDevices', () => [
      {
        ...FAKE_DEVICE,
        activeSessionId: server.live ? SESSION_ID : null,
        activeSessionOwner: server.live ? SESSION_OWNER : null,
      },
    ]),

  startSession: () =>
    server.respond('startSession', () => {
      server.live = true;
      return server.snapshot();
    }),

  endSession: () =>
    server.respond('endSession', () => {
      server.live = false;
      server.state = 'Stopped';
      return undefined as void;
    }),

  getSession: () => server.respond('getSession', () => server.snapshot()),
  getSessionQueue: () => server.respond('getSessionQueue', () => server.queueSnapshot()),

  setQueue: (_id: string, trackIds: string[], startIndex = 0) =>
    server.respond('setQueue', () => {
      server.tracks = [...trackIds];
      server.queueVersion++;
      if (server.tracks.length === 0) {
        server.index = 0;
        server.positionMs = 0;
        server.state = 'Stopped';
      } else {
        server.index = Math.max(0, Math.min(startIndex, server.tracks.length - 1));
        server.positionMs = 0;
        server.state = 'Playing';
      }
      return server.snapshot();
    }),

  addToQueue: (_id: string, trackIds: string[], insertAt: number | null = null) =>
    server.respond('addToQueue', () => {
      const wasEmpty = server.tracks.length === 0;
      if (insertAt === null) {
        server.tracks.push(...trackIds);
      } else {
        const at = Math.max(0, Math.min(insertAt, server.tracks.length));
        server.tracks.splice(at, 0, ...trackIds);
        if (!wasEmpty && at <= server.index) server.index += trackIds.length;
      }
      server.queueVersion++;
      if (wasEmpty) {
        server.index = 0;
        server.state = 'Playing';
      }
      return server.snapshot();
    }),

  removeFromQueue: (_id: string, position: number) =>
    server.respond('removeFromQueue', () => {
      if (position < 0 || position >= server.tracks.length) return server.snapshot();
      const wasCurrent = position === server.index;
      server.tracks.splice(position, 1);
      server.queueVersion++;
      if (position < server.index) server.index--;
      else if (wasCurrent) {
        if (server.tracks.length === 0) {
          server.index = 0;
          server.state = 'Stopped';
        } else {
          server.index = Math.min(position, server.tracks.length - 1);
          server.positionMs = 0;
        }
      }
      return server.snapshot();
    }),

  moveInQueue: (_id: string, from: number, to: number) =>
    server.respond('moveInQueue', () => {
      const len = server.tracks.length;
      if (from < 0 || from >= len || to < 0 || to >= len || from === to) return server.snapshot();
      const current = server.tracks[server.index];
      const [moved] = server.tracks.splice(from, 1);
      server.tracks.splice(to, 0, moved);
      server.queueVersion++;
      const at = server.tracks.indexOf(current);
      if (at !== -1) server.index = at;
      return server.snapshot();
    }),

  playAt: (_id: string, position: number) =>
    server.respond('playAt', () => {
      if (position < 0 || position >= server.tracks.length) return server.snapshot();
      server.index = position;
      server.positionMs = 0;
      server.state = 'Playing';
      return server.snapshot();
    }),

  play: () =>
    server.respond('play', () => {
      if (server.tracks.length > 0) server.state = 'Playing';
      return server.snapshot();
    }),

  pause: () =>
    server.respond('pause', () => {
      server.state = 'Paused';
      return server.snapshot();
    }),

  stop: () =>
    server.respond('stop', () => {
      server.state = 'Stopped';
      server.positionMs = 0;
      return server.snapshot();
    }),

  next: () =>
    server.respond('next', () => {
      const at = server.index + 1;
      if (at >= server.tracks.length && server.repeatMode === 'Off') {
        server.state = 'Stopped';
        server.positionMs = 0;
        return server.snapshot();
      }
      server.index = at % Math.max(1, server.tracks.length);
      server.positionMs = 0;
      server.state = 'Playing';
      return server.snapshot();
    }),

  previous: () =>
    server.respond('previous', () => {
      // Matches the queue model: past three seconds "previous" restarts the current track.
      if (server.positionMs > 3000 || server.index === 0) {
        server.positionMs = 0;
        return server.snapshot();
      }
      server.index--;
      server.positionMs = 0;
      return server.snapshot();
    }),

  seek: (_id: string, positionSeconds: number) =>
    server.respond('seek', () => {
      server.positionMs = Math.max(0, positionSeconds * 1000);
      return server.snapshot();
    }),

  setVolume: (_id: string, percent: number) =>
    server.respond('setVolume', () => {
      server.volumePercent = Math.max(0, Math.min(FAKE_DEVICE.maxVolumePercent, percent));
      return server.snapshot();
    }),

  setRepeat: (_id: string, mode: PlaybackRepeatMode) =>
    server.respond('setRepeat', () => {
      server.repeatMode = mode;
      return server.snapshot();
    }),

  setShuffle: (_id: string, shuffled: boolean) =>
    server.respond('setShuffle', () => {
      server.shuffle = shuffled;
      return server.snapshot();
    }),

  setAutoPlay: (_id: string, enabled: boolean) =>
    server.respond('setAutoPlay', () => {
      server.autoPlay = enabled;
      // The real session tops a short queue up the moment this is switched on, and it marks what
      // it added - a mirror that assumed the queue stood still would not be testing much.
      if (enabled && server.tracks.length - server.index - 1 <= 3) {
        const added = ['auto-1', 'auto-2'];
        server.tracks.push(...added);
        for (const id of added) server.autoPlayed.add(id);
        server.queueVersion++;
      }
      return server.snapshot();
    }),
};

/**
 * A `HubConnection` that never opens a socket. Tests push events through {@link emit} to stand in
 * for the server's 1 Hz state broadcasts.
 */
export function createFakeHub() {
  const handlers = new Map<string, ((...args: unknown[]) => void)[]>();
  return {
    started: false,
    subscribed: [] as string[],
    on(name: string, handler: (...args: unknown[]) => void) {
      const list = handlers.get(name) ?? [];
      list.push(handler);
      handlers.set(name, list);
    },
    onreconnected(_handler: () => void) { /* reconnection is not exercised here */ },
    async start() { this.started = true; },
    async stop() { this.started = false; },
    async invoke(method: string, arg: string) {
      if (method === 'Subscribe') this.subscribed.push(arg);
    },
    /** Deliver a hub event as the server would. */
    emit(name: string, ...args: unknown[]) {
      for (const handler of handlers.get(name) ?? []) handler(...args);
    },
  };
}
