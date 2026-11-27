import { expect, it } from 'vitest';
import { createChladniAttacks } from '../renderers/chladniAttacks';

it('fires at band attacks without retriggering during sustained energy or decay', () => {
  const detector = createChladniAttacks({ sampleRate: 48000, fftSize: 2048 });
  const frequency = new Float32Array(1024).fill(-100);
  const events: number[][] = [];
  for (let frame = 0; frame < 100; frame++) {
    frequency.fill(-100);
    if (frame >= 15 && frame < 45) frequency.fill(-30, 2, 11);
    if (frame >= 60) frequency.fill(-30 - (frame - 60) * 0.4, 11, 107);
    const hits = detector.update(frequency, 1 / 60);
    for (let band = 0; band < 3; band++) if (hits[band]! > 0) events.push([frame, band]);
  }
  expect(events).toEqual([[15, 0], [60, 1]]);
});

it('does not treat a gradual increase in loudness as a percussion attack', () => {
  const detector = createChladniAttacks({ sampleRate: 48000, fftSize: 2048 });
  const frequency = new Float32Array(1024);
  for (let frame = 0; frame < 120; frame++) {
    frequency.fill(-65 + frame * 0.2);
    expect(Array.from(detector.update(frequency, 1 / 60))).toEqual([0, 0, 0]);
  }
});
