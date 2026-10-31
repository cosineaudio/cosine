<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { adminApi, type UpdatePlaybackDeviceRequest } from '../../api/admin'
import { getPlaybackDevices, type PlaybackDevice } from '../../api/playback'
import SkeletonTable from '../../components/ui/SkeletonTable.vue'
import Spinner from '../../components/ui/Spinner.vue'
import { useDeferredLoading } from '../../composables/useDeferredLoading'

/**
 * Every speaker cosine has ever seen, and the things an admin pins to one. The rows are
 * persisted, so a speaker that is unplugged today is still here to be configured - presence is
 * `online`, which is a separate fact from whether the device is known.
 */
const devices = ref<PlaybackDevice[]>([])
const loading = ref(true)
const showSkeleton = useDeferredLoading(loading)
const error = ref<string | null>(null)
const toast = ref<{ message: string; type: 'success' | 'error' } | null>(null)

const editing = ref<PlaybackDevice | null>(null)
const saving = ref(false)
const formError = ref<string | null>(null)

const formName = ref('')
const formProfile = ref('')
const formMaxVolume = ref(100)
/** Seconds in the form, milliseconds on the wire - nobody thinks about speaker lag in ms. */
const formLyricOffset = ref(0)
const formEnabled = ref(true)

/**
 * Offered as a list because these are the answers that come up, but the field stays free text:
 * the server validates the format against what ffmpeg is actually wired for, and hard-coding a
 * dropdown here would be a second, staler list of the same thing.
 */
const profileSuggestions = ['mp3:320', 'mp3:192', 'aac:256', 'flac', 'wav']

async function fetchDevices() {
  try {
    devices.value = await getPlaybackDevices()
    error.value = null
  } catch {
    error.value = 'Failed to load playback devices'
  } finally {
    loading.value = false
  }
}

function showToast(message: string, type: 'success' | 'error') {
  toast.value = { message, type }
  setTimeout(() => { toast.value = null }, 3000)
}

function openEdit(device: PlaybackDevice) {
  editing.value = device
  formError.value = null
  // The override, not the resolved name: prefilling with the advertised one would turn opening
  // the dialog and saving into setting an override that was never asked for.
  formName.value = device.displayName === device.advertisedName ? '' : device.displayName
  formProfile.value = device.transcodeProfile ?? ''
  formMaxVolume.value = device.maxVolumePercent
  formLyricOffset.value = device.lyricOffsetMs / 1000
  formEnabled.value = device.enabled
}

async function handleSave() {
  if (!editing.value) return

  const body: UpdatePlaybackDeviceRequest = {
    displayName: formName.value.trim(),
    transcodeProfile: formProfile.value.trim(),
    maxVolumePercent: Number(formMaxVolume.value),
    // Rounded because a step of 0.1s through a float lands on values like 1.9000000000000001,
    // and the server takes a whole number of milliseconds.
    lyricOffsetMs: Math.round(Number(formLyricOffset.value) * 1000),
    enabled: formEnabled.value,
  }

  saving.value = true
  formError.value = null
  try {
    const updated = await adminApi.updatePlaybackDevice(editing.value.id, body)
    devices.value = devices.value.map(d => (d.id === updated.id ? updated : d))
    editing.value = null
    showToast('Device updated', 'success')
  } catch (e) {
    // Kept in the dialog rather than thrown at a toast: the message names what is wrong with a
    // field the admin is still looking at.
    formError.value = e instanceof Error ? e.message : 'Failed to update device'
  } finally {
    saving.value = false
  }
}

/** "None" rather than "0.0 s": untuned is a state, not a measurement. */
function offsetLabel(ms: number): string {
  if (!ms) return 'None'
  return `${ms > 0 ? '+' : '−'}${(Math.abs(ms) / 1000).toFixed(1)} s`
}

function statusLabel(device: PlaybackDevice): string {
  if (!device.enabled) return 'Disabled'
  if (device.activeSessionId) return `In use by ${device.activeSessionOwner ?? 'someone'}`
  return device.online ? 'Online' : 'Offline'
}

function statusClass(device: PlaybackDevice): string {
  if (!device.enabled) return 'bg-surface-2 text-muted'
  if (device.online) return 'bg-accent text-accent-ink'
  return 'bg-surface-2 text-muted'
}

const anyDevices = computed(() => devices.value.length > 0)

onMounted(fetchDevices)
</script>

