<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import {
  adminApi,
  type LibraryStats,
  type ScanLog,
  type LiveMetrics,
  type StatSnapshot,
  type Recap,
} from '../../api/admin'
import ScanProgress from '../../components/ScanProgress.vue'
import Sparkline from '../../components/Sparkline.vue'
import Skeleton from '../../components/ui/Skeleton.vue'
import Spinner from '../../components/ui/Spinner.vue'
import { useSignalR } from '../../composables/useSignalR'
import { useDeferredLoading } from '../../composables/useDeferredLoading'
import { shimmerDelay } from '../../lib/skeleton'

const stats = ref<LibraryStats | null>(null)
const scanLogs = ref<ScanLog[]>([])
const metrics = ref<LiveMetrics | null>(null)
const history = ref<StatSnapshot[]>([])
const recap = ref<Recap | null>(null)
const loading = ref(true)
const showSkeleton = useDeferredLoading(loading)
const error = ref<string | null>(null)
const startingScan = ref(false)

const { scanProgress } = useSignalR()

let pollInterval: ReturnType<typeof setInterval> | null = null

async function fetchData() {
  try {
    const [s, logs, m, h] = await Promise.all([
      adminApi.getStats(),
      adminApi.getScanLogs(),
      adminApi.getLiveMetrics(),
      adminApi.getStatsHistory(24),
    ])
    stats.value = s
    scanLogs.value = logs.slice(0, 5)
    metrics.value = m
    history.value = h
    error.value = null
  } catch (e) {
    error.value = 'Failed to load dashboard data'
  } finally {
    loading.value = false
  }
}

