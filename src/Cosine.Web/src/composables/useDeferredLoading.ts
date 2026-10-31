import { ref, watch, onScopeDispose, type Ref } from 'vue';

export interface DeferredLoadingOptions {
  /** How long a load must run before anything is shown. */
  delay?: number;
  /** Once shown, how long it stays up regardless of when the data lands. */
  minVisible?: number;
}

/**
 * Gates a loading flag so fast responses never flash a placeholder.
 *
 * cosine is quick enough that most requests resolve inside a couple of frames.
 * Rendering a skeleton on frame one turns those into a strobe - worse than
 * showing nothing at all. So the returned ref stays `false` until the source
 * has been loading for `delay`, and once it does flip, it holds for at least
 * `minVisible` so the placeholder is never a blink either.
 *
 * The net effect: on a fast library the UI looks instant, and on a slow one it
 * degrades into a deliberate, unhurried skeleton.
 *
 *     const loading = ref(true);
 *     const showSkeleton = useDeferredLoading(loading);
 */
export function useDeferredLoading(
  source: Ref<boolean>,
  options: DeferredLoadingOptions = {},
): Ref<boolean> {
  const { delay = 220, minVisible = 400 } = options;

  const visible = ref(false);
  let delayTimer: ReturnType<typeof setTimeout> | null = null;
  let hideTimer: ReturnType<typeof setTimeout> | null = null;
  /** When the placeholder became visible, so we know how much of `minVisible` is left. */
  let shownAt = 0;

  function clearTimers() {
    if (delayTimer !== null) clearTimeout(delayTimer);
    if (hideTimer !== null) clearTimeout(hideTimer);
    delayTimer = null;
    hideTimer = null;
  }

  watch(
    source,
    (loading) => {
      clearTimers();

      if (loading) {
        // Already up from a previous cycle (e.g. a route param change starting a
        // second load) - keep it up rather than restarting the delay, which would
        // blank the placeholder mid-load.
        if (visible.value) return;
        delayTimer = setTimeout(() => {
          visible.value = true;
          shownAt = performance.now();
        }, delay);
        return;
      }

      if (!visible.value) return; // resolved inside the delay - nothing was ever shown

      const remaining = minVisible - (performance.now() - shownAt);
      if (remaining <= 0) {
        visible.value = false;
        return;
      }
      hideTimer = setTimeout(() => {
        visible.value = false;
      }, remaining);
    },
    { immediate: true },
  );

  onScopeDispose(clearTimers);

  return visible;
}
