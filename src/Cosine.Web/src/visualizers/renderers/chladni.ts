import type { VisualizerFactory } from '../types';
import { createCssColorBlend } from '../colorBlend';
import { seededRandom } from '../seededRandom';
import { createChladniAttacks } from './chladniAttacks';

const count = 18000;
const pi = Math.PI;

/** Frequency bands excite a bank of standing-wave geometries and symmetries. */
export const create: VisualizerFactory = (canvas, format) => {
  const context = canvas.getContext('2d');
  if (!context) throw new Error('Canvas 2D is unavailable');
  const ctx = context;
  const tint = createCssColorBlend();
  const x = new Float32Array(count), y = new Float32Array(count);
  const vx = new Float32Array(count), vy = new Float32Array(count);
  const grain = new Float32Array(count);
  const restingOffset = new Float32Array(count);
  const direction = new Float32Array(count);
  const random = seededRandom('chladni');
  let lastTrack: string | null | undefined;

  const detector = createChladniAttacks(format);
  let activity = 0;
  let radialPulse = 0;
  let widthPulse = 0;
  let vibrationPhase = 0;
  let evolution = 0;
  let settlingTime = 0;
  let reduced = false;
  let dpr = 1;
  const bins = [40, 65, 100, 150, 220, 310, 440, 620, 880, 1240, 1760, 2490, 3520, 4980, 7040, 9950, 14000].map(hz =>
    Math.max(1, Math.min(format.fftSize / 2, Math.round(hz * format.fftSize / format.sampleRate))));
  // Include different nodal topologies, rather than only antisymmetric square
  // modes (which all force the same diagonal cross through the composition).
  const modes = [
    { family: 'stripes', order: 1.5, angle: 0.2 },
    { family: 'rings', order: 2, angle: 0 },
    { family: 'cells', order: 2, angle: 0 },
    { family: 'petals', order: 2, angle: 0.3 },
    { family: 'grid', order: 2, angle: 0.25 },
    { family: 'crossed', order: 2, angle: 0 },
    { family: 'interference', order: 2, angle: 0.2 },
    { family: 'scalloped', order: 3, angle: 0.4 },
    { family: 'cells', order: 3, angle: 0.6 },
    { family: 'rings', order: 4, angle: 0 },
    { family: 'petals', order: 4, angle: 0.2 },
    { family: 'grid', order: 4, angle: 0.65 },
    { family: 'interference', order: 4, angle: 0.6 },
    { family: 'crossed', order: 5, angle: 0.3 },
    { family: 'scalloped', order: 5, angle: 0 },
    { family: 'stripes', order: 6, angle: 1.1 },
  ];
  const weights = new Float32Array(modes.length);
  weights[3] = 1;
  const energies = new Float32Array(modes.length);
  const gridSize = 97;
  const field = new Float32Array(gridSize * gridSize * 3);
  // Cache the fixed modal surfaces once. More grains then cost interpolation,
  // rather than evaluating several trigonometric functions per grain per frame.
  const surfaces = modes.map(({ family, order, angle }) => {
    const values = new Float32Array(field.length);
    const ca = Math.cos(angle), sa = Math.sin(angle);
    function evaluate(px: number, py: number) {
      const x = px * ca - py * sa, y = px * sa + py * ca;
      const k = order * pi;
      const r = Math.hypot(x, y);
      const theta = Math.atan2(y, x);
      switch (family) {
        case 'stripes': return Math.cos(k * x) + 0.22 * Math.cos(pi * y);
        case 'grid': return Math.cos(k * x) * Math.cos((order + 1) * pi * y);
        case 'cells': return Math.cos(k * x) + 0.72 * Math.cos((order + 1) * pi * y);
        case 'rings': return Math.cos(k * r);
        case 'petals': return Math.sin(k * r) * Math.cos((order + 1) * theta)
          * Math.min(1, r * r * 16) + 0.18 * Math.cos(pi * r);
        case 'scalloped': return Math.cos(k * r + 0.65 * Math.cos((order + 2) * theta) * Math.min(1, r * r * 12));
        case 'interference': return Math.cos(k * x) + Math.cos(k * (x * 0.5 + y * 0.8660254))
          + Math.cos(k * (x * 0.5 - y * 0.8660254));
        default: return Math.cos(k * x) * Math.cos((order + 1) * pi * y)
          - Math.cos((order + 1) * pi * x) * Math.cos(k * y);
      }
    }
    let power = 0;
    const epsilon = 0.0001;
    for (let row = 0; row < gridSize; row++) for (let col = 0; col < gridSize; col++) {
      const px = col * 2 / (gridSize - 1) - 1, py = row * 2 / (gridSize - 1) - 1;
      const offset = (row * gridSize + col) * 3;
      values[offset] = evaluate(px, py);
      values[offset + 1] = (evaluate(px + epsilon, py) - evaluate(px - epsilon, py)) / (2 * epsilon);
      values[offset + 2] = (evaluate(px, py + epsilon) - evaluate(px, py - epsilon)) / (2 * epsilon);
      power += values[offset]! ** 2;
    }
    // Equalize spatial energy so a family with more summed waves cannot win
    // simply because its formula has a larger numerical amplitude.
    const gain = 1 / Math.sqrt(power / (gridSize * gridSize));
    for (let i = 0; i < values.length; i++) values[i]! *= gain;
    return values;
  });
  function mixField() {
    field.fill(0);
    // Slowly crossfade the resonant geometry, even for a steady spectrum.
    // Band weights still determine the mix; sound energy controls the pace.
    const stage = Math.floor(evolution);
    const fraction = evolution - stage;
    const blend = fraction * fraction * (3 - 2 * fraction);
    for (let band = 0; band < modes.length; band++) {
      const weight = weights[band]!;
      if (weight < 0.0001) continue;
      const surface = surfaces[(band + stage * 5) % modes.length]!;
      const next = surfaces[(band + (stage + 1) * 5) % modes.length]!;
      for (let i = 0; i < field.length; i++) field[i]! += (surface[i]! * (1 - blend) + next[i]! * blend) * weight;
    }
  }
  mixField();

  function advance(dt: number, drive = 0, scatter = 0) {
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
        + drive * 0.004 * Math.sin(vibrationPhase + grain[i]! * pi * 2);
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
    for (let i = 0; i < 28; i++) advance(1 / 60, 0);
  }
  seed();
  return {
    setTrack(id) {
      if (id === lastTrack) return;
      lastTrack = id;
      // Track identity never selects a pattern; measured band energy does.
      detector.reset();
      activity = 0; radialPulse = 0; widthPulse = 0;
      settlingTime = 1.2;
    },
    resize(size) { dpr = size.dpr; },
    update(audio, reducedMotion, dt) {
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
          mixField();
        }
      }
      if (audio) settlingTime = 1.2;
      else settlingTime = Math.max(0, settlingTime - elapsed);
      if (settlingTime > 0) advance(elapsed, activity, Math.max(kick, snare));
    },
    draw(theme) {
      const color = tint.color(theme.tint ?? theme.text);
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
    get settling() { return tint.settling || (!reduced && settlingTime > 0); },
    dispose() { ctx.clearRect(0, 0, canvas.width, canvas.height); },
  };
};
