import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { effectScope, nextTick, type EffectScope } from 'vue';
import { useVisualizer } from '../useVisualizer';

const scopes: EffectScope[] = [];
function preference() {
  const scope = effectScope(); scopes.push(scope);
  return scope.run(useVisualizer)!;
}
beforeEach(() => {
  const data = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => data.get(key) ?? null,
    setItem: (key: string, value: string) => data.set(key, value),
    removeItem: (key: string) => data.delete(key),
    clear: () => data.clear(),
    key: (index: number) => [...data.keys()][index] ?? null,
    get length() { return data.size; },
  });
});
afterEach(() => { scopes.splice(0).forEach(scope => scope.stop()); vi.unstubAllGlobals(); });

it('preserves legacy Off and defaults new users to Spectrum', () => {
  expect(preference().selection.value).toBe('spectrum');
  localStorage.removeItem('cosine_visualizer');
  localStorage.setItem('cosine_visualizer_on', '0');
  expect(preference().enabled.value).toBe(false);
});
it('persists selection and shares changes with the other player', async () => {
  const first = preference(); const second = preference();
  first.selection.value = 'phosphor'; await nextTick(); await nextTick();
  expect(localStorage.getItem('cosine_visualizer')).toBe('phosphor');
  expect(second.selection.value).toBe('phosphor');
  first.selection.value = 'off'; await nextTick(); await nextTick();
  expect(second.enabled.value).toBe(false);
});
it('falls back when a saved renderer has been removed', () => {
  localStorage.setItem('cosine_visualizer', 'removed');
  expect(preference().selection.value).toBe('spectrum');
});