async function fetchRecap() {
  try {
    recap.value = await adminApi.getRecap()
  } catch {
    // Recap is best-effort; a failure shouldn't blank the dashboard.
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

function formatSize(bytes: number): string {
  if (bytes === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const i = Math.floor(Math.log(bytes) / Math.log(1024))
  return (bytes / Math.pow(1024, i)).toFixed(1) + ' ' + units[i]
}

function pct(v: number): string {
  return (v * 100).toFixed(1) + '%'
}

function ms(v: number): string {
  if (v <= 0) return '-'
  return v >= 1000 ? (v / 1000).toFixed(2) + ' s' : Math.round(v) + ' ms'
}

function num(v: number): string {
  return v.toLocaleString()
}

function formatMinutes(mins: number): string {
  if (mins < 60) return `${mins} min`
  const h = Math.floor(mins / 60)
  if (h < 24) return `${h}h ${mins % 60}m`
  const d = Math.floor(h / 24)
  return `${d}d ${h % 24}h`
}

function statusColor(status: string): string {
  if (status === 'completed') return 'text-text'
  if (status === 'failed') return 'text-danger'
  return 'text-muted'
}

const statTiles = [
  { key: 'artistCount', label: 'Artists' },
  { key: 'albumCount', label: 'Albums' },
  { key: 'trackCount', label: 'Tracks' },
] as const

// Per-hour trend series from the persisted snapshots.
const streamSeries = computed(() => history.value.map((h) => h.streamRequests))
const cacheHitSeries = computed(() =>
  history.value.map((h) => {
    const total = h.cacheHits + h.cacheMisses
    return total === 0 ? 0 : (h.cacheHits / total) * 100
  }),
)
const ttfbSeries = computed(() => history.value.map((h) => h.ttfbP95Ms))

// `live` is null on the cumulative tiles and a boolean on the instantaneous one, which is what
// decides whether the tile wears a status dot at all.
type PerfTile = { label: string; value: string; sub: string; live: boolean | null }

const perfTiles = computed<PerfTile[]>(() => {
  const m = metrics.value
  if (!m) return []
  return [
    {
      label: 'Transcoding now',
      value: num(m.transcoding.active),
      sub: `of ${num(m.transcoding.limit)} slots`,
      live: m.transcoding.active > 0,
    },
    { label: 'Cache hit rate', value: pct(m.metrics.cacheHitRate), sub: `${num(m.metrics.cacheHits)} hits / ${num(m.metrics.cacheMisses)} miss`, live: null },
    { label: 'Transcoded', value: pct(m.metrics.transcodePercent), sub: `${num(m.metrics.transcodeCount)} of ${num(m.metrics.streamRequests)} streams`, live: null },
    { label: 'Stream TTFB p50', value: ms(m.metrics.ttfbP50Ms), sub: `p95 ${ms(m.metrics.ttfbP95Ms)}`, live: null },
    { label: 'Redis keys', value: num(m.redisKeyCount), sub: `${num(m.metrics.transcodeCacheHits)} transcode cache hits`, live: null },
  ]
})

onMounted(() => {
  fetchData()
  fetchRecap()
  pollInterval = setInterval(fetchData, 15000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})
</script>

<template>
  <div class="mx-auto max-w-5xl space-y-6 p-6">
    <div class="flex items-center justify-between">
      <h1 class="font-display text-2xl font-bold tracking-tight text-text">Dashboard</h1>
      <button
        :disabled="startingScan || scanProgress !== null"
        class="flex items-center gap-2 rounded-control bg-accent px-4 py-2 text-sm font-medium text-accent-ink transition-transform hover:scale-[1.02] active:scale-95 disabled:cursor-not-allowed disabled:opacity-50"
        @click="startScan"
      >
        <Spinner v-if="startingScan || scanProgress" />
        {{ startingScan ? 'Starting…' : scanProgress ? 'Scanning…' : 'Start scan' }}
      </button>
    </div>

    <div v-if="showSkeleton" class="grid grid-cols-2 gap-4 lg:grid-cols-4">
      <div
        v-for="i in 5"
        :key="i"
        class="rounded-control border border-border bg-surface p-4"
        :style="shimmerDelay(i)"
      >
        <Skeleton class="h-3 w-20" />
        <!-- the tile's number is text-3xl; reserve its line box, not just a bar -->
        <div class="mt-2 flex h-9 items-center">
          <Skeleton class="h-7 w-24" />
        </div>
      </div>
    </div>
    <template v-else-if="loading" />

    <template v-else-if="error">
      <div class="rounded-control border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">{{ error }}</div>
    </template>

    <template v-else-if="stats">
      <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
        <div v-for="tile in statTiles" :key="tile.key" class="rounded-control border border-border bg-surface p-4">
          <div class="label">{{ tile.label }}</div>
          <div class="mt-2 font-mono text-3xl font-semibold tabular-nums text-text">{{ stats[tile.key].toLocaleString() }}</div>
        </div>
        <div class="rounded-control border border-border bg-surface p-4">
          <div class="label">Total size</div>
          <div class="mt-2 font-mono text-3xl font-semibold tabular-nums text-text">{{ formatSize(stats.totalSizeBytes) }}</div>
        </div>
      </div>

      <ScanProgress />

      <!-- Performance -->
      <section v-if="metrics" class="space-y-4">
        <div class="flex items-baseline justify-between">
          <h2 class="font-display text-lg font-semibold text-text">Performance</h2>
          <span class="text-xs text-faint">counters since last restart</span>
        </div>

        <div class="grid grid-cols-2 gap-4 lg:grid-cols-5">
          <div v-for="tile in perfTiles" :key="tile.label" class="rounded-control border border-border bg-surface p-4">
            <div class="label flex items-center gap-1.5">
              <span
                v-if="tile.live !== null"
                :class="['h-1.5 w-1.5 shrink-0 rounded-full', tile.live ? 'animate-pulse bg-accent' : 'bg-faint/50']"
              />
              {{ tile.label }}
            </div>
            <div class="mt-2 font-mono text-2xl font-semibold tabular-nums text-text">{{ tile.value }}</div>
            <div class="mt-1 text-xs text-faint">{{ tile.sub }}</div>
          </div>
        </div>

        <div v-if="history.length > 1" class="grid gap-4 sm:grid-cols-3">
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">Stream requests / hr</div>
            <div class="mt-3 text-accent"><Sparkline :values="streamSeries" :width="220" :height="40" /></div>
          </div>
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">Cache hit rate / hr</div>
            <div class="mt-3 text-accent"><Sparkline :values="cacheHitSeries" :width="220" :height="40" /></div>
          </div>
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">TTFB p95 / hr</div>
            <div class="mt-3 text-accent"><Sparkline :values="ttfbSeries" :width="220" :height="40" /></div>
          </div>
        </div>
        <p v-else class="text-xs text-faint">
          Trend charts appear once a few hourly snapshots have been recorded.
        </p>
      </section>

      <!-- Listening recap -->
      <section v-if="recap && recap.totalPlays > 0" class="space-y-4">
        <h2 class="font-display text-lg font-semibold text-text">Listening recap</h2>

        <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">Total plays</div>
            <div class="mt-2 font-mono text-2xl font-semibold tabular-nums text-text">{{ num(recap.totalPlays) }}</div>
          </div>
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">Listening time</div>
            <div class="mt-2 font-mono text-2xl font-semibold tabular-nums text-text">{{ formatMinutes(recap.estimatedMinutes) }}</div>
          </div>
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">Unique tracks</div>
            <div class="mt-2 font-mono text-2xl font-semibold tabular-nums text-text">{{ num(recap.uniqueTracks) }}</div>
          </div>
          <div class="rounded-control border border-border bg-surface p-4">
            <div class="label">Unique artists</div>
            <div class="mt-2 font-mono text-2xl font-semibold tabular-nums text-text">{{ num(recap.uniqueArtists) }}</div>
          </div>
        </div>

        <div class="grid gap-4 md:grid-cols-2">
          <div class="rounded-control border border-border bg-surface p-4">
            <h3 class="mb-3 text-sm font-semibold text-text">Top tracks</h3>
            <ol class="space-y-2">
              <li v-for="(t, i) in recap.topTracks" :key="t.id" class="flex items-center gap-3 text-sm">
                <span class="w-4 text-right font-mono text-xs text-faint">{{ i + 1 }}</span>
                <span class="min-w-0 flex-1 truncate">
                  <span class="text-text">{{ t.name }}</span>
                  <span v-if="t.subtitle" class="text-muted"> · {{ t.subtitle }}</span>
                </span>
                <span class="font-mono text-xs tabular-nums text-faint">{{ num(t.playCount) }}</span>
              </li>
            </ol>
          </div>

          <div class="rounded-control border border-border bg-surface p-4">
            <h3 class="mb-3 text-sm font-semibold text-text">Top artists</h3>
            <ol class="space-y-2">
              <li v-for="(a, i) in recap.topArtists" :key="a.id" class="flex items-center gap-3 text-sm">
                <span class="w-4 text-right font-mono text-xs text-faint">{{ i + 1 }}</span>
                <span class="min-w-0 flex-1 truncate text-text">{{ a.name }}</span>
                <span class="font-mono text-xs tabular-nums text-faint">{{ num(a.playCount) }}</span>
              </li>
            </ol>
          </div>
        </div>

        <div v-if="recap.topGenres.length" class="rounded-control border border-border bg-surface p-4">
          <h3 class="mb-3 text-sm font-semibold text-text">Top genres</h3>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="g in recap.topGenres"
              :key="g.id"
              class="rounded-control border border-border px-2.5 py-1 text-xs text-muted"
            >
              {{ g.name }} <span class="font-mono tabular-nums text-faint">{{ num(g.playCount) }}</span>
            </span>
          </div>
        </div>
      </section>

      <div class="rounded-control border border-border bg-surface p-4">
        <h2 class="mb-3 font-display text-lg font-semibold text-text">Recent scans</h2>
        <div v-if="scanLogs.length === 0" class="text-sm text-muted">
          No scan history yet.
        </div>
        <div v-else class="space-y-2">
          <div
            v-for="log in scanLogs"
            :key="log.id"
            class="flex items-center justify-between text-sm"
          >
            <div class="flex items-center gap-3">
              <span :class="['text-xs font-medium uppercase tracking-wide', statusColor(log.status)]">
                {{ log.status }}
              </span>
              <span class="text-muted">
                {{ new Date(log.startedAt).toLocaleString() }}
              </span>
            </div>
            <span class="font-mono text-xs tabular-nums text-faint">
              +{{ log.filesAdded }} / -{{ log.filesRemoved }} / ~{{ log.filesUpdated }}
            </span>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>
