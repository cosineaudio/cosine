export interface Artist {
  id: string;
  name: string;
  coverArt?: string;
  artistImageUrl?: string;
  albumCount?: number;
  sortName?: string;
  musicBrainzId?: string;
  starred?: string;
}

export interface Album {
  id: string;
  name: string;
  artist?: string;
  artistId?: string;
  coverArt?: string;
  songCount: number;
  duration: number;
  created: string;
  year?: number;
  genre?: string;
  starred?: string;
  playCount?: number;
}

export interface Track {
  id: string;
  parent?: string;
  isDir: boolean;
  title: string;
  album?: string;
  albumId?: string;
  artist?: string;
  artistId?: string;
  /**
   * OpenSubsonic: every artist credited on the track, in credit order - the featured artists
   * a single `artist`/`artistId` pair cannot express. Absent on tracks with no stored credit.
   */
  artists?: { id: string; name: string }[];
  /** OpenSubsonic: the credit as one string, join phrases included - "Pouya & Fat Nick". */
  displayArtist?: string;
  track?: number;
  discNumber?: number;
  year?: number;
  genre?: string;
  coverArt?: string;
  size?: number;
  contentType?: string;
  suffix?: string;
  duration?: number;
  bitRate?: number;
  starred?: string;
  playCount?: number;
  /** Direct stream URL (internet radio / external sources). Bypasses /rest/stream. */
  streamUrl?: string;
}

export interface IndexGroup {
  name: string;
  artist: Artist[];
}

export interface ArtistsResponse {
  artists: {
    index: IndexGroup[];
  };
}

export interface ArtistDetail {
  id: string;
  name: string;
  coverArt?: string;
  albumCount: number;
  sortName?: string;
  musicBrainzId?: string;
  album: Album[];
}

export interface AlbumDetail extends Album {
  song: Track[];
}

export interface Directory {
  id: string;
  name: string;
  parent?: string;
  child: Track[];
}

export interface SearchResults {
  artist: Artist[];
  album: Album[];
  song: Track[];
}

export interface ArtistInfo {
  biography?: string;
  musicBrainzId?: string;
  lastFmUrl?: string;
  smallImageUrl?: string;
  mediumImageUrl?: string;
  largeImageUrl?: string;
  similarArtist: Artist[];
}

export interface AlbumInfo {
  notes?: string;
  musicBrainzId?: string;
  smallImageUrl?: string;
  mediumImageUrl?: string;
  largeImageUrl?: string;
}
