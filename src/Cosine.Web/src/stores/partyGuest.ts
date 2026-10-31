import { defineStore } from 'pinia';
import { ref, computed } from 'vue';
import type * as signalR from '@microsoft/signalr';
import {
  joinParty, getPartyPreview, getPartySnapshot, addPartyTrack, searchPartyTracks, partyStreamUrl,
  type PartyInfo, type PartyMember, type PartyPreview, type PartyQueueItem, type PartyState,
  type PartyStateTrack, type PartySearchTrack,
} from '@/api/party';
import { authApi } from '@/api/auth';

/**
 * A guest's whole session: join through the share link, mirror the party over its hub, and
 * play the same audio the owner hears, loosely synced to the state the owner's browser relays.
 *
 * Loose sync is the design: states arrive about once a second and the guest seeks only when
 * drifted well past that, because correcting every tick would turn playback into stutter.
 * The audio element only ever starts from a user gesture ("Listen along") - browsers block
 * anything else - and the guest can stop listening without leaving the party.
 */

/** How far (seconds) local playback may drift from the owner before a corrective seek. */
const DRIFT_TOLERANCE_S = 3;

/** sessionStorage key for the ticket, so a reload rejoins without minting a new member. */
const storageKey = (token: string) => `cosine_party_${token}`;

type Phase = 'loading' | 'invite' | 'active' | 'ended';

interface StoredMembership {
  ticket: string;
  partyId: string;
}

