import { describe, it, expect } from 'vitest';
import { creditSegments } from '../artistCredit';
import type { Track } from '../../api/types';

function track(partial: Partial<Track>): Track {
  return { id: 't1', isDir: false, title: 'Song', ...partial } as Track;
}

/**
 * The players used to render one `{{ track.artist }}` button, so a track with three artists on
 * it linked to one and named one. These are the two halves the server now sends -
 * `displayArtist` for the phrasing, `artists` for who is in it - and the rule for lining them
 * up without the client guessing separators.
 */
describe('creditSegments', () => {
  it('links every artist and keeps the join phrase between them', () => {
    const segments = creditSegments(
      track({
        artist: 'Pouya & Fat Nick',
        displayArtist: 'Pouya & Fat Nick',
        artistId: 'a1',
        artists: [
          { id: 'a1', name: 'Pouya' },
          { id: 'a2', name: 'Fat Nick' },
        ],
      }),
    );

    expect(segments).toEqual([
      { text: 'Pouya', id: 'a1' },
      { text: ' & ' },
      { text: 'Fat Nick', id: 'a2' },
    ]);
  });

  it('preserves "feat." rather than normalising it to a comma', () => {
    const segments = creditSegments(
      track({
        displayArtist: 'Skrillex feat. Damian Marley',
        artists: [
          { id: 'a1', name: 'Skrillex' },
          { id: 'a2', name: 'Damian Marley' },
        ],
      }),
    );

    expect(segments.map((s) => s.text).join('')).toBe('Skrillex feat. Damian Marley');
    expect(segments.filter((s) => s.id).length).toBe(2);
  });

  it('handles three artists', () => {
    const segments = creditSegments(
      track({
        displayArtist: 'Eminem feat. Dr. Dre & 50 Cent',
        artists: [
          { id: 'a1', name: 'Eminem' },
          { id: 'a2', name: 'Dr. Dre' },
          { id: 'a3', name: '50 Cent' },
        ],
      }),
    );

    expect(segments.map((s) => s.text).join('')).toBe('Eminem feat. Dr. Dre & 50 Cent');
    expect(segments.filter((s) => s.id).map((s) => s.id)).toEqual(['a1', 'a2', 'a3']);
  });

  /**
   * The credited spelling on a release can differ from the artist's canonical name, in which
   * case the two halves cannot be aligned. Every artist still has to come back linkable -
   * losing the phrasing is acceptable, losing an artist is not.
   */
  it('falls back to a comma list when the display string does not contain the names', () => {
    const segments = creditSegments(
      track({
        displayArtist: 'Various Artists',
        artists: [
          { id: 'a1', name: 'Pouya' },
          { id: 'a2', name: 'Fat Nick' },
        ],
      }),
    );

    expect(segments).toEqual([
      { text: 'Pouya', id: 'a1' },
      { text: ', ' },
      { text: 'Fat Nick', id: 'a2' },
    ]);
  });

  /**
   * Anything scanned before credits existed has no `artists` at all. That path has to render
   * exactly what it always did, or deploying this regresses every un-rescanned library.
   */
  it('falls back to the single artist when there is no credit', () => {
    expect(creditSegments(track({ artist: 'Pink Floyd', artistId: 'a1' }))).toEqual([
      { text: 'Pink Floyd', id: 'a1' },
    ]);
  });

  it('prefers displayArtist over artist when both are present', () => {
    expect(creditSegments(track({ artist: 'ignored', displayArtist: 'Pink Floyd', artistId: 'a1' })))
      .toEqual([{ text: 'Pink Floyd', id: 'a1' }]);
  });

  it('yields a single linked segment for a solo credit', () => {
    expect(
      creditSegments(
        track({ displayArtist: 'Pouya', artistId: 'a1', artists: [{ id: 'a1', name: 'Pouya' }] }),
      ),
    ).toEqual([{ text: 'Pouya', id: 'a1' }]);
  });

  it('yields nothing for a track with no artist, so the caller can show a placeholder', () => {
    expect(creditSegments(track({}))).toEqual([]);
    expect(creditSegments(null)).toEqual([]);
    expect(creditSegments(undefined)).toEqual([]);
  });

  /** An artist whose name contains another's is still split at the right boundaries. */
  it('does not mis-align on a name that is a prefix of the display string', () => {
    const segments = creditSegments(
      track({
        displayArtist: 'Nick & Fat Nick',
        artists: [
          { id: 'a1', name: 'Nick' },
          { id: 'a2', name: 'Fat Nick' },
        ],
      }),
    );

    expect(segments.map((s) => s.text).join('')).toBe('Nick & Fat Nick');
    expect(segments.filter((s) => s.id).map((s) => s.id)).toEqual(['a1', 'a2']);
  });
});
