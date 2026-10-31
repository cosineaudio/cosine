/** m:ss for a track/scrubber time. */
export function formatDuration(seconds: number | undefined | null): string {
  if (!seconds || !isFinite(seconds) || seconds < 0) return '0:00';
  const total = Math.floor(seconds);
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${s.toString().padStart(2, '0')}`;
}

/** Human total runtime, e.g. "1 hr 12 min" or "47 min". */
export function formatTotalTime(seconds: number | undefined | null): string {
  if (!seconds || seconds <= 0) return '0 min';
  const mins = Math.round(seconds / 60);
  if (mins < 60) return `${mins} min`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  return m === 0 ? `${h} hr` : `${h} hr ${m} min`;
}

/** Pluralize a count with its noun, e.g. countLabel(1, 'album') => "1 album". */
export function countLabel(n: number, noun: string): string {
  return `${n.toLocaleString()} ${noun}${n === 1 ? '' : 's'}`;
}
