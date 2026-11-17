import { computed } from 'vue';
import { useStorage } from '@vueuse/core';
import { normalizeSelection, resolveVisualizer } from '@/visualizers/registry';

const KEY = 'cosine_visualizer';

export function useVisualizer() {
  // Preserve the previous on/off setting on first use of the new picker.
  let initial = 'spectrum';
  try { if (localStorage.getItem('cosine_visualizer_on') === '0') initial = 'off'; } catch { /* storage unavailable */ }
  const stored = useStorage(KEY, initial);
  const selection = computed({
    get: () => normalizeSelection(stored.value),
    set: (value: string) => { stored.value = normalizeSelection(value); },
  });
  return {
    selection,
    enabled: computed(() => selection.value !== 'off'),
    placement: computed(() => resolveVisualizer(selection.value).placement),
  };
}
