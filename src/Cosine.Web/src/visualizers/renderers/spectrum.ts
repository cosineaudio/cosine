import type { VisualizerFactory, VisualizerTheme } from '../types';
import { createCssColorBlend } from '../colorBlend';

// Extracted from the original Visualizer.vue. Keep Spectrum's math and drawing unchanged.
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


export const create: VisualizerFactory = (canvas, format) => {
  const context = canvas.getContext('2d');
  if (!context) throw new Error('Canvas 2D is unavailable');
  const ctx = context;
  let dpr = 1;
  const tintBlend = createCssColorBlend();
  const barVals = new Float32Array(BAR_COUNT);
  const barTargets = new Float32Array(BAR_COUNT);
  const rawLevels = new Float32Array(BAR_COUNT);
  let agcPeak = AGC_FLOOR;
  const bandLo = new Int32Array(BAR_COUNT);
  const bandHi = new Int32Array(BAR_COUNT);
  const bandTilt = new Float32Array(BAR_COUNT);

  /** Precompute each bar's bin range and frequency tilt from the analyser's rate. */
  function computeBands(): void {
    const sampleRate = format.sampleRate;
    const fftSize = format.fftSize;
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

  /** Sample the spectrum into per-bar targets: linear-average each band, tilt, to dB,
   *  to a raw level, then auto-gain so the loudest recent bar sits near the top. */
  function computeTargets(floatBuf: Float32Array): void {
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

  function draw(theme: VisualizerTheme): void {
    const el = canvas;
    const w = el.width;
    const h = el.height;
    ctx.clearRect(0, 0, w, h);

    // Base recedes into the art colour (or muted grey); peaks rise into the white
    // accent - importance lives at the top of each bar, not the bottom.
    const grad = ctx.createLinearGradient(0, h, 0, 0);
    grad.addColorStop(0, tintBlend.color(theme.tint ?? theme.muted));
    grad.addColorStop(1, theme.text);
    ctx.fillStyle = grad;

    const gap = Math.max(1, Math.round(2 * dpr));
    const barW = (w - gap * (BAR_COUNT - 1)) / BAR_COUNT;
    const minH = Math.max(1, Math.round(2 * dpr));
    for (let i = 0; i < BAR_COUNT; i++) {
      const barH = Math.max(minH, barVals[i] * h);
      ctx.fillRect(i * (barW + gap), h - barH, barW, barH);
    }
  }


  computeBands();
  return {
    resize(size) { dpr = size.dpr; },
    update(audio, reducedMotion, dt) {
      tintBlend.update(dt, reducedMotion);
      if (audio) computeTargets(audio.frequency!);
      else barTargets.fill(0);
      if (reducedMotion) barVals.set(barTargets);
      else ease();
    },
    draw,
    get settling() { return tintBlend.settling || barVals.some(v => v > 0.004); },
    dispose() { ctx.clearRect(0, 0, canvas.width, canvas.height); },
  };
};
