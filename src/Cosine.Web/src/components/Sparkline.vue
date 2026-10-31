<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{
  values: number[]
  width?: number
  height?: number
}>(), {
  width: 160,
  height: 36,
})

// Build a smooth-enough polyline + filled area from the values, scaled to the box.
const path = computed(() => {
  const vals = props.values
  if (vals.length === 0) return { line: '', area: '' }
  const max = Math.max(...vals, 1)
  const min = Math.min(...vals, 0)
  const range = max - min || 1
  const n = vals.length
  const stepX = n > 1 ? props.width / (n - 1) : 0
  const pad = 3
  const usableH = props.height - pad * 2

  const points = vals.map((v, i) => {
    const x = n > 1 ? i * stepX : props.width / 2
    const y = pad + usableH - ((v - min) / range) * usableH
    return [x, y] as const
  })

  const line = points.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`).join(' ')
  const area = `${line} L${props.width},${props.height} L0,${props.height} Z`
  return { line, area }
})
</script>

<template>
  <svg
    :viewBox="`0 0 ${width} ${height}`"
    :width="width"
    :height="height"
    preserveAspectRatio="none"
    class="overflow-visible"
  >
    <path :d="path.area" fill="currentColor" fill-opacity="0.12" stroke="none" />
    <path :d="path.line" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round" />
  </svg>
</template>
