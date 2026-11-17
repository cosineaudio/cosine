import type { VisualizerFactory } from '../types';
import { seededRandom } from '../seededRandom';
import { createColorBlend } from '../colorBlend';

const maxBalls = 10;

const vertexSource = `
attribute vec2 position;
void main() { gl_Position = vec4(position, 0.0, 1.0); }
`;
const fragmentSource = `
precision mediump float;
uniform vec2 resolution;
uniform vec3 balls[${maxBalls}];
uniform vec3 tint;
void main() {
  vec2 p = (gl_FragCoord.xy - resolution * 0.5) / min(resolution.x, resolution.y);
  float field = 0.0;
  vec3 pigment = vec3(0.0);
  for (int i = 0; i < ${maxBalls}; i++) {
    vec2 delta = p - balls[i].xy;
    float d = dot(delta, delta) + 0.008;
    float radiusSquared = balls[i].z * balls[i].z;
    // Nearby balls still merge, but distant tails cannot join the entire scene.
    float support = 1.0 - smoothstep(radiusSquared * 4.0, max(0.0001, radiusSquared * 16.0), d);
    float strength = radiusSquared / d * support;
    field += strength;
    float variation = float(i) / ${(maxBalls - 1).toFixed(1)};
    vec3 shade = mix(tint * vec3(0.65, 0.85, 1.15), tint * vec3(1.15, 0.75, 0.65), variation);
    pigment += shade * strength;
  }
  vec3 color = pigment / max(field, 0.001);
  float mask = smoothstep(0.92, 1.06, field);
  // Broad scene lighting and softly saturated depth avoid a highlight at
  // each ball's center and the bright ring around its contour.
  float light = smoothstep(-0.8, 0.8, dot(p, vec2(-0.45, 0.6)));
  float depth = 1.0 - exp(-max(0.0, field - 1.0) * 0.45);
  vec3 surface = color * (0.43 + light * 0.12 + depth * 0.05);
  vec3 background = vec3(0.008, 0.010, 0.016);
  gl_FragColor = vec4(mix(background, surface, mask), 1.0);
}
`;

