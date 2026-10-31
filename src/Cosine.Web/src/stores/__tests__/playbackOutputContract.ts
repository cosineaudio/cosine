import { describe, it, expect } from 'vitest';
import type { Track } from '@/api/types';
import type { PlaybackOutput } from '@/stores/playbackOutput';

/**
 * One behavioural suite, run against both outputs (plan §6). Conformance to the *shape* of
 * `PlaybackOutput` is a compile-time matter and needs no runtime test; what this asserts is that
 * the browser and a speaker actually behave the same way when the UI drives them - otherwise
 * "the queue works the same either way" is an assumption rather than a fact, and the two
 * implementations of the queue rules (TypeScript and C#, plan §9) drift silently.
 *
 * Only behaviour both outputs can be held to lives here. Anything that is genuinely local -
 * preload, the analyser, MediaSession - belongs in `player.test.ts`.
 */

export function trackFixture(id: string): Track {
  return { id, isDir: false, title: `Track ${id}`, artist: 'Artist', album: 'Album', duration: 180 };
}

export interface OutputUnderTest {
  output: PlaybackOutput;
  /** Resolve everything the command has set in motion - a round trip, for a remote output. */
  settle: () => Promise<void>;
}

export function describeOutputContract(name: string, create: () => Promise<OutputUnderTest>): void {
  describe(`${name} - PlaybackOutput contract`, () => {
    async function withQueue(ids: string[], startId = ids[0]): Promise<OutputUnderTest> {
      const subject = await create();
      const queue = ids.map(trackFixture);
      subject.output.play(queue.find((t) => t.id === startId)!, queue);
      await subject.settle();
      return subject;
    }

    const ids = (output: PlaybackOutput) => output.queue.map((t) => t.id);

    it('plays a track and adopts the queue it came from', async () => {
      const { output } = await withQueue(['a', 'b', 'c'], 'b');
      expect(ids(output)).toEqual(['a', 'b', 'c']);
      expect(output.queueIndex).toBe(1);
      expect(output.currentTrack?.id).toBe('b');
      expect(output.hasTrack).toBe(true);
      expect(output.currentTrackId).toBe('b');
    });

    // The list handed to `play` belongs to the caller - an album's track list, an artist's top
    // songs, a genre page's songs - and stays on screen. An output that kept the reference would
    // show auto-play's top-up as extra rows on that page.
    it('copies the queue it is given rather than adopting the caller\'s list', async () => {
      const { output, settle } = await create();
      const list = ['a', 'b'].map(trackFixture);
      output.play(list[0], list);
      await settle();

      expect(output.queue).not.toBe(list);

      output.addToQueue([trackFixture('c')], 'end');
      output.removeFromQueue(0);
      output.moveInQueue(0, 1);
      await settle();

      expect(list.map((t) => t.id)).toEqual(['a', 'b']);
    });

    it('jumps to a queue position', async () => {
      const { output, settle } = await withQueue(['a', 'b', 'c']);
      output.playAt(2);
      await settle();
      expect(output.queueIndex).toBe(2);
      expect(output.currentTrack?.id).toBe('c');
    });

    it('ignores a jump to a position the queue does not have', async () => {
      const { output, settle } = await withQueue(['a', 'b']);
      output.playAt(7);
      await settle();
      expect(output.currentTrack?.id).toBe('a');
    });

    it('appends to the end of the queue', async () => {
      const { output, settle } = await withQueue(['a', 'b']);
      output.addToQueue([trackFixture('c')], 'end');
      await settle();
      expect(ids(output)).toEqual(['a', 'b', 'c']);
      expect(output.currentTrack?.id).toBe('a');
    });

    it('inserts directly after the playing track when queued next', async () => {
      const { output, settle } = await withQueue(['a', 'b', 'c']);
      output.addToQueue([trackFixture('x')], 'next');
      await settle();
      expect(ids(output)).toEqual(['a', 'x', 'b', 'c']);
      expect(output.currentTrack?.id).toBe('a');
    });

    it('keeps the playing track when something before it is removed', async () => {
      const { output, settle } = await withQueue(['a', 'b', 'c'], 'b');
      output.removeFromQueue(0);
      await settle();
      expect(ids(output)).toEqual(['b', 'c']);
      expect(output.queueIndex).toBe(0);
      expect(output.currentTrack?.id).toBe('b');
    });

    // Removing the playing entry must not leave the index pointing at a track the queue no longer
    // contains - the entry that shifts into the slot takes over.
    it('hands over to the track that shifts into the removed slot', async () => {
      const { output, settle } = await withQueue(['a', 'b', 'c'], 'b');
      output.removeFromQueue(1);
      await settle();
      expect(ids(output)).toEqual(['a', 'c']);
      expect(output.queueIndex).toBe(1);
      expect(output.currentTrack?.id).toBe('c');
    });

    it('reorders the queue and keeps the playing track selected', async () => {
      const { output, settle } = await withQueue(['a', 'b', 'c', 'd']);
      output.moveInQueue(0, 2);
      await settle();
      expect(ids(output)).toEqual(['b', 'c', 'a', 'd']);
      expect(output.queueIndex).toBe(2);
      expect(output.currentTrack?.id).toBe('a');
    });

    it('ignores a no-op or out-of-range reorder', async () => {
      const { output, settle } = await withQueue(['a', 'b']);
      output.moveInQueue(0, 0);
      output.moveInQueue(9, 0);
      await settle();
      expect(ids(output)).toEqual(['a', 'b']);
    });

    it('empties the queue', async () => {
      const { output, settle } = await withQueue(['a', 'b']);
      output.clearQueue();
      await settle();
      expect(output.queue).toEqual([]);
      expect(output.currentTrack).toBeNull();
      expect(output.hasTrack).toBe(false);
    });

    it('cycles repeat off → one → all → off', async () => {
      const { output, settle } = await withQueue(['a']);
      expect(output.repeatMode).toBe('off');
      output.cycleRepeatMode();
      await settle();
      expect(output.repeatMode).toBe('one');
      output.cycleRepeatMode();
      await settle();
      expect(output.repeatMode).toBe('all');
      output.cycleRepeatMode();
      await settle();
      expect(output.repeatMode).toBe('off');
    });

    it('toggles shuffle', async () => {
      const { output, settle } = await withQueue(['a', 'b']);
      expect(output.isShuffled).toBe(false);
      output.toggleShuffle();
      await settle();
      expect(output.isShuffled).toBe(true);
      output.toggleShuffle();
      await settle();
      expect(output.isShuffled).toBe(false);
    });

    it('clamps volume to 0–1', async () => {
      const { output, settle } = await withQueue(['a']);
      output.setVolume(1.5);
      await settle();
      expect(output.volume).toBe(1);
      output.setVolume(-2);
      await settle();
      expect(output.volume).toBe(0);
    });

    it('restores the pre-mute volume when unmuted', async () => {
      const { output, settle } = await withQueue(['a']);
      output.setVolume(0.4);
      await settle();
      output.toggleMute();
      await settle();
      expect(output.volume).toBe(0);
      output.toggleMute();
      await settle();
      expect(output.volume).toBeCloseTo(0.4, 5);
    });

    it('moves the position on seek', async () => {
      const { output, settle } = await withQueue(['a']);
      output.seek(42);
      await settle();
      expect(output.currentTime).toBeCloseTo(42, 1);
    });
  });
}
