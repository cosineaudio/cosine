import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const hubHolder = vi.hoisted(() => ({ current: null as unknown as ReturnType<typeof createFakeHub> }));

vi.mock('@/composables/useSignalR', () => ({
  createPlaybackHubConnection: () => hubHolder.current,
}));

vi.mock('@/api/playback', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/playback')>();
  const { fakeApi } = await import('./fakePlaybackServer');
  // queueItemToTrack is the real one: mapping a server queue entry onto the Track the components
  // render is part of what is being tested, not part of the fake.
  return { ...actual, ...fakeApi };
});

import { useRemotePlayerStore } from '../remotePlayer';
import { FAKE_DEVICE, createFakeHub, server } from './fakePlaybackServer';
import { describeOutputContract, trackFixture } from './playbackOutputContract';
import type { PlaybackStateSnapshot } from '@/api/playback';

/** Let every response, and everything chained off it, land. */
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

function newRemote() {
  setActivePinia(createPinia());
  server.reset();
  hubHolder.current = createFakeHub();
  return useRemotePlayerStore();
}

/** A hub state event, as the engine's poll loop would broadcast it. */
function stateEvent(overrides: Partial<PlaybackStateSnapshot> = {}): PlaybackStateSnapshot {
  return { ...server.snapshot(), ...overrides };
}

// The same behavioural suite the local output is held to (plan §6).
describeOutputContract('remote output', async () => {
  const store = newRemote();
  await store.activate(FAKE_DEVICE);
  await settle();
  return { output: store, settle };
});

