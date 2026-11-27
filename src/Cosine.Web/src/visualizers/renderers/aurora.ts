import type { VisualizerFactory } from '../types';
import { createColorBlend } from '../colorBlend';

const vertexSource = `
attribute vec2 position;
void main() { gl_Position = vec4(position, 0.0, 1.0); }
`;
const fragmentSource = `
#ifdef GL_FRAGMENT_PRECISION_HIGH
precision highp float;
#else
precision mediump float;
#endif
uniform vec2 resolution;
uniform vec3 levels;
uniform vec3 hits;
uniform vec3 tint;
uniform vec4 flares[3];
uniform float phase;
float hash(float p) {
  p = fract(p * 0.1031);
  p *= p + 33.33;
  return fract(p * (p + p));
}
float noise(float p) {
  float cell = floor(p);
  float f = fract(p);
  f = f * f * (3.0 - 2.0 * f);
  return mix(hash(cell), hash(cell + 1.0), f);
}
void main() {
  vec2 uv = gl_FragCoord.xy / resolution;
  float aspect = min(resolution.x / resolution.y, 2.0);
  float x = (uv.x - 0.5) * aspect;
  vec3 light = vec3(0.0);
  for (int i = 0; i < 3; i++) {
    float layer = float(i);
    float seed = 17.0 + layer * 31.0;
    float t = phase * (0.24 + layer * 0.08);
    // Uneven folds shear with altitude, so the light forms curtains rather than parallel bands.
    float broadFold = noise(x * 2.6 + seed + t);
    float rayX = x + (uv.y - 0.3) * (broadFold - 0.5) * 0.5;
    float folded = rayX + (noise(rayX * 5.0 + seed - t) - 0.5) * 0.24;
    float edgeNoise = noise(folded * 4.0 + seed + t * 0.6);
    float fineFold = noise(folded * 11.0 + seed - t * 0.4);
    float base = 0.12 + layer * 0.065 + edgeNoise * (0.23 + levels.x * 0.1)
      + fineFold * 0.055 + hits.x * (edgeNoise - 0.5) * 0.07;
    float altitude = uv.y - base;
    float rayLength = 0.25 + noise(folded * 7.0 + seed + 9.0) * 0.38 + levels.x * 0.12;
    float rise = smoothstep(-0.018, 0.045, altitude);
    float fade = 1.0 - smoothstep(rayLength * 0.12, rayLength, altitude);
    // Narrow vertical rays vary in strength and length; no repeating stripe pattern.
    float strands = noise(folded * 65.0 + seed + t * 0.3);
    strands = smoothstep(0.1, 0.9, strands);
    float veils = noise(folded * 24.0 + seed - t * 0.25);
    float patches = smoothstep(0.15, 0.8, noise(folded * 3.5 + seed + 13.0));
    float curtain = rise * fade * (0.35 + strands * 0.16 + veils * 0.22) * (0.35 + patches * 0.65);
    float rim = exp(-abs(altitude) * 90.0) * (0.08 + veils * 0.08);
    vec3 green = vec3(0.10, 0.55, 0.32);
    vec3 violet = vec3(0.38, 0.16, 0.48);
    vec3 hue = mix(green, violet, smoothstep(0.04, rayLength * 0.85, altitude));
    hue = mix(hue, tint, 0.25);
    // Each attack blooms locally, spreads upward/outward, then dissipates.
    vec4 flare = flares[i];
    float age = flare.z;
    float spread = 0.10 + min(age, 1.5) * 0.20;
    float dx = (uv.x - flare.x) / spread;
    float dy = (altitude - 0.06 - age * 0.15) / (0.10 + min(age, 1.5) * 0.09);
    float bloom = exp(-dx * dx * 1.5 - dy * dy) * flare.w * exp(-age * 2.0) * rise;
    light += hue * ((curtain + rim) * (0.55 + levels.y * 0.3) + bloom * 1.6);
  }
  light = light / (vec3(1.0) + light);
  float verticalFade = smoothstep(0.0, 0.08, uv.y) * (1.0 - smoothstep(0.9, 1.0, uv.y));
  gl_FragColor = vec4(vec3(0.006, 0.009, 0.016) + light * verticalFade, 1.0);
}
`;

