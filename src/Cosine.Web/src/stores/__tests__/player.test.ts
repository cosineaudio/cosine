import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';

const scrobbleMock = vi.fn().mockResolvedValue(undefined);
const getPlayQueueMock = vi.fn().mockResolvedValue(null);
const savePlayQueueMock = vi.fn().mockResolvedValue(undefined);

vi.mock('@/api/annotation', () => ({
  scrobble: (id: string) => scrobbleMock(id),
}));

vi.mock('@/api/queue', () => ({
  getPlayQueue: () => getPlayQueueMock(),
  savePlayQueue: (state: unknown) => savePlayQueueMock(state),
}));

import { usePlayerStore } from '../player';
import type { Track } from '@/api/types';

function track(id: string): Track {
  return { id, isDir: false, title: `Track ${id}`, artist: 'Artist', album: 'Album', duration: 180 };
}

describe('player store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    scrobbleMock.mockClear();
    savePlayQueueMock.mockClear();
    getPlayQueueMock.mockClear();
  });

  // Scrobbling on play() meant skipping through a queue recorded a play per track.
  // A play is now submitted once half the track (or four minutes) has been reached.
  describe('scrobble', () => {
    it('does not fire when a track merely starts', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b')]);
      expect(scrobbleMock).not.toHaveBeenCalled();
    });

    it('does not fire for a track that is skipped through', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b')]);
      player.currentTime = 4;
      player.play(track('b'));
      player.currentTime = 3;
      expect(scrobbleMock).not.toHaveBeenCalled();
    });

    it('fires once the halfway point is reached', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a')]); // 180 s → 90 s threshold
      player.currentTime = 89;
      expect(scrobbleMock).not.toHaveBeenCalled();

      player.currentTime = 90;
      expect(scrobbleMock).toHaveBeenCalledExactlyOnceWith('a');
    });

    it('does not submit the same play twice', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a')]);
      player.currentTime = 90;
      player.currentTime = 120;
      player.currentTime = 179;
      expect(scrobbleMock).toHaveBeenCalledTimes(1);
    });

    it('caps the threshold at four minutes for long tracks', () => {
      const player = usePlayerStore();
      player.play({ ...track('long'), duration: 3600 }, []);
      player.currentTime = 239;
      expect(scrobbleMock).not.toHaveBeenCalled();

      player.currentTime = 240;
      expect(scrobbleMock).toHaveBeenCalledExactlyOnceWith('long');
    });

    it('submits again when the same track is played again', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a')]);
      player.currentTime = 90;
      player.play(track('a'));
      player.currentTime = 90;
      expect(scrobbleMock).toHaveBeenCalledTimes(2);
    });

    it('never scrobbles internet radio', () => {
      const player = usePlayerStore();
      player.play({ ...track('station'), streamUrl: 'http://radio.example/stream' }, []);
      player.currentTime = 600;
      expect(scrobbleMock).not.toHaveBeenCalled();
    });
  });

  describe('moveInQueue', () => {
    it('reorders the queue and keeps the current track selected', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b'), track('c'), track('d')]);
      expect(player.queueIndex).toBe(0);

      player.moveInQueue(0, 2);

      expect(player.queue.map((t) => t.id)).toEqual(['b', 'c', 'a', 'd']);
      // current track "a" followed to its new index
      expect(player.queueIndex).toBe(2);
      expect(player.currentTrack?.id).toBe('a');
    });

    it('ignores out-of-range or no-op moves', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b')]);
      player.moveInQueue(0, 0);
      player.moveInQueue(5, 1);
      expect(player.queue.map((t) => t.id)).toEqual(['a', 'b']);
    });
  });

  describe('playAt', () => {
    it('jumps to and plays the given queue index', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b'), track('c')]);
      player.playAt(2);
      expect(player.currentTrack?.id).toBe('c');
      expect(player.queueIndex).toBe(2);
    });
  });

  describe('removeFromQueue', () => {
    it('adjusts the index when removing before the current track', () => {
      const player = usePlayerStore();
      player.play(track('b'), [track('a'), track('b'), track('c')]);
      expect(player.queueIndex).toBe(1);
      player.removeFromQueue(0);
      expect(player.queueIndex).toBe(0);
      expect(player.currentTrack?.id).toBe('b');
    });

    it('leaves a later removal alone', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b'), track('c')]);
      player.removeFromQueue(2);
      expect(player.queueIndex).toBe(0);
      expect(player.currentTrack?.id).toBe('a');
      expect(player.queue.map((t) => t.id)).toEqual(['a', 'b']);
    });

    // Removing the playing entry used to leave currentTrack pointing at a track the queue
    // no longer contained.
    it('hands over to the track that shifts into the slot', () => {
      const player = usePlayerStore();
      player.play(track('b'), [track('a'), track('b'), track('c')]);

      player.removeFromQueue(1);

      expect(player.queue.map((t) => t.id)).toEqual(['a', 'c']);
      expect(player.queueIndex).toBe(1);
      expect(player.currentTrack?.id).toBe('c');
      expect(player.queue[player.queueIndex].id).toBe(player.currentTrack?.id);
    });

    it('continues playing when the removed entry was playing', () => {
      const player = usePlayerStore();
      player.play(track('b'), [track('a'), track('b'), track('c')]);
      player.isPlaying = true;

      player.removeFromQueue(1);

      expect(player.currentTrack?.id).toBe('c');
      expect(player.queueIndex).toBe(1);
      expect(player.isPlaying).toBe(true);
    });

    it('falls back to the new last track when the playing entry was last', () => {
      const player = usePlayerStore();
      player.play(track('c'), [track('a'), track('b'), track('c')]);

      player.removeFromQueue(2);

      expect(player.queueIndex).toBe(1);
      expect(player.currentTrack?.id).toBe('b');
    });

    it('clears playback when the last remaining entry is removed', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a')]);

      player.removeFromQueue(0);

      expect(player.queue).toEqual([]);
      expect(player.queueIndex).toBe(0);
      expect(player.currentTrack).toBeNull();
      expect(player.isPlaying).toBe(false);
    });
  });

  // The preloader warms the next track into a hidden element, but a *different* element plays
  // it - so it is only worth anything when the browser can reuse the warmed response. Raw
  // /rest/stream now carries an ETag + Cache-Control; a live transcode cannot, and warming one
  // is a guaranteed second download plus a second ffmpeg process.
  describe('next-track preload', () => {
    const RealAudio = globalThis.Audio;
    let created: HTMLAudioElement[] = [];

    beforeEach(() => {
      created = [];
      globalThis.Audio = function (this: unknown, ...args: unknown[]) {
        const el = new (RealAudio as unknown as new (...a: unknown[]) => HTMLAudioElement)(...args);
        created.push(el);
        return el;
      } as unknown as typeof Audio;
    });

    afterEach(() => {
      globalThis.Audio = RealAudio;
    });

    /** Puts the active element 5 s from the end of a 180 s track and ticks it. */
    function nearTrackEnd(): void {
      const el = created[0];
      Object.defineProperty(el, 'duration', { value: 180, configurable: true });
      Object.defineProperty(el, 'currentTime', { value: 175, configurable: true, writable: true });
      el.dispatchEvent(new Event('timeupdate'));
    }

    it('warms the next track while streaming raw files', () => {
      const player = usePlayerStore();
      player.play(track('a'), [track('a'), track('b')]);

      nearTrackEnd();

      expect(created).toHaveLength(2);
      expect(created[1].src).toContain('/api/stream');
      expect(created[1].src).toContain('b');
    });

    it('does not warm anything while transcoding is enabled', () => {
      const player = usePlayerStore();
      player.preferredFormat = 'mp3';
      player.play(track('a'), [track('a'), track('b')]);

      nearTrackEnd();

      expect(created).toHaveLength(1);
    });

    it('does not warm an internet radio station', () => {
      const player = usePlayerStore();
      const station = { ...track('station'), streamUrl: 'http://radio.example/stream' };
      player.play(track('a'), [track('a'), station]);

      nearTrackEnd();

      expect(created).toHaveLength(1);
    });
  });

  // Shuffle used to draw a fresh random index on every advance, so it could repeat the track
  // that just played and replayed tracks long before the queue was exhausted.
  describe('shuffle', () => {
    const queueOf = (n: number) =>
      Array.from({ length: n }, (_, i) => track(String.fromCharCode(97 + i)));

    it('plays every track once before repeating any', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(6));
      player.toggleShuffle();

      const visited = [player.currentTrack!.id];
      for (let i = 0; i < 5; i++) {
        player.next();
        visited.push(player.currentTrack!.id);
      }

      expect(new Set(visited).size).toBe(6);
      expect([...visited].sort()).toEqual(['a', 'b', 'c', 'd', 'e', 'f']);
    });

    it('does not replay the track that just finished when the pass wraps', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(4));
      player.toggleShuffle();
      player.cycleRepeatMode(); // off -> one
      player.cycleRepeatMode(); // one -> all

      for (let i = 0; i < 3; i++) player.next();
      const last = player.currentTrack!.id;

      player.next(); // the pass is exhausted - a new permutation starts here
      expect(player.currentTrack!.id).not.toBe(last);
    });

    it('stops at the end of the pass when repeat is off', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(3));
      player.toggleShuffle();

      player.next();
      player.next();
      const last = player.currentTrack!.id;
      player.next();

      expect(player.currentTrack!.id).toBe(last);
      expect(player.isPlaying).toBe(false);
    });

    it('steps back through the pass on previous', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(5));
      player.toggleShuffle();

      player.next();
      const second = player.currentTrack!.id;
      player.next();
      player.currentTime = 0; // previous() restarts the track when past 3 s
      player.previous();

      expect(player.currentTrack!.id).toBe(second);
    });

    it('keeps the pass consistent when a track is picked by hand', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(5));
      player.toggleShuffle();
      player.cycleRepeatMode();
      player.cycleRepeatMode(); // repeat all, so advancing always yields a track

      // playAt jumps outside the permutation's running order; the pointer has to follow it,
      // or the next advance resumes from wherever the pointer was left.
      player.playAt(3);
      player.next();

      expect(player.currentTrack!.id).not.toBe(player.queue[3].id);
    });

    // Appending used to redraw the permutation, because the queue's length no longer matched it -
    // so every track already heard this pass went back into play. An auto-playing queue would
    // have spent the evening replaying the last hour instead of moving on.
    it('does not put already-played tracks back into the pass when the queue is appended to', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(5));
      player.toggleShuffle();

      const heard = [player.currentTrack!.id];
      player.next();
      heard.push(player.currentTrack!.id);
      player.next();
      heard.push(player.currentTrack!.id);

      player.addToQueue([track('x'), track('y')]);

      // The rest of the pass: the two originals left plus the two appended, and nothing heard.
      for (let i = 0; i < 4; i++) {
        player.next();
        expect(heard).not.toContain(player.currentTrack!.id);
        heard.push(player.currentTrack!.id);
      }

      expect(new Set(heard).size).toBe(7);
    });

    it('plays appended tracks within the current pass', () => {
      const player = usePlayerStore();
      player.play(track('a'), queueOf(2));
      player.toggleShuffle();

      player.addToQueue([track('x'), track('y')]);

      const rest: string[] = [];
      for (let i = 0; i < 3; i++) {
        player.next();
        rest.push(player.currentTrack!.id);
      }

      expect(rest).toContain('x');
      expect(rest).toContain('y');
    });
  });

  // The debounced save was watched on [queue ids, currentTrackId] only, so it fired ≈1.5 s
  // after a track change and never again - cross-device resume always restored ≈0.
  describe('cross-device position persistence', () => {
    beforeEach(() => {
      vi.useFakeTimers();
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    async function settle() {
      await nextTick();
      await vi.advanceTimersByTimeAsync(1600);
    }

    it('saves the position as playback advances', async () => {
      const player = usePlayerStore();
      await player.initPersistence();
      player.play(track('a'), [track('a'), track('b')]);
      await settle();
      savePlayQueueMock.mockClear();

      player.currentTime = 12;
      await settle();

      expect(savePlayQueueMock).toHaveBeenCalledWith(
        expect.objectContaining({ current: 'a', position: 12000 }),
      );
    });

    it('does not save on every timeupdate', async () => {
      const player = usePlayerStore();
      await player.initPersistence();
      player.play(track('a'), [track('a')]);
      await settle();
      savePlayQueueMock.mockClear();

      player.currentTime = 1;
      player.currentTime = 2;
      player.currentTime = 3;
      await settle();

      expect(savePlayQueueMock).not.toHaveBeenCalled();
    });

    it('saves when playback pauses', async () => {
      const player = usePlayerStore();
      await player.initPersistence();
      player.play(track('a'), [track('a')]);
      player.isPlaying = true;
      await settle();
      savePlayQueueMock.mockClear();

      player.isPlaying = false;
      await settle();

      expect(savePlayQueueMock).toHaveBeenCalledOnce();
    });
  });
});
