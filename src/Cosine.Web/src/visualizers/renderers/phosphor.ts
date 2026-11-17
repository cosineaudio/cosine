import type { VisualizerFactory } from '../types';
import { seededRandom } from '../seededRandom';
import { createColorBlend, createCssColorBlend } from '../colorBlend';

/** Folded translucent field ribbons with audio-shaped poles and travelling pulses. */
export const create: VisualizerFactory = (canvas, format) => {
  const context = canvas.getContext('2d');
  if (!context) throw new Error('Canvas 2D is unavailable');
  const ctx = context;
  const tintBlend = createCssColorBlend();
  const count = 6;
  const steps = 128;
  const layoutBlend = createColorBlend(new Float32Array(count * 4 + 3).fill(0.5));
  let lastTrack: string | null | undefined;
  const curves = Array.from({ length: count }, () => new Float32Array((steps + 1) * 2));
  const distances = Array.from({ length: count }, () => new Float32Array(steps + 1));
  const widths = Array.from({ length: count }, () => new Float32Array(steps + 1));
  const levels = new Float32Array(count);
  const previous = new Float32Array(count);
  const cooldown = new Float32Array(count);
  const pulseAge = new Float32Array(count).fill(2);
  const pulseStrength = new Float32Array(count);
  const phases = new Float32Array([0.2, 1.7, 3.1, 4.4, 2.3, 5.2]);
  // A rolling musical signature sets the composition; faster envelopes articulate it.
  const profile = new Float32Array(count).fill(1 / count);
  const activity = new Float32Array(count);
  let dynamics = 0;
  const breadth = [0.041, 0.031, 0.022, 0.034, 0.012, 0.018];
  const bins = [40, 120, 350, 1000, 2800, 6500, 14000].map(hz =>
    Math.max(1, Math.min(format.fftSize / 2, Math.round(hz * format.fftSize / format.sampleRate))));
  let dpr = 1;
  let motion = 0;
  let reduced = false;

  function curve(line: number) {
    const seed = layoutBlend.value;
    const offset = line * 4;
    const own = levels[line]!;
    const phase = phases[line]!;
    const weight = Math.min(1, profile[line]! * 3);
    const sweep = seed[count * 4]!;
    const loop = seed[count * 4 + 1]!;
    const fold = seed[count * 4 + 2]!;
    const points = curves[line]!;
    const angle = (seed[0]! - 0.5) * 1.4;
    const ca = Math.cos(angle), sa = Math.sin(angle);
    for (let i = 0; i <= steps; i++) {
      const t = i / steps;
      const taper = Math.sin(t * Math.PI);
      // Each track chooses a whole composition family, not just line orientation.
      const sweepX = (t - 0.5) * (0.85 + seed[offset]! * 0.35);
      const sweepY = (line - 2.5) * (0.035 + seed[1]! * 0.04)
        + Math.sin(t * Math.PI * (0.7 + seed[offset + 1]!) + phase * 0.4)
        * (0.10 + own * 0.16);
      const theta = t * Math.PI * (1.4 + seed[offset]! * 1.4) + seed[offset + 1]! * 6.28;
      const radius = 0.10 + line * 0.035 + weight * 0.07;
      const loopX = Math.cos(theta) * radius + (seed[offset + 2]! - 0.5) * 0.18;
      const loopY = Math.sin(theta) * radius * (0.6 + seed[2]! * 0.65)
        + (seed[offset + 3]! - 0.5) * 0.18;
      const foldX = (t - 0.5) * 0.75
        + Math.sin(t * Math.PI * 2 + seed[offset]! * 3) * (0.06 + own * 0.07);
      const foldY = Math.sin(t * Math.PI * (1.4 + seed[3]! * 1.8) + phase * 0.35)
        * (0.12 + seed[offset + 1]! * 0.17) + (line - 2.5) * 0.025;
      const x = sweepX*sweep + loopX*loop + foldX*fold;
      const y = sweepY*sweep + loopY*loop + foldY*fold
        + taper * Math.sin(t * Math.PI * 2 + phase) * dynamics * 0.035;
      points[i*2] = (0.5 + x*ca-y*sa) * canvas.width;
      points[i*2+1] = (0.5 + x*sa+y*ca) * canvas.height;
      const twist = 0.25 + 0.75 * Math.abs(Math.cos(t * Math.PI + seed[offset+2]! * 3 + own));
      widths[line]![i] = taper * twist * Math.min(canvas.width, canvas.height)
        * breadth[line]! * (0.55 + seed[offset+3]!) * (0.3 + weight + own*0.5);
    }
    // Limit ribbon thickness at tight bends to avoid folded-over, spiky edges.
    for (let i = 1; i < steps; i++) {
      const ax = points[i*2]! - points[(i-1)*2]!;
      const ay = points[i*2+1]! - points[(i-1)*2+1]!;
      const bx = points[(i+1)*2]! - points[i*2]!;
      const by = points[(i+1)*2+1]! - points[i*2+1]!;
      const cross = Math.abs(ax*by-ay*bx);
      if (cross > 0.0001) {
        const radius = Math.hypot(ax,ay)*Math.hypot(bx,by)*Math.hypot(ax+bx,ay+by)/(2*cross);
        widths[line]![i] = Math.min(widths[line]![i]!, radius*0.35);
      }
    }
  }
  function fitComposition() {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (let line = 0; line < count; line++) {
      curve(line);
      for (let i = 0; i <= steps; i++) {
        const x = curves[line]![i*2]!, y = curves[line]![i*2+1]!;
        const width = widths[line]![i]!;
        minX = Math.min(minX, x-width); maxX = Math.max(maxX, x+width);
        minY = Math.min(minY, y-width); maxY = Math.max(maxY, y+width);
      }
    }
    // Fit the complete geometry including its width, leaving room for the halo.
    const padding = Math.min(canvas.width, canvas.height) * 0.07 + 8*dpr;
    const scale = Math.min(1, Math.max(1,canvas.width-padding*2)/Math.max(1,maxX-minX),
      Math.max(1,canvas.height-padding*2)/Math.max(1,maxY-minY));
    for (let line = 0; line < count; line++) {
      const points = curves[line]!, lengths = distances[line]!;
      lengths[0] = 0;
      for (let i = 0; i <= steps; i++) {
        points[i*2] = (points[i*2]!-(minX+maxX)*0.5)*scale + canvas.width*0.5;
        points[i*2+1] = (points[i*2+1]!-(minY+maxY)*0.5)*scale + canvas.height*0.5;
        widths[line]![i]! *= scale;
        if (i) lengths[i] = lengths[i-1]! + Math.hypot(points[i*2]!-points[(i-1)*2]!,
          points[i*2+1]!-points[(i-1)*2+1]!);
      }
    }
  }
  function path(line: number) {
    const points = curves[line]!;
    ctx.beginPath();
    ctx.moveTo(points[0]!, points[1]!);
    for (let i = 1; i <= steps; i++) ctx.lineTo(points[i*2]!, points[i*2+1]!);
  }
  function ribbon(line: number, scale: number) {
    const points = curves[line]!;
    ctx.beginPath();
    for (let side = -1; side <= 1; side += 2) {
      for (let n = 0; n <= steps; n++) {
        const i = side === -1 ? n : steps - n;
        const before = Math.max(0, i - 1);
        const after = Math.min(steps, i + 1);
        const dx = points[after*2]! - points[before*2]!;
        const dy = points[after*2+1]! - points[before*2+1]!;
        const offset = widths[line]![i]! * scale * side / Math.max(1, Math.hypot(dx, dy));
        const x = points[i*2]! - dy * offset;
        const y = points[i*2+1]! + dx * offset;
        if (side === -1 && n === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
    }
    ctx.closePath();
  }
  return {
    setTrack(trackId) {
      if (trackId === lastTrack) return;
      lastTrack = trackId;
      const random = seededRandom(trackId ?? 'phosphor');
      const layout = new Float32Array(count * 4 + 3);
      for (let i = 0; i < layout.length; i++) layout[i] = random();
      const family = Math.min(2, Math.floor(layout[count * 4]! * 3));
      for (let i = 0; i < 3; i++) layout[count * 4 + i] = i === family ? 1 : 0;
      layoutBlend.set(layout);
      // Do not carry attack flashes from the previous track into its successor.
      pulseAge.fill(2);
      previous.fill(0);
      cooldown.fill(0);
    },
    resize(size) { dpr = size.dpr; },
    update(audio, reducedMotion, dt) {
      reduced = reducedMotion;
      tintBlend.update(dt, reducedMotion);
      layoutBlend.update(dt, reducedMotion);
      motion = 0;
      for (let band = 0; band < count; band++) {
        let power = 0;
        for (let i = bins[band]!; i < bins[band+1]!; i++) {
          const db = audio?.frequency?.[i] ?? -100;
          power += Math.pow(10, db / 10);
        }
        const db = 10 * Math.log10(Math.max(1e-10, power / Math.max(1, bins[band+1]! - bins[band]!)));
        const raw = Math.pow(Math.max(0, Math.min(1, (db + 65) / 45)), 1.5);
        const attack = Math.max(0, raw - previous[band]!) * (1/60) / Math.max(dt, 1/240);
        previous[band] = raw;
        cooldown[band] = Math.max(0, cooldown[band]! - dt);
        if (!reducedMotion && audio && attack > 0.018 && raw > 0.12 && cooldown[band] === 0) {
          const line = band;
          pulseAge[line] = 0;
          pulseStrength[line] = Math.min(1, attack * 10 + raw * 0.35);
          cooldown[band] = 0.16 + band * 0.025;
        }
        const blend = reducedMotion ? 1 : 1 - Math.exp(-dt / (raw > levels[band]! ? 0.35 : 0.65));
        levels[band]! += (raw - levels[band]!) * blend;
        const activityBlend = reducedMotion ? 1 : 1 - Math.exp(-dt / 0.8);
        activity[band]! += (Math.min(1, attack * 12) - activity[band]!) * activityBlend;
        motion = Math.max(motion, Math.abs(levels[band]! - raw), audio ? 0 : levels[band]!);
      }
      const total = levels.reduce((sum, value) => sum + value, 0);
      if (audio && total > 0.03) {
        const profileBlend = reducedMotion ? 1 : 1 - Math.exp(-dt / 2.5);
        let contrast = 0;
        for (let band = 0; band < count; band++) {
          profile[band]! += (levels[band]! / total - profile[band]!) * profileBlend;
          contrast += Math.abs(previous[band]! - levels[band]!);
          const targetPhase = (profile[band]! - 1 / count) * 9
            + levels[band]! * 1.8 + activity[band]! * 2;
          phases[band]! += (targetPhase - phases[band]!)
            * (reducedMotion ? 1 : 1 - Math.exp(-dt / 0.45));
        }
        dynamics += (Math.min(1, contrast * 2) - dynamics)
          * (reducedMotion ? 1 : 1 - Math.exp(-dt / 0.5));
      }
      for (let i = 0; i < count; i++) {
        pulseAge[i] = reducedMotion ? 2 : Math.min(2, pulseAge[i]! + dt);
        if (pulseAge[i]! < 1.1) motion = Math.max(motion, 0.1);
      }
    },
    draw(theme) {
      const tint = tintBlend.color(theme.tint ?? theme.text);
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.lineCap = 'round';
      ctx.lineJoin = 'round';
      ctx.strokeStyle = tint;
      ctx.fillStyle = tint;
      fitComposition();
      for (let line = count - 1; line >= 0; line--) {
        const band = line;
        const opacity = 0.08 + Math.min(0.32, profile[band]! * 1.2) + levels[band]! * 0.25;
        // Nested translucent sheets shade each fold without blur or white highlights.
        for (let shell = 4; shell >= 1; shell--) {
          ribbon(line, shell / 4);
          ctx.globalAlpha = opacity * (shell === 4 ? 0.12 : 0.16);
          ctx.fill();
        }
        ribbon(line, 1);
        ctx.globalAlpha = opacity * 0.30;
        ctx.lineWidth = 0.8 * dpr;
        ctx.stroke();
        path(line);
        ctx.globalAlpha = opacity * 0.08;
        ctx.lineWidth = 14 * dpr;
        ctx.stroke();
        ctx.globalAlpha = opacity * 0.2;
        ctx.lineWidth = 5 * dpr;
        ctx.stroke();
        ctx.globalAlpha = opacity * 0.65;
        ctx.lineWidth = (1.1 + levels[band]! * 0.6) * dpr;
        ctx.stroke();
        const duration = 1.1 - line * 0.09;
        if (reduced || pulseAge[line]! >= duration) continue;
        const progress = pulseAge[line]! / duration;
        const points = curves[line]!;
        const lengths = distances[line]!;
        const total = Math.max(1, lengths[steps]!);
        const envelope = Math.sin(Math.PI * progress) * pulseStrength[line]!;
        // Arc-length travel keeps pulses steady through the bends.
        for (let i = 1; i <= steps; i++) {
          const position = lengths[i]! / total;
          const distance = (position - progress) / 0.075;
          const intensity = Math.exp(-distance * distance) * envelope;
          if (intensity < 0.008) continue;
          ctx.beginPath();
          ctx.moveTo(points[(i-1)*2]!, points[(i-1)*2+1]!);
          ctx.lineTo(points[i*2]!, points[i*2+1]!);
          ctx.globalAlpha = intensity * 0.15;
          ctx.lineWidth = Math.max(8 * dpr, widths[line]![i]! * 1.5);
          ctx.stroke();
          ctx.globalAlpha = intensity * 0.85;
          ctx.lineWidth = 2.4 * dpr;
          ctx.stroke();
        }
      }
      ctx.globalAlpha = 1;
    },
    get settling() { return tintBlend.settling || layoutBlend.settling || (!reduced && motion > 0.001); },
    dispose() { ctx.clearRect(0, 0, canvas.width, canvas.height); },
  };
};
