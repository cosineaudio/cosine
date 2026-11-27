import { afterEach, expect, it } from 'vitest';
import { DOMWrapper, flushPromises, mount } from '@vue/test-utils';
import VisualizerPicker from '../VisualizerPicker.vue';

const mounted: ReturnType<typeof mount>[] = [];
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); });

it('opens a names-only menu and closes after selecting a renderer', async () => {
  const wrapper = mount(VisualizerPicker, { props: { modelValue: 'spectrum' }, attachTo: document.body });
  mounted.push(wrapper);
  const trigger = wrapper.get('button');
  await trigger.trigger('keydown', { key: 'Enter' });
  await flushPromises();
  const body = new DOMWrapper(document.body);
  const items = body.findAll('[role="menuitemradio"]');
  expect(items.map(item => item.text())).toEqual(['Off', 'Spectrum', 'Phosphor', 'Meld', 'Aurora', 'Ink', 'Chladni']);
  expect(items[1]!.attributes('aria-checked')).toBe('true');
  expect(body.get('[role="menu"]').find('svg, img, canvas').exists()).toBe(false);
  await items[2]!.trigger('click'); await flushPromises();
  expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['phosphor']);
  expect(body.find('[role="menu"]').exists()).toBe(false);
});
