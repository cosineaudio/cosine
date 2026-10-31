import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';

const autoPlayMock = vi.fn();
const genreMock = vi.fn();
const artistSongsMock = vi.fn();
const recentMock = vi.fn(() => [] as { id: string }[]);

vi.mock('@/api/library', () => ({
  getAutoPlayQueue: (opts: unknown) => autoPlayMock(opts),
  getArtistSongs: (id: string) => artistSongsMock(id),
}));

vi.mock('@/api/lists', () => ({
  getSongsByGenre: (genre: string, opts: unknown) => genreMock(genre, opts),
}));

vi.mock('@/lib/recentTracks', () => ({
  getRecentTracks: () => recentMock(),
  pushRecentTrack: () => {},
}));

vi.mock('@/api/annotation', () => ({ scrobble: vi.fn().mockResolvedValue(undefined) }));
vi.mock('@/api/queue', () => ({
  getPlayQueue: vi.fn().mockResolvedValue(null),
  savePlayQueue: vi.fn().mockResolvedValue(undefined),
}));

import { useAutoPlayStore } from '../autoPlay';
import { usePlayerStore } from '../player';
import type { Track } from '@/api/types';

function track(id: string): Track {
  return { id, isDir: false, title: `Track ${id}`, artist: 'Artist', album: 'Album', duration: 180 };
}

const queueOf = (n: number, prefix = 't') =>
  Array.from({ length: n }, (_, i) => track(`${prefix}${i}`));

/** This environment has no Storage; the store treats one that throws as simply unavailable. */
function fakeStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() { return values.size; },
    clear: () => values.clear(),
    getItem: (k: string) => values.get(k) ?? null,
    key: (i: number) => [...values.keys()][i] ?? null,
    removeItem: (k: string) => { values.delete(k); },
    setItem: (k: string, v: string) => { values.set(k, v); },
  };
}

/**
 * Lets the store's watchers run and its in-flight fetch settle. The microtask queue is drained
 * several times over rather than once: a top-up that reads its source before blending is one
 * `await` deeper than one that does not, and how deep the chain runs is not a test's business.
 */
async function settle(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await nextTick();
    await Promise.resolve();
  }
}

