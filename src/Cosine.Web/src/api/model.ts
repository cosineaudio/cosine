/**
 * Types for cosine's own web API (`/api/*`) - mirrors of the server's WebApi DTOs.
 *
 * These are the clean shapes: GUID strings for ids, `durationMs` in milliseconds,
 * `artworkId` for `/api/artwork/{id}`, structured `{ id, name }` refs. The legacy
 * Subsonic-flavoured types in `types.ts` are being migrated away module by module;
 * new code should consume these.
 */

export interface ArtistRef {
  id: string;
  name: string;
}

export interface AlbumRef {
  id: string;
  name: string;
}

export interface TrackDto {
  id: string;
  title: string;
  /** The primary artist. Guests live in `artists`. */
  artist?: ArtistRef;
  /** The full credit in order, primary first. Always at least one entry. */
  artists: ArtistRef[];
  /** The credit as one renderable string - "Pouya & Fat Nick". */
  displayArtist: string;
  album?: AlbumRef;
  trackNumber?: number;
  discNumber?: number;
  year?: number;
  durationMs?: number;
  bitRate?: number;
  fileSize?: number;
  contentType?: string;
  /** File extension without the dot - "flac", "mp3". */
  format?: string;
  /** What `/api/artwork/{id}` takes for this track (its album's id). */
  artworkId: string;
  musicBrainzId?: string;
  /** The caller's own 1–5 rating; absent when unrated. */
  userRating?: number;
}

export interface AlbumSummaryDto {
  id: string;
  name: string;
  artist?: ArtistRef;
  year?: number;
  trackCount: number;
  durationMs: number;
  createdAt: string;
  artworkId: string;
  musicBrainzId?: string;
  userRating?: number;
}

export interface AlbumDetailDto extends AlbumSummaryDto {
  tracks: TrackDto[];
}

export interface ArtistSummaryDto {
  id: string;
  name: string;
  sortName: string;
  albumCount: number;
  artworkId: string;
  musicBrainzId?: string;
}

export interface ArtistDetailDto extends ArtistSummaryDto {
  albums: AlbumSummaryDto[];
}

export interface ArtistIndexGroupDto {
  name: string;
  artists: ArtistSummaryDto[];
}

export interface ArtistIndexResponse {
  ignoredArticles: string;
  groups: ArtistIndexGroupDto[];
}

export interface ArtistInfoDto {
  biography?: string;
  musicBrainzId?: string;
  similarArtists: ArtistSummaryDto[];
}

export interface GenreDto {
  name: string;
  trackCount: number;
  albumCount: number;
}

export interface SearchResponse {
  artists: ArtistSummaryDto[];
  albums: AlbumSummaryDto[];
  tracks: TrackDto[];
}

export interface NowPlayingEntryDto {
  track: TrackDto;
  username: string;
  minutesAgo: number;
  positionMs?: number;
  state: string;
}

export interface TranscodeFormatDto {
  name: string;
  extension: string;
  bitRates: number[];
}
