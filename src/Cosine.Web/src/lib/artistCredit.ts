import type { Track } from '../api/types';

/**
 * One piece of a rendered artist credit. A piece with an `id` is an artist and links to their
 * page; one without is the literal text between two names - " feat. ", " & ", ", ".
 */
export interface CreditSegment {
  text: string;
  id?: string;
}

/**
 * Splits a track's artist credit into linkable names and the text that joins them, so
 * "Pouya & Fat Nick" renders as two links around a plain " & " rather than one link over the
 * whole string or a comma list that throws the phrasing away.
 *
 * The server sends both halves: `displayArtist` is the credit as the release printed it, and
 * `artists` is the ordered list of who is in it. Lining the two up recovers the separators
 * without the client having to guess them - and when they do not line up (a credited spelling
 * that differs from the artist's canonical name), it falls back to the names joined by ", ",
 * which is still every artist, still linkable.
 *
 * A track with no credit - anything scanned before credits existed - yields the single
 * `artist`/`artistId` pair, i.e. exactly what the UI rendered before any of this.
 */
export function creditSegments(track: Track | null | undefined): CreditSegment[] {
  if (!track) return [];

  const artists = track.artists ?? [];
  const display = track.displayArtist ?? track.artist ?? '';

  if (artists.length === 0) {
    return display ? [{ text: display, id: track.artistId }] : [];
  }

  const aligned = align(display, artists);
  if (aligned) return aligned;

  return artists.flatMap((a, i) =>
    i === 0 ? [{ text: a.name, id: a.id }] : [{ text: ', ' }, { text: a.name, id: a.id }],
  );
}

/** Every name found in `display`, in order, or null if any is missing. */
function align(display: string, artists: { id: string; name: string }[]): CreditSegment[] | null {
  const segments: CreditSegment[] = [];
  let cursor = 0;

  for (const artist of artists) {
    const at = display.indexOf(artist.name, cursor);
    if (at < 0) return null;

    if (at > cursor) segments.push({ text: display.slice(cursor, at) });
    segments.push({ text: artist.name, id: artist.id });
    cursor = at + artist.name.length;
  }

  if (cursor < display.length) segments.push({ text: display.slice(cursor) });
  return segments;
}