describe('autoPlay store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', fakeStorage());
    autoPlayMock.mockReset();
    autoPlayMock.mockResolvedValue([track('r1'), track('r2')]);
    genreMock.mockReset();
    genreMock.mockResolvedValue([]);
    artistSongsMock.mockReset();
    artistSongsMock.mockResolvedValue([]);
    recentMock.mockReturnValue([]);
  });

  it('is off until it is switched on, and remembers that it was', async () => {
    const auto = useAutoPlayStore();
    expect(auto.enabled).toBe(false);

    auto.toggle();
    await settle();

    expect(auto.enabled).toBe(true);
    expect(localStorage.getItem('cosine_autoplay')).toBe('true');
  });

  it('extends a queue that is nearly done', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    player.play(track('t0'), queueOf(2));

    auto.setEnabled(true);
    await settle();

    expect(autoPlayMock).toHaveBeenCalledOnce();
    expect(player.queue.map((t) => t.id)).toContain('r1');
  });

  // Otherwise switching it on would append twenty tracks behind an album someone just started.
  it('leaves a queue with plenty left in it alone', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    player.play(track('t0'), queueOf(20));

    auto.setEnabled(true);
    await settle();

    expect(autoPlayMock).not.toHaveBeenCalled();
    expect(player.queue).toHaveLength(20);
  });

  it('fetches as the queue runs down', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    player.play(track('t0'), queueOf(6));
    auto.setEnabled(true);
    await settle();
    expect(autoPlayMock).not.toHaveBeenCalled();

    // Two tracks in, three remain - the threshold.
    player.next();
    player.next();
    await settle();

    expect(autoPlayMock).toHaveBeenCalled();
  });

  // Repeat already means the queue never ends; topping it up as well would grow it forever
  // while it loops over the part that was there first.
  it('adds nothing while repeat is on', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    player.play(track('t0'), queueOf(2));
    player.cycleRepeatMode(); // off -> one

    auto.setEnabled(true);
    await settle();

    expect(autoPlayMock).not.toHaveBeenCalled();
  });

  // Internet radio is one endless stream with no track rows behind it - there is no queue here
  // to extend and nothing to seed a blend with.
  it('adds nothing while internet radio is playing', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    const station: Track = { ...track('station'), streamUrl: 'http://example.com/stream' };
    player.play(station, [station]);

    auto.setEnabled(true);
    await settle();

    expect(autoPlayMock).not.toHaveBeenCalled();
  });

  // A radio that circles back to the track from twenty minutes ago is the failure this whole
  // feature is judged on, so both the queue and the recently played go in the exclusion list.
  it('excludes the queue and what has just been played', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    recentMock.mockReturnValue([{ id: 'heard' }]);
    player.play(track('t0'), queueOf(2));

    auto.setEnabled(true);
    await settle();

    const sent = autoPlayMock.mock.calls[0][0] as {
      seedTrackIds: string[]; excludeTrackIds: string[];
    };
    expect(sent.excludeTrackIds).toContain('t0');
    expect(sent.excludeTrackIds).toContain('t1');
    expect(sent.excludeTrackIds).toContain('heard');
    // The track playing seeds it, ahead of the older history.
    expect(sent.seedTrackIds[0]).toBe('t0');
  });

  it('marks what it added, so the queue panel can show where the radio began', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    player.play(track('t0'), queueOf(2));

    auto.setEnabled(true);
    await settle();

    expect(auto.isAutoPlayTrack('r1')).toBe(true);
    expect(auto.isAutoPlayTrack('t0')).toBe(false);
  });

  // A batch smaller than the threshold satisfies nothing: appending leaves the queue still
  // short, the watcher fires again, and a sparse library becomes a request loop.
  it('fetches at most once per track, even when a batch is too small to clear the threshold', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    autoPlayMock.mockResolvedValue([track('r1')]);
    player.play(track('t0'), queueOf(2));

    auto.setEnabled(true);
    await settle();
    await settle();

    expect(autoPlayMock).toHaveBeenCalledOnce();

    // Moving on to the next track is what makes it ask again.
    player.next();
    await settle();
    expect(autoPlayMock).toHaveBeenCalledTimes(2);
  });

  it('surfaces a batch that comes back with nothing', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    autoPlayMock.mockResolvedValue([]);
    player.play(track('t0'), queueOf(2));

    auto.setEnabled(true);
    await settle();

    expect(auto.error).not.toBeNull();
    expect(player.queue).toHaveLength(2);
  });

  it('does not take a failed fetch out on the queue', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    autoPlayMock.mockRejectedValue(new Error('offline'));
    player.play(track('t0'), queueOf(2));

    auto.setEnabled(true);
    await settle();

    expect(auto.error).not.toBeNull();
    expect(player.queue).toHaveLength(2);
    expect(auto.enabled).toBe(true);
  });

  // Switching it on after the queue has already run out has to start the music again, not
  // silently stack twenty tracks behind a stopped player.
  it('starts playing again when it is switched on after the queue ended', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    player.play(track('t0'), queueOf(1));
    player.duration = 180;
    player.currentTime = 180;
    player.isPlaying = false;

    auto.setEnabled(true);
    await settle();

    expect(player.currentTrack!.id).toBe('r1');
  });

  // A page shows part of a genre, so running out of rows is not running out of the genre.
  describe('continuing the queue it was given', () => {
    /** A queue two tracks from its end, built from 200 songs of a genre. */
    function playingAGenre() {
      const player = usePlayerStore();
      const auto = useAutoPlayStore();
      player.play(track('t0'), queueOf(2));
      auto.setQueueSource({ kind: 'genre', name: 'Doom', offset: 200 });
      return { player, auto };
    }

    it('reads on through the genre instead of blending', async () => {
      const { player, auto } = playingAGenre();
      genreMock.mockResolvedValue(queueOf(20, 'g'));

      auto.setEnabled(true);
      await settle();

      expect(genreMock).toHaveBeenCalledWith('Doom', { count: 20, offset: 200 });
      expect(autoPlayMock).not.toHaveBeenCalled();
      expect(player.queue.map((t) => t.id)).toContain('g0');
    });

    it('carries the cursor on rather than re-reading the same rows', async () => {
      const { player, auto } = playingAGenre();
      genreMock.mockResolvedValue(queueOf(20, 'g'));

      auto.setEnabled(true);
      await settle();

      // Near the end of what that page added, so the next top-up is due.
      player.playAt(18);
      await settle();

      expect(genreMock).toHaveBeenLastCalledWith('Doom', { count: 20, offset: 220 });
    });

    // The genre is finite; endless play is not. A short page is the end of it.
    it('falls back to the blend once the genre runs out', async () => {
      const { player, auto } = playingAGenre();
      genreMock.mockResolvedValue([track('g0'), track('g1')]);

      auto.setEnabled(true);
      await settle();

      expect(player.queue.map((t) => t.id)).toEqual(['t0', 't1', 'g0', 'g1']);
      expect(auto.queueSource).toBeNull();

      player.playAt(1);
      await settle();

      expect(genreMock).toHaveBeenCalledOnce();
      expect(autoPlayMock).toHaveBeenCalled();
    });

    // A paged read can hand back something already queued - by hand, or because the library
    // shifted underneath it. Playing it twice is worse than a short batch.
    it('drops rows the queue already holds', async () => {
      const { player, auto } = playingAGenre();
      genreMock.mockResolvedValue([track('t1'), ...queueOf(19, 'g')]);

      auto.setEnabled(true);
      await settle();

      expect(player.queue.filter((t) => t.id === 't1')).toHaveLength(1);
    });

    /** A queue two tracks from its end, built from an artist's top songs. */
    function playingAnArtist() {
      const player = usePlayerStore();
      const auto = useAutoPlayStore();
      player.play(track('t0'), queueOf(2));
      auto.setQueueSource({ kind: 'artist', id: 'a1' });
      return { player, auto };
    }

    it('reads on into the rest of the artist instead of blending', async () => {
      const { player, auto } = playingAnArtist();
      artistSongsMock.mockResolvedValue(queueOf(40, 'c'));

      auto.setEnabled(true);
      await settle();

      expect(artistSongsMock).toHaveBeenCalledWith('a1');
      expect(autoPlayMock).not.toHaveBeenCalled();
      expect(player.queue.map((t) => t.id)).toContain('c0');
      // One batch, not the whole catalogue.
      expect(player.queue).toHaveLength(22);
    });

    // The catalogue is one request for everything the artist has; re-reading it every batch is
    // what the cursor-free walk would otherwise cost.
    it('holds the catalogue between batches and carries on through it', async () => {
      const { player, auto } = playingAnArtist();
      artistSongsMock.mockResolvedValue(queueOf(40, 'c'));

      auto.setEnabled(true);
      await settle();

      player.playAt(18);
      await settle();

      expect(artistSongsMock).toHaveBeenCalledOnce();
      expect(player.queue.map((t) => t.id)).toContain('c20');
    });

    // Top songs are ranked by play count, so the catalogue holds them too - appending them again
    // would replay the tracks the listener just heard.
    it('skips catalogue tracks already queued or just played', async () => {
      const { player, auto } = playingAnArtist();
      recentMock.mockReturnValue([{ id: 'heard' }]);
      artistSongsMock.mockResolvedValue([track('t1'), track('heard'), ...queueOf(20, 'c')]);

      auto.setEnabled(true);
      await settle();

      const ids = player.queue.map((t) => t.id);
      expect(ids.filter((id) => id === 't1')).toHaveLength(1);
      expect(ids).not.toContain('heard');
    });

    it('falls back to the blend once the catalogue is used up', async () => {
      const { player, auto } = playingAnArtist();
      artistSongsMock.mockResolvedValue(queueOf(3, 'c'));

      auto.setEnabled(true);
      await settle();

      expect(player.queue.map((t) => t.id)).toEqual(['t0', 't1', 'c0', 'c1', 'c2']);
      expect(auto.queueSource).toBeNull();

      player.playAt(2);
      await settle();

      expect(artistSongsMock).toHaveBeenCalledOnce();
      expect(autoPlayMock).toHaveBeenCalled();
    });

    it('blends when the queue came from nowhere in particular', async () => {
      const player = usePlayerStore();
      const auto = useAutoPlayStore();
      player.play(track('t0'), queueOf(2));

      auto.setEnabled(true);
      await settle();

      expect(genreMock).not.toHaveBeenCalled();
      expect(autoPlayMock).toHaveBeenCalledOnce();
    });
  });

  it('trims played history so the queue does not grow forever', async () => {
    const player = usePlayerStore();
    const auto = useAutoPlayStore();
    // 60 played entries behind the current one, which is past the 50 kept.
    player.play(track('t60'), queueOf(62));
    expect(player.queueIndex).toBe(60);

    auto.setEnabled(true);
    await settle();

    expect(player.queueIndex).toBe(50);
    expect(player.currentTrack!.id).toBe('t60');
    expect(player.queue[0].id).toBe('t10');
  });
});