describe('remote player store', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function activated() {
    const store = newRemote();
    await store.activate(FAKE_DEVICE);
    await settle();
    store.play(trackFixture('a'), ['a', 'b', 'c'].map(trackFixture));
    await settle();
    return store;
  }

  describe('session lifecycle', () => {
    it('claims the device, subscribes to its session and mirrors the queue', async () => {
      const store = await activated();

      expect(store.isActive).toBe(true);
      expect(store.deviceName).toBe('Kitchen');
      expect(hubHolder.current.started).toBe(true);
      expect(hubHolder.current.subscribed).toEqual(['session-1']);
      expect(store.queue.map((t) => t.id)).toEqual(['a', 'b', 'c']);
      // The server's queue entries become the same Track shape the rest of the UI renders.
      expect(store.currentTrack).toMatchObject({
        id: 'a', title: 'Track a', album: 'Album', albumId: 'album-1', coverArt: 'album-1',
        duration: 180, suffix: 'flac',
      });
    });

    it('hands the output back when the session ends server-side', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackSessionEnded', 'session-1');
      await settle();
      expect(store.isActive).toBe(false);
      expect(store.queue).toEqual([]);
    });

    it('reports a session someone else has taken', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Hijacked' }));
      await settle();
      expect(store.interruption).toBe('hijacked');
    });
  });

  // Without these, every button waits for a LAN round-trip plus up to a full poll interval before
  // the UI moves - the single biggest factor in whether remote feels local (plan §4.8 item 1).
  describe('optimistic commands', () => {
    it('pauses before the server has answered', async () => {
      const store = await activated();
      expect(store.isPlaying).toBe(true);

      server.defer();
      store.pause();
      await settle();

      expect(store.isPlaying).toBe(false); // no response yet
      server.flush();
      await settle();
      expect(store.isPlaying).toBe(false);
    });

    it('moves the scrubber before the server has answered', async () => {
      const store = await activated();

      server.defer();
      store.seek(42);
      await settle();

      expect(store.currentTime).toBe(42);
      server.flush();
      await settle();
      expect(store.currentTime).toBeCloseTo(42, 1);
    });

    // A state event that was sampled before the command landed would visibly undo the button that
    // was just pressed.
    it('ignores hub state that predates a command still in flight', async () => {
      const store = await activated();

      server.defer();
      store.pause();
      await settle();

      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 90_000 }));
      await settle();
      expect(store.isPlaying).toBe(false);

      server.flush();
      await settle();
      expect(store.isPlaying).toBe(false);
    });

    it('resyncs from the server when the optimistic guess was wrong', async () => {
      const store = await activated();

      server.failNext = 'pause';
      store.pause();
      await settle();

      // The server never paused, so neither has the UI once it has been told the truth.
      expect(store.isPlaying).toBe(true);
      expect(store.error).toBe('pause refused');
    });
  });

  // A 1 Hz sample stepping the scrubber once a second is visibly worse than local's smooth
  // timeupdate, so the position is interpolated between messages (plan §4.8 item 2).
  describe('position interpolation', () => {
    it('advances the position between hub messages while playing', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 10_000 }));
      await settle();
      expect(store.currentTime).toBeCloseTo(10, 1);

      await vi.advanceTimersByTimeAsync(1000);
      expect(store.currentTime).toBeCloseTo(11, 1);
    });

    it('does not advance while paused', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Paused', positionMs: 10_000 }));
      await settle();

      await vi.advanceTimersByTimeAsync(2000);
      expect(store.currentTime).toBeCloseTo(10, 1);
    });

    it('leaves the scrubber alone when the server is within a second of it', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 10_000 }));
      await settle();
      await vi.advanceTimersByTimeAsync(1000);

      // The interpolated position is ~11 s; a sample 400 ms behind it is not worth a visible jump.
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 10_600 }));
      await settle();
      expect(store.currentTime).toBeCloseTo(11, 1);
    });

    it('snaps to the server when they genuinely disagree', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 10_000 }));
      await settle();

      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 95_000 }));
      await settle();
      expect(store.currentTime).toBeCloseTo(95, 1);
    });

    it('restarts the scrubber when the queue advances to the next track', async () => {
      const store = await activated();
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 178_000 }));
      await settle();

      hubHolder.current.emit('PlaybackState', stateEvent({
        state: 'Playing', queueIndex: 1, currentTrackId: 'b', positionMs: 0,
      }));
      await settle();

      expect(store.currentTrack?.id).toBe('b');
      expect(store.currentTime).toBe(0);
    });

    it('holds the seeked position while the device catches up', async () => {
      const store = await activated();
      store.seek(60);
      await settle();

      // The poll that lands right after a seek still reports where the device was.
      hubHolder.current.emit('PlaybackState', stateEvent({ state: 'Playing', positionMs: 2_000 }));
      await settle();
      expect(store.currentTime).toBeCloseTo(60, 1);
    });
  });

  // State moves every second and the queue almost never does, so folding them together would have
  // every client re-diffing the whole list once a second for nothing (plan §4.8 item 3).
  describe('queue events', () => {
    it('takes the queue straight from its own event, without asking for it', async () => {
      const store = await activated();
      server.calls = [];

      server.tracks = ['a', 'b', 'c', 'd'];
      server.queueVersion++;
      hubHolder.current.emit('QueueChanged', server.queueSnapshot());
      await settle();

      expect(store.queue.map((t) => t.id)).toEqual(['a', 'b', 'c', 'd']);
      expect(server.calls).not.toContain('getSessionQueue');
    });

    it('fetches the queue when a state event shows it has moved on without us', async () => {
      const store = await activated();
      server.calls = [];

      server.tracks = ['c', 'b', 'a'];
      server.queueVersion++;
      hubHolder.current.emit('PlaybackState', stateEvent());
      await settle();

      expect(server.calls).toContain('getSessionQueue');
      expect(store.queue.map((t) => t.id)).toEqual(['c', 'b', 'a']);
    });

    it('does not let a late fetch overwrite a newer queue', async () => {
      const store = await activated();

      server.tracks = ['a', 'b', 'c', 'd'];
      server.queueVersion = 9;
      hubHolder.current.emit('QueueChanged', server.queueSnapshot());
      await settle();

      // An event from before that one, arriving late.
      hubHolder.current.emit('QueueChanged', {
        sessionId: 'session-1',
        queueVersion: 3,
        items: [],
      });
      await settle();

      expect(store.queue.map((t) => t.id)).toEqual(['a', 'b', 'c', 'd']);
    });
  });

  describe('device stream format', () => {
    it('reports the device transcode profile rather than any browser preference', async () => {
      setActivePinia(createPinia());
      server.reset();
      hubHolder.current = createFakeHub();
      const store = useRemotePlayerStore();

      await store.activate({ ...FAKE_DEVICE, transcodeProfile: 'mp3:320' });
      await settle();

      expect(store.preferredFormat).toBe('mp3');
      expect(store.preferredBitRate).toBe(320);
      expect(store.isTranscodingEnabled).toBe(true);

      // Setting it from here is deliberately inert: the speaker fetches its own bytes.
      store.setFormatPreference('flac', 0);
      expect(store.preferredFormat).toBe('mp3');
    });

    it('reads as untranscoded when the device has no profile', async () => {
      const store = await activated();
      expect(store.preferredFormat).toBe('');
      expect(store.isTranscodingEnabled).toBe(false);
    });
  });

  describe('taking over a queue from the browser', () => {
    it('sets the queue, seeks into it and matches the play state, in that order', async () => {
      const store = newRemote();
      await store.activate(FAKE_DEVICE);
      await settle();
      server.calls = [];

      // Reads are incidental here; what matters is that the three commands go in order.
      const commands = () => server.calls.filter((c) => c !== 'getSessionQueue');

      await store.adopt(['a', 'b', 'c'].map(trackFixture), 1, 47, false);
      await settle();

      expect(commands()).toEqual(['setQueue', 'seek', 'pause']);
      expect(store.queue.map((t) => t.id)).toEqual(['a', 'b', 'c']);
      expect(store.queueIndex).toBe(1);
      expect(store.currentTime).toBeCloseTo(47, 1);
      expect(store.isPlaying).toBe(false);
    });

    it('keeps playing when the browser was playing', async () => {
      const store = newRemote();
      await store.activate(FAKE_DEVICE);
      await settle();
      server.calls = [];

      // Reads are incidental here; what matters is that the three commands go in order.
      const commands = () => server.calls.filter((c) => c !== 'getSessionQueue');

      await store.adopt([trackFixture('a')], 0, 0, true);
      await settle();

      expect(commands()).toEqual(['setQueue']);
      expect(store.isPlaying).toBe(true);
    });
  });

  // Auto-play is the session's, not this browser's: the speaker keeps fetching after the tab is
  // closed. All this store does is set it and mirror what comes back.
  describe('auto-play', () => {
    it('sends the setting and reflects the queue the session fetched for itself', async () => {
      const store = newRemote();
      await store.activate(FAKE_DEVICE);
      await store.adopt([trackFixture('a')], 0, 0, true);
      await settle();

      store.setAutoPlay(true);
      await settle();

      expect(server.calls).toContain('setAutoPlay');
      expect(store.autoPlay).toBe(true);
      expect(store.queue.length).toBeGreaterThan(1);
      expect(store.isAutoPlayTrack('auto-1')).toBe(true);
      expect(store.isAutoPlayTrack('a')).toBe(false);
    });

    it('moves the toggle before the server answers', async () => {
      const store = newRemote();
      await store.activate(FAKE_DEVICE);
      await settle();

      server.defer();
      store.setAutoPlay(true);

      expect(store.autoPlay).toBe(true);

      server.flush();
      await settle();
      expect(store.autoPlay).toBe(true);
    });

    it('follows a session whose setting was changed elsewhere', async () => {
      const store = newRemote();
      await store.activate(FAKE_DEVICE);
      await settle();

      hubHolder.current.emit('PlaybackState', stateEvent({ autoPlay: true }));
      await settle();

      expect(store.autoPlay).toBe(true);
    });
  });
});
