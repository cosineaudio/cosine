import { describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import AdminLibraryHealthPage from '../admin/AdminLibraryHealthPage.vue';
import { getLibraryHealth } from '@/api/insights';

vi.mock('@/api/insights', () => ({ getLibraryHealth: vi.fn() }));

describe('AdminLibraryHealthPage', () => {
  it('marks an unavailable mount as not checked instead of healthy', async () => {
    vi.mocked(getLibraryHealth).mockResolvedValue({
      checkedAt: '2026-10-01T00:00:00Z', totalTracks: 5, totalAlbums: 1, mediaRootAvailable: false,
      categories: [{ key: 'files', title: 'Unavailable files', description: 'File checks', count: 0, items: [] }],
    });
    const wrapper = mount(AdminLibraryHealthPage, { global: { stubs: { RouterLink: true } } });
    await flushPromises();
    expect(wrapper.text()).toContain('File checks were skipped');
    expect(wrapper.findAll('button')[1]!.text()).toContain('—');
    await wrapper.findAll('button')[1]!.trigger('click');
    expect(wrapper.text()).toContain('File availability could not be checked.');
    expect(wrapper.text()).not.toContain('No issues found');
  });
});
