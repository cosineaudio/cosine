import { client } from './client';
import type { Recap, RecapEntry } from './admin';

export interface ListeningInsights {
  from: string;
  until: string;
  recap: Recap;
  previousMonthPlays: number;
  previousMonthGenres: RecapEntry[];
  libraryTracks: number;
  exploredTracks: number;
}

export interface HealthItem { id: string; kind: string; name: string; detail: string }
export interface HealthCategory {
  key: string; title: string; description: string; count: number; items: HealthItem[];
}
export interface LibraryHealthReport {
  checkedAt: string; totalTracks: number; totalAlbums: number; mediaRootAvailable: boolean; categories: HealthCategory[];
}

export const getListeningInsights = (year: number, month: number) =>
  client.get<ListeningInsights>(`api/me/insights?year=${year}&month=${month}`);
export const getLibraryHealth = () => client.get<LibraryHealthReport>('api/admin/library-health');
