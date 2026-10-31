import { describe, it, expect, vi, beforeEach } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const starMock = vi.fn().mockResolvedValue(undefined);
const unstarMock = vi.fn().mockResolvedValue(undefined);
const getStarred2Mock = vi.fn();

vi.mock('@/api/annotation', () => ({
  star: (arg: unknown) => starMock(arg),
  unstar: (arg: unknown) => unstarMock(arg),
}));
vi.mock('@/api/lists', () => ({
  getStarred2: () => getStarred2Mock(),
}));
vi.mock('vue-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

import { useFavoritesStore } from '../favorites';

describe('favorites store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    starMock.mockClear().mockResolvedValue(undefined);
    unstarMock.mockClear().mockResolvedValue(undefined);
    getStarred2Mock.mockReset();
  });

  it('optimistically stars a song and calls the API', async () => {
    const fav = useFavoritesStore();
    const p = fav.toggle('song', 's1', 'Song');
    // optimistic: reflected before the request resolves
    expect(fav.isSong('s1')).toBe(true);
    await p;
    expect(starMock).toHaveBeenCalledWith({ id: 's1' });
  });

  it('unstars a song that is already starred', async () => {
    const fav = useFavoritesStore();
    await fav.toggle('song', 's1');
    expect(fav.isSong('s1')).toBe(true);
    await fav.toggle('song', 's1');
    expect(fav.isSong('s1')).toBe(false);
    expect(unstarMock).toHaveBeenCalledWith({ id: 's1' });
  });

  it('rolls back the optimistic change when the request fails', async () => {
    starMock.mockRejectedValueOnce(new Error('network'));
    const fav = useFavoritesStore();
    await fav.toggle('album', 'al1', 'Album');
    // rolled back to not-favorited
    expect(fav.isAlbum('al1')).toBe(false);
  });

  it('uses the correct id argument per kind', async () => {
    const fav = useFavoritesStore();
    await fav.toggle('artist', 'ar1');
    expect(starMock).toHaveBeenCalledWith({ artistId: 'ar1' });
  });

  it('seeds starred state from objects carrying a starred timestamp', () => {
    const fav = useFavoritesStore();
    fav.seed({ song: [{ id: 's9', starred: '2026-01-01' } as never], album: [{ id: 'al9' } as never] });
    expect(fav.isSong('s9')).toBe(true);
    expect(fav.isAlbum('al9')).toBe(false); // no starred timestamp → not seeded
  });
});
