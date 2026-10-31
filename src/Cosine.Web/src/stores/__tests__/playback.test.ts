import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const hubHolder = vi.hoisted(() => ({ current: null as unknown as ReturnType<typeof createFakeHub> }));

vi.mock('@/composables/useSignalR', () => ({
  createPlaybackHubConnection: () => hubHolder.current,
}));

vi.mock('@/api/playback', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api/playback')>();
  const { fakeApi } = await import('./fakePlaybackServer');
  return { ...actual, ...fakeApi };
});

vi.mock('@/api/annotation', () => ({ scrobble: vi.fn().mockResolvedValue(undefined) }));
vi.mock('@/api/queue', () => ({
  getPlayQueue: vi.fn().mockResolvedValue(null),
  savePlayQueue: vi.fn().mockResolvedValue(undefined),
}));

import { usePlaybackStore } from '../playback';
import { usePlayerStore } from '../player';
import { useAuthStore } from '../auth';
import { FAKE_DEVICE, SESSION_OWNER, createFakeHub, server } from './fakePlaybackServer';
import { trackFixture } from './playbackOutputContract';

async function settle(): Promise<void> {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

/**
 * The facade is a selector, so what is worth testing is the switching: which output the UI is
 * talking to, and whether the queue and position survive the handover in both directions.
 */
describe('playback facade', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    server.reset();
    hubHolder.current = createFakeHub();
  });

  function signedIn(username = SESSION_OWNER) {
    useAuthStore().user = { id: 'user-1', username, isAdmin: false };
  }

  it('reads the local output until a device is picked', async () => {
    const playback = usePlaybackStore();
    const local = usePlayerStore();
    local.play(trackFixture('a'), ['a', 'b'].map(trackFixture));
    await settle();

    expect(playback.isRemote).toBe(false);
    expect(playback.canAnalyse).toBe(true);
    expect(playback.outputName).toBe('This browser');
    expect(playback.currentTrack?.id).toBe('a');
    expect(playback.queue.map((t) => t.id)).toEqual(['a', 'b']);
  });

  it('hands the queue and position to the device it is switched to', async () => {
    const playback = usePlaybackStore();
    const local = usePlayerStore();
    local.play(trackFixture('b'), ['a', 'b', 'c'].map(trackFixture));
    local.seek(64);
    await settle();

    await playback.selectDevice(FAKE_DEVICE);
    await settle();

    expect(playback.isRemote).toBe(true);
    expect(playback.outputName).toBe('Kitchen');
    // The visualizer has nothing to tap once the audio is coming out of a speaker.
    expect(playback.canAnalyse).toBe(false);
    expect(server.tracks).toEqual(['a', 'b', 'c']);
    expect(server.index).toBe(1);
    expect(server.positionMs).toBe(64_000);
    expect(playback.currentTrack?.id).toBe('b');
    // The browser is paused, not cleared, so switching back is a handover rather than a restart.
    expect(local.isPlaying).toBe(false);
    expect(local.queue.map((t) => t.id)).toEqual(['a', 'b', 'c']);
  });

  it('brings the queue back and frees the device when switched to the browser', async () => {
    const playback = usePlaybackStore();
    const local = usePlayerStore();
    local.play(trackFixture('a'), ['a', 'b'].map(trackFixture));
    await settle();

    await playback.selectDevice(FAKE_DEVICE);
    await settle();
    // Something happened on the device while it was the output.
    server.tracks = ['a', 'b', 'c'];
    server.index = 2;
    server.queueVersion++;
    hubHolder.current.emit('QueueChanged', server.queueSnapshot());
    hubHolder.current.emit('PlaybackState', server.snapshot());
    await settle();

    await playback.selectLocal();
    await settle();

    expect(playback.isRemote).toBe(false);
    expect(server.live).toBe(false);
    expect(local.queue.map((t) => t.id)).toEqual(['a', 'b', 'c']);
    expect(local.currentTrack?.id).toBe('c');
  });

  it('reattaches to a session this user left running', async () => {
    signedIn();
    server.live = true; // a session survived the tab being closed

    const playback = usePlaybackStore();
    await playback.restoreSession();
    await settle();

    expect(playback.isRemote).toBe(true);
    expect(playback.outputName).toBe('Kitchen');
  });

  it('leaves someone else\'s session alone', async () => {
    signedIn('a-different-listener');
    server.live = true;

    const playback = usePlaybackStore();
    await playback.restoreSession();
    await settle();

    expect(playback.isRemote).toBe(false);
  });

  it('refuses internet radio on a speaker, with a reason', async () => {
    const playback = usePlaybackStore();
    await playback.selectDevice(FAKE_DEVICE);
    await settle();
    server.calls = [];

    const station = { ...trackFixture('station'), streamUrl: 'http://radio.example/stream' };
    playback.play(station, [station]);
    await settle();

    expect(server.calls).not.toContain('setQueue');
    expect(playback.remoteError).toContain('Internet radio');
  });
});