export const usePartyGuestStore = defineStore('partyGuest', () => {
  // 'loading' until init resolves who is calling and whether they are already a member -
  // flashing the invitation at someone who is mid-party reads as a broken page.
  const phase = ref<Phase>('loading');
  const joining = ref(false);
  const joinError = ref<string | null>(null);

  /** The invitation, before any commitment - what the join dialog renders. */
  const preview = ref<PartyPreview | null>(null);
  /** Signed-in users join as their account: no name field, cookie instead of ticket. */
  const isSignedIn = ref(false);

  const party = ref<PartyInfo | null>(null);
  const members = ref<PartyMember[]>([]);
  const queue = ref<PartyQueueItem[]>([]);
  const state = ref<PartyState | null>(null);

  /** True once the guest has tapped "Listen along" and audio is following the party. */
  const listening = ref(false);
  const volume = ref(1);

  let ticket = '';
  let partyId = '';
  let hub: signalR.HubConnection | null = null;
  let audio: HTMLAudioElement | null = null;
  /** Which track the audio element currently has loaded, to tell a seek from a track change. */
  let loadedTrackId: string | null = null;
  /** Failed loads for the loaded track, so a broken load is retried but not hammered forever. */
  let loadRetries = 0;
  let audioCtx: AudioContext | null = null;
  let analyserNode: AnalyserNode | null = null;
  let gainNode: GainNode | null = null;

  /**
   * What to render as now playing. The queue entry when the track is in the queue (it carries
   * attribution and position), else the metadata the state itself brought - the state relay
   * outruns the owner's queue mirror, and the card must not go blank in the gap.
   */
  const currentItem = computed<PartyQueueItem | null>(() => {
    const s = state.value;
    if (!s?.trackId) return null;
    const queued = queue.value.find((i) => i.trackId === s.trackId);
    if (queued) return queued;
    if (!s.track || s.track.trackId !== s.trackId) return null;
    return {
      entryId: `state:${s.track.trackId}`,
      position: -1,
      trackId: s.track.trackId,
      title: s.track.title,
      artist: s.track.artist,
      album: s.track.album,
      albumId: s.track.albumId,
      durationMs: s.track.durationMs,
    };
  });

  const guestTicket = computed(() => ticket);
  const partyIdRef = computed(() => partyId);

  /**
   * Entry point from the page. Everyone lands on the invitation ("there's a party going on -
   * join?"), except returning members, whose stored membership resumes straight into the
   * party: a reload must not re-ask a question that was already answered. Signed-in users
   * store an empty ticket (their cookie is the credential); anonymous guests store the minted
   * one, so a reload never creates "Guest (2)".
   */
  async function init(token: string): Promise<void> {
    try {
      await authApi.me();
      isSignedIn.value = true;
    } catch {
      isSignedIn.value = false;
    }

    // Resume an existing membership. An authed marker (empty ticket) is only good while the
    // session that earned it still exists.
    const stored = readStored(token);
    if (stored && (stored.ticket || isSignedIn.value)) {
      try {
        ticket = stored.ticket;
        partyId = stored.partyId;
        const snapshot = await getPartySnapshot(partyId, ticket);
        applySnapshot(snapshot.party, snapshot.queue, snapshot.state ?? null);
        await connectHub();
        phase.value = 'active';
        return;
      } catch {
        // Expired ticket or ended party - both are a fresh invitation from this side.
        sessionStorage.removeItem(storageKey(token));
        ticket = '';
        partyId = '';
      }
    }

    try {
      preview.value = await getPartyPreview(token);
      phase.value = 'invite';
    } catch {
      // Dead or unknown link - to the visitor it is simply over.
      phase.value = 'ended';
    }
  }

  async function join(token: string, displayName: string): Promise<void> {
    if (joining.value) return;
    joining.value = true;
    joinError.value = null;
    try {
      const result = await joinParty(token, displayName);
      ticket = result.ticket ?? '';
      partyId = result.party.id;
      try {
        sessionStorage.setItem(storageKey(token),
          JSON.stringify({ ticket, partyId } satisfies StoredMembership));
      } catch { /* private mode; a reload just re-invites */ }
      applySnapshot(result.party, result.queue, result.state ?? null);
      await connectHub();
      phase.value = 'active';
    } catch {
      // The server answers 404 for every kind of "no" - to the guest they are all this.
      joinError.value = 'This party link is not active. Ask for a new one.';
    } finally {
      joining.value = false;
    }
  }

  function applySnapshot(info: PartyInfo, items: PartyQueueItem[], s: PartyState | null): void {
    party.value = info;
    members.value = info.members;
    queue.value = items;
    if (s) applyState(s);
  }

  async function connectHub(): Promise<void> {
    const { createPartyHubConnection } = await import('@/composables/useSignalR');
    const connection = createPartyHubConnection();
    hub = connection;

    connection.on('PartyState', (s: unknown) => {
      const parsed = toPartyState(s);
      if (parsed) applyState(parsed);
    });
    connection.on('PartyQueue', (items: PartyQueueItem[]) => {
      if (Array.isArray(items)) queue.value = items;
    });
    connection.on('PartyMembers', (list: PartyMember[]) => {
      if (Array.isArray(list)) members.value = list;
    });
    connection.on('PartyEnded', () => endLocally());

    connection.onreconnected(async () => {
      if (!hub) return;
      try {
        await hub.invoke('Subscribe', partyId, ticket || null);
        const snapshot = await getPartySnapshot(partyId, ticket);
        applySnapshot(snapshot.party, snapshot.queue, snapshot.state ?? null);
      } catch {
        endLocally();
      }
    });

    await connection.start();
    await connection.invoke('Subscribe', partyId, ticket || null);
  }

  /** Coerce an untrusted hub payload; see useSignalR's toAudioAnalysisProgress for why. */
  function toPartyState(payload: unknown): PartyState | null {
    if (!payload || typeof payload !== 'object') return null;
    const raw = payload as Record<string, unknown>;
    return {
      trackId: typeof raw.trackId === 'string' ? raw.trackId : null,
      positionMs: typeof raw.positionMs === 'number' && Number.isFinite(raw.positionMs)
        ? raw.positionMs : 0,
      isPlaying: raw.isPlaying === true,
      serverTimestamp: typeof raw.serverTimestamp === 'string'
        ? raw.serverTimestamp : new Date().toISOString(),
      track: toStateTrack(raw.track),
    };
  }

  function toStateTrack(payload: unknown): PartyStateTrack | null {
    if (!payload || typeof payload !== 'object') return null;
    const raw = payload as Record<string, unknown>;
    if (typeof raw.trackId !== 'string' || typeof raw.title !== 'string') return null;
    return {
      trackId: raw.trackId,
      title: raw.title,
      artist: typeof raw.artist === 'string' ? raw.artist : '',
      album: typeof raw.album === 'string' ? raw.album : '',
      albumId: typeof raw.albumId === 'string' ? raw.albumId : '',
      durationMs: typeof raw.durationMs === 'number' && Number.isFinite(raw.durationMs)
        ? raw.durationMs : null,
    };
  }

  function applyState(s: PartyState): void {
    state.value = s;
    if (!listening.value || !audio) return;
    followState(s);
  }

  /** Make the local element match the party: track, position (within tolerance), play/pause. */
  function followState(s: PartyState): void {
    const el = audio;
    if (!el) return;

    if (!s.trackId) {
      el.pause();
      loadedTrackId = null;
      return;
    }

    const target = s.positionMs / 1000;

    if (s.trackId !== loadedTrackId) {
      loadedTrackId = s.trackId;
      loadRetries = 0;
      loadTrack(el, s.trackId, target, s.isPlaying);
      return;
    }

    // A load that failed (the owner's queue mirror may land a beat after the track starts,
    // 404ing the first request) would otherwise stay silent for the whole song - the src is
    // only ever set on a track change. Ticks arrive ~1 Hz, so a few retries cover the gap.
    if (el.error && loadRetries < 5) {
      loadRetries++;
      loadTrack(el, s.trackId, target, s.isPlaying);
      return;
    }

    // While a seek is in flight or the buffer has no runway, the element's clock is stalled,
    // not drifted - and a corrective seek would throw away what has buffered and start the
    // fetch over. Joining mid-song used to loop exactly that way: seek, buffer, "3s behind",
    // re-seek, forever. Judge drift only when playback is actually able to move.
    const canJudgeDrift = !el.seeking && el.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA;
    if (canJudgeDrift && Math.abs(el.currentTime - target) > DRIFT_TOLERANCE_S) {
      try { el.currentTime = target; } catch { /* not seekable */ }
    }
    if (s.isPlaying && el.paused) el.play().catch(() => { /* ignore */ });
    else if (!s.isPlaying && !el.paused) el.pause();
  }

  function loadTrack(el: HTMLAudioElement, trackId: string, target: number, playNow: boolean): void {
    el.src = partyStreamUrl(partyId, trackId, ticket);
    // Seek once metadata exists; before that the element silently ignores currentTime.
    const applySeek = () => {
      try { el.currentTime = target; } catch { /* not seekable */ }
      el.removeEventListener('loadedmetadata', applySeek);
    };
    el.addEventListener('loadedmetadata', applySeek);
    if (playNow) el.play().catch(() => { /* autoplay policy; next gesture fixes it */ });
  }

  /** Must be called from a click - it is the user gesture that unlocks audio playback. */
  function startListening(): void {
    if (!audio) {
      audio = new Audio();
      audio.preload = 'auto';
      audio.volume = volume.value;
    }
    listening.value = true;
    if (audioCtx?.state === 'suspended') audioCtx.resume().catch(() => { /* ignore */ });
    if (state.value) followState(state.value);
  }

  function stopListening(): void {
    listening.value = false;
    audio?.pause();
  }

  function setVolume(level: number): void {
    volume.value = Math.max(0, Math.min(1, level));
    if (gainNode) gainNode.gain.value = volume.value;
    else if (audio) audio.volume = volume.value;
  }

  /**
   * Lazily tap the guest's element for the visualizer, same graph as the library player:
   * source → analyser (full level) → gain (volume) → out, so the bars stay independent of
   * the volume slider. Party streams are same-origin, so the analyser tap is allowed.
   * Called on a user gesture (the guest is already listening when the visualizer mounts).
   */
  function ensureAnalyser(): AnalyserNode | null {
    if (analyserNode) return analyserNode;
    if (!audio || typeof window === 'undefined') return null;
    const Ctx = window.AudioContext
      ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctx) return null;
    try {
      audioCtx = new Ctx();
      const source = audioCtx.createMediaElementSource(audio);
      analyserNode = audioCtx.createAnalyser();
      analyserNode.fftSize = 2048; // 1024 bins - enough low-end resolution for log bands
      analyserNode.smoothingTimeConstant = 0.82;
      gainNode = audioCtx.createGain();
      gainNode.gain.value = volume.value;
      source.connect(analyserNode);
      analyserNode.connect(gainNode);
      gainNode.connect(audioCtx.destination);
      audio.volume = 1; // the gain node owns level now
    } catch {
      analyserNode = null;
    }
    if (audioCtx?.state === 'suspended') audioCtx.resume().catch(() => { /* ignore */ });
    return analyserNode;
  }

  async function addTrack(trackId: string): Promise<boolean> {
    try {
      await addPartyTrack(partyId, trackId, ticket);
      return true;
    } catch {
      return false;
    }
  }

  function search(query: string): Promise<PartySearchTrack[]> {
    return searchPartyTracks(partyId, query, ticket);
  }

  function endLocally(): void {
    phase.value = 'ended';
    stopListening();
    const connection = hub;
    hub = null;
    if (connection) void connection.stop().catch(() => { /* already gone */ });
  }

  /** Leaving the page. The membership stays valid - closing a tab is not leaving the party. */
  function teardown(): void {
    stopListening();
    if (audioCtx) void audioCtx.close().catch(() => { /* already gone */ });
    audioCtx = null;
    analyserNode = null;
    gainNode = null;
    audio = null;
    loadedTrackId = null;
    const connection = hub;
    hub = null;
    if (connection) void connection.stop().catch(() => { /* already gone */ });
  }

  function readStored(token: string): StoredMembership | null {
    try {
      const raw = sessionStorage.getItem(storageKey(token));
      if (!raw) return null;
      const parsed = JSON.parse(raw) as StoredMembership;
      // An empty ticket is a valid marker (signed-in membership); a missing party id is not.
      return typeof parsed.ticket === 'string' && parsed.partyId ? parsed : null;
    } catch {
      return null;
    }
  }

  return {
    phase,
    joining,
    joinError,
    preview,
    isSignedIn,
    party,
    members,
    queue,
    state,
    listening,
    volume,
    currentItem,
    guestTicket,
    partyIdRef,
    init,
    join,
    startListening,
    stopListening,
    setVolume,
    ensureAnalyser,
    addTrack,
    search,
    teardown,
  };
});
