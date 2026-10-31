import { defineStore } from 'pinia';
import { ref, computed, watch } from 'vue';
import type * as signalR from '@microsoft/signalr';
import { toast } from 'vue-sonner';
import type { Track } from '@/api/types';
import {
  createParty, endParty, getMyParty, replacePartyQueue,
  type PartyInfo, type PartyMember, type PartyQueueItem,
} from '@/api/party';
import { ApiError } from '@/api/client';
import { usePlaybackStore } from '@/stores/playback';

/**
 * The owner's half of listen-together. The owner's player stays the source of truth and this
 * store is a relay around it: it publishes playback state to the party hub about once a second,
 * mirrors the local queue to the server whenever it changes, and folds guest-added tracks back
 * into the local queue when the server announces them.
 *
 * It reads through `usePlaybackStore` (the output selector), so hosting keeps working when
 * playback moves to a remote speaker - guests follow whatever the owner hears.
 */

/** How often the owner's browser relays position while playing. Matches the hub's ≤1 Hz design. */
const PUBLISH_INTERVAL_MS = 1000;

/** Debounce for mirroring queue edits out - a drag-reorder fires many changes in a burst. */
const QUEUE_SYNC_DEBOUNCE_MS = 800;

/**
 * Safety-net cadence for the queue mirror. The mirror is event-driven and its PUT fails
 * silently, so without this a lost request (or a missed change) leaves the server copy stale
 * until the host happens to edit again - possibly never. Every tick compares the current id
 * sequence against what the server last accepted and re-mirrors only on a mismatch, so the
 * steady-state cost is one string compare.
 */
const QUEUE_RESYNC_INTERVAL_MS = 10_000;

