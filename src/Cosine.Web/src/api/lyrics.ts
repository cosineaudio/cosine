import { client } from './client';

/** A single lyric line. `start` is a millisecond offset for synced lyrics. */
export interface LyricLine {
  start?: number;
  value: string;
}

export interface StructuredLyrics {
  lang?: string;
  synced: boolean;
  displayArtist?: string;
  displayTitle?: string;
  offset?: number;
  line: LyricLine[];
}

/** Plain, unsynced lyrics matched by artist + title. */
export interface PlainLyrics {
  artist?: string;
  title?: string;
  value: string;
}

export interface TrackLyrics {
  /** Stored structured sets, synced first. Empty when the track has none. */
  structured: StructuredLyrics[];
  /** Best-effort plain text - only populated when no structured set exists. */
  plain: PlainLyrics | null;
}

export interface TrackLyricsDto {
  structured: {
    lang?: string;
    synced: boolean;
    offset?: number;
    displayArtist?: string;
    displayTitle?: string;
    lines: { start?: number; value: string }[];
  }[];
  plain: PlainLyrics | null;
}

/** Wire → app shape. Shared with the party guest surface, which serves the same DTO. */
export function mapTrackLyrics(data: TrackLyricsDto): TrackLyrics {
  return {
    structured: data.structured.map((s) => ({
      lang: s.lang,
      synced: s.synced,
      offset: s.offset,
      displayArtist: s.displayArtist,
      displayTitle: s.displayTitle,
      line: s.lines,
    })),
    plain: data.plain && data.plain.value ? data.plain : null,
  };
}

/**
 * A track's lyrics in one call - structured sets plus the plain-text fallback. Replaces the
 * old getLyricsBySongId-then-getLyrics fallback dance; the server does the falling back.
 */
export async function getTrackLyrics(trackId: string): Promise<TrackLyrics> {
  const data = await client.get<TrackLyricsDto>(
    `api/tracks/${encodeURIComponent(trackId)}/lyrics`);
  return mapTrackLyrics(data);
}
