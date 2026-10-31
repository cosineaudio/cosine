import type { Track } from '@/api/types';
import type { RepeatMode } from '@/stores/player';

/**
 * What an output - the browser, or a speaker on the LAN - has to be able to do. This is the
 * client-side counterpart to `IPlaybackTarget` on the server (plan §4.2/§4.7): the same idea on
 * both sides of the wire, a declared contract rather than two stores that happen to agree.
 *
 * It is exhaustive over the surface the UI actually consumes, so a member that drifts or goes
 * missing is a compile error at the store rather than a component silently reading `undefined`.
 * Both stores assert conformance at the bottom of their own file.
 *
 * Two things deliberately live outside it, on the facade instead:
 *
 * - `ensureAnalyser` - the visualizer taps the *local* audio element. There is nothing to tap
 *   when a speaker is playing, which is why `canAnalyse` exists.
 * - `initPersistence` - the browser's own cross-device queue persistence. It runs for the life
 *   of the app whichever output is selected, so it is not an output's business.
 */
export interface PlaybackOutput {
  // ---- State ----
  readonly currentTrack: Track | null;
  readonly hasTrack: boolean;
  readonly currentTrackId: string | null;
  readonly queue: Track[];
  readonly queueIndex: number;
  readonly isPlaying: boolean;
  /** Seconds into the current track. */
  readonly currentTime: number;
  /** Seconds. */
  readonly duration: number;
  /** Fraction (0–1) of the track already buffered, for the scrubber's buffered range. */
  readonly bufferedProgress: number;
  /**
   * The transport has been asked for something and it has not taken effect yet.
   *
   * The two outputs get here differently - the browser stalls waiting on audio
   * data, a speaker is still round-tripping the command - but to a listener
   * they are one state: something was pressed and no sound has changed. The UI
   * treats them identically, so the contract carries one member rather than
   * making every transport control know which output it is driving.
   */
  readonly isBusy: boolean;
  /** 0–1. */
  readonly volume: number;
  readonly isShuffled: boolean;
  readonly repeatMode: RepeatMode;
  readonly preferredFormat: string;
  readonly preferredBitRate: number;
  readonly isTranscodingEnabled: boolean;

  // ---- Commands ----
  play(track: Track, newQueue?: Track[]): void;
  pause(): void;
  resume(): void;
  togglePlay(): void;
  next(): void;
  previous(): void;
  seek(seconds: number): void;
  setVolume(level: number): void;
  toggleMute(): void;
  addToQueue(tracks: Track[], position?: 'next' | 'end'): void;
  removeFromQueue(index: number): void;
  moveInQueue(from: number, to: number): void;
  playAt(index: number): void;
  clearQueue(): void;
  toggleShuffle(): void;
  cycleRepeatMode(): void;
  setFormatPreference(format: string, bitRate: number): void;
}
