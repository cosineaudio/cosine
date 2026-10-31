<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed, watch } from 'vue'
import { adminApi, type ScanLog, type AudioAnalysisStatus } from '../../api/admin'
import ScanProgress from '../../components/ScanProgress.vue'
import SkeletonTable from '../../components/ui/SkeletonTable.vue'
import Spinner from '../../components/ui/Spinner.vue'
import { useSignalR } from '../../composables/useSignalR'
import { useDeferredLoading } from '../../composables/useDeferredLoading'

const scanLogs = ref<ScanLog[]>([])
const loading = ref(true)
const showSkeleton = useDeferredLoading(loading)
const error = ref<string | null>(null)
const startingScan = ref(false)
const rebuildingIndex = ref(false)
const rebuildDone = ref(false)
const audio = ref<AudioAnalysisStatus | null>(null)
const startingAnalysis = ref(false)

const isAnalyzingAudio = computed(() => audioAnalysisProgress.value !== null)

/**
 * Live progress while a pass is running, stored coverage otherwise. The hub reports how far
 * through the pending set the job is, which is not the same number as library coverage - a
 * re-analysis of 50 tracks in a 5000-track library is 100% of the job and 1% of the library.
 */
const audioPercent = computed(() => {
  const running = audioAnalysisProgress.value
  if (running) return running.total === 0 ? 0 : Math.round((running.processed / running.total) * 100)

  const status = audio.value
  if (!status || status.total === 0) return 0
  return Math.round((status.analyzed / status.total) * 100)
})

/**
 * Some steps have nothing to count - normalization spends most of its time computing statistics
 * over the whole library at once. Showing those as 0% reads as wedged, so the bar goes
 * indeterminate instead.
 */
const audioIndeterminate = computed(
  () => audioAnalysisProgress.value !== null && audioAnalysisProgress.value.total === 0,
)

/** Matches the wording the server uses in its own logs, so the two can be read together. */
function formatDuration(seconds: number): string {
  if (seconds >= 3600) return `${Math.floor(seconds / 3600)}h ${Math.round((seconds % 3600) / 60)}m`
  if (seconds >= 60) return `${Math.floor(seconds / 60)}m ${Math.round(seconds % 60)}s`
  return `${Math.round(seconds)}s`
}

/** Throughput, ETA and failures - whichever of them are known yet. */
const audioDetail = computed(() => {
  const running = audioAnalysisProgress.value
  if (!running) return ''

  const parts: string[] = []
  if (running.tracksPerMinute > 0) parts.push(`${Math.round(running.tracksPerMinute)} tracks/min`)
  if (running.etaSeconds !== null) parts.push(`${formatDuration(running.etaSeconds)} left`)
  if (running.failed > 0) parts.push(`${running.failed.toLocaleString()} failed`)
  return parts.join(' · ')
})

const { scanProgress, audioAnalysisProgress, audioAnalysisCompleted } = useSignalR()

let pollInterval: ReturnType<typeof setInterval> | null = null

const isScanning = computed(() => scanProgress.value !== null)

async function fetchLogs() {
  try {
    scanLogs.value = await adminApi.getScanLogs()
    error.value = null
  } catch (e) {
    error.value = 'Failed to load scan logs'
  } finally {
    loading.value = false
  }
}

async function startScan() {
  startingScan.value = true
  try {
    await adminApi.startScan()
  } catch (e) {
    error.value = 'Failed to start scan'
  } finally {
    startingScan.value = false
  }
}

async function rebuildIndex() {
  rebuildingIndex.value = true
  rebuildDone.value = false
  try {
    await adminApi.rebuildIndex()
    rebuildDone.value = true
  } catch (e) {
    error.value = 'Failed to rebuild search index'
  } finally {
    rebuildingIndex.value = false
  }
}

async function fetchAudioAnalysis() {
  try {
    audio.value = await adminApi.getAudioAnalysis()
  } catch {
    // Coverage is informational; failing to read it must not blank the scan log page.
    audio.value = null
  }
}

async function startAudioAnalysis() {
  startingAnalysis.value = true
  try {
    await adminApi.startAudioAnalysis()
  } catch (e) {
    error.value = 'Failed to start audio analysis'
  } finally {
    startingAnalysis.value = false
  }
}

function statusColor(status: string): string {
  if (status === 'completed') return 'bg-surface-2 text-text'
  if (status === 'failed') return 'bg-danger/15 text-danger'
  return 'bg-surface-2 text-muted'
}

// Coverage is read once on mount and again when the hub says a pass finished - the running
// figure comes from AudioAnalysisProgress, so there is nothing to poll for in between.
watch(audioAnalysisCompleted, fetchAudioAnalysis)

