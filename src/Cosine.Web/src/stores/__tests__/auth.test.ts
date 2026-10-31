import { describe, it, expect, vi, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '../auth'

const mockMe = vi.fn()
const mockLogin = vi.fn()
const mockLogout = vi.fn()

vi.mock('../../api/auth', () => ({
  authApi: {
    me: () => mockMe(),
    login: (data: { username: string; password: string }) => mockLogin(data),
    logout: () => mockLogout(),
  },
}))

describe('auth store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.clearAllMocks()
  })

  describe('initial state', () => {
    it('has null user initially', () => {
      const auth = useAuthStore()
      expect(auth.user).toBeNull()
      expect(auth.isAuthenticated).toBe(false)
      expect(auth.isAdmin).toBe(false)
      expect(auth.loading).toBe(false)
      expect(auth.error).toBeNull()
    })

    it('is initializing by default', () => {
      const auth = useAuthStore()
      expect(auth.initialized).toBe(false)
    })
  })

  describe('fetchUser', () => {
    it('sets user on successful response', async () => {
      const user = { id: '1', username: 'test', isAdmin: false }
      mockMe.mockResolvedValue(user)

      const auth = useAuthStore()
      await auth.fetchUser()

      expect(auth.user).toEqual(user)
      expect(auth.isAuthenticated).toBe(true)
      expect(auth.initialized).toBe(true)
    })

    it('sets null user on failed response', async () => {
      mockMe.mockRejectedValue(new Error('Network error'))

      const auth = useAuthStore()
      await auth.fetchUser()

      expect(auth.user).toBeNull()
      expect(auth.isAuthenticated).toBe(false)
      expect(auth.initialized).toBe(true)
    })
  })

  describe('login', () => {
    it('sets user on successful login', async () => {
      const user = { id: '1', username: 'admin', isAdmin: true }
      mockLogin.mockResolvedValue(user)

      const auth = useAuthStore()
      await auth.login('admin', 'password')

      expect(auth.user).toEqual(user)
      expect(auth.isAdmin).toBe(true)
      expect(auth.loading).toBe(false)
      expect(auth.error).toBeNull()
    })

    it('sets error message on failed login', async () => {
      const { UnauthorizedError } = await import('../../api/client')
      mockLogin.mockRejectedValue(new UnauthorizedError({}))

      const auth = useAuthStore()
      try {
        await auth.login('bad', 'pass')
      } catch {
        // expected
      }

      expect(auth.user).toBeNull()
      expect(auth.error).toBe('Invalid username or password')
      expect(auth.loading).toBe(false)
    })

    it('sets generic error on non-auth failures', async () => {
      mockLogin.mockRejectedValue(new Error('Network down'))

      const auth = useAuthStore()
      try {
        await auth.login('test', 'pass')
      } catch {
        // expected
      }

      expect(auth.error).toBe('An unexpected error occurred')
    })
  })

  describe('logout', () => {
    it('clears user and calls logout API', async () => {
      const auth = useAuthStore()
      auth.user = { id: '1', username: 'test', isAdmin: false }

      await auth.logout()

      expect(auth.user).toBeNull()
      expect(mockLogout).toHaveBeenCalledOnce()
    })

    it('clears user even if logout API fails', async () => {
      mockLogout.mockRejectedValue(new Error('fail'))

      const auth = useAuthStore()
      auth.user = { id: '1', username: 'test', isAdmin: false }

      await auth.logout()

      expect(auth.user).toBeNull()
    })
  })
})
