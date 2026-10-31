import { client } from './client';
import { dtoToTrack } from './adapt';
import type { Track } from './types';
import type { TrackDto } from './model';

export interface PlayQueueState {
  /** The saved tracks, in order. */
  entry: Track[];
  /** Id of the track that was current. */
  current?: string;
  /** Playback position within the current track, in milliseconds. */
  position?: number;
}

/**
 * Restore the cross-device play queue. Returns null when the server has no saved queue -
 * the endpoint answers 204 and the client maps an empty body to undefined.
 */
export async function getPlayQueue(): Promise<PlayQueueState | null> {
  const data = await client.get<
    { tracks: TrackDto[]; currentTrackId?: string; positionMs?: number } | undefined
  >('api/queue');
  if (!data) return null;
  return {
    entry: data.tracks.map(dtoToTrack),
    current: data.currentTrackId,
    position: data.positionMs,
  };
}

export interface SavePlayQueueOptions {
  /** Track ids in queue order. */
  ids: string[];
  /** Id of the currently-playing track. */
  current?: string;
  /** Position within the current track, in milliseconds. */
  position?: number;
}

/** Persist the play queue for cross-device continuity. An empty list clears it. */
export async function savePlayQueue(opts: SavePlayQueueOptions): Promise<void> {
  await client.put('api/queue', {
    trackIds: opts.ids,
    currentTrackId: opts.current,
    positionMs: opts.position != null ? Math.max(0, Math.round(opts.position)) : undefined,
  });
}
