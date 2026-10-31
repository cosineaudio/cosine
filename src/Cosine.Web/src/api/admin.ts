import { client } from './client'
import type { PlaybackDevice } from './playback'

export interface LibraryStats {
  artistCount: number
  albumCount: number
  trackCount: number
  totalSizeBytes: number
  totalDurationMs: number
}

export interface AdminSetting {
  key: string
  value: string
}

export interface ScanLog {
  id: string
  startedAt: string
  finishedAt?: string
  status: string
  filesAdded: number
  filesRemoved: number
  filesUpdated: number
  errorMessage?: string
}

export interface MetricsSnapshot {
  cacheHits: number
  cacheMisses: number
  cacheHitRate: number
  streamRequests: number
  transcodeCount: number
  transcodeCacheHits: number
  directPlayCount: number
  transcodePercent: number
  transcodeCacheHitRate: number
  ttfbP50Ms: number
  ttfbP95Ms: number
  ttfbSampleCount: number
  sinceUtc: string
}

/** Transcode slots held right now, out of the configured ceiling. A point-in-time reading. */
export interface TranscodeActivity {
  active: number
  limit: number
}

export interface LiveMetrics {
  metrics: MetricsSnapshot
  redisKeyCount: number
  transcoding: TranscodeActivity
}

export interface StatSnapshot {
  id: string
  capturedAt: string
  cacheHits: number
  cacheMisses: number
  streamRequests: number
  transcodeCount: number
  transcodeCacheHits: number
  directPlayCount: number
  playsRecorded: number
  ttfbP50Ms: number
  ttfbP95Ms: number
  redisKeyCount: number
}

export interface RecapEntry {
  id: string
  name: string
  subtitle?: string
  playCount: number
}

export interface PlaysBucket {
  date: string
  count: number
}

export interface Recap {
  totalPlays: number
  estimatedMinutes: number
  uniqueTracks: number
  uniqueArtists: number
  topTracks: RecapEntry[]
  topArtists: RecapEntry[]
  topAlbums: RecapEntry[]
  topGenres: RecapEntry[]
  playsOverTime: PlaysBucket[]
}

export interface AudioAnalysisStatus {
  /** False when the server has no pgvector - content-based similarity is off entirely. */
  available: boolean
  /** Tracks with a current-version feature vector. */
  analyzed: number
  total: number
  /** Analysed but not yet normalized, so still invisible to similarity queries. */
  pendingNormalization: number
}

export interface UserDto {
  id: string
  username: string
  email?: string
  isAdmin: boolean
  lastLoginAt?: string
  createdAt: string
}

export interface CreateUserRequest {
  username: string
  password: string
  email?: string
  isAdmin: boolean
}

export interface UpdateUserRequest {
  email?: string
  isAdmin?: boolean
  password?: string
}

/**
 * Every field is optional and an absent one is left alone, so the form can send only what it
 * changed. `displayName` and `transcodeProfile` are cleared by sending an empty string - absent
 * already means "don't touch".
 */
export interface UpdatePlaybackDeviceRequest {
  displayName?: string
  transcodeProfile?: string
  maxVolumePercent?: number
  lyricOffsetMs?: number
  enabled?: boolean
}

export const adminApi = {
  getStats(): Promise<LibraryStats> {
    return client.get<LibraryStats>('/api/admin/stats')
  },

  getSettings(): Promise<AdminSetting[]> {
    return client.get<AdminSetting[]>('/api/admin/settings')
  },

  updateSetting(key: string, value: string): Promise<void> {
    return client.put<void>(`/api/admin/settings/${encodeURIComponent(key)}`, { value })
  },

  startScan(): Promise<{ scanLogId: string }> {
    return client.post<{ scanLogId: string }>('/api/admin/scan')
  },

  getScanLogs(): Promise<ScanLog[]> {
    return client.get<ScanLog[]>('/api/admin/scan-logs')
  },

  rebuildIndex(): Promise<void> {
    return client.post<void>('/api/admin/reindex')
  },

  getAudioAnalysis(): Promise<AudioAnalysisStatus> {
    return client.get<AudioAnalysisStatus>('/api/admin/audio-analysis')
  },

  startAudioAnalysis(): Promise<void> {
    return client.post<void>('/api/admin/audio-analysis')
  },

  getLiveMetrics(): Promise<LiveMetrics> {
    return client.get<LiveMetrics>('/api/admin/stats/metrics')
  },

  getStatsHistory(hours = 24): Promise<StatSnapshot[]> {
    return client.get<StatSnapshot[]>(`/api/admin/stats/history?hours=${hours}`)
  },

  getRecap(days?: number): Promise<Recap> {
    return client.get<Recap>(`/api/admin/stats/recap${days ? `?days=${days}` : ''}`)
  },

  getUsers(): Promise<UserDto[]> {
    return client.get<UserDto[]>('/api/admin/users')
  },

  createUser(data: CreateUserRequest): Promise<UserDto> {
    return client.post<UserDto>('/api/admin/users', data)
  },

  updateUser(id: string, data: UpdateUserRequest): Promise<void> {
    return client.put<void>(`/api/admin/users/${id}`, data)
  },

  deleteUser(id: string): Promise<void> {
    return client.delete<void>(`/api/admin/users/${id}`)
  },

  /**
   * The device list is not here: it is `getPlaybackDevices()` in `api/playback`, the same one the
   * output picker reads. Only the mutation is admin-only.
   */
  updatePlaybackDevice(id: string, data: UpdatePlaybackDeviceRequest): Promise<PlaybackDevice> {
    return client.put<PlaybackDevice>(`/api/admin/playback/devices/${id}`, data)
  },
}