<template>
  <div class="mx-auto max-w-5xl space-y-6 p-6">
    <div>
      <h1 class="font-display text-2xl font-bold tracking-tight text-text">Playback devices</h1>
      <p class="mt-1 text-sm text-muted">
        Speakers cosine has found on the network. Settings here are per device and survive a
        restart.
      </p>
    </div>

    <div v-if="toast" :class="[
      'rounded-control px-3 py-2 text-sm',
      toast.type === 'success' ? 'border border-border bg-surface-2 text-text' : 'border border-danger/40 bg-danger/10 text-danger',
    ]">
      {{ toast.message }}
    </div>

    <SkeletonTable
      v-if="showSkeleton"
      :rows="5"
      :headers="['Device', 'Status', 'Sends', 'Max volume', 'Lyric offset', 'Last seen', '']"
    />
    <template v-else-if="loading" />
    <div v-else-if="error" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ error }}</div>

    <div v-else class="reveal overflow-hidden rounded-control border border-border bg-surface">
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-border text-left">
              <th class="label p-3">Device</th>
              <th class="label p-3">Status</th>
              <th class="label p-3">Sends</th>
              <th class="label p-3">Max volume</th>
              <th class="label p-3">Lyric offset</th>
              <th class="label p-3">Last seen</th>
              <th class="label w-20 p-3"></th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="device in devices"
              :key="device.id"
              class="border-b border-border/60 last:border-0 hover:bg-surface-2/40"
            >
              <td class="p-3">
                <div class="font-medium text-text">{{ device.displayName }}</div>
                <div class="text-xs text-faint">
                  {{ device.driverId }}
                  <span v-if="device.displayName !== device.advertisedName">
                    · advertises “{{ device.advertisedName }}”
                  </span>
                </div>
              </td>
              <td class="p-3">
                <span :class="[
                  'rounded-[3px] px-2 py-0.5 text-xs font-medium uppercase tracking-wide',
                  statusClass(device),
                ]">
                  {{ statusLabel(device) }}
                </span>
              </td>
              <td class="p-3 text-muted">{{ device.transcodeProfile || 'Original file' }}</td>
              <td class="p-3 text-muted">{{ device.maxVolumePercent }}%</td>
              <td class="p-3 tabular-nums text-muted">{{ offsetLabel(device.lyricOffsetMs) }}</td>
              <td class="p-3 text-muted">
                {{ device.lastSeenAt ? new Date(device.lastSeenAt).toLocaleString() : 'Never' }}
              </td>
              <td class="p-3">
                <button class="text-xs text-muted transition-colors hover:text-text" @click="openEdit(device)">
                  Edit
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-if="!anyDevices" class="p-6 text-center text-sm text-muted">
        No speakers found yet. Remote playback needs <code class="text-faint">Playback__AdvertisedBaseUrl</code>
        set and the server on the same network as the speakers.
      </div>
    </div>

    <!-- Edit Modal -->
    <div v-if="editing" class="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="editing = null" />
      <div class="relative w-full max-w-md rounded-control border border-border bg-surface p-6">
        <h2 class="mb-4 font-display text-lg font-semibold text-text">
          Edit {{ editing.displayName }}
        </h2>
        <form class="space-y-4" @submit.prevent="handleSave">
          <div>
            <label class="label mb-1.5 block">Name</label>
            <input
              v-model="formName"
              type="text"
              :placeholder="editing.advertisedName"
              class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent"
            />
            <p class="mt-1 text-xs text-faint">Leave blank to use the name the device advertises.</p>
          </div>

          <div>
            <label class="label mb-1.5 block">Transcode profile</label>
            <input
              v-model="formProfile"
              type="text"
              list="playback-profiles"
              placeholder="Original file"
              class="w-full rounded-control border border-border bg-surface-2 px-3 py-2 text-sm text-text placeholder:text-faint focus:border-transparent"
            />
            <datalist id="playback-profiles">
              <option v-for="p in profileSuggestions" :key="p" :value="p" />
            </datalist>
            <p class="mt-1 text-xs text-faint">
              <code>mp3:320</code>, <code>flac</code>, or blank to send the library file untouched.
              Set this for a device that refuses some of your files - hi-res FLAC is the usual one.
            </p>
          </div>

          <div>
            <label class="label mb-1.5 block">Max volume</label>
            <div class="flex items-center gap-3">
              <input
                v-model.number="formMaxVolume"
                type="range"
                min="0"
                max="100"
                class="flex-1 accent-white"
              />
              <span class="w-12 text-right text-sm tabular-nums text-muted">{{ formMaxVolume }}%</span>
            </div>
            <p class="mt-1 text-xs text-faint">Applies straight away, including to a session already playing.</p>
          </div>

          <div>
            <label class="label mb-1.5 block">Lyric offset</label>
            <div class="flex items-center gap-3">
              <input
                v-model.number="formLyricOffset"
                type="number"
                step="0.1"
                min="-10"
                max="10"
                class="w-28 rounded-control border border-border bg-surface-2 px-3 py-2 text-sm tabular-nums text-text focus:border-transparent"
              />
              <span class="text-sm text-muted">seconds</span>
            </div>
            <p class="mt-1 text-xs text-faint">
              A speaker buffers before it makes a sound, so synced lyrics in the web player run
              ahead of the room. Positive holds them back - try <code>2</code> for a Sonos and
              adjust by ear. Affects the lyrics display only, never the audio.
            </p>
          </div>

          <label class="flex items-center gap-2 text-sm text-text">
            <input v-model="formEnabled" type="checkbox" class="accent-white" />
            Offer this device
          </label>
          <p class="-mt-2 text-xs text-faint">
            Unchecking hides it from the output picker and refuses new sessions. Anything already
            playing on it is left alone.
          </p>

          <p v-if="formError" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
            {{ formError }}
          </p>

          <div class="flex justify-end gap-3">
            <button type="button" class="px-4 py-2 text-sm text-muted transition-colors hover:text-text" @click="editing = null">
              Cancel
            </button>
            <button type="submit" :disabled="saving" class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink disabled:opacity-50">
              <Spinner v-if="saving" />Save
            </button>
          </div>
        </form>
      </div>
    </div>
  </div>
</template>
