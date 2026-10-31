import { client } from './client';
import type { TranscodeFormatDto } from './model';

export type TranscodeFormat = TranscodeFormatDto;

/** The formats and bitrates the server can actually transcode to. */
export async function getTranscodeFormats(): Promise<TranscodeFormat[]> {
  const data = await client.get<{ formats: TranscodeFormatDto[] }>('api/transcoding/formats');
  return data.formats;
}

export function transcodeStreamUrl(id: string, format: string, bitRate: number): string {
  return `/api/stream/${encodeURIComponent(id)}?format=${encodeURIComponent(format)}&bitRate=${bitRate}`;
}
