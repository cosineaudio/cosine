/** Shared helpers for the skeleton composites. */

/** How many tiles the shimmer cascade runs across before it starts over. */
const STAGGER_WAVE = 8;
const STAGGER_STEP_MS = 60;

/**
 * Staggers a tile's shimmer so a grid cascades instead of pulsing in unison.
 *
 * The delay wraps every `STAGGER_WAVE` tiles: a straight `i * step` would put a
 * six-second lag on the sixtieth tile of a long grid, which just reads as
 * "that one is broken".
 */
export function shimmerDelay(index: number): Record<string, string> {
  return { '--shimmer-delay': `${(index % STAGGER_WAVE) * STAGGER_STEP_MS}ms` };
}

/**
 * A stable pseudo-random bar width in `[min, max]` percent.
 *
 * Uniform-width placeholder bars read as a chart, not as text. Varying them by
 * index looks like prose without needing randomness - which would also make the
 * width change on every re-render.
 */
export function barWidth(index: number, min: number, max: number): string {
  // Golden-ratio increments spread sequential indices evenly across the range.
  const fraction = (index * 0.618033988749895) % 1;
  return `${Math.round(min + fraction * (max - min))}%`;
}