export const usePartyStore = defineStore('party', () => {
  const playback = usePlaybackStore();

  const party = ref<PartyInfo | null>(null);
  const members = ref<PartyMember[]>([]);
  const queueItems = ref<PartyQueueItem[]>([]);
  const starting = ref(false);

  const isHosting = computed(() => party.value !== null);
  const shareLink = computed(() =>
    party.value?.token ? `${location.origin}/party/${party.value.token}` : null);

  /** Who added which track, for the queue panel's attribution badges. */
  const addedByTrackId = computed(() => {
    const map = new Map<string, string>();
    for (const item of queueItems.value) {
      if (item.addedBy) map.set(item.trackId, item.addedBy);
    }
    return map;
  });

  let hub: signalR.HubConnection | null = null;
  let publishTimer: ReturnType<typeof setInterval> | null = null;
  let queueSyncTimer: ReturnType<typeof setTimeout> | null = null;
  let resyncTimer: ReturnType<typeof setInterval> | null = null;
  let stopWatchers: Array<() => void> = [];
  let lastPublished = '';
  /** The id sequence the server last accepted, for the resync timer's convergence check. */
  let lastMirrored = '';
  /** Guest entries already folded into the local player - a fold must happen exactly once. */
  let seenGuestEntries = new Set<string>();

  /** Reattach to a party this user was already hosting - a page reload must not end it. */
  async function init(): Promise<void> {
    if (party.value) return;
    try {
      const existing = await getMyParty();
      if (existing) await activate(existing);
    } catch { /* not hosting, or offline - either way there is nothing to restore */ }
  }

  async function start(): Promise<void> {
    if (party.value || starting.value) return;
    starting.value = true;
    try {
      const created = await createParty(null, playback.queue.map((t) => t.id));
      await activate(created);
    } finally {
      starting.value = false;
    }
  }

  async function end(): Promise<void> {
    const current = party.value;
    if (!current) return;
    try {
      await endParty(current.id);
    } catch (e) {
      // Already dead server-side (idle sweep, another tab) still counts as ended here. Any
      // other failure must keep the hosting state: deactivating anyway leaves a live party
      // behind that the next "start" hands back - old guests and all - as if it were new.
      if (!(e instanceof ApiError) || e.status !== 404) throw e;
    }
    await deactivate();
  }

  async function activate(info: PartyInfo): Promise<void> {
    party.value = info;
    members.value = info.members;

    const { createPartyHubConnection } = await import('@/composables/useSignalR');
    const connection = createPartyHubConnection();
    hub = connection;

    connection.on('PartyQueue', (items: PartyQueueItem[]) => {
      if (Array.isArray(items)) reconcileQueue(items);
    });

    connection.on('PartyMembers', (list: PartyMember[]) => {
      if (!Array.isArray(list)) return;
      // Announce arrivals: the owner is DJing, not watching the member list.
      if (list.length > members.value.length) {
        const known = new Set(members.value.map((m) => m.id));
        for (const m of list) {
          if (!known.has(m.id) && !m.isOwner) toast(`${m.displayName} joined the party`);
        }
      }
      members.value = list;
    });

    connection.on('PartyEnded', () => {
      toast('The listening party has ended');
      void deactivate();
    });

    connection.onreconnected(async () => {
      const current = party.value;
      if (!current || !hub) return;
      await hub.invoke('Subscribe', current.id, null);
      // Guests may have queued tracks while this tab was away; owner edits also went unsent.
      // Re-publishing the local queue converges both (attribution survives server-side).
      scheduleQueueSync();
    });

    await connection.start();
    await connection.invoke('Subscribe', info.id, null);

    startPublishing(info.id);
    // Mirror queue edits out. Watching the id sequence rather than the array keeps replace
    // cheap: reorders and edits change the string, per-second position updates do not.
    stopWatchers.push(watch(() => partyTrackIds().join('|'), scheduleQueueSync));
    // And mirror once now: reattaching (reload, second tab, another device) resumes a party
    // whose server queue was seeded by whichever tab came before. If this tab then only plays
    // *through* its queue - next() moves the index, not the ids - the watcher never fires, the
    // server queue stays frozen, and guests 404 on every track outside it.
    scheduleQueueSync();
    resyncTimer = setInterval(() => {
      if (partyTrackIds().join('|') !== lastMirrored) scheduleQueueSync();
    }, QUEUE_RESYNC_INTERVAL_MS);
  }

  async function deactivate(): Promise<void> {
    if (publishTimer) { clearInterval(publishTimer); publishTimer = null; }
    if (queueSyncTimer) { clearTimeout(queueSyncTimer); queueSyncTimer = null; }
    if (resyncTimer) { clearInterval(resyncTimer); resyncTimer = null; }
    for (const stop of stopWatchers) stop();
    stopWatchers = [];
    lastPublished = '';
    lastMirrored = '';
    seenGuestEntries = new Set();
    party.value = null;
    members.value = [];
    queueItems.value = [];

    const connection = hub;
    hub = null;
    if (!connection) return;
    try { await connection.stop(); } catch { /* already gone */ }
  }

  /**
   * The ≤1 Hz state relay. Publishes every tick while playing (guests drift-correct against
   * it), and on change while paused (a pause or a track swap must still reach them).
   */
  function startPublishing(partyId: string): void {
    publishTimer = setInterval(() => {
      const connection = hub;
      if (!connection || connection.state !== 'Connected') return;

      const trackId = playback.currentTrackId;
      const positionMs = Math.round(playback.currentTime * 1000);
      const isPlaying = playback.isPlaying;

      const fingerprint = `${trackId}|${isPlaying}`;
      if (!isPlaying && fingerprint === lastPublished) return;
      lastPublished = fingerprint;

      connection.invoke('PublishState', partyId, trackId, positionMs, isPlaying)
        .catch(() => { /* transient; the next tick retries */ });
    }, PUBLISH_INTERVAL_MS);
  }

  /**
   * What the party's queue should hold: the local queue, plus the playing track when it sits
   * outside it (played directly without replacing the queue) - the server scopes guest audio,
   * artwork and lyrics to exactly this list, so a track missing from it 404s for every guest.
   * Internet radio has no track row behind it and is left out.
   */
  function partyTrackIds(): string[] {
    const ids = playback.queue.map((t) => t.id);
    const current = playback.currentTrack;
    if (current && !current.streamUrl && !ids.includes(current.id)) ids.unshift(current.id);
    return ids;
  }

  function scheduleQueueSync(): void {
    if (queueSyncTimer) clearTimeout(queueSyncTimer);
    queueSyncTimer = setTimeout(() => {
      const current = party.value;
      if (!current) return;
      const ids = partyTrackIds();
      replacePartyQueue(current.id, ids)
        .then(() => { lastMirrored = ids.join('|'); })
        .catch(() => { /* the resync timer retries */ });
    }, QUEUE_SYNC_DEBOUNCE_MS);
  }

  /**
   * A queue broadcast arrived. Fold guest-added entries (the only rows carrying a member id -
   * the host's own tracks got here through the mirror) into the local player so they actually
   * get played: each entry exactly once, by entry id, whether or not the rest of the broadcast
   * matches the local queue. The old whole-queue prefix comparison failed whenever the mirror
   * was mid-debounce or stale, and an add missed that way was then erased by the next replace.
   */
  function reconcileQueue(items: PartyQueueItem[]): void {
    queueItems.value = items;

    const localIds = new Set(partyTrackIds());
    const added = items.filter((item) =>
      item.addedByMemberId
      && !seenGuestEntries.has(item.entryId)
      && !localIds.has(item.trackId));
    for (const item of items) {
      if (item.addedByMemberId) seenGuestEntries.add(item.entryId);
    }
    if (added.length === 0) return;

    playback.addToQueue(added.map(itemToTrack), 'end');
    for (const item of added) {
      if (item.addedBy) toast(`${item.addedBy} added "${item.title}"`);
    }
  }

  /**
   * A queue broadcast carries everything the player needs - building the Track here saves a
   * per-track fetch. `coverArt` is the album id, which is exactly what `/api/artwork/{id}`
   * takes (see TrackDto.ArtworkId).
   */
  function itemToTrack(item: PartyQueueItem): Track {
    return {
      id: item.trackId,
      isDir: false,
      title: item.title,
      artist: item.artist,
      album: item.album,
      albumId: item.albumId,
      coverArt: item.albumId,
      duration: item.durationMs != null ? Math.round(item.durationMs / 1000) : undefined,
    };
  }

  return {
    party,
    members,
    queueItems,
    starting,
    isHosting,
    shareLink,
    addedByTrackId,
    init,
    start,
    end,
  };
});
