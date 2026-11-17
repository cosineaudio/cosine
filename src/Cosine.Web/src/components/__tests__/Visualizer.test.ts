import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import Visualizer from '../Visualizer.vue';
import VisualizerCanvas from '../VisualizerCanvas.vue';
import type { VisualizerDefinition, VisualizerRenderer } from '@/visualizers/types';

const mocks = vi.hoisted(() => ({ load: vi.fn(), secondLoad: vi.fn() }));
vi.mock('@/visualizers/registry', () => ({
  resolveVisualizer: (id: string) => ({ id, audio: id === 'phosphor' ? 'waveform' : 'frequency', load: id === 'phosphor' ? mocks.secondLoad : mocks.load }),
}));
vi.mock('@/stores/playback', () => ({ usePlaybackStore: () => { throw new Error('Guest must not create library playback'); } }));

const mounted: ReturnType<typeof mount>[] = [];
let frames: Map<number, FrameRequestCallback>;
let nextId: number;
let hidden: boolean;
let reduced: boolean;
let renderer: VisualizerRenderer;
let create: ReturnType<typeof vi.fn>;
let analyser: AnalyserNode;
let disconnect: ReturnType<typeof vi.fn>;
function advance(time = 16) {
  const pending = [...frames.values()];
  frames.clear();
  pending.forEach(callback => callback(time));
}
function host() {
  const wrapper = mount(Visualizer, { props: { tint: null, active: true, visualizer: 'spectrum', analyser: () => analyser, playing: true } });
  mounted.push(wrapper);
  return wrapper;
}
beforeEach(() => {
  frames = new Map(); nextId = 0; hidden = false; reduced = false;
  vi.spyOn(document, 'hidden', 'get').mockImplementation(() => hidden);
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frames.set(++nextId, callback); return nextId; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
  vi.stubGlobal('matchMedia', () => ({ get matches() { return reduced; }, addEventListener: vi.fn(), removeEventListener: vi.fn() }));
  disconnect = vi.fn();
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect = disconnect; });
  renderer = { resize: vi.fn(), update: vi.fn(), draw: vi.fn(), settling: false, dispose: vi.fn() };
  create = vi.fn(() => renderer);
  mocks.load.mockResolvedValue({ create });
  mocks.secondLoad.mockResolvedValue({ create });
  analyser = { context: { sampleRate: 48000 }, fftSize: 2048, frequencyBinCount: 1024,
    getFloatFrequencyData: vi.fn(), getFloatTimeDomainData: vi.fn(),
  } as unknown as AnalyserNode;
});
afterEach(() => {
  mounted.splice(0).forEach(wrapper => wrapper.unmount());
  vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.clearAllMocks();
});

describe('visualizer lifecycle', () => {
  it('samples only required audio and stops when hidden or inactive', async () => {
    const wrapper = host(); await flushPromises();
    expect(mocks.secondLoad).not.toHaveBeenCalled();
    advance();
    expect(analyser.getFloatFrequencyData).toHaveBeenCalledTimes(1);
    expect(analyser.getFloatTimeDomainData).not.toHaveBeenCalled();
    hidden = true; document.dispatchEvent(new Event('visibilitychange'));
    expect(frames.size).toBe(0);
    hidden = false; document.dispatchEvent(new Event('visibilitychange'));
    expect(frames.size).toBe(1);
    await wrapper.setProps({ active: false });
    expect(frames.size).toBe(0);
    wrapper.unmount(); mounted.pop();
    expect(renderer.dispose).toHaveBeenCalledOnce();
    expect(disconnect).toHaveBeenCalledOnce();
  });

  it('uses a new canvas and disposes the old renderer when switching', async () => {
    const wrapper = host(); await flushPromises();
    const oldCanvas = wrapper.get('canvas').element;
    await wrapper.setProps({ visualizer: 'phosphor' }); await flushPromises();
    expect(renderer.dispose).toHaveBeenCalledOnce();
    expect(wrapper.get('canvas').element).not.toBe(oldCanvas);
    expect(frames.size).toBe(1);
    advance();
    expect(analyser.getFloatTimeDomainData).toHaveBeenCalledOnce();
    expect(analyser.getFloatFrequencyData).not.toHaveBeenCalled();
  });

  it('does not revive a renderer whose module finishes loading after switching', async () => {
    let finish!: (value: { create: typeof create }) => void;
    mocks.load.mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const wrapper = host();
    await wrapper.setProps({ visualizer: 'phosphor' }); await flushPromises();
    const staleCreate = vi.fn(() => renderer);
    finish({ create: staleCreate }); await flushPromises();
    expect(staleCreate).not.toHaveBeenCalled();
    expect(frames.size).toBe(1);
  });

  it('lets pause decay finish and then stops scheduling frames', async () => {
    const wrapper = host(); await flushPromises();
    await wrapper.setProps({ playing: false });
    advance();
    expect(renderer.update).toHaveBeenLastCalledWith(null, false, 1 / 60);
    expect(frames.size).toBe(0);
    await wrapper.setProps({ playing: true });
    expect(frames.size).toBe(1);
  });

  it('renders one static frame with reduced motion', async () => {
    reduced = true;
    host(); await flushPromises();
    expect(renderer.update).toHaveBeenCalledWith(expect.objectContaining({ frequency: expect.any(Float32Array) }), true, 1 / 60);
    expect(frames.size).toBe(0);
  });

  it('shows unavailable when a source cannot be analysed without loading a renderer', async () => {
    const wrapper = mount(VisualizerCanvas, { props: {
      definition: { id: 'test', name: 'Test', audio: 'frequency', load: mocks.load } as VisualizerDefinition,
      tint: null, active: true, analyser: () => null,
    } });
    mounted.push(wrapper); await flushPromises();
    expect(wrapper.text()).toContain('isn’t available');
    expect(mocks.load).not.toHaveBeenCalled();
    expect(frames.size).toBe(0);
  });
});
