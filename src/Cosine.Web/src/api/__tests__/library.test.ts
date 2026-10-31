import { describe, it, expect, vi, beforeEach } from 'vitest'
import { getArtistSongs, getAutoPlayQueue } from '../library'

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

function lastCall(): { url: string; init: { method?: string; body?: string } } {
  const [url, init] = mockFetch.mock.calls[mockFetch.mock.calls.length - 1]
  return { url, init }
}

const trackDto = (id: string, title: string) => ({
  id,
  title,
  artists: [],
  displayArtist: '',
  artworkId: 'al-1',
})

describe('library api', () => {
  describe('getArtistSongs', () => {
    it('returns the songs as legacy tracks', async () => {
      mockJson({ tracks: [trackDto('t-1', 'One'), trackDto('t-2', 'Two')] })

      const songs = await getArtistSongs('ar-1')

      expect(songs.map((s) => s.id)).toEqual(['t-1', 't-2'])
      expect(lastCall().url).toBe('/api/artists/ar-1/songs')
    })

    it('escapes the artist id', async () => {
      mockJson({ tracks: [] })

      await getArtistSongs('ar 1/2')

      expect(lastCall().url).toBe('/api/artists/ar%201%2F2/songs')
    })
  })

  describe('getAutoPlayQueue', () => {
    it('POSTs seeds and exclusions as JSON', async () => {
      mockJson({ tracks: [trackDto('t-9', 'Next')] })

      const songs = await getAutoPlayQueue({
        seedTrackIds: ['t-1'],
        excludeTrackIds: ['t-2', 't-3'],
        count: 5,
      })

      expect(songs.map((s) => s.id)).toEqual(['t-9'])
      const { url, init } = lastCall()
      expect(url).toBe('/api/autoplay')
      expect(init.method).toBe('POST')
      expect(JSON.parse(init.body!)).toEqual({
        seedTrackIds: ['t-1'],
        excludeTrackIds: ['t-2', 't-3'],
        count: 5,
      })
    })
  })
})
