import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import AppSidebar from '../layout/AppSidebar.vue';

describe('AppSidebar', () => {
  it('highlights Home only at the home route when navigating through the library', async () => {
    const names = ['home', 'search', 'albums', 'artists', 'genres', 'favorites', 'playlists', 'history', 'insights', 'radio'];
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', component: { template: '<router-view />' }, children: names.map(name => ({
        path: name === 'home' ? '' : name, name, component: { template: '<div />' },
      })) }],
    });
    await router.push('/');
    await router.isReady();
    const wrapper = mount(AppSidebar, { global: { plugins: [createPinia(), router] } });
    const home = () => wrapper.get('nav a[href="/"]');
    expect(home().classes()).toContain('!bg-surface-2');
    for (const name of ['favorites', 'playlists', 'history', 'insights', 'radio', 'albums']) {
      await router.push({ name });
      expect(home().classes()).not.toContain('!bg-surface-2');
      const highlighted = wrapper.findAll('nav a').filter(link => link.classes().includes('!bg-surface-2'));
      expect(highlighted).toHaveLength(1);
      expect(highlighted[0]!.attributes('href')).toBe(`/${name}`);
    }
    await router.push({ name: 'home' });
    expect(home().classes()).toContain('!bg-surface-2');
    wrapper.unmount();
  });
});