/** Up to ten implicit surfaces in one bounded-resolution pass; no textures or postprocessing. */
export const create: VisualizerFactory = (canvas, format) => {
  const context = canvas.getContext('webgl', { alpha: false, antialias: false, depth: false, stencil: false });
  if (!context) throw new Error('WebGL is unavailable');
  const gl = context;
  let program: WebGLProgram | null = null;
  let buffer: WebGLBuffer | null = null;
  let resolution: WebGLUniformLocation | null = null;
  let ballUniform: WebGLUniformLocation | null = null;
  let tintUniform: WebGLUniformLocation | null = null;
  let lost = false;
  const balls = new Float32Array(maxBalls * 3);
  const parameters = new Float32Array(maxBalls * 2);
  for (let i = 0; i < maxBalls; i++) {
    parameters[i*2] = i < 7 ? 0.105 + (i % 3) * 0.012 : 0;
    parameters[i*2+1] = 0.55 + i * 0.065;
  }
  const layoutBlend = createColorBlend(parameters);
  const phases = new Float32Array(maxBalls);
  let lastTrack: string | null | undefined;
  const levels = new Float32Array(3);
  const previous = new Float32Array(3);
  const hits = new Float32Array(3);
  const colorBlend = createColorBlend([0.35, 0.55, 0.65]);
  const color = colorBlend.value;
  let lastTint: string | null | undefined;
  let aspect = 1;
  let drift = 0;
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
      ballUniform = gl.getUniformLocation(program, 'balls[0]');
      tintUniform = gl.getUniformLocation(program, 'tint');
      gl.viewport(0, 0, canvas.width, canvas.height);
    } catch (error) {
      release();
      throw error;
    } finally {
      if (vertex) gl.deleteShader(vertex);
      if (fragment) gl.deleteShader(fragment);
    }
  }
  function positionBalls() {
    const width = Math.max(1, aspect);
    const height = Math.max(1, 1 / aspect);
    for (let i = 0; i < maxBalls; i++) {
      const angle = i * 2.399963;
      const movement = phases[i]!;
      const spread = 0.31 + (i % 3) * 0.025 + hits[1]! * 0.045;
      balls[i * 3] = width * (Math.cos(angle + movement) * spread + Math.sin(movement * 0.8 + i * 2) * 0.08);
      balls[i * 3 + 1] = height * (Math.sin(angle - movement * 0.7) * spread + Math.cos(movement * 0.6 + i) * 0.08);
      balls[i * 3 + 2] = layoutBlend.value[i*2]! * (1 + levels[0]! * 0.25 + hits[0]! * 0.12);
    }
  }
  function paint() {
    if (lost || !program) return;
    gl.useProgram(program);
    gl.uniform2f(resolution, canvas.width, canvas.height);
    gl.uniform3fv(ballUniform, balls);
    gl.uniform3fv(tintUniform, color);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
  function onLost(event: Event) { event.preventDefault(); lost = true; }
  function onRestored() {
    program = null; buffer = null;
    try { initialize(); lost = false; paint(); }
    catch (error) { console.error('Unable to restore Meld', error); }
  }
  initialize();
  positionBalls();
  canvas.addEventListener('webglcontextlost', onLost);
  canvas.addEventListener('webglcontextrestored', onRestored);
  return {
    setTrack(trackId) {
      if (trackId === lastTrack) return;
      lastTrack = trackId;
      const random = seededRandom(trackId ?? 'metaballs');
      const count = 4 + Math.floor(random() * (maxBalls - 3));
      const density = Math.sqrt(7 / count);
      const next = new Float32Array(maxBalls * 2);
      let totalArea = 0;
      for (let i = 0; i < maxBalls; i++) {
        next[i*2] = i < count ? Math.min(0.16, (0.11 + random() * 0.045) * density) : 0;
        totalArea += next[i*2]! * next[i*2]!;
        next[i*2+1] = (0.25 + random() * 0.95) * (random() < 0.5 ? -1 : 1);
      }
      const areaScale = Math.min(1, Math.sqrt(0.095 / Math.max(totalArea, 0.001)));
      for (let i = 0; i < maxBalls; i++) next[i*2]! *= areaScale;
      layoutBlend.set(next);
      positionBalls();
    },
    resize(size) {
      aspect = size.width / size.height;
      gl.viewport(0, 0, size.width, size.height);
      positionBalls();
    },
    update(audio, reducedMotion, dt) {
      colorBlend.update(dt, reducedMotion);
      layoutBlend.update(dt, reducedMotion);
      for (let band = 0; band < 3; band++) {
        let sum = 0;
        const lo = bins[band]!; const hi = bins[band + 1]!;
        if (audio?.frequency) for (let i = lo; i < hi; i++) sum += Math.pow(10, audio.frequency[i]! / 20);
        const magnitude = sum / Math.max(1, hi - lo);
        const db = magnitude > 0 ? 20 * Math.log10(magnitude) : -75;
        const raw = Math.max(0, Math.min(1, (db + 65) / 40));
        const target = Math.pow(raw, 2.5);
        const onset = Math.min(1, Math.max(0, raw - previous[band]! - 0.008) * 8) * raw;
        previous[band] = raw;
        hits[band] = reducedMotion ? 0 : Math.max(onset, hits[band]! * Math.exp(-dt / 0.2));
        const blend = reducedMotion ? 1 : 1 - Math.exp(-dt / (target > levels[band]! ? 0.045 : 0.3));
        levels[band] = levels[band]! + (target - levels[band]!) * blend;
        if (levels[band]! < 0.001) levels[band] = 0;
        if (hits[band]! < 0.001) hits[band] = 0;
      }
      // Quiet music still carries the forms; easing the motion response does
      // not amplify their size or turn soft passages into attack flashes.
      const driftTarget = audio && !reducedMotion ? 0.12 : 0;
      drift = reducedMotion ? 0 : drift + (driftTarget - drift) * (1 - Math.exp(-dt / 0.6));
      if (drift < 0.001) drift = 0;
      if (!reducedMotion) {
        const drive = drift + Math.sqrt(levels[0]!) * 0.4 + Math.sqrt(levels[1]!) * 0.45
          + hits[1]! * 0.8 + hits[2]! * 0.4;
        for (let i = 0; i < maxBalls; i++) phases[i]! += dt * drive * layoutBlend.value[i*2+1]!;
      }
      positionBalls();
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
    get settling() { return colorBlend.settling || layoutBlend.settling || drift > 0 || levels.some(value => value > 0.001) || hits.some(value => value > 0.001); },
    dispose() {
      canvas.removeEventListener('webglcontextlost', onLost);
      canvas.removeEventListener('webglcontextrestored', onRestored);
      release();
    },
  };
};
