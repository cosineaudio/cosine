/** Buffers are borrowed for this frame; renderers must not mutate them. */
export interface AudioFrame {
  frequency?: Float32Array<ArrayBuffer>;
  waveform?: Float32Array<ArrayBuffer>;
}

export interface AudioFormat { sampleRate: number; fftSize: number }
export interface RenderSize { width: number; height: number; dpr: number }
export interface VisualizerTheme { tint: string | null; text: string; muted: string }

/** The host owns the canvas size and animation loop; the renderer owns its context/resources. */
export interface VisualizerRenderer {
  setTrack?(trackId: string | null): void;
  resize(size: RenderSize): void;
  update(audio: AudioFrame | null, reducedMotion: boolean, deltaSeconds: number): void;
  draw(theme: VisualizerTheme): void;
  readonly settling: boolean;
  dispose(): void;
}
export type VisualizerFactory = (canvas: HTMLCanvasElement, format: AudioFormat) => VisualizerRenderer;
export interface VisualizerDefinition {
  id: string;
  name: string;
  /** Spectrum retains its original floor; immersive renderers use the full viewport. */
  placement: 'floor' | 'full';
  /** Optional drawing-buffer budget for soft GPU effects; CSS size stays full viewport. */
  maxPixels?: number;
  audio: 'frequency' | 'waveform' | 'both';
  load(): Promise<{ create: VisualizerFactory }>;
}
