import { defineStore } from 'pinia';
import { ref, computed, watch } from 'vue';
import type { Track } from '@/api/types';
import { getArtistSongs, getAutoPlayQueue } from '@/api/library';
import { getSongsByGenre } from '@/api/lists';
import { getRecentTracks } from '@/lib/recentTracks';
import { shuffled } from '@/lib/shuffle';
import { usePlayerStore } from '@/stores/player';
import { useRemotePlayerStore } from '@/stores/remotePlayer';

/**
 * Where a queue came from, when that has more to give than a blend does.
 *
 * A page shows a bounded slice of something larger - the genre page loads 200 songs of a genre
 * that may have thousands; an artist's top songs are ten of a catalogue - so running off the end
 * of what was loaded is not the same thing as running out of music.
 *
 * The two kinds are read differently because the lists are. A genre is paged from the server, and
 * `offset` is how far into it the queue already reaches. A catalogue arrives whole in one request
 * and is walked by taking whatever is not queued yet, which is what an artist's top ten needs:
 * those ten are ranked by play count, so they are not a prefix of anything.
 */
export type QueueSource =
  | {
    kind: 'genre';
    name: string;
    offset: number;
    /** The list was shuffled on the way in, so a page read in library order would read oddly. */
    shuffle?: boolean;
  }
  | { kind: 'artist'; id: string; shuffle?: boolean };

/**
 * Endless play for the browser's own output: as the queue runs down it fetches more, seeded from
 * what has just been played.
 *
 * It sits *above* `player.ts` rather than inside it, and drives it through the same `addToQueue`
 * and `removeFromQueue` any component would call. The queue model does not need to know where its
 * entries came from, and `player.ts` is the one file remote playback was careful not to touch.
 *
 * A speaker's queue is not topped up from here. The server does that for a session
 * (`PlaybackSessionManager.MaybeAutoPlayAsync`), which is the whole reason it lives there: it has
 * to keep going with no tab open. This store stands down while a speaker is the output, and the
 * facade in `stores/playback.ts` routes the toggle to whichever of the two owns it.
 */

const ENABLED_KEY = 'cosine_autoplay';

/**
 * How few tracks may be left ahead of the playing one before more are fetched. Topping up early
 * rather than at the end is what keeps the join silent: the request, the decode and `player.ts`'s
 * own 15-second preload all have to happen before the current track runs out.
 */
const REFILL_THRESHOLD = 3;

/** How many tracks one top-up asks for. */
const BATCH_SIZE = 20;

/** How many recently played tracks are offered as seeds. Their artists are what actually seed it. */
const SEED_COUNT = 8;

/**
 * How many played entries stay in the queue behind the current one. An endless queue would
 * otherwise grow for as long as it plays, and every entry is a line in the queue panel and an id
 * in the play-queue save. The cost is that "previous" cannot walk back forever.
 */
const HISTORY_LIMIT = 50;

/** Seconds from the end within which a paused player counts as having run out rather than been paused. */
const ENDED_TOLERANCE = 1.5;

