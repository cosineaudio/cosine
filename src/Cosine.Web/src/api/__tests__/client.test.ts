import { describe, it, expect, vi, beforeEach } from 'vitest'
import { client, ApiError, UnauthorizedError, setUnauthorizedHandler } from '../client'

const mockFetch = vi.fn()
globalThis.fetch = mockFetch

beforeEach(() => {
  mockFetch.mockReset()
  setUnauthorizedHandler(null)
})

function mockResponse(status: number, body: unknown) {
  mockFetch.mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
    text: () => Promise.resolve(JSON.stringify(body)),
  })
}

/** A response with no body at all - what 202 Accepted actually sends. */
function mockEmptyResponse(status: number) {
  mockFetch.mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.reject(new SyntaxError('Unexpected end of JSON input')),
    text: () => Promise.resolve(''),
  })
}

describe('client', () => {
  describe('get', () => {
    it('sends GET request with credentials', async () => {
      mockResponse(200, { data: 'ok' })
      const result = await client.get('/test')
      expect(result).toEqual({ data: 'ok' })
      expect(mockFetch).toHaveBeenCalledWith('/test', {
        method: 'GET',
        headers: { 'X-Requested-With': 'XMLHttpRequest' },
        credentials: 'include',
        body: undefined,
      })
    })

    it('throws ApiError on non-200 response', async () => {
      mockResponse(500, { error: 'Server error' })
      await expect(client.get('/test')).rejects.toThrow(ApiError)
      await expect(client.get('/test')).rejects.toThrow('Server error')
    })

    it('throws UnauthorizedError on 401', async () => {
      mockResponse(401, { error: 'Invalid' })
      try {
        await client.get('/test')
        expect.fail('Should have thrown')
      } catch (e) {
        expect(e).toBeInstanceOf(UnauthorizedError)
        expect((e as ApiError).status).toBe(401)
      }
    })

    // Every "queue a background job" endpoint (reindex, artist images, track credits, audio
    // analysis) answers 202 with an empty body. Parsing that as JSON threw, so the caller saw a
    // failure for work the server had accepted and already begun.
    it('returns undefined on 202 Accepted with an empty body', async () => {
      mockEmptyResponse(202)

      await expect(client.post('/api/admin/audio-analysis')).resolves.toBeUndefined()
    })

    it('returns undefined on any success with an empty body', async () => {
      mockEmptyResponse(200)

      await expect(client.get('/test')).resolves.toBeUndefined()
    })

    it('returns undefined on 204 No Content', async () => {
      mockResponse(204, {})
      const result = await client.get('/test')
      expect(result).toBeUndefined()
    })
  })

  describe('post', () => {
    it('sends POST with JSON body', async () => {
      mockResponse(200, { id: '1' })
      const result = await client.post('/test', { name: 'foo' })
      expect(result).toEqual({ id: '1' })
      expect(mockFetch).toHaveBeenCalledWith('/test', {
        method: 'POST',
        headers: { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ name: 'foo' }),
      })
    })
  })

  describe('put', () => {
    it('sends PUT with JSON body', async () => {
      mockResponse(200, { updated: true })
      const result = await client.put('/test', { name: 'bar' })
      expect(result).toEqual({ updated: true })
      expect(mockFetch).toHaveBeenCalledWith('/test', {
        method: 'PUT',
        headers: { 'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ name: 'bar' }),
      })
    })
  })

  describe('delete', () => {
    it('sends DELETE request', async () => {
      mockResponse(204, {})
      const result = await client.delete('/test')
      expect(result).toBeUndefined()
      expect(mockFetch).toHaveBeenCalledWith('/test', {
        method: 'DELETE',
        headers: { 'X-Requested-With': 'XMLHttpRequest' },
        credentials: 'include',
        body: undefined,
      })
    })
  })

  // The server's CSRF gate accepts unsafe methods only when the request carries proof of
  // same-origin script: X-Requested-With (set here) or a same-origin Sec-Fetch-Site.
  describe('csrf header', () => {
    it('is sent on every request, including login', async () => {
      mockResponse(200, { id: '1' })
      await client.post('/auth/login', { username: 'u', password: 'p' })
      const [, init] = mockFetch.mock.calls[0]
      expect(init.headers['X-Requested-With']).toBe('XMLHttpRequest')
    })
  })

  describe('unauthorized handler', () => {
    it('fires on a 401 and still rejects', async () => {
      const handler = vi.fn()
      setUnauthorizedHandler(handler)
      mockResponse(401, { error: 'Invalid' })

      await expect(client.get('/api/admin/users')).rejects.toBeInstanceOf(UnauthorizedError)
      expect(handler).toHaveBeenCalledOnce()
    })

    it('does not let a throwing handler mask the original error', async () => {
      setUnauthorizedHandler(() => { throw new Error('router blew up') })
      mockResponse(401, { error: 'Invalid' })

      await expect(client.get('/api/me')).rejects.toBeInstanceOf(UnauthorizedError)
    })
  })

  describe('ApiError', () => {
    it('stores status and message', () => {
      const err = new ApiError(404, 'Not found')
      expect(err.status).toBe(404)
      expect(err.message).toBe('Not found')
    })

    it('handles missing error body gracefully', async () => {
      mockFetch.mockResolvedValue({
        ok: false,
        status: 500,
        json: () => Promise.reject(new Error('not json')),
      })
      await expect(client.get('/test')).rejects.toThrow('Request failed')
    })
  })
})
