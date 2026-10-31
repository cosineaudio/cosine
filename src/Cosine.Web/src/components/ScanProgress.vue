<script setup lang="ts">
import { computed } from 'vue'
import { useSignalR } from '../composables/useSignalR'

const { scanProgress: scan } = useSignalR()

const isScanning = computed(() => scan.value !== null)
const percent = computed(() => {
  if (!scan.value || scan.value.total === 0) return 0
  return Math.round((scan.value.current / scan.value.total) * 100)
})

const phaseLabel = computed(() => {
  if (!scan.value) return ''
  const phase = scan.value.phase
  if (phase === 'starting') return 'Initializing…'
  if (phase === 'scanning') return 'Scanning files…'
  if (phase === 'indexing') return 'Indexing…'
  return phase
})
</script>

<template>
  <div v-if="isScanning" class="rounded-control border border-border bg-surface p-4">
    <div class="mb-2 flex items-center justify-between">
      <span class="text-sm font-medium text-text">{{ phaseLabel }}</span>
      <span class="font-mono text-xs tabular-nums text-muted">{{ percent }}%</span>
    </div>
    <div class="h-1.5 w-full overflow-hidden rounded-full bg-surface-2">
      <div
        class="h-full rounded-full bg-accent transition-all duration-300"
        :style="{ width: percent + '%' }"
      />
    </div>
    <p
      v-if="scan?.currentFile"
      class="mt-2 truncate font-mono text-[11px] text-faint"
    >
      {{ scan.currentFile }}
    </p>
  </div>
</template>
