<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { LogIn } from 'lucide-vue-next'
import { useAuthStore } from '../stores/auth'
import Spinner from '../components/ui/Spinner.vue'
import { authApi, type AuthConfiguration } from '../api/auth'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()

const username = ref('')
const password = ref('')
const error = ref<string | null>(null)
const loading = ref(false)
const config = ref<AuthConfiguration | null>(null)
const configError = ref(false)
const providerError = computed(() => {
  if (route.query.authError === 'account') return 'This identity could not be connected. Sign in with your local account and link it from Profile, or ask an administrator for access.'
  if (route.query.authError === 'denied') return 'Single sign-on was cancelled or access was denied.'
  if (route.query.authError) return 'Single sign-on failed. Please try again.'
  return null
})
const destination = computed(() => {
  const value = route.query.redirect
  return typeof value === 'string' && value.startsWith('/') && !/^[/][\\/]/.test(value) && !/[\x00-\x1f\x7f]/.test(value) ? value : '/'
})
const oidcLoginUrl = computed(() => '/auth/oidc/login?returnUrl=' + encodeURIComponent(destination.value))

async function loadConfig() {
  configError.value = false
  try { config.value = await authApi.config() }
  catch { configError.value = true }
}
onMounted(loadConfig)

async function handleLogin() {
  error.value = null
  loading.value = true
  try {
    await auth.login(username.value, password.value)
    router.push(destination.value)
  } catch {
    error.value = auth.error || 'Login failed'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="grid min-h-viewport bg-bg text-text lg:grid-cols-2">
    <!-- left: oversized wordmark + statement -->
    <section
      class="relative hidden flex-col justify-between overflow-hidden border-r border-border p-12 lg:flex"
    >
      <span class="label">Self-hosted · Subsonic</span>
      <div>
        <h1 class="font-display font-bold uppercase leading-[0.9] tracking-tight text-text" style="font-size: clamp(3.5rem, 8vw, 7rem)">
          cosine
        </h1>
        <p class="mt-6 max-w-md text-lg leading-relaxed text-muted">
          Your music, unapologetically. A pirate-radio booth for the records you own -
          loud, direct, and yours alone.
        </p>
      </div>
      <p class="font-mono text-xs text-faint">// broadcasting from your own shelf</p>
    </section>

    <!-- right: minimal form -->
    <section class="flex flex-col items-center justify-center px-6 py-16">
      <div class="w-full max-w-sm">
        <!-- mobile wordmark -->
        <h1 class="mb-10 font-display text-4xl font-bold lowercase tracking-tight text-text lg:hidden">
          cosine
        </h1>

        <div class="mb-8">
          <h2 class="font-display text-title font-semibold tracking-tight text-text">Sign in</h2>
          <p class="mt-1 text-sm text-muted">Sign in to start listening.</p>
        </div>

        <p v-if="providerError" class="mb-5 text-sm text-danger" role="alert">{{ providerError }}</p>
        <div v-if="configError" class="mb-5 text-sm text-danger" role="alert">
          Could not load sign-in options.
          <button class="underline" type="button" @click="loadConfig">Try again</button>
        </div>
        <Spinner v-else-if="!config" size="md" />
        <a v-if="config?.oidcEnabled" :href="oidcLoginUrl"
          class="mb-6 flex w-full items-center justify-center rounded-control border border-border bg-surface px-4 py-3 font-medium hover:bg-surface-2">
          Sign in with {{ config.oidcDisplayName }}
        </a>
        <form v-if="config?.localLoginEnabled" class="space-y-5" @submit.prevent="handleLogin">
          <div>
            <label for="username" class="label mb-2 block">Username</label>
            <input
              id="username"
              v-model="username"
              type="text"
              required
              autocomplete="username"
              class="w-full rounded-control border border-border bg-surface px-3 py-2.5 text-text placeholder:text-faint focus:border-transparent"
              placeholder="you"
            />
          </div>

          <div>
            <label for="password" class="label mb-2 block">Password</label>
            <input
              id="password"
              v-model="password"
              type="password"
              required
              autocomplete="current-password"
              class="w-full rounded-control border border-border bg-surface px-3 py-2.5 text-text placeholder:text-faint focus:border-transparent"
              placeholder="••••••••"
            />
          </div>

          <div v-if="error" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger" role="alert">
            {{ error }}
          </div>

          <button
            type="submit"
            :disabled="loading"
            class="flex w-full items-center justify-center gap-2 rounded-control bg-accent py-2.5 font-medium text-accent-ink transition-transform hover:scale-[1.01] active:scale-[0.99] disabled:cursor-not-allowed disabled:opacity-50"
          >
            <Spinner v-if="loading" size="md" />
            <LogIn v-else :size="18" :stroke-width="2" />
            {{ loading ? 'Signing in…' : 'Sign in' }}
          </button>
        </form>
      </div>
    </section>
  </div>
</template>
