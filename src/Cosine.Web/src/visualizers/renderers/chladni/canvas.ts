import type { Simulation } from './simulation';
import { count } from './surfaces';

export interface Painter {
  draw(color: string, dpr: number, radialPulse: number): void;
  reseed(): void;
  dispose(): void;
}
/** Original Canvas path remains available when WebGL2 cannot be created. */
export function createCanvasPainter(canvas: HTMLCanvasElement, state: Simulation): Painter {
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('Canvas 2D is unavailable');
  const { x, y, grain } = state;
  return {
    draw(color, dpr, radialPulse) {
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      const scaleX = canvas.width * 0.51;
      const scaleY = canvas.height * 0.51;
      const centerX = canvas.width * 0.5, centerY = canvas.height * 0.5;
      const shades = [
        `color-mix(in srgb, ${color} 88%, #e6b98b)`,
        color,
        `color-mix(in srgb, ${color} 82%, #c4ddff)`,
      ];
      // Three batches keep grain rendering inexpensive; no per-grain gradients.
      for (let layer = 0; layer < 3; layer++) {
        ctx.fillStyle = shades[layer]!;
        ctx.beginPath();
        for (let i = layer; i < count; i += 3) {
          const radius = (1.8 + grain[i]! * 1.6) * dpr;
          const pulseScale = 1 + radialPulse / Math.max(0.15, Math.hypot(x[i]!, y[i]!));
          let drawX = x[i]! * pulseScale, drawY = y[i]! * pulseScale;
          if (Math.abs(drawX) > 0.975) drawX = Math.sign(drawX) * (1.95 - Math.abs(drawX));
          if (Math.abs(drawY) > 0.975) drawY = Math.sign(drawY) * (1.95 - Math.abs(drawY));
          const px = centerX + drawX * scaleX;
          const py = centerY + drawY * scaleY;
          ctx.rect(px - radius * 0.5, py - radius * 0.5, radius, radius);
        }
        ctx.globalAlpha = 0.32 + layer * 0.22;
        ctx.fill();
      }
      ctx.globalAlpha = 1;
    },
    reseed() {},
    dispose() { ctx.clearRect(0, 0, canvas.width, canvas.height); },
  };
}
