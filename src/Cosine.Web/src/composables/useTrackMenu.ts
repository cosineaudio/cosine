import { computed, type ComputedRef } from 'vue';
import { useRouter } from 'vue-router';
import { Play, ListPlus, ListEnd, Disc, User, Heart, Radio } from 'lucide-vue-next';
import type { MenuItem } from '@/components/ui/ContextMenu.vue';
import { useFavoritesStore } from '@/stores/favorites';
import { usePlayback } from '@/composables/usePlayback';
import { usePlaylistMenu } from '@/composables/usePlaylistMenu';
import { useRadio } from '@/composables/useRadio';
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
  /**
   * The list the track is shown in - an album, playlist, genre page. "Start radio" anchors the
   * station on it as well as on the track, so the radio stays near where it was started.
   */
  context?: () => Track[];
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
  const favorites = useFavoritesStore();
  const { queueTracks } = usePlayback();
  const { addToPlaylistItem } = usePlaylistMenu();

  const go = opts.navigate ?? ((name: 'album' | 'artist', id: string) => router.push({ name, params: { id } }));

  const radio = useRadio();

  return computed<MenuItem[]>(() => {
    const t = track();
    if (!t) return [];
    const isFav = favorites.isSong(t.id);

    return [
      ...(opts.play ? [{ label: 'Play', icon: Play, onSelect: opts.play } as MenuItem] : []),
      { label: 'Play next', icon: ListPlus, onSelect: () => queueTracks([t], 'next') },
      { label: 'Add to queue', icon: ListEnd, onSelect: () => queueTracks([t], 'end') },
      { label: 'Start radio', icon: Radio, onSelect: () => radio.fromTrack(t, opts.context?.() ?? []) },
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
