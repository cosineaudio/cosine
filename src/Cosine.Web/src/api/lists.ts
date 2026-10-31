import { client } from './client';
import { dtoToAlbum, dtoToArtist, dtoToTrack } from './adapt';
import type { Album, Artist, Track } from './types';
import type { AlbumSummaryDto, NowPlayingEntryDto, TrackDto } from './model';

export type AlbumListType =
  | 'newest'
  | 'recent'
  | 'frequent'
  | 'random'
  | 'alphabeticalByName'
  | 'alphabeticalByArtist'
  | 'byYear'
  | 'byGenre'
  | 'starred';

export interface AlbumListOptions {
  size?: number;
  offset?: number;
  fromYear?: number;
  toYear?: number;
  genre?: string;
}

/** The Subsonic list types the pages still pass, translated onto /api/albums sorts. */
const SORT_BY_TYPE: Record<AlbumListType, string | null> = {
  newest: 'newest',
  recent: 'recent',
  frequent: 'frequent',
  random: 'random',
  alphabeticalByName: 'name',
  alphabeticalByArtist: 'artist',
  starred: 'starred',
  // byYear/byGenre are filters on /api/albums, not sorts.
  byYear: null,
  byGenre: null,
};

export async function getAlbumList2(
  type: AlbumListType,
  opts: AlbumListOptions = {},
): Promise<Album[]> {
  const params = new URLSearchParams();
  const sort = SORT_BY_TYPE[type];
  if (sort) params.set('sort', sort);
  if (opts.size != null) params.set('size', String(opts.size));
  if (opts.offset != null) params.set('offset', String(opts.offset));
  if (type === 'byYear') {
    if (opts.fromYear != null) params.set('fromYear', String(opts.fromYear));
    if (opts.toYear != null) params.set('toYear', String(opts.toYear));
  }
  if (type === 'byGenre' && opts.genre) params.set('genre', opts.genre);
  const data = await client.get<{ albums: AlbumSummaryDto[] }>(`api/albums?${params}`);
  return data.albums.map(dtoToAlbum);
}

export async function getRandomSongs(opts: { size?: number } = {}): Promise<Track[]> {
  const params = new URLSearchParams();
  if (opts.size != null) params.set('size', String(opts.size));
  const data = await client.get<{ tracks: TrackDto[] }>(`api/tracks/random?${params}`);
  return data.tracks.map(dtoToTrack);
}

export async function getSongsByGenre(genre: string, opts: { count?: number; offset?: number } = {}): Promise<Track[]> {
  const params = new URLSearchParams();
  if (opts.count != null) params.set('count', String(opts.count));
  if (opts.offset != null) params.set('offset', String(opts.offset));
  const data = await client.get<{ tracks: TrackDto[] }>(
    `api/genres/${encodeURIComponent(genre)}/tracks?${params}`);
  return data.tracks.map(dtoToTrack);
}

export interface Starred {
  artist: Artist[];
  album: Album[];
  song: Track[];
}

export async function getStarred2(): Promise<Starred> {
  const data = await client.get<{
    artists: { id: string; name: string; sortName: string; albumCount: number; artworkId: string }[];
    albums: AlbumSummaryDto[];
    tracks: TrackDto[];
  }>('api/me/starred');
  return {
    artist: data.artists.map(dtoToArtist),
    album: data.albums.map(dtoToAlbum),
    song: data.tracks.map(dtoToTrack),
  };
}

export interface NowPlayingEntry extends Track {
  username?: string;
  minutesAgo?: number;
  playerName?: string;
}

export async function getNowPlaying(): Promise<NowPlayingEntry[]> {
  const data = await client.get<{ entries: NowPlayingEntryDto[] }>('api/now-playing');
  return data.entries.map((e) => ({
    ...dtoToTrack(e.track),
    username: e.username,
    minutesAgo: e.minutesAgo,
  }));
}
