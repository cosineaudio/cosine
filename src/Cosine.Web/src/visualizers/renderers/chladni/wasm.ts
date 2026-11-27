import kernelUrl from './kernel.wasm?url';
import { hashSeed } from '../../seededRandom';
import { count, fieldLength, modes, getSurfaces } from './surfaces';
import { createJsSimulation, type Simulation } from './simulation';

interface Kernel extends WebAssembly.Exports {
  memory: WebAssembly.Memory;
  data(): number;
  set_seed(seed: number): void;
  random(): number;
  seed(): void;
  step(evolution: number, dt: number, drive: number, scatter: number, width: number, phase: number, mix: number, moving: number): void;
}
let compiled: WebAssembly.Module | undefined;
let pending: Promise<void> | undefined;
/** Load once at the existing asynchronous registry seam. CSP/unsupported SIMD falls back to JS. */
export function prepareSimulation() {
  return pending ??= (async () => {
    try {
      const response = await fetch(kernelUrl);
      if (!response.ok) throw new Error(`Chladni kernel: HTTP ${response.status}`);
      compiled = await WebAssembly.compile(await response.arrayBuffer());
    } catch { /* JavaScript remains available when Wasm cannot load or compile. */ }
  })();
}

export function createWasmSimulation(module: WebAssembly.Module): Simulation {
  const kernel = new WebAssembly.Instance(module, { math: {
    sin: Math.sin, cos: Math.cos, exp: Math.exp, hypot: Math.hypot, atan2: Math.atan2,
  } }).exports as Kernel;
  // Fixed 3 MiB memory never grows, so these views remain valid for its lifetime.
  const buffer = kernel.memory.buffer;
  let offset = kernel.data();
  const floats = (length: number) => {
    const view = new Float32Array(buffer, offset, length); offset += length * 4; return view;
  };
  const x = floats(count), y = floats(count), vx = floats(count), vy = floats(count), grain = floats(count);
  floats(count); floats(count); // resting offsets and direction, owned by the kernel
  const weights = floats(modes.length);
  const modeOrder = new Int32Array(buffer, offset, modes.length); offset += modes.length * 4;
  floats(fieldLength);
  for (const surface of getSurfaces()) floats(fieldLength).set(surface);
  for (let i = 0; i < modeOrder.length; i++) modeOrder[i] = i;
  kernel.set_seed(hashSeed('chladni'));
  return {
    x, y, vx, vy, grain, weights, modeOrder,
    resetRandom: key => kernel.set_seed(hashSeed(key)), random: () => kernel.random(), seed: () => kernel.seed(),
    step: (evolution, dt, drive, scatter, width, phase, mix, moving) => kernel.step(evolution, dt, drive, scatter, width, phase, +mix, +moving),
  };
}
export function createSimulation(): Simulation {
  if (compiled) {
    try { return createWasmSimulation(compiled); } catch { /* Fall back if instantiation is unavailable. */ }
  }
  return createJsSimulation();
}
