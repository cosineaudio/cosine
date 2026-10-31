import type { Artist, Album, Track } from './types';
import type {
  ArtistSummaryDto,
  AlbumSummaryDto,
  AlbumDetailDto,
  TrackDto,
} from './model';
import type { AlbumDetail } from './types';

/**
 * Adapters from the web API's clean DTOs back onto the legacy Subsonic-shaped types the
 * components still render (`queueItemToTrack` in playback.ts is the precedent). They exist so
 * each api module can migrate to `/api/*` without touching a single component; the final
 * migration step flips the components to `model.ts` and deletes this file.
 */

export function dtoToTrack(dto: TrackDto): Track {
  return {
    id: dto.id,
    parent: dto.album?.id,
    isDir: false,
    title: dto.title,
    album: dto.album?.name,
    albumId: dto.album?.id,
    // The legacy `artist` field carried the full display credit, not the primary artist's
    // canonical name - SongMapping did the same on /rest.
    artist: dto.displayArtist || dto.artist?.name,
    artistId: dto.artist?.id,
    artists: dto.artists,
    displayArtist: dto.displayArtist,
    track: dto.trackNumber,
    discNumber: dto.discNumber,
    year: dto.year,
    coverArt: dto.artworkId,
    size: dto.fileSize,
    contentType: dto.contentType,
    suffix: dto.format,
    duration: dto.durationMs != null ? Math.round(dto.durationMs / 1000) : undefined,
    bitRate: dto.bitRate,
  };
}

export function dtoToAlbum(dto: AlbumSummaryDto): Album {
  return {
    id: dto.id,
    name: dto.name,
    artist: dto.artist?.name,
    artistId: dto.artist?.id,
    coverArt: dto.artworkId,
    songCount: dto.trackCount,
    duration: Math.round(dto.durationMs / 1000),
    created: dto.createdAt,
    year: dto.year,
  };
}

export function dtoToAlbumDetail(dto: AlbumDetailDto): AlbumDetail {
  return { ...dtoToAlbum(dto), song: dto.tracks.map(dtoToTrack) };
}

export function dtoToArtist(dto: ArtistSummaryDto): Artist {
  return {
    id: dto.id,
    name: dto.name,
    coverArt: dto.artworkId,
    albumCount: dto.albumCount,
    sortName: dto.sortName,
    musicBrainzId: dto.musicBrainzId,
  };
}
