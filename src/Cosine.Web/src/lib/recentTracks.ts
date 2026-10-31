import type { Track } from '@/api/types';

/**
 * A small, local "recently played" log. The backend exposes no per-track play
 * history endpoint (only `scrobble`, which records counts), so we keep a capped
 * client-side list written when a track starts. Deliberately not tied to the
 * scrobble threshold: this is "what did I put on", not "what counted as a play".
 */
const KEY = 'cosine_recent_tracks';
const MAX = 100;

export function getRecentTracks(): Track[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as Track[]) : [];
  } catch {
    return [];
  }
}

/** Record a track as most-recently played, moving duplicates to the front. */
export function pushRecentTrack(track: Track): void {
  try {
    const list = getRecentTracks().filter((t) => t.id !== track.id);
    list.unshift(track);
    localStorage.setItem(KEY, JSON.stringify(list.slice(0, MAX)));
  } catch {
    /* storage unavailable - non-fatal */
  }
}
