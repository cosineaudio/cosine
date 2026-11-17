import type { AudioFormat } from '../types';

/** Aurora's band normalization, positive-delta threshold, and flare retrigger gate. */
export function createChladniAttacks(format: AudioFormat) {
  const bins = [40, 250, 2500, 12000].map(hz =>
    Math.max(1, Math.min(format.fftSize / 2, Math.round(hz * format.fftSize / format.sampleRate))));
  const previous = new Float32Array(3);
  const age = new Float32Array(3).fill(10);
  const attacks = new Float32Array(3);
  return {
    reset() { previous.fill(0); age.fill(10); attacks.fill(0); },
    update(frequency: Float32Array | undefined, dt: number) {
      for (let band = 0; band < 3; band++) {
        let sum = 0;
        const lo = bins[band]!, hi = bins[band + 1]!;
        if (frequency) for (let i = lo; i < hi; i++) sum += Math.pow(10, (frequency[i] ?? -100) / 20);
        const magnitude = sum / Math.max(1, hi - lo);
        const db = magnitude > 0 ? 20 * Math.log10(magnitude) : -75;
        const raw = Math.max(0, Math.min(1, (db + 65) / 40));
        const onset = Math.min(1, Math.max(0, raw - previous[band]! - 0.008) * 8) * raw;
        age[band]! += dt;
        attacks[band] = 0;
        if (onset > 0.06 && age[band]! > 0.2) {
          attacks[band] = Math.min(1, onset * 2.5);
          age[band] = 0;
        }
        previous[band] = raw;
      }
      return attacks;
    },
  };
}
