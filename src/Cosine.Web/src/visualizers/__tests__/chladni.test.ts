import { expect, it, vi } from 'vitest';
import { create } from '../renderers/chladni';

function spectrum(kick: boolean, snare = false) {
  const values = new Float32Array(1024).fill(-100);
  values.fill(kick ? -25 : -100, 2, 11);
  values.fill(snare ? -25 : -100, 11, 512);
  return values;
}

it('moves grains on attacks, preserves input, settles on pause, and respects reduced motion', () => {
  const coordinates: number[] = [];
  const ctx = {
    clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
    rect: (...values: number[]) => coordinates.push(...values),
  };
  const canvas = document.createElement('canvas');
  canvas.width = 800; canvas.height = 600;
  vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
  const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
  renderer.setTrack?.('track');
  renderer.resize({ width: 800, height: 600, dpr: 1 });
  const theme = { tint: '#336699', text: '#fff', muted: '#888' };
  const draw = () => {
    coordinates.length = 0;
    renderer.draw(theme);
    expect(coordinates.every(Number.isFinite)).toBe(true);
    return [...coordinates];
  };
  const original = draw();
  const waveform = Float32Array.from({ length: 2048 }, (_, i) => Math.sin(i * 0.1) * 0.4);
  const copy = waveform.slice();
  renderer.update({ waveform }, false, 1 / 60);
  expect(draw()).not.toEqual(original);
  expect(waveform).toEqual(copy);
  // A steady tone must keep transporting grains after its initial attack fades.
  for (let i = 0; i < 240; i++) renderer.update({ waveform }, false, 1 / 60);
  const sustained = draw();
  renderer.update({ waveform }, false, 1 / 60);
  const moving = draw();
  let travel = 0;
  for (let i = 0; i < moving.length; i += 4) {
    travel += Math.hypot(moving[i]! - sustained[i]!, moving[i + 1]! - sustained[i + 1]!);
  }
  expect(travel / (moving.length / 4)).toBeGreaterThan(0.03);
  for (let i = 0; i < 100; i++) renderer.update(null, false, 1 / 60);
  expect(renderer.settling).toBe(false);
  const paused = draw();
  renderer.update(null, false, 1 / 60);
  expect(draw()).toEqual(paused);
  renderer.update({ waveform }, true, 1 / 60);
  expect(draw()).toEqual(paused);
  expect(renderer.settling).toBe(false);
  renderer.dispose();
});

it('repeats a track seed while varying patterns with track identity and frequency bands', () => {
  function run(track: string, hz: number) {
    const points: number[] = [];
    const ctx = {
      clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
      rect: (x: number, y: number) => { if (points.length < 100) points.push(x, y); },
    };
    const canvas = document.createElement('canvas');
    canvas.width = 800; canvas.height = 600;
    vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
    const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
    renderer.setTrack?.(track);
    const frequency = new Float32Array(1024).fill(-100);
    frequency[Math.round(hz * 2048 / 48000)] = -20;
    const input = frequency.slice();
    for (let i = 0; i < 90; i++) renderer.update({ frequency }, false, 1 / 60);
    renderer.draw({ tint: '#336699', text: '#fff', muted: '#888' });
    expect(frequency).toEqual(input);
    renderer.dispose();
    return points;
  }
  const bass = run('first-track', 100);
  expect(run('first-track', 100)).toEqual(bass);
  expect(run('different-track', 100)).not.toEqual(bass);
  expect(run('first-track', 3000)).not.toEqual(bass);
});

it('restores a track starting shape after another track and leaves the current track running', () => {
  const points: number[] = [];
  const ctx = { clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
    rect: (x: number, y: number) => points.push(x, y) };
  const canvas = document.createElement('canvas');
  canvas.width = 800; canvas.height = 600;
  vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
  const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
  const draw = () => {
    points.length = 0;
    renderer.draw({ tint: '#336699', text: '#fff', muted: '#888' });
    return [...points];
  };
  renderer.setTrack?.('first-track');
  const start = draw();
  renderer.update({ frequency: spectrum(true) }, false, 0.05);
  const evolved = draw();
  expect(evolved).not.toEqual(start);
  renderer.setTrack?.('first-track');
  expect(draw()).toEqual(evolved);
  renderer.setTrack?.('different-track');
  const other = draw();
  // Compare spatial density as well as grain positions: the shape must differ.
  const density = (coordinates: number[]) => {
    const bins = new Array<number>(144).fill(0);
    for (let i = 0; i < coordinates.length; i += 2) {
      const col = Math.max(0, Math.min(11, Math.floor(coordinates[i]! / 800 * 12)));
      const row = Math.max(0, Math.min(11, Math.floor(coordinates[i + 1]! / 600 * 12)));
      bins[row * 12 + col]! += 1 / 18000;
    }
    return bins;
  };
  const initialDensity = density(start);
  expect(density(other).reduce((sum, value, i) => sum + Math.abs(value - initialDensity[i]!), 0)).toBeGreaterThan(0.2);
  renderer.setTrack?.('first-track');
  expect(draw()).toEqual(start);
  renderer.dispose();
});

