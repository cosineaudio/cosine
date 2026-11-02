import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import InsightsPage from '../InsightsPage.vue';
import { getListeningInsights, type ListeningInsights } from '@/api/insights';

vi.mock('@/api/insights', () => ({ getListeningInsights: vi.fn() }));
vi.mock('@/composables/usePlayback', () => ({ usePlayback: () => ({ playTracks: vi.fn() }) }));

const empty: ListeningInsights = {
  from: '2026-10-01T00:00:00Z', until: '2026-11-01T00:00:00Z',
  recap: { totalPlays: 0, estimatedMinutes: 0, uniqueTracks: 0, uniqueArtists: 0, topTracks: [], topArtists: [], topAlbums: [], topGenres: [], playsOverTime: [] },
  previousMonthPlays: 0, previousMonthGenres: [], libraryTracks: 100, exploredTracks: 25,
};
const options = { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } };

beforeEach(() => { vi.resetAllMocks(); vi.mocked(getListeningInsights).mockResolvedValue(empty); });

describe('InsightsPage', () => {
  it('shows an honest empty month and retained-history exploration', async () => {
    const wrapper = mount(InsightsPage, options);
    await flushPromises();
    expect(wrapper.text()).toContain('No listening recorded this month');
    expect(wrapper.text()).toContain("25 of 100 tracks");
    expect(wrapper.get('[role="progressbar"]').attributes('aria-valuenow')).toBe('25');
    expect(wrapper.text()).not.toContain('Daily listening');
  });

  it('requests the selected month and fills missing activity days with zero', async () => {
    vi.mocked(getListeningInsights).mockResolvedValue({ ...empty, recap: { ...empty.recap, totalPlays: 2, playsOverTime: [{ date: '2026-10-02T00:00:00Z', count: 2 }] } });
    const wrapper = mount(InsightsPage, options);
    await flushPromises();
    wrapper.getComponent({ name: 'MonthStepper' }).vm.$emit('update:modelValue', '2026-10');
    await flushPromises();
    expect(getListeningInsights).toHaveBeenLastCalledWith(2026, 10);
    expect(wrapper.findAll('[tabindex="0"]')).toHaveLength(31);
    expect(wrapper.get('[aria-label="2026-10-01: 0 plays"]').exists()).toBe(true);
    expect(wrapper.get('[aria-label="2026-10-02: 2 plays"]').exists()).toBe(true);
  });

  it('offers retry when the request fails', async () => {
    vi.mocked(getListeningInsights).mockRejectedValueOnce(new Error('Could not load insights'));
    const wrapper = mount(InsightsPage, options);
    await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain('Could not load insights');
    await wrapper.get('[role="alert"] button').trigger('click');
    await flushPromises();
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Explore your library');
  });
});
