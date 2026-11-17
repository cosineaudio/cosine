import type { VisualizerFactory } from '../types';
import { createColorBlend } from '../colorBlend';
import { createFluid } from './ink/fluid';

export const create: VisualizerFactory = (canvas, format) => {
  const fluid = createFluid(canvas);
  const maxProducerLifetime = 3;
  const colorBlend = createColorBlend([0.35, 0.55, 0.65]);
  const emitters = new Float32Array(6 * 4);
  const directions = new Float32Array(6 * 2);
  const ages = new Float32Array(6).fill(10);
  const strengths = new Float32Array(6);
  const cooldown = new Float32Array(3);
  // One compact, rapidly decaying beat emitter per register.
  const splats = new Float32Array(3 * 4);
  const splatAges = new Float32Array(3).fill(1);
  const splatStrengths = new Float32Array(3);
  const splatCooldown = new Float32Array(3);
  const attackFloor = new Float32Array(3);
  const attackPeak = new Float32Array(3);
  const splatArmed = [true, true, true];
  let kickFastEnergy = 0;
  let kickSlowEnergy = 0;
  let kickReleaseTime = 0;
  const kickEnd = Math.round(150 * format.fftSize / format.sampleRate);
  const binCount = format.fftSize / 2;
  const amplitudes = new Float32Array(binCount);
  const attackWeights = new Float32Array(binCount);
  const signatures = Array.from({ length: 6 }, () => new Float32Array(binCount));
  const references = new Float32Array(6);
  const previous = new Float32Array(3);
  const importance = new Float32Array(3);
  const previousSpectrum = new Float32Array(format.fftSize / 2).fill(-100);
  const bins = [40, 250, 2500, 12000].map(hz => Math.max(1, Math.min(format.fftSize / 2, Math.round(hz * format.fftSize / format.sampleRate))));
  let lastTint: string | null | undefined;
  let accumulated = 0;
  let tail = 0;
  return {
    resize(size) { fluid.resize(size.width, size.height); },
    update(audio, reducedMotion, dt) {
      colorBlend.update(dt, reducedMotion);
      if (reducedMotion) { tail = 0; accumulated = 0; return; }
      for (let i = 0; i < 6; i++) ages[i]! += dt;
      for (let band = 0; band < 3; band++) {
        splatAges[band]! += dt;
        splatCooldown[band] = Math.max(0, splatCooldown[band]! - dt);
        cooldown[band] = Math.max(0, cooldown[band]! - dt);
        let sum = 0;
        let signatureWeight = 0;
        let kickWeight = 0;
        let kickMagnitude = 0;
        let bassPower = 0;
        let flux = 0;
        let audibleBins = 0;
        const lo = bins[band]!; const hi = bins[band + 1]!;
        const cadence = (1 / 60) / Math.max(dt, 1 / 240);
        for (let i = lo; i < hi; i++) {
          const db = Math.max(-100, audio?.frequency?.[i] ?? -100);
          const magnitude = audio ? Math.pow(10, db / 20) : 0;
          sum += magnitude;
          if (band === 0) bassPower += magnitude * magnitude;
          amplitudes[i] = magnitude;
          attackWeights[i] = Math.max(0, (db - previousSpectrum[i]!) * cadence - 0.15) * magnitude;
          signatureWeight += attackWeights[i]!;
          if (band === 0 && i < kickEnd) {
            kickWeight += attackWeights[i]!;
            kickMagnitude += magnitude;
          }
          if (db > -65) {
            flux += Math.max(0, (db - previousSpectrum[i]!) * cadence - 0.15);
            audibleBins++;
          }
          previousSpectrum[i] = db;
        }
        const magnitude = sum / Math.max(1, hi - lo);
        const db = magnitude > 0 ? 20 * Math.log10(magnitude) : -75;
        const raw = Math.max(0, Math.min(1, (db + 65) / 40));
        // Loudness controls output continuously; attacks only articulate its start.
        importance[band] = Math.pow(raw, 1.8) * (band === 0 ? 1 : band === 1 ? 0.6 : 0.25);
        // Detect attacks in individual bins; a broad band average hides kicks and snares.
        const spectralAttack = flux / Math.max(1, audibleBins) / 3;
        const bandAttack = Math.max(0, (raw - previous[band]!) * cadence - 0.004) * 12;
        const onset = Math.min(1, Math.max(spectralAttack, bandAttack)) * Math.min(1, raw * 2);
        previous[band] = raw;
        // Weight spectral changes by audible energy, so quiet FFT bins cannot
        // drown out a kick. Also inspect its low-frequency body independently.
        const weightedAttack = signatureWeight / Math.max(sum, 0.000001);
        const kickAttack = kickWeight / Math.max(kickMagnitude, 0.000001);
        const transient = Math.max(weightedAttack, band === 0 ? kickAttack : 0);
        const threshold = Math.max(band === 0 ? 0.16 : 0.24, attackFloor[band]! * 1.6);
        attackPeak[band] = Math.max(transient, attackPeak[band]! * Math.exp(-dt / 0.12));
        let freshKick = true;
        if (band === 0) {
          // Total bass power is insensitive to energy moving between FFT bins
          // during a sustained note. Require both envelope contrast and a sharp rise.
          const energy = bassPower / Math.max(1, hi - lo);
          const before = kickFastEnergy;
          kickFastEnergy += (energy - kickFastEnergy) * (1 - Math.exp(-dt / 0.012));
          kickSlowEnergy += (energy - kickSlowEnergy) * (1 - Math.exp(-dt / 0.09));
          const contrastDb = 10 * Math.log10(Math.max(kickFastEnergy, 1e-10) / Math.max(kickSlowEnergy, 1e-10));
          const riseDb = 10 * Math.log10(Math.max(kickFastEnergy, 1e-10) / Math.max(before, 1e-10)) * cadence;
          freshKick = contrastDb > 1.3 && riseDb > 0.3;
          // Rearm only after the energy attack actually settles, not a dip
          // in spectral flux caused by vibrato or changing harmonics.
          kickReleaseTime = contrastDb < 0.4 ? kickReleaseTime + dt : 0;
          if (kickReleaseTime >= 0.04) splatArmed[band] = true;
        } else if (transient < Math.max(threshold * 0.7, attackPeak[band]! * 0.45)) {
          splatArmed[band] = true;
        }
        const splatHit = !!audio && freshKick && splatArmed[band] && splatCooldown[band] === 0
          && transient > threshold && raw > 0.2 && importance[band]! > 0.025;
        if (splatHit) {
          splats[band * 4] = 0.12 + Math.random() * 0.76;
          splats[band * 4 + 1] = 0.12 + Math.random() * 0.76;
          splatAges[band] = 0;
          splatStrengths[band] = importance[band]!
            * Math.min(1.25, 0.45 + transient / Math.max(threshold, 0.001) * 0.2);
          splatCooldown[band] = band === 0 ? 0.10 : 0.13;
          splatArmed[band] = false;
        }
        // Adapt to the song's recent activity without letting a single peak
        // raise the threshold enough to mask the following beats.
        const floorTarget = Math.min(transient, Math.max(0.3, attackFloor[band]! * 3));
        attackFloor[band]! += (floorTarget - attackFloor[band]!) * (1 - Math.exp(-dt / 0.65));
        if (audio && onset > 0.07 && importance[band]! > 0.015 && cooldown[band] === 0) {
          // Keep sustained producers in place; reuse quiet slots before displacing active ones.
          let slot = 0;
          for (let i = 1; i < 6; i++) {
            if (emitters[i * 4 + 2]! < emitters[slot * 4 + 2]!) slot = i;
          }
          const strength = 1.2 + onset * 1.8;
          // A quiet detail must not evict a louder sustained sound.
          if (strength * importance[band]! < emitters[slot * 4 + 2]!) continue;
          const offset = slot * 4;
          const angle = Math.random() * Math.PI * 2;
          emitters[offset] = 0.12 + Math.random() * 0.76;
          emitters[offset + 1] = 0.12 + Math.random() * 0.76;
          emitters[offset + 2] = 0.001;
          emitters[offset + 3] = band;
          if (splatHit) {
            emitters[offset] = splats[band * 4]!;
            emitters[offset + 1] = splats[band * 4 + 1]!;
          }
          directions[slot * 2] = Math.cos(angle);
          directions[slot * 2 + 1] = Math.sin(angle);
          const signature = signatures[slot]!;
          signature.fill(0);
          let reference = 0;
          const useAttack = signatureWeight > 0.000001;
          const total = Math.max(0.000001, useAttack ? signatureWeight : sum);
          for (let i = lo; i < hi; i++) {
            signature[i] = (useAttack ? attackWeights[i]! : amplitudes[i]!) / total;
            reference += signature[i]! * amplitudes[i]!;
          }
          references[slot] = reference;
          ages[slot] = 0;
          strengths[slot] = strength;
          cooldown[band] = band === 0 ? 0.22 : band === 1 ? 0.25 : 0.3;
        }
      }
      for (let slot = 0; slot < 6; slot++) {
        const band = emitters[slot * 4 + 3]!;
        let matchingEnergy = 0;
        const signature = signatures[slot]!;
        for (let i = bins[band]!; i < bins[band + 1]!; i++) matchingEnergy += signature[i]! * amplitudes[i]!;
        const ratio = matchingEnergy / Math.max(references[slot]!, 0.000001);
        // Sustain follows the frequencies of the original attack, not unrelated band loudness.
        // Release after a smaller drop from the attack level, even if the tail remains audible.
        const expired = ages[slot]! >= maxProducerLifetime;
        const held = !expired && audio && ratio > 0.5 && matchingEnergy > 0.0002;
        const attackDecay = band === 0 ? 0.24 : band === 1 ? 0.18 : 0.1;
        const sustainLevel = band === 0 ? 0.38 : band === 1 ? 0.30 : 0.2;
        const attack = strengths[slot]! * Math.exp(-ages[slot]! / attackDecay);
        const sustain = held ? strengths[slot]! * sustainLevel * Math.min(1.2, ratio) : 0;
        const target = expired ? 0 : Math.max(attack, sustain) * importance[band]!;
        const current = emitters[slot * 4 + 2]!;
        const release = band === 0 ? 0.22 : band === 1 ? 0.16 : 0.09;
        const blend = 1 - Math.exp(-dt / (target > current ? 0.035 : release));
        emitters[slot * 4 + 2] = current + (target - current) * blend;
        // Once released, a different note cannot revive this old producer without a new attack.
        if ((expired || (!held && ages[slot]! > 0.6)) && (emitters[slot * 4 + 2]! < 0.01 || ages[slot]! >= maxProducerLifetime + 0.5)) {
          emitters[slot * 4 + 2] = 0;
          strengths[slot] = 0;
          references[slot] = 0;
          signature.fill(0);
        }
      }
      for (let band = 0; band < 3; band++) {
        const progress = Math.min(1, splatAges[band]! / 0.12);
        splats[band * 4 + 2] = progress < 1 ? splatStrengths[band]! * Math.exp(-progress * 5) : 0;
        splats[band * 4 + 3] = progress;
      }
      tail = audio ? 1.5 : Math.max(0, tail - dt);
      if (!audio && tail === 0) { accumulated = 0; return; }
      // Fixed simulation rate, capped catch-up; slow devices do not accumulate unbounded work.
      accumulated = Math.min(accumulated + dt, 2 / 60);
      while (accumulated >= 1 / 60) {
        fluid.step(1 / 60, emitters, directions, splats);
        accumulated -= 1 / 60;
      }
    },
    draw(theme) {
      if (lastTint !== theme.tint) {
        lastTint = theme.tint;
        const target = new Float32Array([0.35, 0.55, 0.65]);
        const rgb = theme.tint?.match(/[\d.]+/g)?.slice(0, 3).map(Number);
        if (rgb?.length === 3) {
          const scale = 0.85 / Math.max(170, ...rgb);
          for (let i = 0; i < 3; i++) target[i] = rgb[i]! * scale;
        }
        colorBlend.set(target);
      }
      fluid.draw(colorBlend.value, splats);
    },
    get settling() { return tail > 0 || colorBlend.settling; },
    dispose() { fluid.dispose(); },
  };
};
