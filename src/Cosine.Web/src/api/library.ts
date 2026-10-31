import { client } from './client';
import { dtoToTrack } from './adapt';
import type { Track } from './types';
import type { TrackDto } from './model';

/**
 * Library queries the OpenSubsonic spec has no call for. They live on cosine's own web API -
 * inventing a `/rest` method name would put it in the namespace the spec allocates from, so
 * these have always been kept off the Subsonic surface.
 */

/**
 * Every song by an artist, in album order - their own albums plus anything credited to them
 * elsewhere. One request, where the Subsonic route is one `getAlbum` per album.
 */
export async function getArtistSongs(artistId: string): Promise<Track[]> {
  const data = await client.get<{ tracks: TrackDto[] }>(
    `api/artists/${encodeURIComponent(artistId)}/songs`,
  );
  return data.tracks.map(dtoToTrack);
}

export interface AutoPlayOptions {
  /** Recently played track ids, most recent first. Their artists seed the blend. */
  seedTrackIds: string[];
  /** What the caller already holds - its queue and what it has just played. */
  excludeTrackIds?: string[];
  /** Batch size. The server clamps it. */
  count?: number;
}

/**
 * The next stretch of an endless queue, seeded from what has just been played and filtered
 * against what the caller already has. POST because the exclusion list is the caller's whole
 * queue, which does not belong in a URL.
 */
export async function getAutoPlayQueue(opts: AutoPlayOptions): Promise<Track[]> {
  const data = await client.post<{ tracks: TrackDto[] }>('api/autoplay', {
    seedTrackIds: opts.seedTrackIds,
    excludeTrackIds: opts.excludeTrackIds ?? [],
    count: opts.count ?? 20,
  });
  return data.tracks.map(dtoToTrack);
}
