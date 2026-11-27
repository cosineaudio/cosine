/** Shared album-color easing. The mutable output is reused by Canvas and WebGL renderers. */
export function createColorBlend(initial: ArrayLike<number>) {
  const value = new Float32Array(initial);
  const from = new Float32Array(initial);
  const target = new Float32Array(initial);
  let initialized = false;
  let elapsed = 2.4;
  return {
    value,
    set(next: ArrayLike<number>) {
      if (!initialized) {
        value.set(next); from.set(next); target.set(next);
        initialized = true;
        return;
      }
      if (target.every((channel, i) => Math.abs(channel - next[i]!) < 0.00001)) return;
      from.set(value); target.set(next); elapsed = 0;
    },
    update(dt: number, reducedMotion: boolean) {
      if (elapsed >= 2.4) return;
      elapsed = reducedMotion ? 2.4 : Math.min(2.4, elapsed + dt);
      const t = elapsed / 2.4;
      const blend = t * t * t * (t * (6 * t - 15) + 10);
      for (let i = 0; i < value.length; i++) value[i] = from[i]! + (target[i]! - from[i]!) * blend;
    },
    get settling() { return elapsed < 2.4; },
  };
}

function readCssColor(css: string): number[] {
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(css)?.[1];
  if (hex) {
    const full = hex.length === 3 ? [...hex].map(char => char + char).join('') : hex;
    return [0, 2, 4].map(offset => parseInt(full.slice(offset, offset + 2), 16));
  }
  const rgb = /^rgb\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)\s*\)$/i.exec(css);
  if (rgb) return rgb.slice(1).map(Number);
  // Theme tokens can be oklch or named colors. Resolve only when the target changes.
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 1;
  const context = canvas.getContext('2d', { willReadFrequently: true });
  if (!context) return [128, 128, 128];
  context.fillStyle = css;
  context.fillRect(0, 0, 1, 1);
  return Array.from(context.getImageData(0, 0, 1, 1).data).slice(0, 3);
}

export function createCssColorBlend() {
  const blend = createColorBlend([0, 0, 0]);
  let target: string | undefined;
  return {
    update: blend.update,
    color(css: string) {
      if (css !== target) { target = css; blend.set(readCssColor(css)); }
      // Keep original CSS values when settled, including the original Spectrum gradients.
      return blend.settling ? `rgb(${blend.value[0]}, ${blend.value[1]}, ${blend.value[2]})` : css;
    },
    get settling() { return blend.settling; },
  };
}
