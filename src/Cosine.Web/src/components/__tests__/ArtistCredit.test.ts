// @vitest-environment happy-dom
import { describe, it, expect, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import ArtistCredit from '../ArtistCredit.vue';
import type { Track } from '../../api/types';

const push = vi.fn();
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }));

function track(partial: Partial<Track>): Track {
  return { id: 't1', isDir: false, title: 'Song', ...partial } as Track;
}

const collab = track({
  artist: 'Pouya & Fat Nick',
  displayArtist: 'Pouya & Fat Nick',
  artistId: 'a1',
  artists: [
    { id: 'a1', name: 'Pouya' },
    { id: 'a2', name: 'Fat Nick' },
  ],
});

/**
 * The rendered half of the credit. The pure segment logic is covered in
 * `lib/__tests__/artistCredit.test.ts`; what matters here is that each name is a separate
 * control, that clicking one does not also trigger the row it sits in, and that the caller's
 * layout classes actually land on the element.
 */
describe('ArtistCredit', () => {
  it('renders one button per artist and plain text between them', () => {
    const wrapper = mount(ArtistCredit, { props: { track: collab } });

    const buttons = wrapper.findAll('button');
    expect(buttons.map((b) => b.text())).toEqual(['Pouya', 'Fat Nick']);
    expect(wrapper.text()).toBe('Pouya & Fat Nick');
  });

  it('routes to the clicked artist, not the primary one', async () => {
    push.mockClear();
    const wrapper = mount(ArtistCredit, { props: { track: collab } });

    await wrapper.findAll('button')[1].trigger('click');

    expect(push).toHaveBeenCalledWith({ name: 'artist', params: { id: 'a2' } });
  });

  /**
   * Every surface but the players puts this inside a row that plays on click. Without
   * `@click.stop`, tapping a guest's name would navigate *and* start the track.
   */
  it('does not let the click reach the surrounding row', async () => {
    const rowClick = vi.fn();
    const wrapper = mount(
      {
        components: { ArtistCredit },
        props: ['track'],
        template: '<div @click="$emit(\'rowclick\')"><ArtistCredit :track="track" /></div>',
      },
      { props: { track: collab }, attrs: { onRowclick: rowClick } },
    );

    await wrapper.findAll('button')[0].trigger('click');

    expect(push).toHaveBeenCalled();
    expect(rowClick).not.toHaveBeenCalled();
  });

  it('uses a caller-supplied navigate so the full-screen player can close first', async () => {
    push.mockClear();
    const navigate = vi.fn();
    const wrapper = mount(ArtistCredit, { props: { track: collab, navigate } });

    await wrapper.findAll('button')[1].trigger('click');

    expect(navigate).toHaveBeenCalledWith('a2');
    expect(push).not.toHaveBeenCalled();
  });

  /**
   * A single root element, so the caller's classes are inherited. A v-if/v-else pair of roots
   * silently drops them, and every row's truncation with them.
   */
  it('inherits the caller class onto its root', () => {
    const wrapper = mount(ArtistCredit, {
      props: { track: collab },
      attrs: { class: 'block truncate text-muted' },
    });

    expect(wrapper.classes()).toEqual(expect.arrayContaining(['block', 'truncate', 'text-muted']));
  });

  /**
   * It must contribute no display utility of its own. It used to set `inline-block`, and two
   * Tailwind display classes on one element are resolved by stylesheet order rather than
   * attribute order - so a caller passing `block` did not reliably win. That turned the
   * full-screen player's artist line inline and pulled the "FLAC · 856 kbps" badge, which is
   * meant to sit on its own line beneath it, up alongside the artists.
   */
  it('adds no display class that could beat the caller layout', () => {
    const wrapper = mount(ArtistCredit, { props: { track: collab }, attrs: { class: 'block' } });

    const display = wrapper.classes().filter((c) =>
      ['inline', 'inline-block', 'flex', 'inline-flex', 'grid', 'contents'].includes(c),
    );
    expect(display).toEqual([]);
    expect(wrapper.classes()).toContain('block');
  });

  it('falls back to a single link for a track with no credit', async () => {
    push.mockClear();
    const wrapper = mount(ArtistCredit, {
      props: { track: track({ artist: 'Pink Floyd', artistId: 'a9' }) },
    });

    const button = wrapper.get('button');
    expect(button.text()).toBe('Pink Floyd');
    await button.trigger('click');
    expect(push).toHaveBeenCalledWith({ name: 'artist', params: { id: 'a9' } });
  });

  it('shows the placeholder, unlinked, for a track with no artist at all', () => {
    const wrapper = mount(ArtistCredit, { props: { track: track({}) } });

    expect(wrapper.text()).toBe('Unknown artist');
    expect(wrapper.findAll('button')).toHaveLength(0);
  });

  it('renders nothing clickable for a null track', () => {
    const wrapper = mount(ArtistCredit, { props: { track: null, placeholder: '-' } });

    expect(wrapper.text()).toBe('-');
  });
});
