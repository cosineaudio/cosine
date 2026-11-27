import { ref } from 'vue';
import { toast } from 'vue-sonner';
import { usePlaybackStore } from '@/stores/playback';
import type { Track } from '@/api/types';

/**
 * Starting a radio, from anywhere. A radio is an auto-play station: the first track plays now,
 * and auto-play carries on from a station anchored on that track and on wherever it was started
 * from - the album, playlist, artist or list it sat in. So the first thing heard is always the
 * thing that was picked, and "+" / "−" steer the radio exactly as they steer any auto-play queue.
 */
export function useRadio() {
  const player = usePlaybackStore();
  /** True while a context is being fetched (an artist's catalogue); song radio is instant. */
  const pending = ref(false);

  /** Library tracks only - internet radio has no track row to build a station on. */
  const playable = (tracks: Track[]) => tracks.filter((t) => !t.streamUrl);

  /** Radio from one track: it plays first, and `context` is what else the station is about. */
  async function fromTrack(track: Track, context: Track[] = []): Promise<void> {
    if (track.streamUrl) {
      toast.error('Internet radio cannot seed a radio');
      return;
    }
    try {
      await player.startStation(track, playable(context));
      toast.success('Radio started');
    } catch {
      toast.error('Could not start radio');
    }
  }

  /**
   * Radio from a whole context - an album, playlist or artist. A random track from it plays
   * first, and the station is anchored on all of it.
   *
   * @param tracks The context, or a loader for it when it is not already on the page.
   * @param name What it is called, for the toast.
   */
  async function fromTracks(
    tracks: Track[] | (() => Promise<Track[]>),
    name?: string,
  ): Promise<void> {
    if (pending.value) return;
    pending.value = true;
    try {
      const context = playable(typeof tracks === 'function' ? await tracks() : tracks);
      const first = context[Math.floor(Math.random() * context.length)];
      if (!first) {
        toast.error('Nothing here to start a radio from');
        return;
      }
      await player.startStation(first, context);
      toast.success(name ? `Radio started from ${name}` : 'Radio started');
    } catch {
      toast.error('Could not start radio');
    } finally {
      pending.value = false;
    }
  }

  return { pending, fromTrack, fromTracks };
}
