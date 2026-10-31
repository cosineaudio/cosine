import { client } from './client';

export interface InternetRadioStation {
  id: string;
  name: string;
  streamUrl: string;
  homepageUrl?: string;
}

/** All configured internet-radio stations. */
export async function getInternetRadioStations(): Promise<InternetRadioStation[]> {
  const data = await client.get<{ stations: InternetRadioStation[] }>('api/radio-stations');
  return data.stations;
}

/** Add a station (admin only). */
export async function createInternetRadioStation(opts: {
  streamUrl: string;
  name: string;
  homepageUrl?: string;
}): Promise<void> {
  await client.post('api/radio-stations', opts);
}

/** Update an existing station (admin only). */
export async function updateInternetRadioStation(opts: {
  id: string;
  streamUrl: string;
  name: string;
  homepageUrl?: string;
}): Promise<void> {
  const { id, ...body } = opts;
  await client.put(`api/radio-stations/${encodeURIComponent(id)}`, body);
}

/** Delete a station (admin only). */
export async function deleteInternetRadioStation(id: string): Promise<void> {
  await client.delete(`api/radio-stations/${encodeURIComponent(id)}`);
}
