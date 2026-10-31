<script setup lang="ts">
import { computed, ref } from 'vue';

const props = withDefaults(
  defineProps<{
    /** Current position in seconds. */
    current: number;
    /** Total duration in seconds. */
    duration: number;
    /** Buffered fraction 0–1 (optional). */
    buffered?: number;
    /** Visual size. */
    size?: 'sm' | 'md';
  }>(),
  { buffered: 0, size: 'sm' },
);

const emit = defineEmits<{ seek: [seconds: number] }>();

const dragging = ref(false);
const dragValue = ref(0);
const trackEl = ref<HTMLElement | null>(null);

const playedPct = computed(() => {
  if (dragging.value) return clampPct(dragValue.value * 100);
  if (props.duration <= 0) return 0;
  return clampPct((props.current / props.duration) * 100);
});

const bufferedPct = computed(() => clampPct((props.buffered ?? 0) * 100));

function clampPct(n: number): number {
  return Math.max(0, Math.min(100, n));
}

function fractionFromEvent(clientX: number): number {
  const el = trackEl.value;
  if (!el) return 0;
  const rect = el.getBoundingClientRect();
  if (rect.width === 0) return 0;
  return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
}

function onPointerDown(e: PointerEvent) {
  if (props.duration <= 0) return;
  dragging.value = true;
  dragValue.value = fractionFromEvent(e.clientX);
  (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
}

function onPointerMove(e: PointerEvent) {
  if (!dragging.value) return;
  dragValue.value = fractionFromEvent(e.clientX);
}

function onPointerUp(e: PointerEvent) {
  if (!dragging.value) return;
  dragValue.value = fractionFromEvent(e.clientX);
  emit('seek', dragValue.value * props.duration);
  dragging.value = false;
  (e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId);
}

function onKeydown(e: KeyboardEvent) {
  if (props.duration <= 0) return;
  const step = e.shiftKey ? 30 : 5;
  if (e.key === 'ArrowLeft') {
    emit('seek', Math.max(0, props.current - step));
    e.preventDefault();
  } else if (e.key === 'ArrowRight') {
    emit('seek', Math.min(props.duration, props.current + step));
    e.preventDefault();
  } else if (e.key === 'Home') {
    emit('seek', 0);
    e.preventDefault();
  } else if (e.key === 'End') {
    emit('seek', props.duration);
    e.preventDefault();
  }
}
</script>

<template>
  <div
    ref="trackEl"
    role="slider"
    tabindex="0"
    :aria-valuemin="0"
    :aria-valuemax="Math.round(duration)"
    :aria-valuenow="Math.round(current)"
    aria-label="Seek"
    class="group/scrub relative flex w-full cursor-pointer touch-none select-none items-center focus:outline-none"
    :class="size === 'md' ? 'h-4' : 'h-3'"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="onPointerUp"
    @pointercancel="onPointerUp"
    @keydown="onKeydown"
  >
    <div
      class="relative w-full overflow-hidden rounded-full bg-surface-2"
      :class="size === 'md' ? 'h-1.5' : 'h-1'"
    >
      <!-- buffered range -->
      <div
        class="absolute inset-y-0 left-0 bg-text/15"
        :style="{ width: bufferedPct + '%' }"
      />
      <!-- played range -->
      <div
        class="absolute inset-y-0 left-0 bg-accent"
        :style="{ width: playedPct + '%' }"
      />
    </div>
    <!-- thumb -->
    <div
      class="pointer-events-none absolute top-1/2 -translate-x-1/2 -translate-y-1/2 rounded-full bg-accent opacity-0 transition-opacity group-hover/scrub:opacity-100 group-focus/scrub:opacity-100"
      :class="[size === 'md' ? 'h-3.5 w-3.5' : 'h-3 w-3', dragging ? '!opacity-100' : '']"
      :style="{ left: playedPct + '%' }"
    />
  </div>
</template>