export const useAutoPlayStore = defineStore('autoPlay', () => {
  const player = usePlayerStore();
  const remote = useRemotePlayerStore();

  const enabled = ref<boolean>(loadEnabled());
  /** Set while a batch is in flight, so a queue that changes underneath it asks only once. */
  const loading = ref(false);
  /** Non-null when the last attempt failed or came back with nothing, for the UI to explain. */
  const error = ref<string | null>(null);

  /**
   * Ids this store appended, so the queue panel can show where the listener's own queue ended.
   * Trimmed alongside the queue - an id that has fallen off the front is not coming back.
   */
  const addedIds = ref<Set<string>>(new Set());

  /**
   * What the current queue was built from, set by the facade whenever a new queue is played.
   * Cleared once that source has nothing left, which is what hands the queue over to the blend.
   */
  const source = ref<QueueSource | null>(null);

  /**
   * The track a batch was last fetched for, so one track gets at most one fetch. Without it a
   * batch smaller than the threshold satisfies nothing: appending leaves the queue still short,
   * the watcher fires again, and a sparse library turns into a request loop.
   */
  let lastAttemptTrackId: string | null = null;

  /** The artist catalogue currently being read through, held between batches. */
  let catalogue: { artistId: string; tracks: Track[] } | null = null;

  function loadEnabled(): boolean {
    try {
      return localStorage.getItem(ENABLED_KEY) === 'true';
    } catch {
      return false;
    }
  }

  watch(enabled, (on) => {
    try {
      localStorage.setItem(ENABLED_KEY, String(on));
    } catch { /* storage unavailable - the toggle still works for this session */ }
  });

  function setEnabled(on: boolean): void {
    enabled.value = on;
    error.value = null;
    if (on) void maybeTopUp();
  }

  function toggle(): void {
    setEnabled(!enabled.value);
  }

  function isAutoPlayTrack(trackId: string): boolean {
    return addedIds.value.has(trackId);
  }

  /** Record where a newly played queue came from. Pass null for one with nothing behind it. */
  function setQueueSource(next: QueueSource | null): void {
    source.value = next ? { ...next } : null;
    // A different queue is a different catalogue, and the old one is dead weight either way.
    if (next?.kind !== 'artist' || next.id !== catalogue?.artistId) catalogue = null;
  }

  /** Tracks left after the one playing. */
  const remaining = computed(() => Math.max(0, player.queue.length - player.queueIndex - 1));

  /**
   * The queue ran out and playback stopped on the last track, rather than being paused partway
   * through one. Enabling auto-play at that point should start it again, not silently queue
   * twenty tracks behind a stopped player.
   */
  function stoppedAtTheEnd(): boolean {
    if (player.isPlaying || player.queue.length === 0) return false;
    if (player.queueIndex !== player.queue.length - 1) return false;
    const total = player.duration;
    return total > 0 && player.currentTime >= total - ENDED_TOLERANCE;
  }

  /**
   * The local output is the one being driven, and this queue is one that can be extended.
   * Internet radio is neither: it is a single endless stream with no track rows behind it.
   */
  function shouldTopUp(): boolean {
    if (!enabled.value || loading.value) return false;
    if (remote.isActive) return false;
    if (player.queue.length === 0) return false;
    // Repeat already means the queue never ends; there is nothing here to add to it.
    if (player.repeatMode !== 'off') return false;
    if (player.currentTrack?.streamUrl) return false;
    if (player.currentTrackId !== null && player.currentTrackId === lastAttemptTrackId) return false;
    return remaining.value <= REFILL_THRESHOLD;
  }

  async function maybeTopUp(): Promise<void> {
    if (!shouldTopUp()) return;

    lastAttemptTrackId = player.currentTrackId;
    loading.value = true;
    const resume = stoppedAtTheEnd();
    try {
      const tracks = await fetchBatch();
      if (tracks.length === 0) {
        error.value = 'Auto-play could not find anything else to play';
        return;
      }

      error.value = null;
      for (const track of tracks) addedIds.value.add(track.id);
      // Appended, never inserted: the radio goes behind anything queued by hand.
      player.addToQueue(tracks, 'end');

      // The queue had already run out when auto-play was switched on, so nothing else is going
      // to start these.
      if (resume) player.playAt(player.queueIndex + 1);

      trimHistory();
    } catch {
      error.value = 'Auto-play could not reach the server';
    } finally {
      loading.value = false;
    }
  }

  /**
   * More of what the listener actually chose, before anything is blended in. Finishing the 200
   * songs the genre page loaded should carry on through the genre, and the last of an artist's
   * top ten into the rest of that artist, rather than jumping to whatever the similarity index
   * makes of the last few tracks.
   */
  async function fetchBatch(): Promise<Track[]> {
    if (source.value) {
      const more = await continueSource();
      if (more.length > 0) return more;
    }
    return fetchBlend();
  }

  /**
   * The next batch from whatever the queue was built from, or nothing once that has run out. The
   * source is dropped at that point, so the queue falls through to the blend from then on and
   * endless play stays endless.
   */
  async function continueSource(): Promise<Track[]> {
    const src = source.value;
    if (!src) return [];
    const batch = src.kind === 'genre' ? await continueGenre(src) : await continueArtist(src);
    return src.shuffle ? shuffled(batch) : batch;
  }

  async function continueGenre(src: Extract<QueueSource, { kind: 'genre' }>): Promise<Track[]> {
    const page = await getSongsByGenre(src.name, { count: BATCH_SIZE, offset: src.offset });
    // A short page is the end of the genre. A full one advances the cursor by what it held, so
    // the next top-up carries on rather than re-reading the same rows.
    source.value = page.length < BATCH_SIZE ? null : { ...src, offset: src.offset + page.length };

    // A page can overlap the queue - the listener may have queued some of it by hand, and the
    // library shifts under a paged read. Duplicates are dropped rather than played twice; if that
    // empties the page, this top-up falls through to the blend and the next one reads on.
    return notAlreadyHeard(page);
  }

  /**
   * The rest of an artist's catalogue, in album order, a batch at a time. There is no cursor to
   * keep: what has not been queued or just played *is* the remainder, and each batch joins the
   * queue, so the next call reads on from there. Top songs need it that way - they are ranked by
   * play count, so they are ten tracks from anywhere in the catalogue rather than the first ten.
   */
  async function continueArtist(src: Extract<QueueSource, { kind: 'artist' }>): Promise<Track[]> {
    const remaining = notAlreadyHeard(await catalogueOf(src.id));
    // Everything left fits in this batch, so there will be nothing to come back for.
    if (remaining.length <= BATCH_SIZE) source.value = null;
    return remaining.slice(0, BATCH_SIZE);
  }

  /**
   * One artist's catalogue arrives whole (`getArtistSongs` is a single request), and is held for
   * as long as the queue is being fed from it rather than re-fetched every batch. A scan that
   * adds to the artist mid-listen is not picked up until the next queue - worth a stale batch,
   * against re-reading a thousand-track catalogue every twenty songs.
   */
  async function catalogueOf(artistId: string): Promise<Track[]> {
    if (catalogue?.artistId !== artistId) {
      catalogue = { artistId, tracks: await getArtistSongs(artistId) };
    }
    return catalogue.tracks;
  }

  /** Drop what is already in the queue or has just played, so nothing comes round twice. */
  function notAlreadyHeard(tracks: Track[]): Track[] {
    const known = new Set([
      ...player.queue.map((t) => t.id),
      ...getRecentTracks().map((t) => t.id),
    ]);
    return tracks.filter((t) => !known.has(t.id));
  }

  /**
   * Seeds are what was actually played, most recent first - not queue order, which says nothing
   * about what was heard once shuffle is on. Everything queued or recently heard is excluded, so
   * a batch cannot hand back the track from twenty minutes ago.
   */
  async function fetchBlend(): Promise<Track[]> {
    const recent = getRecentTracks().map((t) => t.id);
    const current = player.currentTrack?.id;
    const seeds = [...new Set([...(current ? [current] : []), ...recent])].slice(0, SEED_COUNT);
    if (seeds.length === 0) return [];

    const exclude = [...new Set([...player.queue.map((t) => t.id), ...recent])];

    return getAutoPlayQueue({ seedTrackIds: seeds, excludeTrackIds: exclude, count: BATCH_SIZE });
  }

  /**
   * Drop played entries off the front. `removeFromQueue` already moves `queueIndex` with them, so
   * this is the queue panel's own delete applied to the oldest row over and over rather than a
   * second path into the queue that could disagree with the first.
   */
  function trimHistory(): void {
    while (player.queueIndex > HISTORY_LIMIT) {
      const dropped = player.queue[0];
      player.removeFromQueue(0);
      if (dropped) addedIds.value.delete(dropped.id);
    }
  }

  // The queue advancing is what drives this: every track change re-asks whether the end is close
  // enough to fetch more. `enabled` is watched through setEnabled instead, so switching it on
  // with a queue that has already run out can also restart playback.
  watch(
    () => [player.queueIndex, player.queue.length, player.repeatMode] as const,
    () => { void maybeTopUp(); },
  );

  // Reaching the end of a queue that auto-play was switched on for mid-track: `player.next()`
  // pauses rather than advancing, and no index has changed for the watcher above to see.
  watch(
    () => player.isPlaying,
    (playing) => { if (!playing) void maybeTopUp(); },
  );

  return {
    enabled,
    loading,
    error,
    setEnabled,
    toggle,
    isAutoPlayTrack,
    setQueueSource,
    /** Exposed for tests and for the queue panel to explain what it is still working through. */
    queueSource: source,
    /** Exposed for the facade, which hands a speaker's queue over with the setting intact. */
    maybeTopUp,
  };
});
