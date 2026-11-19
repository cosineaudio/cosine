const pi = Math.PI;
export const count = 18000;
export const gridSize = 97;
export const fieldLength = gridSize * gridSize * 3;
// Include different nodal topologies, rather than only antisymmetric square
// modes (which all force the same diagonal cross through the composition).
export const modes = [
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

let cached: Float32Array[] | undefined;
/** Immutable, format-independent geometry shared by renderer instances. */
export function getSurfaces() {
  if (cached) return cached;
  cached = modes.map(({ family, order, angle }) => {
    const values = new Float32Array(fieldLength);
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
  return cached;
}
