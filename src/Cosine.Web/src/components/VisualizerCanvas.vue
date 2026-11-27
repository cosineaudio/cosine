<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { usePlaybackStore } from '@/stores/playback';
import type { AudioFrame, VisualizerDefinition, VisualizerRenderer, VisualizerTheme } from '@/visualizers/types';

const props = defineProps<{
  definition: VisualizerDefinition;
  tint: string | null;
  trackId?: string | null;
  active: boolean;
  analyser?: () => AnalyserNode | null;
  playing?: boolean;
}>();
const player = props.analyser ? null : usePlaybackStore();
const isPlaying = () => props.analyser ? props.playing === true : player!.isPlaying;
const canvas = ref<HTMLCanvasElement | null>(null);
const unavailable = ref(false);
let renderer: VisualizerRenderer | null = null;
let analyser: AnalyserNode | null = null;
const audio: AudioFrame = {};
const theme: VisualizerTheme = { tint: props.tint, text: '#ffffff', muted: '#888888' };
let rafId = 0;
let previousTime = 0;
let observer: ResizeObserver | null = null;
let disposed = false;
const motion = window.matchMedia?.('(prefers-reduced-motion: reduce)');

function draw() { renderer?.draw(theme); }
function resize() {
  const el = canvas.value;
  if (!el || !renderer) return;
  let dpr = Math.min(window.devicePixelRatio || 1, 2);
  const rect = el.getBoundingClientRect();
  if (props.definition.maxPixels) {
    dpr = Math.min(dpr, Math.sqrt(props.definition.maxPixels / Math.max(1, rect.width * rect.height)));
  }
  el.width = Math.max(1, Math.round(rect.width * dpr));
  el.height = Math.max(1, Math.round(rect.height * dpr));
  renderer.resize({ width: el.width, height: el.height, dpr });
  draw();
}
function sample(): AudioFrame | null {
  if (!analyser || !isPlaying()) return null;
  if (audio.frequency) analyser.getFloatFrequencyData(audio.frequency);
  if (audio.waveform) analyser.getFloatTimeDomainData(audio.waveform);
  return audio;
}
function gateOpen() { return props.active && !document.hidden && !!renderer; }
function frame(time: number) {
  rafId = 0;
  if (!gateOpen()) return;
  const dt = previousTime ? Math.min((time - previousTime) / 1000, 0.1) : 1 / 60;
  previousTime = time;
  renderer!.update(sample(), false, dt);
  draw();
  if (isPlaying() || renderer!.settling) rafId = requestAnimationFrame(frame);
}
function stop() {
  if (rafId) cancelAnimationFrame(rafId);
  rafId = 0;
  previousTime = 0;
}
function sync() {
  if (motion?.matches) {
    stop();
    renderer?.update(props.active ? sample() : null, true, 1 / 60);
    draw();
    return;
  }
  if (gateOpen() && !rafId) rafId = requestAnimationFrame(frame);
  else if (!gateOpen() && rafId) { stop(); draw(); }
}

onMounted(async () => {
  const root = getComputedStyle(document.documentElement);
  theme.text = root.getPropertyValue('--color-text').trim() || theme.text;
  theme.muted = root.getPropertyValue('--color-muted').trim() || theme.muted;
  // Tap synchronously, before awaiting a module, preserving the opening user gesture.
  analyser = props.analyser ? props.analyser() : player!.ensureAnalyser();
  if (!analyser) { unavailable.value = true; return; }
  try {
    const module = await props.definition.load();
    if (disposed || !canvas.value) return;
    if (props.definition.audio !== 'waveform') audio.frequency = new Float32Array(analyser.frequencyBinCount);
    if (props.definition.audio !== 'frequency') audio.waveform = new Float32Array(analyser.fftSize);
    renderer = module.create(canvas.value, { sampleRate: analyser.context.sampleRate, fftSize: analyser.fftSize });
    renderer.setTrack?.(props.trackId ?? null);
    resize();
    observer = new ResizeObserver(resize);
    observer.observe(canvas.value);
    document.addEventListener('visibilitychange', sync);
    motion?.addEventListener('change', sync);
    sync();
  } catch (error) {
    if (disposed) return;
    stop();
    observer?.disconnect();
    renderer?.dispose();
    renderer = null;
    unavailable.value = true;
    console.error(`Unable to load visualizer ${props.definition.id}`, error);
  }
});
onBeforeUnmount(() => {
  disposed = true;
  stop();
  observer?.disconnect();
  document.removeEventListener('visibilitychange', sync);
  motion?.removeEventListener('change', sync);
  renderer?.dispose();
  renderer = null;
});
watch(() => [props.active, isPlaying()] as const, sync);
watch(() => props.trackId, trackId => {
  renderer?.setTrack?.(trackId ?? null);
  sync();
});
watch(() => props.tint, tint => {
  theme.tint = tint;
  draw();
  // A renderer may begin a color transition even while playback is paused.
  if (renderer?.settling) sync();
});
</script>

<template>
  <div class="flex h-full w-full items-end justify-center">
    <p v-if="unavailable" class="self-center text-center text-sm text-muted">
      Visualizer isn’t available for this source.
    </p>
    <canvas v-else ref="canvas" class="h-full w-full" aria-hidden="true" />
  </div>
</template>
