<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { usePlaybackStore } from '@/stores/playback';

const props = defineProps<{
  /** Art-derived dominant colour; the bars fade up into it. Null → mono white. */
  tint: string | null;
  /** Whether this tab is currently visible. The draw loop only runs when true. */
  active: boolean;
  /**
   * External analyser provider. When set, the component renders that graph instead of the
   * session player's (the guest party page taps its own audio element), and `playing` must
   * be supplied too - the playback store is not consulted at all.
   */
  analyser?: () => AnalyserNode | null;
  /** Play state driving the animation. Only read when `analyser` is provided. */
  playing?: boolean;
}>();

// A guest page has no session; only reach for the playback store when self-sourcing.
const player = props.analyser ? null : usePlaybackStore();
const isPlayingNow = () => (props.analyser ? props.playing === true : player!.isPlaying);

const canvas = ref<HTMLCanvasElement | null>(null);
const unavailable = ref(false);

const BAR_COUNT = 56;
// Log-spaced band edges. Skips sub-bass rumble and the empty top octave.
const F_MIN = 35;
const F_MAX = 17000;
// Amplitude window (dB) → a raw log-power level (getFloatFrequencyData returns
// real dB). MIN_DB is the noise floor: anything below it reads as zero height,
// so the resting state collapses instead of sitting as a permanent slab.
const MIN_DB = -70;
const MAX_DB = -25;
// Expansion curve: height = level^GAMMA. >1 crushes quiet bars toward zero while
// leaving peaks tall - kills the always-on bottom band, boosts contrast.
const GAMMA = 1.8;
// Pink-ish tilt: lift highs so bass stops pinning the low bars to the ceiling.
const TILT_DB_PER_OCT = 2.0;
// Asymmetric smoothing: snap up on transients, ease down slowly (VU-meter feel).
const ATTACK = 0.42;
const RELEASE = 0.09;
// Auto-gain: scale so the loudest recent bar sits near the top without clipping.
// Peak rises instantly, decays slowly, and floors so quiet passages stay quiet.
const AGC_TARGET = 0.92;
const AGC_DECAY = 0.992;
const AGC_FLOOR = 0.4;

let analyser: AnalyserNode | null = null;
let floatBuf: Float32Array<ArrayBuffer> | null = null;
const barVals = new Float32Array(BAR_COUNT);
const barTargets = new Float32Array(BAR_COUNT);
const rawLevels = new Float32Array(BAR_COUNT);
let agcPeak = AGC_FLOOR;
const bandLo = new Int32Array(BAR_COUNT);
const bandHi = new Int32Array(BAR_COUNT);
const bandTilt = new Float32Array(BAR_COUNT);

let rafId = 0;
let ctx: CanvasRenderingContext2D | null = null;
let ro: ResizeObserver | null = null;
let dpr = 1;
let baseColor = '#ffffff'; // accent - the bar tips (peaks = what matters)
let dimColor = '#888888';  // recedes - the bar base (always-present, least important)

const reduceMotion =
  typeof window !== 'undefined' &&
  window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

/** Precompute each bar's bin range and frequency tilt from the analyser's rate. */
function computeBands(): void {
  if (!analyser) return;
  const sampleRate = analyser.context.sampleRate;
  const fftSize = analyser.fftSize;
  const binHz = sampleRate / fftSize;
  const nBins = fftSize / 2;
  const ratio = F_MAX / F_MIN;
  for (let i = 0; i < BAR_COUNT; i++) {
    const fLo = F_MIN * Math.pow(ratio, i / BAR_COUNT);
    const fHi = F_MIN * Math.pow(ratio, (i + 1) / BAR_COUNT);
    let lo = Math.max(1, Math.round(fLo / binHz)); // skip DC bin
    let hi = Math.min(nBins, Math.max(lo + 1, Math.round(fHi / binHz)));
    bandLo[i] = lo;
    bandHi[i] = hi;
    bandTilt[i] = TILT_DB_PER_OCT * Math.log2(Math.sqrt(fLo * fHi) / F_MIN);
  }
}

function resize(): void {
  const el = canvas.value;
  if (!el) return;
  dpr = Math.min(window.devicePixelRatio || 1, 2);
  const rect = el.getBoundingClientRect();
  el.width = Math.max(1, Math.round(rect.width * dpr));
  el.height = Math.max(1, Math.round(rect.height * dpr));
  ctx = el.getContext('2d');
  draw();
}

function readColors(): void {
  if (typeof document === 'undefined') return;
  const root = getComputedStyle(document.documentElement);
  const text = root.getPropertyValue('--color-text').trim();
  const muted = root.getPropertyValue('--color-muted').trim();
  if (text) baseColor = text;
  if (muted) dimColor = muted;
}