onMounted(() => {
  fetchLogs()
  fetchAudioAnalysis()
  pollInterval = setInterval(fetchLogs, isScanning.value ? 3000 : 15000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})
</script>

<template>
  <div class="mx-auto max-w-5xl space-y-6 p-6">
    <div class="flex items-center justify-between">
      <h1 class="font-display text-2xl font-bold tracking-tight text-text">Scanner</h1>
      <div class="flex items-center gap-2">
        <button
          :disabled="rebuildingIndex"
          class="flex items-center gap-2 rounded-control border border-border bg-surface px-4 py-2 text-sm font-medium text-text transition-transform hover:scale-[1.02] active:scale-95 disabled:cursor-not-allowed disabled:opacity-50"
          title="Reindex all artists, albums and tracks for search"
          @click="rebuildIndex"
        >
          <!-- the label is kept: unlike a plain submit, these words say which
               long-running server job is happening, not merely that one is -->
          <Spinner v-if="rebuildingIndex" />
          {{ rebuildingIndex ? 'Rebuilding…' : rebuildDone ? 'Index rebuilt ✓' : 'Rebuild search index' }}
        </button>
        <button
          :disabled="startingScan || isScanning"
          class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95 disabled:cursor-not-allowed disabled:opacity-50"
          @click="startScan"
        >
          <Spinner v-if="startingScan || isScanning" />
          {{ startingScan ? 'Starting…' : isScanning ? 'Scanning…' : 'Start scan' }}
        </button>
      </div>
    </div>

    <ScanProgress />

    <!-- Audio analysis coverage. Runs automatically after each scan; the button is here for a
         forced re-run, which is what an operator needs after the analyzer version changes. -->
    <div v-if="audio" class="reveal rounded-control border border-border bg-surface p-4">
      <div class="flex items-start justify-between gap-4">
        <div class="min-w-0">
          <h2 class="font-display text-sm font-semibold tracking-tight text-text">Audio analysis</h2>
          <p v-if="!audio.available" class="mt-1 text-sm text-muted">
            Unavailable - this server's PostgreSQL has no pgvector extension. Similar artists and
            autoplay fall back to tags and listening history.
          </p>
          <template v-else-if="audioAnalysisProgress">
            <p class="mt-1 text-sm text-muted">
              {{ audioAnalysisProgress.step
              }}<span v-if="audioAnalysisProgress.total > 0">
                - {{ audioAnalysisProgress.processed.toLocaleString() }} of
                {{ audioAnalysisProgress.total.toLocaleString() }}</span>…
            </p>
            <p v-if="audioDetail" class="mt-0.5 font-mono text-[11px] tabular-nums text-faint">
              {{ audioDetail }}
            </p>
            <!-- The last finished track. Between two counts a second apart this is the only
                 thing that visibly moves, and it is what tells an operator the sweep is
                 working through real files rather than spinning. -->
            <p
              v-if="audioAnalysisProgress.currentTrack"
              class="mt-0.5 truncate font-mono text-[11px] text-faint"
            >
              {{ audioAnalysisProgress.currentTrack }}
            </p>
          </template>
          <p v-else class="mt-1 text-sm text-muted">
            {{ audio.analyzed.toLocaleString() }} of {{ audio.total.toLocaleString() }} tracks
            analysed<span v-if="audio.pendingNormalization > 0">,
            {{ audio.pendingNormalization.toLocaleString() }} awaiting normalization</span>.
          </p>
        </div>

        <button
          v-if="audio.available"
          :disabled="startingAnalysis || isAnalyzingAudio"
          class="flex shrink-0 items-center gap-2 rounded-control border border-border bg-surface px-4 py-2 text-sm font-medium text-text transition-transform hover:scale-[1.02] active:scale-95 disabled:cursor-not-allowed disabled:opacity-50"
          title="Analyse any tracks with no current feature vector, then renormalize"
          @click="startAudioAnalysis"
        >
          <Spinner v-if="startingAnalysis || isAnalyzingAudio" />
          {{ startingAnalysis ? 'Queueing…' : isAnalyzingAudio ? 'Analysing…' : 'Analyse audio' }}
        </button>
      </div>

      <div
        v-if="audio.available && (audio.total > 0 || isAnalyzingAudio)"
        class="mt-3 h-1.5 overflow-hidden rounded-full bg-surface-2"
      >
        <div
          v-if="audioIndeterminate"
          class="h-full animate-pulse rounded-full bg-accent/60"
          style="width: 100%"
        />
        <!-- No transition while a pass runs: at two updates a second a 300ms tween is still
             animating when the next one lands, so the bar lags permanently behind the number
             printed next to it. The tween is worth having only for the jump to stored coverage. -->
        <div
          v-else
          class="h-full rounded-full bg-accent"
          :class="isAnalyzingAudio ? '' : 'transition-all'"
          :style="{ width: `${audioPercent}%` }"
        />
      </div>
    </div>

    <SkeletonTable
      v-if="showSkeleton"
      :rows="6"
      :headers="['Status', 'Started', 'Finished', 'Added', 'Removed', 'Updated', 'Error']"
    />
    <template v-else-if="loading" />
    <div v-else-if="error" class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ error }}</div>

    <div v-else class="reveal overflow-hidden rounded-control border border-border bg-surface">
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-border text-left">
              <th class="label p-3">Status</th>
              <th class="label p-3">Started</th>
              <th class="label p-3">Finished</th>
              <th class="label p-3">Added</th>
              <th class="label p-3">Removed</th>
              <th class="label p-3">Updated</th>
              <th class="label p-3">Error</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="log in scanLogs"
              :key="log.id"
              class="border-b border-border/60 last:border-0 hover:bg-surface-2/40"
            >
              <td class="p-3">
                <span :class="['rounded-[3px] px-2 py-0.5 text-xs font-medium uppercase tracking-wide', statusColor(log.status)]">
                  {{ log.status }}
                </span>
              </td>
              <td class="p-3 text-muted">{{ new Date(log.startedAt).toLocaleString() }}</td>
              <td class="p-3 text-muted">
                {{ log.finishedAt ? new Date(log.finishedAt).toLocaleString() : '-' }}
              </td>
              <td class="p-3 font-mono tabular-nums text-text">+{{ log.filesAdded }}</td>
              <td class="p-3 font-mono tabular-nums text-muted">-{{ log.filesRemoved }}</td>
              <td class="p-3 font-mono tabular-nums text-muted">~{{ log.filesUpdated }}</td>
              <td class="max-w-xs truncate p-3 text-xs text-danger">
                {{ log.errorMessage || '-' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-if="scanLogs.length === 0" class="p-6 text-center text-sm text-muted">
        No scan history yet.
      </div>
    </div>
  </div>
</template>
