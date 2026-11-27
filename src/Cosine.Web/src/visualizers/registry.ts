import type { VisualizerDefinition } from './types';

// Register/remove a renderer here. Modules load only when selected.
export const visualizers: readonly VisualizerDefinition[] = [
  { id: 'spectrum', name: 'Spectrum', placement: 'floor', audio: 'frequency', load: () => import('./renderers/spectrum') },
  { id: 'phosphor', name: 'Phosphor', placement: 'full', audio: 'frequency', load: () => import('./renderers/phosphor') },
  { id: 'metaballs', name: 'Meld', placement: 'full', maxPixels: 600_000, audio: 'frequency', load: () => import('./renderers/metaballs') },
  { id: 'aurora', name: 'Aurora', placement: 'full', maxPixels: 450_000, audio: 'frequency', load: () => import('./renderers/aurora') },
  { id: 'ink', name: 'Ink', placement: 'full', maxPixels: 800_000, audio: 'frequency', load: () => import('./renderers/ink') },
  { id: 'chladni', name: 'Chladni', placement: 'full', audio: 'both', load: async () => { const module = await import('./renderers/chladni'); await module.prepareSimulation(); return module; } },
];
export const defaultVisualizer = visualizers[0]!;
export function resolveVisualizer(id: string): VisualizerDefinition {
  return visualizers.find(v => v.id === (id === 'contour' ? 'metaballs' : id)) ?? defaultVisualizer;
}
export function normalizeSelection(id: string | null): string {
  return id === 'off' ? 'off' : resolveVisualizer(id ?? '').id;
}
