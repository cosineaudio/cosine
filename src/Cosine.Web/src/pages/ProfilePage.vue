<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { toast } from 'vue-sonner';
import { Copy, Check, Eye, EyeOff, KeyRound, Cable } from 'lucide-vue-next';
import { authApi, type Profile, type AuthConfiguration } from '@/api/auth';
import { useRouter, useRoute } from 'vue-router';
import { useAuthStore } from '@/stores/auth';
import Skeleton from '@/components/ui/Skeleton.vue';
import { useDeferredLoading } from '@/composables/useDeferredLoading';

const profile = ref<Profile | null>(null);
const config = ref<AuthConfiguration | null>(null);
const actionError = ref<string | null>(null);
const actionLoading = ref(false);
const router = useRouter();
const route = useRoute();
const auth = useAuthStore();

async function linkIdentity() {
  actionLoading.value = true;
  actionError.value = null;
  try {
    const { redirectUrl } = await authApi.linkOidc();
    window.location.assign(redirectUrl);
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : 'Could not start account linking';
    actionLoading.value = false;
  }
}

async function rotateCredentials() {
  if (!window.confirm('Rotate your client token and sign out all sessions? You will need to reconnect your Subsonic clients.')) return;
  actionLoading.value = true;
  actionError.value = null;
  try {
    await authApi.rotateCredentials();
    auth.clearSession();
    await router.push('/login');
  } catch (e) {
    actionError.value = e instanceof Error ? e.message : 'Could not rotate credentials';
  } finally { actionLoading.value = false; }
}
const loading = ref(true);
const showSkeleton = useDeferredLoading(loading);
const error = ref(false);
const revealed = ref(false);
const copiedField = ref<string | null>(null);

const serverUrl = window.location.origin;

const maskedToken = computed(() => {
  const token = profile.value?.subsonicToken ?? '';
  return revealed.value ? token : '•'.repeat(Math.min(token.length, 48));
});

const memberSince = computed(() => {
  if (!profile.value) return '';
  return new Date(profile.value.createdAt).toLocaleDateString(undefined, {
    year: 'numeric', month: 'long', day: 'numeric',
  });
});

async function copy(label: string, value: string) {
  try {
    await navigator.clipboard.writeText(value);
    copiedField.value = label;
    toast.success(`${label} copied`);
    setTimeout(() => { if (copiedField.value === label) copiedField.value = null; }, 2000);
  } catch {
    toast.error('Could not copy - clipboard unavailable');
  }
}

onMounted(async () => {
  try {
    [profile.value, config.value] = await Promise.all([authApi.profile(), authApi.config()]);
  } catch {
    error.value = true;
  } finally {
    loading.value = false;
  }
});
</script>