/** Sample the spectrum into per-bar targets: linear-average each band, tilt, to dB,
 *  to a raw level, then auto-gain so the loudest recent bar sits near the top. */
function computeTargets(): void {
  if (!analyser || !floatBuf) return;
  analyser.getFloatFrequencyData(floatBuf);
  let frameMax = 0;
  for (let i = 0; i < BAR_COUNT; i++) {
    const lo = bandLo[i];
    const hi = bandHi[i];
    let sum = 0;
    for (let b = lo; b < hi; b++) sum += Math.pow(10, floatBuf[b] / 20);
    const mag = sum / (hi - lo);
    let db = mag > 0 ? 20 * Math.log10(mag) : MIN_DB;
    db += bandTilt[i];
    const raw = Math.max(0, (db - MIN_DB) / (MAX_DB - MIN_DB)); // open at the top
    rawLevels[i] = raw;
    if (raw > frameMax) frameMax = raw;
  }
  // Rolling peak: jump to any new max, otherwise decay toward the floor.
  agcPeak = Math.max(frameMax, agcPeak * AGC_DECAY, AGC_FLOOR);
  const gain = AGC_TARGET / agcPeak;
  for (let i = 0; i < BAR_COUNT; i++) {
    const v = rawLevels[i] * gain;
    barTargets[i] = Math.pow(v > 1 ? 1 : v, GAMMA);
  }
}

function ease(): void {
  for (let i = 0; i < BAR_COUNT; i++) {
    const t = barTargets[i];
    const v = barVals[i];
    barVals[i] = v + (t - v) * (t > v ? ATTACK : RELEASE);
  }
}

function draw(): void {
  const el = canvas.value;
  if (!el || !ctx) return;
  const w = el.width;
  const h = el.height;
  ctx.clearRect(0, 0, w, h);

  // Base recedes into the art colour (or muted grey); peaks rise into the white
  // accent - importance lives at the top of each bar, not the bottom.
  const grad = ctx.createLinearGradient(0, h, 0, 0);
  grad.addColorStop(0, props.tint ?? dimColor);
  grad.addColorStop(1, baseColor);
  ctx.fillStyle = grad;

  const gap = Math.max(1, Math.round(2 * dpr));
  const barW = (w - gap * (BAR_COUNT - 1)) / BAR_COUNT;
  const minH = Math.max(1, Math.round(2 * dpr));
  for (let i = 0; i < BAR_COUNT; i++) {
    const barH = Math.max(minH, barVals[i] * h);
    ctx.fillRect(i * (barW + gap), h - barH, barW, barH);
  }
}

function gateOpen(): boolean {
  return props.active && !unavailable.value && (typeof document === 'undefined' || !document.hidden);
}

/** Keep animating while playing, or while paused bars are still settling to zero. */
function shouldContinue(): boolean {
  if (!gateOpen()) return false;
  if (isPlayingNow()) return true;
  for (let i = 0; i < BAR_COUNT; i++) if (barVals[i] > 0.004) return true;
  return false;
}

function frame(): void {
  if (isPlayingNow()) computeTargets();
  else barTargets.fill(0);
  ease();
  draw();
  rafId = shouldContinue() ? requestAnimationFrame(frame) : 0;
}

/** Start or stop the loop based on the current gate. */
function sync(): void {
  if (reduceMotion) {
    if (props.active && isPlayingNow()) {
      computeTargets();
      barVals.set(barTargets);
    } else {
      barVals.fill(0);
    }
    draw();
    return;
  }
  if (gateOpen() && !rafId) rafId = requestAnimationFrame(frame);
  else if (!gateOpen() && rafId) {
    cancelAnimationFrame(rafId);
    rafId = 0;
    draw();
  }
}

function onVisibility(): void {
  sync();
}

onMounted(() => {
  readColors();
  analyser = props.analyser ? props.analyser() : player!.ensureAnalyser();
  if (!analyser) {
    unavailable.value = true;
  } else {
    floatBuf = new Float32Array(new ArrayBuffer(analyser.frequencyBinCount * 4));
    computeBands();
  }
  resize();
  ro = new ResizeObserver(resize);
  if (canvas.value) ro.observe(canvas.value);
  document.addEventListener('visibilitychange', onVisibility);
  sync();
});

onBeforeUnmount(() => {
  if (rafId) cancelAnimationFrame(rafId);
  rafId = 0;
  ro?.disconnect();
  document.removeEventListener('visibilitychange', onVisibility);
});

watch(() => [props.active, isPlayingNow()] as const, sync);
watch(() => props.tint, draw);
</script>

<template>
  <div class="flex h-full w-full items-end justify-center">
    <p v-if="unavailable" class="self-center text-center text-sm text-muted">
      Visualizer isn’t available for this source.
    </p>
    <canvas v-else ref="canvas" class="h-full w-full" aria-hidden="true" />
  </div>
</template>
