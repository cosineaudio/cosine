const BASE_URL = ''

interface RequestOptions {
  method?: string
  body?: unknown
  /** Extra request headers - e.g. the listen-together guest ticket. */
  headers?: Record<string, string>
}

async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body } = opts

  // CSRF gate: the server rejects unsafe methods on /api, /auth and /playback unless the
  // request provably came from same-origin script. Cross-site attackers can't set custom
  // headers without a CORS preflight the server never approves, so presence is the proof.
  const headers: Record<string, string> = { 'X-Requested-With': 'XMLHttpRequest', ...opts.headers }
  if (body && method !== 'GET') {
    headers['Content-Type'] = 'application/json'
  }

  // Ensure a root-absolute path: relative paths like "api/artists" would otherwise
  // resolve against the current route (e.g. /artist/{id}/api/artists) and 404.
  const absolutePath = path.startsWith('/') ? path : `/${path}`
  const url = `${BASE_URL}${absolutePath}`

  const response = await fetch(url, {
    method,
    headers,
    credentials: 'include',
    body: body ? JSON.stringify(body) : undefined,
  })

  if (!response.ok) {
    if (response.status === 401) {
      throw unauthorized(await response.json().catch(() => ({})))
    }
    // `error` is this API's own shape; `detail` and `title` are ProblemDetails, which is what
    // the playback endpoints answer with. Without them a refused device setting would reach the
    // admin as "Request failed" instead of the sentence naming what is wrong with it.
    const errorBody = await response.json().catch(() => ({ error: 'Request failed' }))
    throw new ApiError(
      response.status,
      errorBody.error || errorBody.detail || errorBody.title || 'Request failed')
  }

  if (response.status === 204) {
    return undefined as T
  }

  // Read as text first and treat an empty body as no payload. It is the *body* that may be
  // absent, not only the 204 status: every "queue a background job" endpoint answers 202
  // Accepted with nothing in it, and calling response.json() on that throws "Unexpected end of
  // JSON input" - which the caller cannot tell apart from the request having actually failed.
  // That is how "Rebuild search index" and "Analyse audio" both reported failure for work the
  // server had already accepted and started.
  const text = await response.text()
  if (!text) {
    return undefined as T
  }

  return JSON.parse(text) as T
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
    this.name = 'ApiError'
  }
}

export class UnauthorizedError extends ApiError {
  constructor(_body?: unknown) {
    super(401, 'Unauthorized')
    this.name = 'UnauthorizedError'
  }
}

/**
 * Called whenever a request comes back unauthenticated, so the app can drop its session and
 * send the user to the login page. Without it an expired cookie throws `UnauthorizedError`
 * into unhandled rejections and the UI sits on a dead page until the user happens to navigate.
 * Registered by the app shell; the client stays free of router/store imports.
 */
let unauthorizedHandler: (() => void) | null = null

export function setUnauthorizedHandler(handler: (() => void) | null): void {
  unauthorizedHandler = handler
}

function unauthorized(body?: unknown): UnauthorizedError {
  // A failing handler must not replace the error the caller is waiting for.
  try { unauthorizedHandler?.() } catch { /* ignore */ }
  return new UnauthorizedError(body)
}

export const client = {
  get<T>(path: string, headers?: Record<string, string>): Promise<T> {
    return request<T>(path, { headers })
  },
  post<T>(path: string, body?: unknown, headers?: Record<string, string>): Promise<T> {
    return request<T>(path, { method: 'POST', body, headers })
  },
  put<T>(path: string, body?: unknown): Promise<T> {
    return request<T>(path, { method: 'PUT', body })
  },
  patch<T>(path: string, body?: unknown): Promise<T> {
    return request<T>(path, { method: 'PATCH', body })
  },
  delete<T>(path: string): Promise<T> {
    return request<T>(path, { method: 'DELETE' })
  },
}
