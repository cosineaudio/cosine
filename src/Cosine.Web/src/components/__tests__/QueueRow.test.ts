import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import type { Track } from '@/api/types';

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('vue-sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), loading: vi.fn(() => 'toast-id') },
}));
vi.mock('@/api/browsing', () => ({
  getSimilarSongs2: vi.fn().mockResolvedValue([]),
  coverArtUrl: (id: string) => `/art/${id}`,
}));
vi.mock('@/api/playlists', () => ({
  getPlaylists: vi.fn().mockResolvedValue([]),
  createPlaylist: vi.fn().mockResolvedValue(undefined),
  deletePlaylist: vi.fn().mockResolvedValue(undefined),
  updatePlaylist: vi.fn().mockResolvedValue(undefined),
  isSystemPlaylist: () => false,
}));
vi.mock('@/api/annotation', () => ({
  star: vi.fn().mockResolvedValue(undefined),
  unstar: vi.fn().mockResolvedValue(undefined),
}));
vi.mock('@/api/lists', () => ({ getStarred2: vi.fn().mockResolvedValue({}) }));
vi.mock('@/stores/playback', () => ({
  usePlaybackStore: () => ({ addToQueue: vi.fn(), play: vi.fn() }),
}));

import QueueRow from '../QueueRow.vue';

const TRACK = { id: 's1', title: 'Perihelion', album: 'Night Signal', duration: 12 } as Track;

describe('QueueRow', () => {
  beforeEach(() => setActivePinia(createPinia()));

  /**
   * The row's root is now the context-menu wrapper, which renders no element of its own. Vue only
   * inherits fallthrough attributes onto a single root *element*, so the drag wiring is placed by
   * hand - and would be dropped silently, not loudly, if that ever came undone. Reordering the
   * queue by dragging is the thing that breaks, so it is the thing that gets asserted.
   */
  it('lands the list’s drag wiring on the draggable row itself', async () => {
    const onDragstart = vi.fn();
    const onDrop = vi.fn();
    const wrapper = mount(QueueRow, {
      props: { track: TRACK },
      attrs: { onDragstart, onDrop },
    });

    const row = wrapper.get('[draggable="true"]');
    await row.trigger('dragstart');
    await row.trigger('drop');

    expect(onDragstart).toHaveBeenCalledTimes(1);
    expect(onDrop).toHaveBeenCalledTimes(1);
  });

  it('still emits play and remove from its own controls', async () => {
    const wrapper = mount(QueueRow, { props: { track: TRACK } });

    await wrapper.get(`[aria-label="Play ${TRACK.title}"]`).trigger('click');
    await wrapper.get(`[aria-label="Remove ${TRACK.title} from queue"]`).trigger('click');

    expect(wrapper.emitted('play')).toHaveLength(1);
    expect(wrapper.emitted('remove')).toHaveLength(1);
  });

  it('offers a menu trigger alongside the remove button', () => {
    const wrapper = mount(QueueRow, { props: { track: TRACK } });
    expect(wrapper.find(`[aria-label="More options for ${TRACK.title}"]`).exists()).toBe(true);
  });
});
