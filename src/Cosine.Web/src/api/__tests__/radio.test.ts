import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  getInternetRadioStations,
  createInternetRadioStation,
  deleteInternetRadioStation,
} from '../radio'

const mockFetch = vi.fn()
globalThis.fetch = mockFetch

beforeEach(() => {
  mockFetch.mockReset()
})

function mockJson(body: unknown, status = 200) {
  mockFetch.mockResolvedValue({
    ok: true,
    status,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  })
}

function lastCall(): { url: string; init: { method?: string; body?: string } } {
  const [url, init] = mockFetch.mock.calls[mockFetch.mock.calls.length - 1]
  return { url, init }
}

describe('radio api', () => {
  it('returns the station list', async () => {
    mockJson({
      stations: [
        { id: '1', name: 'Jazz FM', streamUrl: 'http://a/stream' },
        { id: '2', name: 'Rock', streamUrl: 'http://b/stream', homepageUrl: 'http://b' },
      ],
    })
    const stations = await getInternetRadioStations()
    expect(stations).toHaveLength(2)
    expect(stations[0].name).toBe('Jazz FM')
    expect(lastCall().url).toContain('/api/radio-stations')
  })

  it('creates via POST with a JSON body', async () => {
    mockJson({ id: '9', name: 'My Station', streamUrl: 'http://x/s' }, 201)
    await createInternetRadioStation({ name: 'My Station', streamUrl: 'http://x/s', homepageUrl: 'http://x' })
    const { url, init } = lastCall()
    expect(url).toContain('/api/radio-stations')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body!)).toEqual({
      name: 'My Station',
      streamUrl: 'http://x/s',
      homepageUrl: 'http://x',
    })
  })

  it('deletes by id', async () => {
    mockFetch.mockResolvedValue({
      ok: true, status: 204,
      json: () => Promise.reject(new SyntaxError('no body')),
      text: () => Promise.resolve(''),
    })
    await deleteInternetRadioStation('abc')
    const { url, init } = lastCall()
    expect(url).toContain('/api/radio-stations/abc')
    expect(init.method).toBe('DELETE')
  })
})
