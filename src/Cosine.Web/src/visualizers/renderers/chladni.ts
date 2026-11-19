import type { VisualizerFactory } from '../types';
import { createCssColorBlend } from '../colorBlend';
import { createChladniAttacks } from './chladniAttacks';
import { modes } from './chladni/surfaces';
import { createSimulation } from './chladni/wasm';
import { createPainter } from './chladni/webgl';
export { prepareSimulation } from './chladni/wasm';

/** Track-seeded standing waves with audio-driven particle motion. */
export const create: VisualizerFactory = (canvas, format) => {
  const simulation = createSimulation();
  const { vx, vy, weights, modeOrder } = simulation;
  const painter = createPainter(canvas, simulation);
  const tint = createCssColorBlend();
  let initialized = false;
  let lastTrack: string | null | undefined;
  const detector = createChladniAttacks(format);
  let activity = 0, radialPulse = 0, widthPulse = 0, vibrationPhase = 0, evolution = 0, settlingTime = 0;
  let reduced = false, dpr = 1;
  const bins = [40, 65, 100, 150, 220, 310, 440, 620, 880, 1240, 1760, 2490, 3520, 4980, 7040, 9950, 14000].map(hz =>
    Math.max(1, Math.min(format.fftSize / 2, Math.round(hz * format.fftSize / format.sampleRate))));
  const energies = new Float32Array(modes.length);
  function initialize(id: string | null) {
    initialized = true;
    lastTrack = id;
    simulation.resetRandom(id ?? 'chladni');
    for (let i = 0; i < modes.length; i++) modeOrder[i] = i;
    for (let i = modeOrder.length - 1; i > 0; i--) {
      const j = Math.floor(simulation.random() * (i + 1));
      [modeOrder[i], modeOrder[j]] = [modeOrder[j]!, modeOrder[i]!];
    }
    weights.fill(0); weights[3] = 1;
    evolution = 0; vibrationPhase = 0;
    detector.reset();
    activity = 0; radialPulse = 0; widthPulse = 0;
    simulation.step(0, 0, 0, 0, 0, 0, true, false);
    simulation.seed(); painter.reseed();
    settlingTime = 1.2;
  }
  // The host calls setTrack immediately after create. Do not seed the fallback first.
  function ensureSeeded() { if (!initialized) initialize(null); }
  return {
    setTrack(id) { if (!initialized || id !== lastTrack) initialize(id); },
    resize(size) { dpr = size.dpr; },
    update(audio, reducedMotion, dt) {
      ensureSeeded();
      reduced = reducedMotion;
      tint.update(dt, reducedMotion);
      if (reducedMotion) { detector.reset(); settlingTime = 0; activity = 0; radialPulse = 0; widthPulse = 0; vx.fill(0); vy.fill(0); return; }
      const elapsed = Math.max(0, Math.min(0.05, dt));
      let power = 0;
      const waveform = audio?.waveform;
      if (waveform) for (let i = 0; i < waveform.length; i++) power += waveform[i]! * waveform[i]!;
      const rms = Math.sqrt(power / Math.max(1, waveform?.length ?? 0));
      const attacks = detector.update(audio?.frequency, dt);
      const kick = attacks[0]!;
      const snare = Math.max(attacks[1]!, attacks[2]!);
      const targetActivity = Math.min(1, Math.sqrt(Math.max(0, rms - 0.002) * 5));
      activity += (targetActivity - activity) * (1 - Math.exp(-elapsed / 0.18));
      if (activity < 0.001) activity = 0;
      vibrationPhase += elapsed * activity * 9;
      widthPulse = Math.max(kick, snare, widthPulse * Math.exp(-elapsed / 0.24));
      if (widthPulse < 0.001) widthPulse = 0;
      // Return each radial displacement to the modal field between hits, so
      // repeated kicks cannot permanently pump the population to the perimeter.
      radialPulse = Math.max(-0.05, Math.min(0.05,
        radialPulse * Math.exp(-elapsed / 0.18) + (kick - snare) * 0.025));
      if (Math.abs(radialPulse) < 0.0001) radialPulse = 0;
      let remix = false;
      if (audio?.frequency) {
        let total = 0;
        for (let band = 0; band < modes.length; band++) {
          let sum = 0;
          const lo = bins[band]!, hi = bins[band + 1]!;
          for (let i = lo; i < hi; i++) sum += Math.pow(10, (audio.frequency[i] ?? -100) / 10);
          // Mean band power avoids favoring the wider high-frequency bands.
          const power = sum / Math.max(1, hi - lo);
          energies[band] = power > 1e-8 ? Math.pow(power, 0.55) : 0;
          total += energies[band]!;
        }
        if (total > 0) {
          evolution = (evolution + elapsed * (0.085 + activity * 0.045)) % modes.length;
          const blend = 1 - Math.exp(-elapsed / 0.65);
          for (let band = 0; band < modes.length; band++) weights[band]! += (energies[band]! / total - weights[band]!) * blend;
          remix = true;
        }
      }
      if (audio) settlingTime = 1.2;
      else settlingTime = Math.max(0, settlingTime - elapsed);
      simulation.step(evolution, elapsed, activity, Math.max(kick, snare), widthPulse, vibrationPhase, remix, settlingTime > 0);
    },
    draw(theme) {
      ensureSeeded();
      painter.draw(tint.color(theme.tint ?? theme.text), dpr, radialPulse);
    },
    get settling() { return tint.settling || (!reduced && settlingTime > 0); },
    dispose() { painter.dispose(); },
  };
};
