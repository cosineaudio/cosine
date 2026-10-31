import { client } from './client';

/**
 * Star / unstar songs, albums, or artists. The option-bag signature is kept from the
 * Subsonic era (`id` = song, `albumId`, `artistId`); each id routes to its
 * `/api/me/starred/{kind}/{id}` resource.
 */
export async function star(opts: { id?: string; albumId?: string; artistId?: string }): Promise<void> {
  await Promise.all(starCalls(opts).map((path) => client.put<void>(path)));
}

export async function unstar(opts: { id?: string; albumId?: string; artistId?: string }): Promise<void> {
  await Promise.all(starCalls(opts).map((path) => client.delete<void>(path)));
}

function starCalls(opts: { id?: string; albumId?: string; artistId?: string }): string[] {
  const paths: string[] = [];
  if (opts.id) paths.push(`api/me/starred/tracks/${encodeURIComponent(opts.id)}`);
  if (opts.albumId) paths.push(`api/me/starred/albums/${encodeURIComponent(opts.albumId)}`);
  if (opts.artistId) paths.push(`api/me/starred/artists/${encodeURIComponent(opts.artistId)}`);
  return paths;
}

/**
 * Report a play. `submission` keeps its Subsonic-era meaning: true is the deliberate
 * "this counts as a listen" signal, false only refreshes now-playing.
 */
export async function scrobble(id: string, submission = true): Promise<void> {
  await client.post('api/plays', { trackId: id, countPlay: submission });
}

export async function setRating(id: string, rating: number): Promise<void> {
  await client.put(`api/me/ratings/${encodeURIComponent(id)}`, { rating });
}
