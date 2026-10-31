import { client } from './client';
import { dtoToAlbumDetail, dtoToArtist, dtoToAlbum, dtoToTrack } from './adapt';
import type {
  ArtistsResponse,
  ArtistDetail,
  AlbumDetail,
  Track,
  SearchResults,
  ArtistInfo,
} from './types';
import type {
  ArtistIndexResponse,
  ArtistDetailDto,
  ArtistInfoDto,
  AlbumDetailDto,
  TrackDto,
  SearchResponse,
} from './model';

export async function getArtists(): Promise<ArtistsResponse> {
  const data = await client.get<ArtistIndexResponse>('api/artists');
  return {
    artists: {
      index: data.groups.map((g) => ({ name: g.name, artist: g.artists.map(dtoToArtist) })),
    },
  };
}

export async function getArtist(id: string): Promise<ArtistDetail> {
  const dto = await client.get<ArtistDetailDto>(`api/artists/${encodeURIComponent(id)}`);
  return { ...dtoToArtist(dto), albumCount: dto.albumCount, album: dto.albums.map(dtoToAlbum) };
}

export async function getAlbum(id: string): Promise<AlbumDetail> {
  const dto = await client.get<AlbumDetailDto>(`api/albums/${encodeURIComponent(id)}`);
  return dtoToAlbumDetail(dto);
}

export async function getTrack(id: string): Promise<Track> {
  const dto = await client.get<TrackDto>(`api/tracks/${encodeURIComponent(id)}`);
  return dtoToTrack(dto);
}

export async function getGenres(): Promise<{ genres: { genre: { value: string; songCount: number; albumCount: number }[] } }> {
  const data = await client.get<{ genres: { name: string; trackCount: number; albumCount: number }[] }>('api/genres');
  return {
    genres: {
      genre: data.genres.map((g) => ({
        value: g.name,
        songCount: g.trackCount,
        albumCount: g.albumCount,
      })),
    },
  };
}

export async function search3(query: string, opts?: { artistCount?: number; albumCount?: number; songCount?: number }): Promise<{ searchResult3: SearchResults }> {
  const params = new URLSearchParams({ q: query });
  if (opts?.artistCount) params.set('artistCount', String(opts.artistCount));
  if (opts?.albumCount) params.set('albumCount', String(opts.albumCount));
  if (opts?.songCount) params.set('trackCount', String(opts.songCount));
  const data = await client.get<SearchResponse>(`api/search?${params}`);
  return {
    searchResult3: {
      artist: data.artists.map(dtoToArtist),
      album: data.albums.map(dtoToAlbum),
      song: data.tracks.map(dtoToTrack),
    },
  };
}

/** Artist bio and similar artists. `similarArtist` is always an array for callers. */
export async function getArtistInfo2(
  id: string,
  opts: { count?: number } = {},
): Promise<ArtistInfo> {
  const params = new URLSearchParams();
  if (opts.count != null) params.set('count', String(opts.count));
  const suffix = params.size > 0 ? `?${params}` : '';
  const info = await client.get<ArtistInfoDto>(
    `api/artists/${encodeURIComponent(id)}/info${suffix}`);
  return {
    biography: info.biography,
    musicBrainzId: info.musicBrainzId,
    similarArtist: info.similarArtists.map(dtoToArtist),
  };
}

/** An artist's most-played tracks, keyed on the artist id. */
export async function getTopSongs(artistId: string, count = 10): Promise<Track[]> {
  const data = await client.get<{ tracks: TrackDto[] }>(
    `api/artists/${encodeURIComponent(artistId)}/top-songs?count=${count}`);
  return data.tracks.map(dtoToTrack);
}

/** Tracks similar to a given song/album/artist id. */
export async function getSimilarSongs2(id: string, count = 50): Promise<Track[]> {
  const params = new URLSearchParams({ seedId: id, count: String(count) });
  const data = await client.get<{ tracks: TrackDto[] }>(`api/similar-tracks?${params}`);
  return data.tracks.map(dtoToTrack);
}

export function streamUrl(id: string): string {
  return `/api/stream/${encodeURIComponent(id)}`;
}

export function coverArtUrl(id: string, size?: number): string {
  const suffix = size ? `?size=${size}` : '';
  return `/api/artwork/${encodeURIComponent(id)}${suffix}`;
}