<template>
  <div class="mx-auto max-w-3xl px-6 pb-16 pt-8">
    <header class="mb-8">
      <h1 class="font-display font-bold tracking-tight text-text" :style="{ fontSize: 'var(--text-display-l)', lineHeight: '1' }">
        Profile
      </h1>
    </header>

    <p v-if="error" class="text-sm text-muted">Could not load your profile. Try reloading the page.</p>

    <template v-else-if="showSkeleton">
      <!-- account -->
      <section class="mb-10">
        <div class="flex items-center gap-4">
          <Skeleton class="h-16 w-16" rounded="full" />
          <div class="flex min-w-0 flex-col gap-2">
            <Skeleton class="h-7 w-44" />
            <Skeleton class="h-4 w-64" />
          </div>
        </div>
      </section>

      <!-- connect a client -->
      <section>
        <div class="mb-4 flex items-center gap-2">
          <Skeleton class="h-5 w-44" />
        </div>
        <div class="rounded-control border border-border bg-surface p-5">
          <div class="mb-5 flex flex-col gap-2">
            <Skeleton class="h-3.5 w-full" />
            <Skeleton class="h-3.5 w-4/5" />
          </div>
          <div class="grid gap-4">
            <div v-for="i in 3" :key="i">
              <Skeleton class="mb-1 h-3 w-24" />
              <div class="flex items-center gap-2">
                <Skeleton class="h-9 flex-1" />
                <Skeleton class="h-9 w-9 shrink-0" />
              </div>
            </div>
          </div>
        </div>
      </section>
    </template>

    <template v-else-if="profile">
      <!-- account -->
      <section class="mb-10">
        <div class="flex items-center gap-4">
          <div class="grid h-16 w-16 place-items-center rounded-full bg-surface-2 font-mono text-xl font-semibold text-text ring-1 ring-border">
            {{ profile.username.slice(0, 2).toUpperCase() }}
          </div>
          <div class="min-w-0">
            <p class="truncate font-display text-2xl font-bold tracking-tight text-text">
              {{ profile.username }}
              <span v-if="profile.isAdmin" class="ml-2 align-middle rounded-[3px] border border-border px-1.5 py-0.5 font-sans text-[10px] font-medium uppercase tracking-wider text-muted">admin</span>
            </p>
            <p class="truncate text-sm text-muted">
              {{ profile.email || 'No email set' }} · member since {{ memberSince }}
            </p>
          </div>
        </div>
      </section>

      <section v-if="config?.oidcEnabled" class="mb-10 rounded-control border border-border bg-surface p-5">
        <h2 class="font-display text-lg font-bold">Single sign-on</h2>
        <p v-if="profile.oidcLinked" class="mt-2 text-sm text-muted">Your account is connected to {{ config.oidcDisplayName }}.</p>
        <template v-else>
          <p class="mt-2 text-sm text-muted">Connect {{ config.oidcDisplayName }} to keep using this account and its library data. Sign in again first if your session is more than five minutes old.</p>
          <button type="button" :disabled="actionLoading" class="mt-4 rounded-control border border-border px-4 py-2 text-sm hover:bg-surface-2" @click="linkIdentity">Connect {{ config.oidcDisplayName }}</button>
        </template>
        <p v-if="route.query.authError" role="alert" class="mt-3 text-sm text-danger">The identity could not be connected. Please try again, or check whether it belongs to another account.</p>
      </section>
      <p v-if="actionError" role="alert" class="mb-5 text-sm text-danger">{{ actionError }}</p>

      <!-- connect a client -->
      <section>
        <div class="mb-4 flex items-center gap-2">
          <Cable :size="18" :stroke-width="2" class="text-muted" />
          <h2 class="font-display text-lg font-bold tracking-tight text-text">Connect a client</h2>
        </div>

        <div class="rounded-control border border-border bg-surface p-5">
          <p class="mb-5 text-sm leading-relaxed text-muted">
            Subsonic apps like Feishin, Symfonium or DSub connect with the details below.
            Use the <span class="text-text">token as your password</span> - your account
            password only works when the app's <span class="text-text">legacy authentication</span> option is enabled and password login is available for your account.
          </p>

          <div class="grid gap-4">
            <!-- server url -->
            <div>
              <label class="mb-1 block text-[11px] font-medium uppercase tracking-wider text-faint">Server URL</label>
              <div class="flex items-center gap-2">
                <code class="min-w-0 flex-1 truncate rounded-control border border-border bg-bg px-3 py-2 font-mono text-sm text-text">{{ serverUrl }}</code>
                <button
                  type="button"
                  class="grid h-9 w-9 shrink-0 place-items-center rounded-control border border-border text-muted transition-colors hover:bg-surface-2 hover:text-text"
                  aria-label="Copy server URL"
                  @click="copy('Server URL', serverUrl)"
                >
                  <Check v-if="copiedField === 'Server URL'" :size="16" :stroke-width="2" />
                  <Copy v-else :size="16" :stroke-width="2" />
                </button>
              </div>
            </div>

            <!-- username -->
            <div>
              <label class="mb-1 block text-[11px] font-medium uppercase tracking-wider text-faint">Username</label>
              <div class="flex items-center gap-2">
                <code class="min-w-0 flex-1 truncate rounded-control border border-border bg-bg px-3 py-2 font-mono text-sm text-text">{{ profile.username }}</code>
                <button
                  type="button"
                  class="grid h-9 w-9 shrink-0 place-items-center rounded-control border border-border text-muted transition-colors hover:bg-surface-2 hover:text-text"
                  aria-label="Copy username"
                  @click="copy('Username', profile.username)"
                >
                  <Check v-if="copiedField === 'Username'" :size="16" :stroke-width="2" />
                  <Copy v-else :size="16" :stroke-width="2" />
                </button>
              </div>
            </div>

            <!-- token -->
            <div>
              <label class="mb-1 flex items-center gap-1.5 text-[11px] font-medium uppercase tracking-wider text-faint">
                <KeyRound :size="12" :stroke-width="2" /> Token <span class="normal-case tracking-normal">(use as password)</span>
              </label>
              <div class="flex items-center gap-2">
                <code class="min-w-0 flex-1 truncate rounded-control border border-border bg-bg px-3 py-2 font-mono text-sm text-text">{{ maskedToken }}</code>
                <button
                  type="button"
                  class="grid h-9 w-9 shrink-0 place-items-center rounded-control border border-border text-muted transition-colors hover:bg-surface-2 hover:text-text"
                  :aria-label="revealed ? 'Hide token' : 'Reveal token'"
                  @click="revealed = !revealed"
                >
                  <EyeOff v-if="revealed" :size="16" :stroke-width="2" />
                  <Eye v-else :size="16" :stroke-width="2" />
                </button>
                <button
                  type="button"
                  class="grid h-9 w-9 shrink-0 place-items-center rounded-control border border-border text-muted transition-colors hover:bg-surface-2 hover:text-text"
                  aria-label="Copy token"
                  @click="copy('Token', profile.subsonicToken)"
                >
                  <Check v-if="copiedField === 'Token'" :size="16" :stroke-width="2" />
                  <Copy v-else :size="16" :stroke-width="2" />
                </button>
              </div>
              <p class="mt-2 text-xs leading-relaxed text-faint">
                Keep this secret - anyone with it can stream from your library as you.
              </p>
            </div>
          </div>
          <button type="button" :disabled="actionLoading" class="mt-5 rounded-control border border-border px-4 py-2 text-sm hover:bg-surface-2" @click="rotateCredentials">Rotate token and sign out all sessions</button>
        </div>
      </section>
    </template>
  </div>
</template>
