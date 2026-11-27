import { seededRandom } from '../../seededRandom';
import { count, gridSize, fieldLength, modes, getSurfaces } from './surfaces';

export interface Simulation {
  x: Float32Array; y: Float32Array; vx: Float32Array; vy: Float32Array;
  grain: Float32Array; weights: Float32Array; modeOrder: Int32Array;
  resetRandom(key: string): void;
  random(): number;
  seed(): void;
  step(evolution: number, dt: number, drive: number, scatter: number, width: number, phase: number, mix: boolean, advance: boolean): void;
}

/** JavaScript fallback and numerical reference for the Wasm kernel. */
export function createJsSimulation(): Simulation {
  const pi = Math.PI;
  let random = seededRandom('chladni');
  const x = new Float32Array(count), y = new Float32Array(count);
  const vx = new Float32Array(count), vy = new Float32Array(count);
  const grain = new Float32Array(count), restingOffset = new Float32Array(count), direction = new Float32Array(count);
  const weights = new Float32Array(modes.length), modeOrder = Int32Array.from(modes, (_, i) => i);
  const field = new Float32Array(fieldLength), surfaces = getSurfaces();
  function mixField(evolution: number) {
    field.fill(0);
    // Slowly crossfade the resonant geometry, even for a steady spectrum.
    // Band weights still determine the mix; sound energy controls the pace.
    const stage = Math.floor(evolution);
    const fraction = evolution - stage;
    const blend = fraction * fraction * (3 - 2 * fraction);
    for (let band = 0; band < modes.length; band++) {
      const weight = weights[band]!;
      if (weight < 0.0001) continue;
      const surface = surfaces[modeOrder[(band + stage * 5) % modes.length]!]!;
      const next = surfaces[modeOrder[(band + (stage + 1) * 5) % modes.length]!]!;
      for (let i = 0; i < field.length; i++) field[i]! += (surface[i]! * (1 - blend) + next[i]! * blend) * weight;
    }
  }

  function advance(dt: number, drive: number, scatter: number, widthPulse: number, vibrationPhase: number) {
    const attraction = 1 - Math.exp(-dt * (8 - widthPulse * 6));
    const drag = Math.exp(-dt * (7 - widthPulse * 3));

    for (let i = 0; i < count; i++) {
      const px = x[i]!, py = y[i]!;
      const gridX = (px + 1) * (gridSize - 1) * 0.5;
      const gridY = (py + 1) * (gridSize - 1) * 0.5;
      const col = Math.floor(gridX), row = Math.floor(gridY);
      const fx = gridX - col, fy = gridY - row;
      const offset = (row * gridSize + col) * 3;
      let value = 0, gx = 0, gy = 0;
      for (let corner = 0; corner < 4; corner++) {
        const right = corner & 1, bottom = corner >> 1;
        const weight = (right ? fx : 1 - fx) * (bottom ? fy : 1 - fy);
        const index = offset + right * 3 + bottom * gridSize * 3;
        value += field[index]! * weight;
        gx += field[index + 1]! * weight;
        gy += field[index + 2]! * weight;
      }
      const gradient = Math.sqrt(gx * gx + gy * gy + 0.3);
      // Each grain rests at a small signed distance from the node, forming
      // a dense ribbon of sand instead of collapsing onto an infinitesimal line.
      if (scatter > 0) {
        // Break up the population itself, not just its shared radial transform.
        // Independent impulses and new resting offsets prevent repeated attacks
        // from putting the same grains back onto identical thin tracks.
        const angle = random() * pi * 2;
        const impulse = scatter * (0.18 + random() * 0.32);
        vx[i]! += Math.cos(angle) * impulse;
        vy[i]! += Math.sin(angle) * impulse;
        restingOffset[i] = random() - 0.5;
      }
      const restDistance = restingOffset[i]! * (0.032 + widthPulse * 0.09)
        + (drive ? drive * 0.004 * Math.sin(vibrationPhase + grain[i]! * pi * 2) : 0);
      const distance = value / gradient - restDistance;
      const step = Math.max(-0.035, Math.min(0.035, distance)) * attraction / gradient;
      vx[i]! *= drag; vy[i]! *= drag;
      // Sustained sound transports grains along the nodes without changing
      // the frequency-derived shape. Opposing directions avoid a rotating field.
      const flow = drive * (0.035 + grain[i]! * 0.035) * dt
        * direction[i]! / gradient;
      let nextX = px - gx * step - gy * flow + vx[i]! * dt;
      let nextY = py - gy * step + gx * flow + vy[i]! * dt;
      let bounceX = 0, bounceY = 0;
      if (Math.abs(nextX) > 0.975) {
        nextX = Math.sign(nextX) * (1.95 - Math.abs(nextX));
        bounceX = Math.sign(nextX);
      }
      if (Math.abs(nextY) > 0.975) {
        nextY = Math.sign(nextY) * (1.95 - Math.abs(nextY));
        bounceY = Math.sign(nextY);
      }
      if (bounceX || bounceY) {
        direction[i]! *= -1;
        const speed = Math.max(0.025 + drive * 0.05, Math.hypot(vx[i]!, vy[i]!) * 0.65);
        // Scatter around the inward wall normal. At corners the bisector
        // and narrower spread keep both components pointing into the view.
        const spread = bounceX && bounceY ? 0.45 : 0.9;
        const angle = Math.atan2(-bounceY, -bounceX) + (random() - 0.5) * spread * 2;
        vx[i] = Math.cos(angle) * speed;
        vy[i] = Math.sin(angle) * speed;
      }
      x[i] = nextX;
      y[i] = nextY;
    }
  }
  function seed() {
    for (let i = 0; i < count; i++) {
      x[i] = random() * 1.96 - 0.98;
      y[i] = random() * 1.96 - 0.98;
      grain[i] = random();
      restingOffset[i] = random() - 0.5;
      direction[i] = i % 2 ? 1 : -1;
    }
    vx.fill(0); vy.fill(0);
    for (let i = 0; i < 28; i++) advance(1 / 60, 0, 0, 0, 0);
  }
  return {
    x, y, vx, vy, grain, weights, modeOrder,
    resetRandom(key) { random = seededRandom(key); }, random: () => random(), seed,
    step(evolution, dt, drive, scatter, width, phase, mix, moving) {
      if (mix) mixField(evolution);
      if (moving) advance(dt, drive, scatter, width, phase);
    },
  };
}
