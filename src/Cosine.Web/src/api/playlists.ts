import { client } from './client';
import { dtoToTrack } from './adapt';
import type { Track } from './types';
import type { TrackDto } from './model';

export interface Playlist {
  id: string;
  name: string;
  comment?: string;
  owner?: string;
  public?: boolean;
  songCount: number;
  duration: number;
  created?: string;
  coverArt?: string;
  /** True for job-managed system playlists (Release Radar) - not user-editable. */
  isSystem?: boolean;
}

/** System playlists are matched by the server flag, with a name fallback. */
export function isSystemPlaylist(pl: Pick<Playlist, 'isSystem' | 'name'>): boolean {
  return pl.isSystem === true || pl.name === 'Release Radar';
}

/** One playlist entry: the entry's own stable id plus the track it holds. */
export interface PlaylistEntry {
  entryId: string;
  track: Track;
}

export interface PlaylistDetail extends Playlist {
  /** The tracks in order - what list renderers and "play playlist" consume. */
  entry: Track[];
  /** The same rows with their entry ids - what removal names. */
  entries: PlaylistEntry[];
}

interface PlaylistSummaryDto {
  id: string;
  name: string;
  comment?: string;
  owner?: string;
  isPublic: boolean;
  isSystem: boolean;
  trackCount: number;
  durationMs: number;
  createdAt: string;
}

interface PlaylistDetailDto extends PlaylistSummaryDto {
  entries: { entryId: string; track: TrackDto }[];
}

function dtoToPlaylist(dto: PlaylistSummaryDto): Playlist {
  return {
    id: dto.id,
    name: dto.name,
    comment: dto.comment,
    owner: dto.owner,
    public: dto.isPublic,
    isSystem: dto.isSystem,
    songCount: dto.trackCount,
    duration: Math.round(dto.durationMs / 1000),
    created: dto.createdAt,
  };
}

function dtoToPlaylistDetail(dto: PlaylistDetailDto): PlaylistDetail {
  const entries = dto.entries.map((e) => ({ entryId: e.entryId, track: dtoToTrack(e.track) }));
  return { ...dtoToPlaylist(dto), entries, entry: entries.map((e) => e.track) };
}

export async function getPlaylists(): Promise<Playlist[]> {
  const data = await client.get<{ playlists: PlaylistSummaryDto[] }>('api/playlists');
  return data.playlists.map(dtoToPlaylist);
}

export async function getPlaylist(id: string): Promise<PlaylistDetail> {
  const dto = await client.get<PlaylistDetailDto>(`api/playlists/${encodeURIComponent(id)}`);
  return dtoToPlaylistDetail(dto);
}

export async function createPlaylist(name: string, songIds: string[] = []): Promise<void> {
  await client.post('api/playlists', { name, trackIds: songIds });
}

export interface UpdatePlaylistOptions {
  name?: string;
  comment?: string;
  isPublic?: boolean;
}

/** Metadata only - track changes go through add/remove/replace below. */
export async function updatePlaylist(id: string, opts: UpdatePlaylistOptions): Promise<void> {
  await client.patch(`api/playlists/${encodeURIComponent(id)}`, opts);
}

export async function addTracksToPlaylist(id: string, trackIds: string[]): Promise<void> {
  await client.post(`api/playlists/${encodeURIComponent(id)}/tracks`, { trackIds });
}

/** Removes one entry by its id - indexes cannot drift, entry ids cannot either. */
export async function removePlaylistEntry(id: string, entryId: string): Promise<void> {
  await client.delete(
    `api/playlists/${encodeURIComponent(id)}/tracks/${encodeURIComponent(entryId)}`);
}

/**
 * Replaces the contents in order - the reorder primitive. Two calls (clear, then add)
 * because a move primitive doesn't exist server-side, same as the Subsonic surface.
 */
export async function replacePlaylistTracks(id: string, trackIds: string[]): Promise<void> {
  await client.delete(`api/playlists/${encodeURIComponent(id)}/tracks`);
  if (trackIds.length > 0)
    await client.post(`api/playlists/${encodeURIComponent(id)}/tracks`, { trackIds });
}

export async function deletePlaylist(id: string): Promise<void> {
  await client.delete(`api/playlists/${encodeURIComponent(id)}`);
}
