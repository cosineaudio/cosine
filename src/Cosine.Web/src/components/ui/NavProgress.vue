<script setup lang="ts">
import { ref, watch, onUnmounted } from 'vue';
import { useNavProgress } from '@/composables/useNavProgress';

/**
 * Hairline navigation indicator, sitting on the topbar's bottom border.
 *
 * It creeps to 85% and waits there rather than pretending to know how far along
 * a chunk fetch is; landing snaps it to 100% and fades it out. Below the delay
 * it never renders at all, so warm navigations - which is nearly all of them -
 * stay completely silent.
 */
withDefaults(
  defineProps<{
    /**
     * Which edge of the positioned ancestor to sit on. 'bottom' rides the
     * topbar's border hairline; 'top' is for shells without a topbar.
     */
    edge?: 'top' | 'bottom';
  }>(),
  { edge: 'bottom' },
);

const DELAY_MS = 250;
/** Long enough to read as "still going", short enough not to feel stuck. */
const CREEP_MS = 600;
const CREEP_TARGET = 85;
const FADE_MS = 200;

const navigating = useNavProgress();

const shown = ref(false);
const width = ref(0);
const leaving = ref(false);

let delayTimer: ReturnType<typeof setTimeout> | null = null;
let hideTimer: ReturnType<typeof setTimeout> | null = null;
let raf = 0;

function clearTimers() {
  if (delayTimer !== null) clearTimeout(delayTimer);
  if (hideTimer !== null) clearTimeout(hideTimer);
  cancelAnimationFrame(raf);
  delayTimer = null;
  hideTimer = null;
}

watch(navigating, (busy) => {
  clearTimers();

  if (busy) {
    if (shown.value) return;
    delayTimer = setTimeout(() => {
      shown.value = true;
      leaving.value = false;
      width.value = 0;
      // Two frames: one for the element to exist at 0%, one for the width
      // change to be a transition rather than an initial value.
      raf = requestAnimationFrame(() => {
        raf = requestAnimationFrame(() => { width.value = CREEP_TARGET; });
      });
    }, DELAY_MS);
    return;
  }

  if (!shown.value) return; // navigation finished inside the delay
  width.value = 100;
  leaving.value = true;
  hideTimer = setTimeout(() => {
    shown.value = false;
    width.value = 0;
    leaving.value = false;
  }, FADE_MS);
});

onUnmounted(clearTimers);
</script>

<template>
  <div
    v-if="shown"
    class="nav-progress pointer-events-none absolute inset-x-0 z-50 h-0.5 bg-accent"
    :class="edge === 'top' ? 'top-0' : 'bottom-0'"
    :style="{
      width: `${width}%`,
      opacity: leaving ? 0 : 1,
      '--np-duration': leaving ? '150ms' : `${CREEP_MS}ms`,
    }"
    role="progressbar"
    aria-label="Loading page"
  />
</template>

<style scoped>
.nav-progress {
  transition:
    width var(--np-duration, 600ms) var(--ease-out),
    opacity 200ms linear;
}

/*
 * With motion suppressed the creep conveys nothing - the global reduce rule
 * already collapses the transition - so show it as a settled full-width bar
 * that simply appears and disappears.
 */
@media (prefers-reduced-motion: reduce) {
  .nav-progress {
    width: 100% !important;
  }
}
</style>
