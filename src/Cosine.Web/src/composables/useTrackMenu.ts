import { computed, type ComputedRef } from 'vue';
import { useRouter } from 'vue-router';
import { toast } from 'vue-sonner';
import { Play, ListPlus, ListEnd, Disc, User, Heart, Radio } from 'lucide-vue-next';
import { getSimilarSongs2 } from '@/api/browsing';
import type { MenuItem } from '@/components/ui/ContextMenu.vue';
import { usePlaybackStore } from '@/stores/playback';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import { usePlaylistMenu } from '@/composables/usePlaylistMenu';
import type { Track } from '@/api/types';

export interface TrackMenuOptions {
  /**
   * Plays the track. Omitted for a track that is already the one playing - the
   * now-playing bar, the full-screen player, the current queue entry - where an
   * item that restarts it is noise.
   */
  play?: () => void;
  /**
   * Opens an album or artist. Defaults to a plain route push; the full-screen
   * player overrides it so the overlay closes before the page underneath changes.
   */
  navigate?: (name: 'album' | 'artist', id: string) => void;
  /** Extra items appended below the defaults, e.g. "Remove from queue". */
  extra?: () => MenuItem[];
}

/**
 * The one track menu, shared by every surface that shows a track: library rows,
 * queue entries, the now-playing bar and the full-screen player. What you can do
 * with a track does not depend on where you are looking at it - the only things
 * that vary are whether "Play" makes sense, what list it plays within, and how
 * navigating away has to be staged.
 */
export function useTrackMenu(
  track: () => Track | null | undefined,
  opts: TrackMenuOptions = {},
): ComputedRef<MenuItem[]> {
  const router = useRouter();
  const player = usePlaybackStore();
  const favorites = useFavoritesStore();
  const { queueTracks } = usePlayback();
  const { addToPlaylistItem } = usePlaylistMenu();

  const go = opts.navigate ?? ((name: 'album' | 'artist', id: string) => router.push({ name, params: { id } }));

  /**
   * Seed a radio from this song's artist and similar artists.
   *
   * Building the queue is a similarity query across the library, not a lookup, so
   * it can take a moment. The menu closes the instant it is picked, which leaves
   * nowhere on the trigger to put a spinner - so the feedback is a toast that
   * starts as "Starting radio…" and is then replaced in place by the outcome.
   */
  let radioPending = false;

  async function startRadio(t: Track): Promise<void> {
    if (radioPending) return;
    radioPending = true;
    const id = toast.loading('Starting radio…');
    try {
      const tracks = await getSimilarSongs2(t.id, 100);
      if (tracks.length === 0) {
        toast.error('Not enough library data to build a radio yet', { id });
        return;
      }
      player.play(tracks[0], tracks);
      toast.success('Radio started', { id });
    } catch {
      toast.error('Could not start radio', { id });
    } finally {
      radioPending = false;
    }
  }

  return computed<MenuItem[]>(() => {
    const t = track();
    if (!t) return [];
    const isFav = favorites.isSong(t.id);

    return [
      ...(opts.play ? [{ label: 'Play', icon: Play, onSelect: opts.play } as MenuItem] : []),
      { label: 'Play next', icon: ListPlus, onSelect: () => queueTracks([t], 'next') },
      { label: 'Add to queue', icon: ListEnd, onSelect: () => queueTracks([t], 'end') },
      { label: 'Start radio', icon: Radio, onSelect: () => startRadio(t) },
      { separator: true, label: '' },
      {
        label: isFav ? 'Remove favorite' : 'Favorite',
        icon: Heart,
        onSelect: () => favorites.toggle('song', t.id, t.title),
      },
      addToPlaylistItem(() => [t.id]),
      ...(t.albumId
        ? [{ label: 'Go to album', icon: Disc, onSelect: () => go('album', t.albumId!) } as MenuItem]
        : []),
      ...(t.artistId
        ? [{ label: 'Go to artist', icon: User, onSelect: () => go('artist', t.artistId!) } as MenuItem]
        : []),
      ...(opts.extra?.() ?? []),
    ];
  });
}
