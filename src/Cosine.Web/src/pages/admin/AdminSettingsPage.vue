<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { adminApi, type AdminSetting } from '../../api/admin'
import Skeleton from '../../components/ui/Skeleton.vue'
import Spinner from '../../components/ui/Spinner.vue'
import { useDeferredLoading } from '../../composables/useDeferredLoading'
import { shimmerDelay } from '../../lib/skeleton'

const settings = ref<AdminSetting[]>([])
const loading = ref(true)
const showSkeleton = useDeferredLoading(loading)
const error = ref<string | null>(null)
const toast = ref<{ message: string; type: 'success' | 'error' } | null>(null)
const savingKeys = ref<Set<string>>(new Set())

function showToast(message: string, type: 'success' | 'error') {
  toast.value = { message, type }
  setTimeout(() => { toast.value = null }, 3000)
}

async function fetchSettings() {
  try {
    settings.value = await adminApi.getSettings()
    error.value = null
  } catch (e) {
    error.value = 'Failed to load settings'
  } finally {
    loading.value = false
  }
}

async function saveSetting(setting: AdminSetting) {
  savingKeys.value.add(setting.key)
  try {
    await adminApi.updateSetting(setting.key, setting.value)
    showToast('Setting saved', 'success')
  } catch (e) {
    showToast('Failed to save setting', 'error')
  } finally {
    savingKeys.value.delete(setting.key)
  }
}

function getLabel(key: string): string {
  const labels: Record<string, string> = {
    'scan.enabled': 'Auto-Scan',
    'scan.schedule': 'Scan Schedule (cron)',
    'playhistory.retention_days': 'History Retention (days)',
    'transcoding.cache_max_bytes': 'Transcode Cache Limit (bytes, blank = server default)',
    'library.media_root': 'Media Root Path',
    'artwork.fetch_artist_images': 'Fetch Artist Pictures (MusicBrainz/Deezer)',
    'musicbrainz.enrich_track_artists': 'Fetch Track Artist Credits (MusicBrainz)',
    'musicbrainz.base_url': 'MusicBrainz Server',
    'musicbrainz.contact': 'MusicBrainz Contact (User-Agent)',
    'musicbrainz.rate_limit_ms': 'MusicBrainz Delay Between Requests (ms)',
  }
  return labels[key] || key
}

/**
 * The one-line "why" under a control. Only the settings whose consequence is not obvious from
 * the label get one - notably that MusicBrainz has no API key to paste, which is the first
 * thing anyone looks for.
 */
function getHint(key: string): string | null {
  const hints: Record<string, string> = {
    'musicbrainz.enrich_track_artists':
      'Featured artists are usually missing from file tags - taggers built around one artist per album write the album artist into every track. Turning this on looks the credits up by release ID. Off by default: it tells MusicBrainz what is in your library.',
    'musicbrainz.base_url':
      'There is no API key for MusicBrainz. Point this at your own mirror if you want to lift the 1 request/second limit.',
    'musicbrainz.contact':
      'Optional. Added to the User-Agent, which is how MusicBrainz asks clients to identify themselves.',
    'musicbrainz.rate_limit_ms':
      'MusicBrainz allows 1 request/second for anonymous clients. Only lower this against your own mirror.',
  }
  return hints[key] ?? null
}

function getType(key: string): string {
  if (key === 'scan.enabled') return 'toggle'
  if (key === 'artwork.fetch_artist_images') return 'toggle'
  if (key === 'musicbrainz.enrich_track_artists') return 'toggle'
  if (key === 'playhistory.retention_days') return 'number'
  if (key === 'transcoding.cache_max_bytes') return 'number'
  if (key === 'musicbrainz.rate_limit_ms') return 'number'
  return 'text'
}

function getValue(setting: AdminSetting): string {
  return setting.value
}

function setValue(setting: AdminSetting, value: string) {
  setting.value = value
}

onMounted(fetchSettings)
</script>

<template>
  <div class="mx-auto max-w-3xl space-y-6 p-6">
    <h1 class="font-display text-2xl font-bold tracking-tight text-text">Settings</h1>

    <div v-if="toast" :class="[
      'rounded-control px-3 py-2 text-sm',
      toast.type === 'success' ? 'border border-border bg-surface-2 text-text' : 'border border-danger/40 bg-danger/10 text-danger',
    ]">
      {{ toast.message }}
    </div>

    <div v-if="showSkeleton" class="divide-y divide-border rounded-control border border-border bg-surface">
      <div v-for="i in 6" :key="i" class="flex items-center justify-between gap-4 p-4" :style="shimmerDelay(i)">
        <div class="flex flex-1 flex-col gap-2">
          <Skeleton class="h-4 w-48" />
          <Skeleton class="h-3 w-72" />
          <Skeleton class="h-3 w-32" />
        </div>
        <Skeleton class="h-8 w-24 shrink-0" />
      </div>
    </div>
    <template v-else-if="loading" />
    <div v-else-if="error" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ error }}</div>

    <div v-else class="reveal divide-y divide-border rounded-control border border-border bg-surface">
      <div v-for="setting in settings" :key="setting.key" class="flex items-center justify-between gap-4 p-4">
        <div class="flex-1">
          <label class="text-sm font-medium text-text">{{ getLabel(setting.key) }}</label>
          <p v-if="getHint(setting.key)" class="mt-1 text-xs text-muted">{{ getHint(setting.key) }}</p>
          <p class="mt-0.5 font-mono text-xs text-faint">{{ setting.key }}</p>
        </div>

        <div class="flex items-center gap-3">
          <!-- Toggle -->
          <label v-if="getType(setting.key) === 'toggle'" class="relative inline-flex cursor-pointer items-center">
            <input
              type="checkbox"
              :checked="getValue(setting) === 'true'"
              class="peer sr-only"
              @change="setValue(setting, getValue(setting) === 'true' ? 'false' : 'true')"
            />
            <div class="h-5 w-9 rounded-full bg-surface-2 after:absolute after:start-[2px] after:top-[2px]
                        after:h-4 after:w-4 after:rounded-full after:bg-muted after:transition-all after:content-['']
                        peer-checked:bg-accent peer-checked:after:translate-x-full peer-checked:after:bg-accent-ink" />
          </label>

          <!-- Number Input -->
          <input
            v-else-if="getType(setting.key) === 'number'"
            type="number"
            :value="getValue(setting)"
            class="w-24 rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text focus:border-transparent"
            @input="setValue(setting, ($event.target as HTMLInputElement).value)"
          />

          <!-- Text Input -->
          <input
            v-else
            type="text"
            :value="getValue(setting)"
            class="w-64 rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text focus:border-transparent"
            @input="setValue(setting, ($event.target as HTMLInputElement).value)"
          />

          <button
            :disabled="savingKeys.has(setting.key)"
            class="flex items-center gap-1.5 rounded-control border border-border px-3 py-1.5 text-xs font-medium text-text transition-colors hover:bg-surface-2 disabled:cursor-not-allowed disabled:opacity-50"
            @click="saveSetting(setting)"
          >
            <Spinner v-if="savingKeys.has(setting.key)" />Save
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
