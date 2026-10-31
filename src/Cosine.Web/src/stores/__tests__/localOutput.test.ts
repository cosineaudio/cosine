import { vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';

vi.mock('@/api/annotation', () => ({ scrobble: vi.fn().mockResolvedValue(undefined) }));
vi.mock('@/api/queue', () => ({
  getPlayQueue: vi.fn().mockResolvedValue(null),
  savePlayQueue: vi.fn().mockResolvedValue(undefined),
}));

import { usePlayerStore } from '../player';
import { describeOutputContract } from './playbackOutputContract';

/**
 * The local half of the shared contract (plan §6). `player.test.ts` covers what only the browser
 * does - preload, scrobble thresholds, the analyser; this file asserts that the behaviour the
 * *interface* promises is the same behaviour a speaker gives, so "local and remote agree" is
 * checked rather than assumed.
 */
describeOutputContract('local output', async () => {
  setActivePinia(createPinia());
  return {
    output: usePlayerStore(),
    settle: async () => { await nextTick(); },
  };
});
