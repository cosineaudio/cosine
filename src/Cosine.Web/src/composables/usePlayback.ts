import { toast } from 'vue-sonner';
import { usePlaybackStore } from '@/stores/playback';
import type { QueueSource } from '@/stores/autoPlay';
import { getAlbum } from '@/api/browsing';
import { shuffled } from '@/lib/shuffle';
import type { Track } from '@/api/types';

/**
 * High-level playback actions shared by cards, rows, and detail pages.
 * Keeps "play this album" / "add to queue" logic in one place instead of
 * re-fetching and wiring the player store at every call site.
 */
export function usePlayback() {
  const player = usePlaybackStore();

  /**
   * @param source Where the list came from, when the caller is showing only part of it - auto-play
   * reads on through it rather than blending as soon as the visible rows run out.
   */
  function playTracks(tracks: Track[], startIndex = 0, source: QueueSource | null = null): void {
    if (tracks.length === 0) return;
    player.play(tracks[startIndex], tracks, source);
  }

  async function albumTracks(albumId: string): Promise<Track[]> {
    const album = await getAlbum(albumId);
    return album.song ?? [];
  }

  async function playAlbum(albumId: string, shuffle = false): Promise<void> {
    try {
      let tracks = await albumTracks(albumId);
      if (tracks.length === 0) {
        toast.error('This album has no playable tracks');
        return;
      }
      if (shuffle) tracks = shuffled(tracks);
      playTracks(tracks, 0);
    } catch {
      toast.error('Could not play album');
    }
  }

  async function queueAlbum(albumId: string, position: 'next' | 'end'): Promise<void> {
    try {
      const tracks = await albumTracks(albumId);
      if (tracks.length === 0) return;
      player.addToQueue(tracks, position);
      toast.success(position === 'next' ? 'Playing next' : 'Added to queue');
    } catch {
      toast.error('Could not add to queue');
    }
  }

  function queueTracks(tracks: Track[], position: 'next' | 'end'): void {
    if (tracks.length === 0) return;
    player.addToQueue(tracks, position);
    toast.success(position === 'next' ? 'Playing next' : 'Added to queue');
  }

  return { player, playTracks, playAlbum, queueAlbum, queueTracks, albumTracks };
}
