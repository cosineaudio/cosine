import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { authApi, type User } from '../api/auth'
import { UnauthorizedError } from '../api/client'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)
  const initializing = ref(true)
  const error = ref<string | null>(null)
  const loading = ref(false)

  const isAuthenticated = computed(() => user.value !== null)
  const isAdmin = computed(() => user.value?.isAdmin ?? false)
  const initialized = computed(() => !initializing.value)

  async function fetchUser() {
    initializing.value = true
    try {
      user.value = await authApi.me()
    } catch (e) {
      user.value = null
    } finally {
      initializing.value = false
    }
  }

  async function login(username: string, password: string) {
    loading.value = true
    error.value = null
    try {
      user.value = await authApi.login({ username, password })
    } catch (e) {
      if (e instanceof UnauthorizedError) {
        error.value = 'Invalid username or password'
      } else {
        error.value = 'An unexpected error occurred'
      }
      throw e
    } finally {
      loading.value = false
    }
  }

  /** Drop the local session without calling the server - for a cookie that already expired. */
  function clearSession() {
    user.value = null
  }

  async function logout() {
    try {
      await authApi.logout()
    } catch {
      // Ignore API errors on logout - clear local state anyway
    } finally {
      user.value = null
    }
  }

  return {
    user,
    initializing,
    error,
    loading,
    isAuthenticated,
    isAdmin,
    initialized,
    fetchUser,
    login,
    logout,
    clearSession,
  }
})
