import { describe, it, expect, vi, beforeEach } from 'vitest'
import { getArtists, getArtistInfo2, getTopSongs, getSimilarSongs2, getAlbum } from '../browsing'
import { dtoToTrack, dtoToAlbum } from '../adapt'
import type { TrackDto } from '../model'

const mockFetch = vi.fn()
globalThis.fetch = mockFetch

beforeEach(() => {
  mockFetch.mockReset()
})

function mockJson(body: unknown) {
  mockFetch.mockResolvedValue({
    ok: true,
    status: 200,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  })
}

function lastUrl(): string {
  return mockFetch.mock.calls[mockFetch.mock.calls.length - 1][0] as string
}

const trackDto: TrackDto = {
  id: 't-1',
  title: 'Hit',
  artist: { id: 'ar-1', name: 'Pouya' },
  artists: [
    { id: 'ar-1', name: 'Pouya' },
    { id: 'ar-2', name: 'Fat Nick' },
  ],
  displayArtist: 'Pouya & Fat Nick',
  album: { id: 'al-1', name: 'Drop Out of School' },
  trackNumber: 3,
  year: 2016,
  durationMs: 214_400,
  fileSize: 3_000_000_000,
  format: 'flac',
  artworkId: 'al-1',
}

describe('browsing api (/api)', () => {
  it('getArtists reshapes index groups onto the legacy ArtistsResponse', async () => {
    mockJson({
      ignoredArticles: 'The A An',
      groups: [
        {
          name: 'P',
          artists: [{ id: 'ar-1', name: 'Pouya', sortName: 'pouya', albumCount: 2, artworkId: 'ar-1' }],
        },
      ],
    })

    const res = await getArtists()

    expect(lastUrl()).toContain('/api/artists')
    expect(res.artists.index).toHaveLength(1)
    expect(res.artists.index[0].name).toBe('P')
    expect(res.artists.index[0].artist[0]).toMatchObject({
      id: 'ar-1',
      name: 'Pouya',
      coverArt: 'ar-1',
      albumCount: 2,
    })
  })

  it('getArtistInfo2 always returns an array of similar artists', async () => {
    mockJson({ biography: 'A band.', similarArtists: [] })
    const info = await getArtistInfo2('ar-1', { count: 5 })
    expect(info.biography).toBe('A band.')
    expect(info.similarArtist).toEqual([])
    expect(lastUrl()).toContain('/api/artists/ar-1/info?count=5')
  })

  it('getTopSongs keys on the artist id now', async () => {
    mockJson({ tracks: [trackDto] })
    const songs = await getTopSongs('ar-1', 3)
    expect(songs).toHaveLength(1)
    expect(lastUrl()).toContain('/api/artists/ar-1/top-songs?count=3')
  })

  it('getSimilarSongs2 passes the seed id', async () => {
    mockJson({ tracks: [trackDto, { ...trackDto, id: 't-2' }] })
    const songs = await getSimilarSongs2('ar-1')
    expect(songs).toHaveLength(2)
    expect(lastUrl()).toContain('/api/similar-tracks?seedId=ar-1')
  })

  it('getAlbum adapts the detail DTO with its tracks', async () => {
    mockJson({
      id: 'al-1', name: 'Drop Out of School', artist: { id: 'ar-1', name: 'Pouya' },
      trackCount: 1, durationMs: 214_400, createdAt: '2026-01-01T00:00:00Z',
      artworkId: 'al-1', tracks: [trackDto],
    })

    const album = await getAlbum('al-1')

    expect(album.song).toHaveLength(1)
    expect(album.songCount).toBe(1)
    expect(album.artist).toBe('Pouya')
  })
})

describe('adapters', () => {
  it('dtoToTrack produces the legacy Subsonic-flavoured shape', () => {
    const track = dtoToTrack(trackDto)

    expect(track).toMatchObject({
      id: 't-1',
      isDir: false,
      title: 'Hit',
      album: 'Drop Out of School',
      albumId: 'al-1',
      // The legacy `artist` field carried the full display credit.
      artist: 'Pouya & Fat Nick',
      artistId: 'ar-1',
      coverArt: 'al-1',
      track: 3,
      suffix: 'flac',
      size: 3_000_000_000,
    })
    // Milliseconds → whole seconds, the unit every component still expects.
    expect(track.duration).toBe(214)
  })

  it('dtoToAlbum converts duration and renames the count', () => {
    const album = dtoToAlbum({
      id: 'al-1', name: 'X', artist: { id: 'ar-1', name: 'Pouya' },
      trackCount: 12, durationMs: 2_400_500, createdAt: '2026-01-01T00:00:00Z',
      artworkId: 'al-1',
    })

    expect(album.songCount).toBe(12)
    expect(album.duration).toBe(2401)
    expect(album.coverArt).toBe('al-1')
  })
})
