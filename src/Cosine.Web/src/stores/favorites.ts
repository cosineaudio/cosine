import { defineStore } from 'pinia';
import { ref } from 'vue';
import { toast } from 'vue-sonner';
import { star, unstar } from '@/api/annotation';
import { getStarred2 } from '@/api/lists';
import type { Album, Artist, Track } from '@/api/types';

/**
 * Tracks which song/album/artist ids the user has starred, so a FavoriteButton
 * anywhere in the app reflects and toggles the same state. Optimistic - the UI
 * updates immediately and rolls back if the request fails.
 */
export const useFavoritesStore = defineStore('favorites', () => {
  const songs = ref<Set<string>>(new Set());
  const albums = ref<Set<string>>(new Set());
  const artists = ref<Set<string>>(new Set());
  const loaded = ref(false);

  async function ensureLoaded(force = false): Promise<void> {
    if (loaded.value && !force) return;
    try {
      const starred = await getStarred2();
      songs.value = new Set(starred.song.map((s) => s.id));
      albums.value = new Set(starred.album.map((a) => a.id));
      artists.value = new Set(starred.artist.map((a) => a.id));
      loaded.value = true;
    } catch {
      /* leave empty; buttons still work optimistically */
    }
  }

  const isSong = (id: string) => songs.value.has(id);
  const isAlbum = (id: string) => albums.value.has(id);
  const isArtist = (id: string) => artists.value.has(id);

  async function toggle(
    kind: 'song' | 'album' | 'artist',
    id: string,
    name?: string,
  ): Promise<void> {
    const set = { song: songs, album: albums, artist: artists }[kind];
    const wasStarred = set.value.has(id);
    // optimistic
    const next = new Set(set.value);
    if (wasStarred) next.delete(id);
    else next.add(id);
    set.value = next;

    const idArg =
      kind === 'song' ? { id } : kind === 'album' ? { albumId: id } : { artistId: id };
    try {
      await (wasStarred ? unstar(idArg) : star(idArg));
      if (!wasStarred) {
        toast.success(`Added ${name ? `“${name}”` : 'to favorites'}`);
      }
    } catch {
      // rollback
      const rolled = new Set(set.value);
      if (wasStarred) rolled.add(id);
      else rolled.delete(id);
      set.value = rolled;
      toast.error('Could not update favorite');
    }
  }

  /** Seed state from objects that already carry a `starred` timestamp. */
  function seed(items: { song?: Track[]; album?: Album[]; artist?: Artist[] }): void {
    for (const s of items.song ?? []) if (s.starred) songs.value.add(s.id);
    for (const a of items.album ?? []) if (a.starred) albums.value.add(a.id);
    for (const a of items.artist ?? []) if (a.starred) artists.value.add(a.id);
  }

  return { songs, albums, artists, loaded, ensureLoaded, isSong, isAlbum, isArtist, toggle, seed };
});