it('responds to a bass attack masked by steady midrange loudness', () => {
  function movement(withKick: boolean) {
    const points: number[] = [];
    const ctx = {
      clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
      rect: (x: number, y: number) => points.push(x, y),
    };
    const canvas = document.createElement('canvas');
    canvas.width = 800; canvas.height = 600;
    vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
    const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
    const tone = (bass: number) => Float32Array.from({ length: 2048 }, (_, i) =>
      0.3 * Math.sin(2 * Math.PI * 1000 * i / 48000) + bass * Math.sin(2 * Math.PI * 70 * i / 48000));
    const waveform = tone(0);
    for (let i = 0; i < 120; i++) renderer.update({ waveform }, false, 1 / 60);
    const theme = { tint: '#336699', text: '#fff', muted: '#888' };
    renderer.draw(theme);
    const before = [...points];
    points.length = 0;
    renderer.update({ waveform: tone(withKick ? 0.12 : 0), frequency: spectrum(withKick) }, false, 1 / 60);
    renderer.draw(theme);
    let travel = 0;
    for (let i = 0; i < points.length; i += 2) {
      travel += Math.hypot(points[i]! - before[i]!, points[i + 1]! - before[i + 1]!);
    }
    renderer.dispose();
    return travel / (points.length / 2);
  }
  expect(movement(true) - movement(false)).toBeGreaterThan(1);
});

it('keeps grains in view during a minute of sustained audio and repeated bass hits', () => {
  const points: number[][] = [];
  const ctx = {
    clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
    rect: (...values: number[]) => points.push(values),
  };
  const canvas = document.createElement('canvas');
  canvas.width = 800; canvas.height = 600;
  vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
  const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
  renderer.resize({ width: 800, height: 600, dpr: 1 });
  const tone = (level: number) => Float32Array.from({ length: 2048 }, (_, i) =>
    Math.sin(2 * Math.PI * 70 * i / 48000) * level);
  const quiet = tone(0.04), hit = tone(0.4);
  for (let i = 0; i < 1200; i++) renderer.update({ waveform: i % 10 < 2 ? hit : quiet, frequency: spectrum(i % 10 < 2) }, false, 0.05);
  renderer.draw({ tint: '#336699', text: '#fff', muted: '#888' });
  expect(points).toHaveLength(18000);
  expect(points.every(([x, y, w, h]) => x! >= 0 && y! >= 0 && x! + w! <= 800 && y! + h! <= 600)).toBe(true);
  const atEdge = points.filter(([x, y]) => x! < 16 || x! > 784 || y! < 12 || y! > 588).length;
  expect(atEdge / points.length).toBeLessThan(0.25);
  renderer.dispose();
});

it('expands on a kick and contracts on a snare-like attack', () => {
  function displacement(kind: 'kick' | 'snare') {
    const points: number[] = [];
    const ctx = { clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
      rect: (x: number, y: number) => points.push(x, y) };
    const canvas = document.createElement('canvas');
    canvas.width = 800; canvas.height = 600;
    vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
    const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
    const theme = { tint: '#336699', text: '#fff', muted: '#888' };
    renderer.draw(theme);
    const before = [...points];
    points.length = 0;
    const waveform = Float32Array.from({ length: 2048 }, (_, i) => {
      const t = 2 * Math.PI * i / 48000;
      return kind === 'kick' ? 0.4 * Math.sin(70 * t)
        : 0.12 * Math.sin(350 * t) + 0.3 * Math.sin(2500 * t) + 0.15 * Math.sin(4300 * t);
    });
    renderer.update({ waveform, frequency: spectrum(kind === 'kick', kind === 'snare') }, false, 1 / 60);
    renderer.draw(theme);
    let change = 0;
    for (let i = 0; i < points.length; i += 2) {
      change += Math.hypot(points[i]! - 400, points[i + 1]! - 300)
        - Math.hypot(before[i]! - 400, before[i + 1]! - 300);
    }
    renderer.dispose();
    return change / (points.length / 2);
  }
  expect(displacement('kick')).toBeGreaterThan(2);
  expect(displacement('snare')).toBeLessThan(-2);
});

it('changes the spatial pattern during a sustained, unchanging frequency band', () => {
  const density = new Float32Array(144);
  const ctx = {
    clearRect: vi.fn(), beginPath: vi.fn(), fill: vi.fn(),
    rect: (x: number, y: number) => {
      const col = Math.max(0, Math.min(11, Math.floor(x / 800 * 12)));
      const row = Math.max(0, Math.min(11, Math.floor(y / 600 * 12)));
      density[row * 12 + col]! += 1 / 18000;
    },
  };
  const canvas = document.createElement('canvas');
  canvas.width = 800; canvas.height = 600;
  vi.spyOn(canvas, 'getContext').mockImplementation(type => type === '2d' ? ctx as unknown as CanvasRenderingContext2D : null);
  const renderer = create(canvas, { sampleRate: 48000, fftSize: 2048 });
  const frequency = new Float32Array(1024).fill(-100);
  frequency[Math.round(100 * 2048 / 48000)] = -20;
  const theme = { tint: '#336699', text: '#fff', muted: '#888' };
  for (let i = 0; i < 80; i++) renderer.update({ frequency }, false, 0.05);
  renderer.draw(theme);
  const first = density.slice();
  density.fill(0);
  for (let i = 0; i < 240; i++) renderer.update({ frequency }, false, 0.05);
  renderer.draw(theme);
  const redistribution = density.reduce((total, value, i) => total + Math.abs(value - first[i]!), 0);
  expect(redistribution).toBeGreaterThan(0.2);
  renderer.dispose();
});
