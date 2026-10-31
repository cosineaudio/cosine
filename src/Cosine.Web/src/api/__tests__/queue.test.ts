import { describe, it, expect, vi, beforeEach } from 'vitest'
import { getPlayQueue, savePlayQueue } from '../queue'

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

/** 204 No Content - what the server answers when no queue is saved. */
function mockNoContent() {
  mockFetch.mockResolvedValue({
    ok: true,
    status: 204,
    json: () => Promise.reject(new SyntaxError('no body')),
    text: () => Promise.resolve(''),
  })
}

function lastCall(): { url: string; init: { method?: string; body?: string } } {
  const [url, init] = mockFetch.mock.calls[mockFetch.mock.calls.length - 1]
  return { url, init }
}

const trackDto = (id: string) => ({
  id,
  title: id,
  artists: [],
  displayArtist: '',
  artworkId: 'al-1',
})

describe('queue api', () => {
  describe('getPlayQueue', () => {
    it('returns the saved queue state', async () => {
      mockJson({ tracks: [trackDto('s1'), trackDto('s2')], currentTrackId: 's2', positionMs: 4200 })
      const state = await getPlayQueue()
      expect(state).not.toBeNull()
      expect(state!.entry).toHaveLength(2)
      expect(state!.current).toBe('s2')
      expect(state!.position).toBe(4200)
      expect(lastCall().url).toContain('/api/queue')
    })

    it('returns null when there is no saved queue (204)', async () => {
      mockNoContent()
      expect(await getPlayQueue()).toBeNull()
    })
  })

  describe('savePlayQueue', () => {
    it('PUTs the ids, current id, and a rounded position as JSON', async () => {
      mockNoContent()
      await savePlayQueue({ ids: ['s1', 's2', 's3'], current: 's2', position: 4200.7 })
      const { url, init } = lastCall()
      expect(url).toContain('/api/queue')
      expect(init.method).toBe('PUT')
      expect(JSON.parse(init.body!)).toEqual({
        trackIds: ['s1', 's2', 's3'],
        currentTrackId: 's2',
        positionMs: 4201,
      })
    })

    it('omits current and position when not provided', async () => {
      mockNoContent()
      await savePlayQueue({ ids: ['s1'] })
      expect(JSON.parse(lastCall().init.body!)).toEqual({ trackIds: ['s1'] })
    })
  })
})
