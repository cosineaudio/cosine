import { afterEach, describe, expect, it } from 'vitest';
import { mount, flushPromises, DOMWrapper } from '@vue/test-utils';
import MonthStepper from '../ui/MonthStepper.vue';

const body = new DOMWrapper(document.body);
const mounted: ReturnType<typeof mount>[] = [];
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); });
function picker(value = '2026-11') {
  const wrapper = mount(MonthStepper, {
    props: { modelValue: value, max: '2026-11' },
    attachTo: document.body,
  });
  mounted.push(wrapper);
  return wrapper;
}

describe('MonthStepper', () => {
  it('steps across year boundaries and prevents future months', async () => {
    const wrapper = picker('2026-01');
    await wrapper.get('[aria-label="Previous month"]').trigger('click');
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['2025-12']);
    await wrapper.setProps({ modelValue: '2026-11' });
    expect(wrapper.get('[aria-label="Next month"]').attributes('disabled')).toBeDefined();
    await wrapper.setProps({ modelValue: '1970-01' });
    expect(wrapper.get('[aria-label="Previous month"]').attributes('disabled')).toBeDefined();
  });

  it('opens a month grid, disables future months, and selects a month', async () => {
    const wrapper = picker();
    await wrapper.get('[aria-label^="Choose month,"]').trigger('click');
    await flushPromises();
    expect(body.get('[aria-label="December 2026"]').attributes('disabled')).toBeDefined();
    expect(body.get('[aria-label="November 2026"]').attributes('aria-pressed')).toBe('true');
    await body.get('[aria-label="October 2026"]').trigger('click');
    await flushPromises();
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['2026-10']);
    expect(body.find('[aria-label="Months in 2026"]').exists()).toBe(false);
  });

  it('browses years without selecting and can return to the current month', async () => {
    const wrapper = picker('2025-03');
    await wrapper.get('[aria-label^="Choose month,"]').trigger('click');
    await flushPromises();
    await body.get('[aria-label="Previous year"]').trigger('click');
    expect(body.find('[aria-label="Months in 2024"]').exists()).toBe(true);
    expect(wrapper.emitted('update:modelValue')).toBeUndefined();
    const current = body.findAll('button').find(button => button.text() === 'This month')!;
    await current.trigger('click');
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['2026-11']);
  });
});
