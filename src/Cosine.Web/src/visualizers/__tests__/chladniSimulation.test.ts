import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, it } from 'vitest';
import { createJsSimulation, type Simulation } from '../renderers/chladni/simulation';
import { createWasmSimulation } from '../renderers/chladni/wasm';

const module = new WebAssembly.Module(new Uint8Array(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../renderers/chladni/kernel.wasm'))));
function initialize(sim: Simulation, key: string) {
  sim.resetRandom(key);
  for (let i = 0; i < 16; i++) sim.modeOrder[i] = i;
  for (let i = 15; i > 0; i--) {
    const j = Math.floor(sim.random() * (i + 1));
    [sim.modeOrder[i], sim.modeOrder[j]] = [sim.modeOrder[j]!, sim.modeOrder[i]!];
  }
  sim.weights.fill(0); sim.weights[3] = 1;
  sim.step(0, 0, 0, 0, 0, 0, true, false); sim.seed();
}
function expectSame(a: Simulation, b: Simulation) {
  for (const key of ['x', 'y', 'vx', 'vy', 'grain'] as const) {
    let error = 0;
    for (let i = 0; i < a[key].length; i++) error = Math.max(error, Math.abs(a[key][i]! - b[key][i]!));
    expect(error, key).toBeLessThan(0.000001);
  }
}

it('matches JS seeds, changing modal fields, attacks and wall bounces without copying particle buffers', () => {
  const js = createJsSimulation(), wasm = createWasmSimulation(module);
  const buffer = wasm.x.buffer;
  initialize(js, 'track-🎶'); initialize(wasm, 'track-🎶');
  expectSame(js, wasm);
  for (let frame = 0; frame < 600; frame++) {
    for (let band = 0; band < 16; band++) js.weights[band] = wasm.weights[band] = (band + 1) / 136;
    const hit = frame % 10 === 0 ? 0.8 : 0;
    for (const sim of [js, wasm]) sim.step(frame * 0.013 % 16, 0.05, 0.7, hit, hit, frame * 0.3, true, true);
    if (frame % 100 === 0) expectSame(js, wasm);
  }
  expectSame(js, wasm);
  expect(wasm.x.buffer).toBe(buffer);
  expect(wasm.y.buffer).toBe(buffer);
  expect(wasm.x.every(x => Number.isFinite(x) && Math.abs(x) <= 0.975)).toBe(true);
});

it('isolates Wasm instances and restores the same seeded layout after another track', () => {
  const first = createWasmSimulation(module), second = createWasmSimulation(module);
  initialize(first, 'first'); initialize(second, 'first');
  const before = second.x.slice();
  initialize(first, 'second');
  expect(second.x).toEqual(before);
  expect(first.x).not.toEqual(before);
  initialize(first, 'first');
  expectSame(first, second);
  expect(first.x.buffer).not.toBe(second.x.buffer);
});