/** Three folded light curtains in one bounded-resolution pass, with no textures or blur. */
export const create: VisualizerFactory = (canvas, format) => {
  const context = canvas.getContext('webgl', { alpha: false, antialias: false, depth: false, stencil: false });
  if (!context) throw new Error('WebGL is unavailable');
  const gl = context;
  let program: WebGLProgram | null = null;
  let buffer: WebGLBuffer | null = null;
  let resolution: WebGLUniformLocation | null = null;
  let levelsUniform: WebGLUniformLocation | null = null;
  let hitsUniform: WebGLUniformLocation | null = null;
  let phaseUniform: WebGLUniformLocation | null = null;
  let flaresUniform: WebGLUniformLocation | null = null;
  let tintUniform: WebGLUniformLocation | null = null;
  let lost = false;
  const flares = new Float32Array([0.3, 0, 10, 0, 0.5, 0, 10, 0, 0.7, 0, 10, 0]);
  let flareSequence = 0;
  const levels = new Float32Array(3);
  const previous = new Float32Array(3);
  const hits = new Float32Array(3);
  const colorBlend = createColorBlend([0.35, 0.55, 0.65]);
  const color = colorBlend.value;
  let lastTint: string | null | undefined;
  let phase = 0;
  const bins = [40, 250, 2500, 12000].map(hz => Math.max(1, Math.min(format.fftSize / 2, Math.round(hz * format.fftSize / format.sampleRate))));

  function compile(type: number, source: string) {
    const shader = gl.createShader(type);
    if (!shader) throw new Error('Unable to allocate shader');
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const message = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader);
      throw new Error(message ?? 'Shader compilation failed');
    }
    return shader;
  }
  function release() {
    if (buffer) gl.deleteBuffer(buffer);
    if (program) gl.deleteProgram(program);
    buffer = null; program = null;
  }
  function initialize() {
    let vertex: WebGLShader | null = null;
    let fragment: WebGLShader | null = null;
    try {
      vertex = compile(gl.VERTEX_SHADER, vertexSource);
      fragment = compile(gl.FRAGMENT_SHADER, fragmentSource);
      program = gl.createProgram();
      if (!program) throw new Error('Unable to allocate shader program');
      gl.attachShader(program, vertex); gl.attachShader(program, fragment);
      gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'Shader linking failed');
      gl.useProgram(program);
      buffer = gl.createBuffer();
      if (!buffer) throw new Error('Unable to allocate vertex buffer');
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
      const position = gl.getAttribLocation(program, 'position');
      gl.enableVertexAttribArray(position);
      gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
      resolution = gl.getUniformLocation(program, 'resolution');
      levelsUniform = gl.getUniformLocation(program, 'levels');
      hitsUniform = gl.getUniformLocation(program, 'hits');
      phaseUniform = gl.getUniformLocation(program, 'phase');
      tintUniform = gl.getUniformLocation(program, 'tint');
      flaresUniform = gl.getUniformLocation(program, 'flares[0]');
      gl.viewport(0, 0, canvas.width, canvas.height);
    } catch (error) {
      release();
      throw error;
    } finally {
      if (vertex) gl.deleteShader(vertex);
      if (fragment) gl.deleteShader(fragment);
    }
  }
  function paint() {
    if (lost || !program) return;
    gl.useProgram(program);
    gl.uniform2f(resolution, canvas.width, canvas.height);
    gl.uniform3fv(levelsUniform, levels);
    gl.uniform3fv(hitsUniform, hits);
    gl.uniform1f(phaseUniform, phase);
    gl.uniform3fv(tintUniform, color);
    gl.uniform4fv(flaresUniform, flares);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
  function onLost(event: Event) { event.preventDefault(); lost = true; }
  function onRestored() {
    program = null; buffer = null;
    try { initialize(); lost = false; paint(); }
    catch (error) { console.error('Unable to restore Aurora', error); }
  }
  initialize();
  canvas.addEventListener('webglcontextlost', onLost);
  canvas.addEventListener('webglcontextrestored', onRestored);
  return {
    resize(size) {
      gl.viewport(0, 0, size.width, size.height);
        },
    update(audio, reducedMotion, dt) {
      colorBlend.update(dt, reducedMotion);
      for (let band = 0; band < 3; band++) {
        let sum = 0;
        const lo = bins[band]!; const hi = bins[band + 1]!;
        if (audio?.frequency) for (let i = lo; i < hi; i++) sum += Math.pow(10, audio.frequency[i]! / 20);
        const magnitude = sum / Math.max(1, hi - lo);
        const db = magnitude > 0 ? 20 * Math.log10(magnitude) : -75;
        const raw = Math.max(0, Math.min(1, (db + 65) / 40));
        const target = Math.pow(raw, 2.5);
        const onset = Math.min(1, Math.max(0, raw - previous[band]! - 0.008) * 8) * raw;
        const offset = band * 4;
        flares[offset + 2] = Math.min(10, flares[offset + 2]! + dt);
        if (reducedMotion) flares[offset + 3] = 0;
        else if (onset > 0.06 && flares[offset + 2]! > 0.2) {
          flareSequence++;
          flares[offset] = 0.15 + ((flareSequence * 0.61803398875 + band * 0.23) % 1) * 0.7;
          flares[offset + 2] = 0;
          flares[offset + 3] = Math.min(1, onset * 2.5);
        }
        if (flares[offset + 2]! > 2.5) flares[offset + 3] = 0;
        previous[band] = raw;
        hits[band] = reducedMotion ? 0 : Math.max(onset, hits[band]! * Math.exp(-dt / 0.28));
        const blend = reducedMotion ? 1 : 1 - Math.exp(-dt / (target > levels[band]! ? 0.06 : 0.4));
        levels[band] = levels[band]! + (target - levels[band]!) * blend;
        if (levels[band]! < 0.001) levels[band] = 0;
        if (hits[band]! < 0.001) hits[band] = 0;
      }
      if (!reducedMotion) phase += dt * (levels[0]! * 0.35 + levels[1]! * 0.4 + hits[1]! * 0.8 + hits[2]! * 0.4);
        },
    draw(theme) {
      if (lastTint !== theme.tint) {
        lastTint = theme.tint;
        const targetColor = new Float32Array([0.35, 0.55, 0.65]);
        const rgb = theme.tint?.match(/[\d.]+/g)?.slice(0, 3).map(Number);
        if (rgb?.length === 3) {
          const scale = 0.7 / Math.max(170, ...rgb);
          for (let i = 0; i < 3; i++) targetColor[i] = Math.max(0.09, rgb[i]! * scale);
        } else targetColor.set([0.35, 0.55, 0.65]);
        colorBlend.set(targetColor);
      }
      paint();
    },
    get settling() { return colorBlend.settling || levels.some(value => value > 0.001) || hits.some(value => value > 0.001)
      || flares[3]! > 0 || flares[7]! > 0 || flares[11]! > 0; },
    dispose() {
      canvas.removeEventListener('webglcontextlost', onLost);
      canvas.removeEventListener('webglcontextrestored', onRestored);
      release();
    },
  };
};
