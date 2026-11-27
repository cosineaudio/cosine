import { createHash } from 'node:crypto';
import { describe, expect, it, vi } from 'vitest';
import { create } from '../renderers/spectrum';
import baseline from './spectrum-baseline.json';

// Draw-command hashes captured from the original Visualizer.vue before extraction.
// Covers attack, sustained playback, pause decay, reduced motion, and both DPRs.
// This is a numerical regression check, not a visual approval.
describe('Spectrum compatibility', () => {
  it('preserves every drawing command across playback and pause', () => {
    const commands: unknown[][] = [];
    const context = {
      clearRect: (...args: number[]) => commands.push(['clear', ...args]),
      createLinearGradient: (...args: number[]) => {
        commands.push(['gradient', ...args]);
        return { addColorStop: (...args: unknown[]) => commands.push(['stop', ...args]) };
      },
      fillRect: (...args: number[]) => commands.push(['rect', ...args]),
    };
    const input = Float32Array.from({ length: 1024 }, (_, i) => -70 + (i * 17 % 61));
    const hashes: string[] = [];
    for (const dpr of [1, 2]) {
      const canvas = document.createElement('canvas');
      canvas.width = 800 * dpr;
      canvas.height = 320 * dpr;
      vi.spyOn(canvas, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D);
      const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
      renderer.resize({ width: canvas.width, height: canvas.height, dpr });
      for (let frame = 0; frame <= 90; frame++) {
        commands.length = 0;
        renderer.update(frame < 45 || frame === 90 ? { frequency: input } : null, frame === 90, 1 / 60);
        renderer.draw({ tint: '#336699', text: '#ffffff', muted: '#888888' });
        hashes.push(createHash('sha256').update(JSON.stringify(commands)).digest('hex'));
      }
      renderer.dispose();
    }
    expect(hashes).toEqual(baseline);
  });
});
