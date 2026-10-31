import { ref, watch, unref, type Ref } from 'vue';
import { Vibrant } from 'node-vibrant/browser';
import { coverArtUrl } from '@/api/browsing';

/**
 * Dominant-color theming (LIBERTAE "art is the color").
 *
 * Extracts a single representative colour from a cover-art image and returns it
 * as an `rgb(...)` string suitable for washing large art-adjacent backdrops
 * (page headers, the full-screen player). Controls stay mono - only backdrops
 * take colour. Results are cached by cover id so the same art never re-decodes.
 */

const cache = new Map<string, string | null>();
const inflight = new Map<string, Promise<string | null>>();

function toRgb(rgb: [number, number, number] | undefined): string | null {
  if (!rgb) return null;
  const [r, g, b] = rgb;
  return `rgb(${Math.round(r)}, ${Math.round(g)}, ${Math.round(b)})`;
}

async function extract(url: string): Promise<string | null> {
  if (cache.has(url)) return cache.get(url) ?? null;
  const existing = inflight.get(url);
  if (existing) return existing;

  const job = (async () => {
    try {
      const palette = await Vibrant.from(url).getPalette();
      const swatch =
        palette.Vibrant ??
        palette.DarkVibrant ??
        palette.Muted ??
        palette.LightVibrant ??
        palette.DarkMuted ??
        null;
      const value = toRgb(swatch?.rgb as [number, number, number] | undefined);
      cache.set(url, value);
      return value;
    } catch {
      cache.set(url, null);
      return null;
    } finally {
      inflight.delete(url);
    }
  })();

  inflight.set(url, job);
  return job;
}

type StringSource = Ref<string | null | undefined> | (() => string | null | undefined);

/**
 * Reactive dominant colour for an arbitrary image URL - for surfaces whose art
 * doesn't come from the session's cover-art endpoint (the guest party page
 * reaches artwork through its ticket). Same cache, keyed by URL.
 */
export function useDominantColorFromUrl(source: StringSource) {
  const tint = ref<string | null>(null);
  const getUrl = typeof source === 'function' ? source : () => unref(source);

  watch(
    getUrl,
    async (url) => {
      if (!url) {
        tint.value = null;
        return;
      }
      if (cache.has(url)) {
        tint.value = cache.get(url) ?? null;
        return;
      }
      const value = await extract(url);
      // Only apply if this url is still the active one (guards fast navigation).
      if (getUrl() === url) tint.value = value;
    },
    { immediate: true },
  );

  return { tint };
}

/**
 * Reactive dominant colour for a cover-art id. Pass a ref or getter; the tint
 * updates (and races are guarded) whenever the id changes.
 */
export function useDominantColor(source: StringSource) {
  const getId = typeof source === 'function' ? source : () => unref(source);
  // A tiny thumbnail is plenty for palette extraction and keeps it fast.
  return useDominantColorFromUrl(() => {
    const id = getId();
    return id ? coverArtUrl(id, 128) : null;
  });
}
