import { defineStore } from 'pinia';
import { ref } from 'vue';
import { toast } from 'vue-sonner';
import {
  getPlaylists,
  createPlaylist,
  deletePlaylist,
  updatePlaylist,
  addTracksToPlaylist,
  type Playlist,
} from '@/api/playlists';

export const usePlaylistsStore = defineStore('playlists', () => {
  const items = ref<Playlist[]>([]);
  const loaded = ref(false);
  const loading = ref(false);

  async function load(force = false): Promise<void> {
    if (loaded.value && !force) return;
    if (loading.value) return; // an in-flight load is already fetching
    loading.value = true;
    try {
      items.value = await getPlaylists();
      loaded.value = true;
    } catch {
      /* ignore */
    } finally {
      loading.value = false;
    }
  }

  async function create(name: string, songIds: string[] = []): Promise<void> {
    try {
      await createPlaylist(name, songIds);
      await load(true);
      toast.success(`Created “${name}”`);
    } catch {
      toast.error('Could not create playlist');
    }
  }

  async function remove(id: string, name?: string): Promise<void> {
    try {
      await deletePlaylist(id);
      items.value = items.value.filter((p) => p.id !== id);
      toast.success(`Deleted ${name ? `“${name}”` : 'playlist'}`);
    } catch {
      toast.error('Could not delete playlist');
    }
  }

  async function rename(id: string, name: string): Promise<void> {
    try {
      await updatePlaylist(id, { name });
      const pl = items.value.find((p) => p.id === id);
      if (pl) pl.name = name;
    } catch {
      toast.error('Could not rename playlist');
    }
  }

  async function addSongs(id: string, songIds: string[], name?: string): Promise<void> {
    try {
      await addTracksToPlaylist(id, songIds);
      await load(true);
      toast.success(`Added to ${name ? `“${name}”` : 'playlist'}`);
    } catch {
      toast.error('Could not add to playlist');
    }
  }

  return { items, loaded, loading, load, create, remove, rename, addSongs };
});
