import { client } from './client';
import { mapTrackLyrics, type TrackLyrics, type TrackLyricsDto } from './lyrics';

/**
 * Listen-together API. Two callers share these shapes: the owner (cookie-authenticated, full
 * party controls) and guests (no account - a signed ticket from `joinParty` is their whole
 * identity, sent as the `X-Party-Ticket` header, or `?ticket=` on media URLs where the
 * browser's elements cannot send headers).
 *
 * Every guest-side failure is a 404 by design - bad ticket, ended party and out-of-scope
 * track are deliberately indistinguishable. Callers should treat 404 as "the party is over".
 */

export interface PartyMember {
  id: string;
  displayName: string;
  isOwner: boolean;
  joinedAt: string;
}

export interface PartyInfo {
  id: string;
  name: string;
  /** Share-link secret. Only present in owner responses. */
  token?: string | null;
  createdAt: string;
  members: PartyMember[];
}

export interface PartyQueueItem {
  entryId: string;
  position: number;
  trackId: string;
  title: string;
  artist: string;
  album: string;
  albumId: string;
  durationMs?: number | null;
  /** Display name of the guest who queued it; null for the host's own entries. */
  addedBy?: string | null;
  addedByMemberId?: string | null;
}

export interface PartyState {
  trackId?: string | null;
  positionMs: number;
  isPlaying: boolean;
  serverTimestamp: string;
  /**
   * What the playing track is, resolved server-side. The state relay outruns the owner's
   * queue mirror (and survives it failing), so the now-playing card renders from this and
   * never waits on the track appearing in the queue.
   */
  track?: PartyStateTrack | null;
}

export interface PartyStateTrack {
  trackId: string;
  title: string;
  artist: string;
  album: string;
  albumId: string;
  durationMs?: number | null;
}

export interface PartySnapshot {
  party: PartyInfo;
  queue: PartyQueueItem[];
  state?: PartyState | null;
}

export interface JoinPartyResult extends PartySnapshot {
  /** Null for signed-in users - their cookie is the credential (see the guest controller). */
  ticket: string | null;
}

export interface PartySearchTrack {
  id: string;
  title: string;
  artist: string;
  album: string;
  albumId: string;
  durationMs?: number | null;
}

// ---- Owner (cookie) ----

export function createParty(name: string | null, trackIds: string[]): Promise<PartyInfo> {
  return client.post<PartyInfo>('api/party', { name, trackIds });
}

/** The owner's live party, or null - the endpoint answers 204 when there is none. */
export async function getMyParty(): Promise<PartyInfo | null> {
  return (await client.get<PartyInfo | undefined>('api/party/mine')) ?? null;
}

export function endParty(partyId: string): Promise<void> {
  return client.delete(`api/party/${encodeURIComponent(partyId)}`);
}

export function replacePartyQueue(partyId: string, trackIds: string[]): Promise<void> {
  return client.put(`api/party/${encodeURIComponent(partyId)}/queue`, { trackIds });
}

// ---- Guest (ticket, or cookie for signed-in members) ----
//
// Every ticket parameter below is optional: an anonymous guest sends the minted ticket, a
// signed-in member sends nothing and their session cookie rides along instead.

function ticketHeader(ticket?: string | null): Record<string, string> | undefined {
  return ticket ? { 'X-Party-Ticket': ticket } : undefined;
}

function ticketQuery(ticket?: string | null, extra = ''): string {
  const params = [
    ...(ticket ? [`ticket=${encodeURIComponent(ticket)}`] : []),
    ...(extra ? [extra] : []),
  ];
  return params.length > 0 ? `?${params.join('&')}` : '';
}

/** What a link-holder may know before joining - enough to render the invitation. */
export interface PartyPreview {
  name: string;
  hostName?: string | null;
  memberCount: number;
}

export function getPartyPreview(token: string): Promise<PartyPreview> {
  return client.get<PartyPreview>(`api/party/preview/${encodeURIComponent(token)}`);
}

export async function joinParty(token: string, displayName: string): Promise<JoinPartyResult> {
  return client.post<JoinPartyResult>(
    `api/party/join/${encodeURIComponent(token)}`, { displayName });
}

export function getPartySnapshot(partyId: string, ticket?: string | null): Promise<PartySnapshot> {
  return client.get<PartySnapshot>(
    `api/party/${encodeURIComponent(partyId)}/snapshot`, ticketHeader(ticket));
}

export function addPartyTrack(
  partyId: string, trackId: string, ticket?: string | null): Promise<PartyQueueItem> {
  return client.post<PartyQueueItem>(
    `api/party/${encodeURIComponent(partyId)}/queue`, { trackId }, ticketHeader(ticket));
}

export function searchPartyTracks(
  partyId: string, query: string, ticket?: string | null): Promise<PartySearchTrack[]> {
  return client.get<PartySearchTrack[]>(
    `api/party/${encodeURIComponent(partyId)}/search?q=${encodeURIComponent(query)}`,
    ticketHeader(ticket));
}

/** Lyrics for the listen-along view - same DTO as /api/tracks/{id}/lyrics, party-scoped. */
export async function getPartyLyrics(
  partyId: string, trackId: string, ticket?: string | null): Promise<TrackLyrics> {
  const data = await client.get<TrackLyricsDto>(
    `api/party/${encodeURIComponent(partyId)}/lyrics/${encodeURIComponent(trackId)}`,
    ticketHeader(ticket));
  return mapTrackLyrics(data);
}

export function partyStreamUrl(partyId: string, trackId: string, ticket?: string | null): string {
  return `/api/party/${encodeURIComponent(partyId)}/stream/${encodeURIComponent(trackId)}${ticketQuery(ticket)}`;
}

export function partyArtworkUrl(
  partyId: string, albumId: string, ticket?: string | null, size?: number): string {
  return `/api/party/${encodeURIComponent(partyId)}/artwork/${encodeURIComponent(albumId)}${ticketQuery(ticket, size ? `size=${size}` : '')}`;
}
