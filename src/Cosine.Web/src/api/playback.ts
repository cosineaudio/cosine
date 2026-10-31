import { client } from './client';
import type { Track } from './types';

/**
 * Remote playback. These are plain JSON endpoints under
 * `/playback`, not Subsonic - no envelope to unwrap, and no `f=json` to append.
 *
 * Every transport command answers with the session's state *after* the command, which is what
 * makes the optimistic UI in `stores/remotePlayer.ts` safe: the response is authoritative and
 * newer than any hub message that was already in flight when the command was sent.
 */

/** Mirrors `Cosine.Core.Entities.PlaybackSessionState`, which serialises by name. */
export type PlaybackSessionState =
  | 'Idle'
  | 'Playing'
  | 'Paused'
  | 'Stopped'
  /** Someone else took the device - its own UI, another app, another cosine session. */
  | 'Hijacked'
  /** The device stopped answering. The session is kept so it can resume. */
  | 'Offline';

/** Mirrors `Cosine.Core.Entities.PlaybackRepeatMode`. */
export type PlaybackRepeatMode = 'Off' | 'All' | 'One';

export interface PlaybackDevice {
  id: string;
  driverId: string;
  deviceKey: string;
  /** What to show: the admin's chosen name where there is one. */
  displayName: string;
  /** What the device calls itself, so the admin page can show what an override replaced. */
  advertisedName: string;
  /** Seen by discovery recently. Offline devices are still listed, greyed out. */
  online: boolean;
  address: string | null;
  transcodeProfile: string | null;
  maxVolumePercent: number;
  /**
   * Milliseconds to shift synced lyrics by while this device is the output. Positive shows them
   * later, matching a speaker that buffers before it makes a sound. Display only.
   */
  lyricOffsetMs: number;
  enabled: boolean;
  lastSeenAt: string | null;
  /** Non-null when the device is busy. */
  activeSessionId: string | null;
  activeSessionOwner: string | null;
}

export interface PlaybackStateSnapshot {
  sessionId: string;
  deviceId: string;
  deviceName: string;
  state: PlaybackSessionState;
  queueIndex: number;
  currentTrackId: string | null;
  positionMs: number;
  durationMs: number | null;
  volumePercent: number;
  repeatMode: PlaybackRepeatMode;
  shuffle: boolean;
  /** Bumped on every queue mutation; a client behind this knows a queue event is coming. */
  queueVersion: number;
  ownerUserId: string;
  ownerUsername: string;
  /** When the position was sampled, so the client interpolates from a known instant. */
  serverTimestamp: string;
  /**
   * Whether the session tops its own queue up as it runs down. The session owns this, not the
   * browser: a speaker is expected to keep playing after the tab that started it is closed.
   */
  autoPlay: boolean;
}

export interface PlaybackQueueItem {
  position: number;
  trackId: string;
  title: string;
  /** The whole credit as one string - "Pouya & Fat Nick". */
  artist: string;
  /** The primary artist. `artists` is what makes each name individually clickable. */
  artistId: string;
  /** Every credited artist, in credit order. Absent for a track with no stored credit. */
  artists?: { id: string; name: string }[];
  album: string;
  albumId: string;
  durationMs: number;
  suffix: string | null;
  bitRate: number | null;
  /** True for an entry the session queued itself, so the queue panel can mark it. */
  autoPlay: boolean;
}

export interface PlaybackQueueSnapshot {
  sessionId: string;
  queueVersion: number;
  items: PlaybackQueueItem[];
}

/**
 * A server queue entry as the rest of the UI expects to see it. The components render `Track`
 * and nothing else, so the remote queue becomes one here rather than every component learning a
 * second shape. Cover art is fetched by album id, matching how `/rest` maps a song's `coverArt`.
 */
export function queueItemToTrack(item: PlaybackQueueItem): Track {
  return {
    id: item.trackId,
    isDir: false,
    title: item.title,
    album: item.album,
    albumId: item.albumId,
    artist: item.artist,
    artistId: item.artistId,
    artists: item.artists,
    // The server already joined the credit into `artist`; naming it here too keeps the remote
    // queue and a `/rest` song identical to everything that renders an artist line.
    displayArtist: item.artist,
    coverArt: item.albumId,
    duration: Math.round(item.durationMs / 1000),
    suffix: item.suffix ?? undefined,
    bitRate: item.bitRate ?? undefined,
  };
}

// ---- Devices ----

export function getPlaybackDevices(): Promise<PlaybackDevice[]> {
  return client.get<PlaybackDevice[]>('playback/devices');
}

// ---- Session lifecycle ----

/** @param takeOver Ends another user's session and claims the device. Admins only. */
export function startSession(deviceId: string, takeOver = false): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>('playback/sessions', { deviceId, takeOver });
}

export function getSession(id: string): Promise<PlaybackStateSnapshot> {
  return client.get<PlaybackStateSnapshot>(`playback/sessions/${id}`);
}

export function getSessionQueue(id: string): Promise<PlaybackQueueSnapshot> {
  return client.get<PlaybackQueueSnapshot>(`playback/sessions/${id}/queue`);
}

export function endSession(id: string): Promise<void> {
  return client.delete<void>(`playback/sessions/${id}`);
}

// ---- Queue ----

export function setQueue(
  id: string, trackIds: string[], startIndex = 0,
): Promise<PlaybackStateSnapshot> {
  return client.put<PlaybackStateSnapshot>(`playback/sessions/${id}/queue`, { trackIds, startIndex });
}

/** @param insertAt Null (or omitted) appends; otherwise inserts before that position. */
export function addToQueue(
  id: string, trackIds: string[], insertAt: number | null = null,
): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/queue`, { trackIds, insertAt });
}

export function removeFromQueue(id: string, position: number): Promise<PlaybackStateSnapshot> {
  return client.delete<PlaybackStateSnapshot>(`playback/sessions/${id}/queue/${position}`);
}

export function moveInQueue(id: string, from: number, to: number): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/queue/move`, { from, to });
}

export function playAt(id: string, position: number): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/queue/${position}/play`);
}

// ---- Transport ----

export function play(id: string): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/play`);
}

export function pause(id: string): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/pause`);
}

export function stop(id: string): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/stop`);
}

export function next(id: string): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/next`);
}

export function previous(id: string): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/previous`);
}

export function seek(id: string, positionSeconds: number): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/seek`, { positionSeconds });
}

export function setVolume(id: string, percent: number): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/volume`, { percent });
}

export function setRepeat(id: string, mode: PlaybackRepeatMode): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/repeat`, { mode });
}

export function setShuffle(id: string, shuffled: boolean): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/shuffle`, { shuffled });
}

/**
 * Keep the session's queue going once it runs down, seeded from what it has played. Enabling it
 * on an already-short queue fills it immediately, so the response's queue version has usually
 * moved by the time this returns.
 */
export function setAutoPlay(id: string, enabled: boolean): Promise<PlaybackStateSnapshot> {
  return client.post<PlaybackStateSnapshot>(`playback/sessions/${id}/autoplay`, { enabled });
}
