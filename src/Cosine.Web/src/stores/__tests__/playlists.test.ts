import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const getPlaylistsMock = vi.fn();
const createPlaylistMock = vi.fn().mockResolvedValue(undefined);
const deletePlaylistMock = vi.fn().mockResolvedValue(undefined);
const updatePlaylistMock = vi.fn().mockResolvedValue(undefined);
const addTracksMock = vi.fn().mockResolvedValue(undefined);

vi.mock('@/api/playlists', () => ({
  getPlaylists: () => getPlaylistsMock(),
  createPlaylist: (name: string, ids: string[]) => createPlaylistMock(name, ids),
  deletePlaylist: (id: string) => deletePlaylistMock(id),
  updatePlaylist: (id: string, opts: unknown) => updatePlaylistMock(id, opts),
  addTracksToPlaylist: (id: string, ids: string[]) => addTracksMock(id, ids),
}));
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

import { usePlaylistsStore } from '../playlists';

function playlist(id: string, name: string) {
  return { id, name, songCount: 0, duration: 0 };
}

describe('playlists store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    getPlaylistsMock.mockReset().mockResolvedValue([]);
    createPlaylistMock.mockClear().mockResolvedValue(undefined);
    deletePlaylistMock.mockClear().mockResolvedValue(undefined);
    updatePlaylistMock.mockClear().mockResolvedValue(undefined);
    addTracksMock.mockClear().mockResolvedValue(undefined);
  });

  it('loads playlists once and caches them', async () => {
    getPlaylistsMock.mockResolvedValue([playlist('p1', 'One')]);
    const store = usePlaylistsStore();
    await store.load();
    expect(store.items).toHaveLength(1);
    await store.load(); // cached - no second fetch
    expect(getPlaylistsMock).toHaveBeenCalledTimes(1);
  });

  it('creates a playlist then reloads the list', async () => {
    getPlaylistsMock.mockResolvedValue([playlist('p1', 'New')]);
    const store = usePlaylistsStore();
    await store.create('New', ['s1', 's2']);
    expect(createPlaylistMock).toHaveBeenCalledWith('New', ['s1', 's2']);
    expect(store.items).toHaveLength(1);
  });

  it('adds songs to a playlist via addTracksToPlaylist', async () => {
    const store = usePlaylistsStore();
    await store.addSongs('p1', ['s3'], 'My List');
    expect(addTracksMock).toHaveBeenCalledWith('p1', ['s3']);
  });

  it('removes a playlist optimistically from the cached list', async () => {
    getPlaylistsMock.mockResolvedValue([playlist('p1', 'One'), playlist('p2', 'Two')]);
    const store = usePlaylistsStore();
    await store.load();
    await store.remove('p1', 'One');
    expect(deletePlaylistMock).toHaveBeenCalledWith('p1');
    expect(store.items.map((p) => p.id)).toEqual(['p2']);
  });

  it('renames a playlist and updates the cached item', async () => {
    getPlaylistsMock.mockResolvedValue([playlist('p1', 'Old')]);
    const store = usePlaylistsStore();
    await store.load();
    await store.rename('p1', 'Fresh');
    expect(updatePlaylistMock).toHaveBeenCalledWith('p1', { name: 'Fresh' });
    expect(store.items[0].name).toBe('Fresh');
  });
});
