import { computed, ref, type ComputedRef } from 'vue';

/**
 * Whether a route navigation is currently resolving.
 *
 * Every route in the app is a dynamic import, so a cold navigation spends time
 * fetching a chunk *before* the destination component - and therefore its
 * skeleton - exists to render anything. That gap is what this covers; once the
 * page mounts, its own skeletons take over.
 *
 * A plain boolean rather than a counter: vue-router serialises navigations, and
 * a redirect runs `beforeEach` twice against a single `afterEach`, which would
 * leave a counter stuck above zero forever.
 */
const navigating = ref(false);

export function startNavigation(): void {
  navigating.value = true;
}

export function endNavigation(): void {
  navigating.value = false;
}

export function useNavProgress(): ComputedRef<boolean> {
  return computed(() => navigating.value);
}
