import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { MenuItem } from '@/components/ui/ContextMenu.vue';
import type { Track } from '@/api/types';

const pushMock = vi.fn();
const getPlaylistsMock = vi.fn();
const updatePlaylistMock = vi.fn().mockResolvedValue(undefined);
const addTracksMock = vi.fn().mockResolvedValue(undefined);
const addToQueueMock = vi.fn();

vi.mock('vue-router', () => ({ useRouter: () => ({ push: pushMock }) }));
vi.mock('vue-sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), loading: vi.fn(() => 'toast-id') },
}));
vi.mock('@/api/browsing', () => ({ getSimilarSongs2: vi.fn().mockResolvedValue([]) }));
vi.mock('@/api/playlists', () => ({
  getPlaylists: () => getPlaylistsMock(),
  createPlaylist: vi.fn().mockResolvedValue(undefined),
  deletePlaylist: vi.fn().mockResolvedValue(undefined),
  updatePlaylist: (id: string, opts: unknown) => updatePlaylistMock(id, opts),
  addTracksToPlaylist: (id: string, ids: string[]) => addTracksMock(id, ids),
  isSystemPlaylist: (pl: { isSystem?: boolean; name: string }) =>
    pl.isSystem === true || pl.name === 'Release Radar',
}));
vi.mock('@/api/annotation', () => ({
  star: vi.fn().mockResolvedValue(undefined),
  unstar: vi.fn().mockResolvedValue(undefined),
}));
vi.mock('@/api/lists', () => ({ getStarred2: vi.fn().mockResolvedValue({}) }));
vi.mock('@/stores/playback', () => ({
  usePlaybackStore: () => ({ addToQueue: addToQueueMock, play: vi.fn() }),
}));

import { useTrackMenu } from '../useTrackMenu';
import { usePlaylistsStore } from '@/stores/playlists';

const TRACK = { id: 's1', title: 'Song', albumId: 'al1', artistId: 'ar1' } as Track;

/** Menu labels, with the separators dropped - they carry no label to assert on. */
function labels(items: MenuItem[]): string[] {
  return items.filter((i) => !i.separator).map((i) => i.label);
}

function find(items: MenuItem[], label: string): MenuItem {
  const item = items.find((i) => i.label === label);
  if (!item) throw new Error(`no menu item "${label}" in [${labels(items).join(', ')}]`);
  return item;
}

describe('useTrackMenu', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    pushMock.mockClear();
    addToQueueMock.mockClear();
    updatePlaylistMock.mockClear().mockResolvedValue(undefined);
    addTracksMock.mockClear().mockResolvedValue(undefined);
    getPlaylistsMock.mockReset().mockResolvedValue([
      { id: 'p1', name: 'Goon Songs', songCount: 1, duration: 161 },
      { id: 'p2', name: 'Release Radar', songCount: 0, duration: 0, isSystem: true },
    ]);
  });

  it('omits Play for a track that is already playing', () => {
    const menu = useTrackMenu(() => TRACK);
    expect(labels(menu.value)[0]).toBe('Play next');
  });

  it('leads with Play when the caller supplies one', () => {
    const play = vi.fn();
    const menu = useTrackMenu(() => TRACK, { play });
    find(menu.value, 'Play').onSelect?.();
    expect(play).toHaveBeenCalled();
  });

  it('is empty with no track, so a bar with nothing playing has nothing to offer', () => {
    const menu = useTrackMenu(() => null);
    expect(menu.value).toEqual([]);
  });

  it('offers every user playlist plus New playlist…, and hides system playlists', async () => {
    const playlists = usePlaylistsStore();
    await playlists.load();

    const menu = useTrackMenu(() => TRACK);
    const children = find(menu.value, 'Add to playlist').children ?? [];

    expect(labels(children)).toEqual(['New playlist…', 'Goon Songs']);
  });

  it('adds the track to the chosen playlist', async () => {
    const playlists = usePlaylistsStore();
    await playlists.load();

    const menu = useTrackMenu(() => TRACK);
    const children = find(menu.value, 'Add to playlist').children ?? [];
    await find(children, 'Goon Songs').onSelect?.();

    expect(addTracksMock).toHaveBeenCalledWith('p1', ['s1']);
  });

  it('queues the track at either end', () => {
    const menu = useTrackMenu(() => TRACK);
    find(menu.value, 'Play next').onSelect?.();
    find(menu.value, 'Add to queue').onSelect?.();

    expect(addToQueueMock).toHaveBeenNthCalledWith(1, [TRACK], 'next');
    expect(addToQueueMock).toHaveBeenNthCalledWith(2, [TRACK], 'end');
  });

  it('routes to the album and artist by default', () => {
    const menu = useTrackMenu(() => TRACK);
    find(menu.value, 'Go to album').onSelect?.();
    expect(pushMock).toHaveBeenCalledWith({ name: 'album', params: { id: 'al1' } });
  });

  it('hands navigation to the caller when it has an overlay to close first', () => {
    const navigate = vi.fn();
    const menu = useTrackMenu(() => TRACK, { navigate });
    find(menu.value, 'Go to artist').onSelect?.();

    expect(navigate).toHaveBeenCalledWith('artist', 'ar1');
    expect(pushMock).not.toHaveBeenCalled();
  });

  it('drops navigation items a track cannot support', () => {
    const menu = useTrackMenu(() => ({ id: 's2', title: 'Orphan' }) as Track);
    expect(labels(menu.value)).not.toContain('Go to album');
    expect(labels(menu.value)).not.toContain('Go to artist');
  });

  it('appends the caller’s extra items last', () => {
    const menu = useTrackMenu(() => TRACK, {
      extra: () => [{ label: 'Remove from queue', danger: true }],
    });
    expect(labels(menu.value).at(-1)).toBe('Remove from queue');
  });
});
