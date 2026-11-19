import type { Simulation } from './simulation';
import { count } from './surfaces';
import { createCanvasPainter, type Painter } from './canvas';

const particleVertex = `#version 300 es
precision highp float;
layout(location=0) in float x;
layout(location=1) in float y;
layout(location=2) in float grain;
uniform vec2 resolution;
uniform float dpr;
uniform float pulse;
out vec2 distanceFromCenter;
flat out float radius;
flat out int layer;
void main() {
  vec2 p = vec2(x, y);
  p *= 1.0 + pulse / max(0.15, length(p));
  if (abs(p.x) > 0.975) p.x = sign(p.x) * (1.95 - abs(p.x));
  if (abs(p.y) > 0.975) p.y = sign(p.y) * (1.95 - abs(p.y));
  radius = (1.8 + grain * 1.6) * dpr * 0.5;
  vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1)) * 2.0 - 1.0;
  distanceFromCenter = corner * (radius + 0.5);
  vec2 center = (0.5 + p * 0.51) * resolution;
  vec2 pixel = center + distanceFromCenter;
  gl_Position = vec4(pixel.x / resolution.x * 2.0 - 1.0, 1.0 - pixel.y / resolution.y * 2.0, 0, 1);
  layer = gl_InstanceID % 3;
}`;
const particleFragment = `#version 300 es
precision highp float;
in vec2 distanceFromCenter;
flat in float radius;
flat in int layer;
out vec4 coverage;
void main() {
  vec2 edge = clamp(vec2(radius + 0.5) - abs(distanceFromCenter), 0.0, 1.0);
  float a = edge.x * edge.y;
  coverage = vec4(layer == 0 ? a : 0.0, layer == 1 ? a : 0.0, layer == 2 ? a : 0.0, 0.0);
}`;
const compositeVertex = `#version 300 es
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0, 1);
}`;
const compositeFragment = `#version 300 es
precision highp float;
uniform sampler2D mask;
uniform vec3 colors[3];
out vec4 result;
void main() {
  vec3 a = texelFetch(mask, ivec2(gl_FragCoord.xy), 0).rgb * vec3(0.32, 0.54, 0.76);
  vec3 color = colors[0] * a.x;
  color = colors[1] * a.y + color * (1.0 - a.y);
  color = colors[2] * a.z + color * (1.0 - a.z);
  float alpha = 1.0 - (1.0 - a.x) * (1.0 - a.y) * (1.0 - a.z);
  result = vec4(color, alpha); // premultiplied alpha, matching the transparent Canvas layer
}`;

/** Two GPU draws: union grain coverage per layer, then composite the three shades. */
export function createPainter(canvas: HTMLCanvasElement, state: Simulation): Painter {
  const context = canvas.getContext('webgl2', { alpha: true, premultipliedAlpha: true, antialias: false, depth: false, stencil: false });
  if (!context) return createCanvasPainter(canvas, state);
  const gl = context;
  const attributes = [state.x, state.y, state.grain];
  const colorCanvas = document.createElement('canvas');
  colorCanvas.width = colorCanvas.height = 1;
  const colorContext = colorCanvas.getContext('2d', { willReadFrequently: true });
  if (!colorContext) throw new Error('Canvas color parsing is unavailable');
  const colors = new Float32Array(9);
  let lastColor = '', lastDpr = 1, lastPulse = 0;
  let particles: WebGLProgram | null = null, composite: WebGLProgram | null = null;
  let vao: WebGLVertexArrayObject | null = null;
  let texture: WebGLTexture | null = null, framebuffer: WebGLFramebuffer | null = null;
  let buffers: WebGLBuffer[] = [];
  let width = 0, height = 0, grainDirty = true, lost = false;
  let resolution: WebGLUniformLocation | null, dprUniform: WebGLUniformLocation | null;
  let pulse: WebGLUniformLocation | null, colorUniform: WebGLUniformLocation | null;
  function program(vertex: string, fragment: string) {
    const shaders: WebGLShader[] = [];
    const result = gl.createProgram();
    if (!result) throw new Error('Unable to allocate Chladni program');
    try {
      for (const [type, source] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]] as const) {
        const shader = gl.createShader(type);
        if (!shader) throw new Error('Unable to allocate Chladni shader');
        shaders.push(shader); gl.shaderSource(shader, source); gl.compileShader(shader);
        if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) ?? 'Chladni shader failed');
        gl.attachShader(result, shader);
      }
      gl.linkProgram(result);
      if (!gl.getProgramParameter(result, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(result) ?? 'Chladni linking failed');
      return result;
    } catch (error) { gl.deleteProgram(result); throw error; }
    finally { shaders.forEach(shader => gl.deleteShader(shader)); }
  }
  function release() {
    gl.useProgram(null); gl.bindVertexArray(null); gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.bindTexture(gl.TEXTURE_2D, null); gl.bindBuffer(gl.ARRAY_BUFFER, null);
    buffers.forEach(buffer => gl.deleteBuffer(buffer)); buffers = [];
    gl.deleteVertexArray(vao); gl.deleteTexture(texture); gl.deleteFramebuffer(framebuffer);
    gl.deleteProgram(particles); gl.deleteProgram(composite);
    vao = null; texture = null; framebuffer = null; particles = null; composite = null;
  }
  function initialize() {
    try {
      particles = program(particleVertex, particleFragment);
      composite = program(compositeVertex, compositeFragment);
      resolution = gl.getUniformLocation(particles, 'resolution'); dprUniform = gl.getUniformLocation(particles, 'dpr');
      pulse = gl.getUniformLocation(particles, 'pulse'); colorUniform = gl.getUniformLocation(composite, 'colors[0]');
      vao = gl.createVertexArray(); texture = gl.createTexture(); framebuffer = gl.createFramebuffer();
      if (!vao || !texture || !framebuffer) throw new Error('Unable to allocate Chladni buffers');
      gl.bindVertexArray(vao);
      for (let i = 0; i < 3; i++) {
        const buffer = gl.createBuffer();
        if (!buffer) throw new Error('Unable to allocate Chladni particles');
        buffers.push(buffer); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, count * 4, gl.DYNAMIC_DRAW);
        gl.enableVertexAttribArray(i); gl.vertexAttribPointer(i, 1, gl.FLOAT, false, 0, 0); gl.vertexAttribDivisor(i, 1);
      }
      gl.bindVertexArray(null);
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.disable(gl.DITHER);
      width = height = 0; grainDirty = true;
    } catch (error) { release(); throw error; }
  }
  function paint() {
    if (lost || !particles || !composite) return;
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, texture);
    if (width !== canvas.width || height !== canvas.height) {
      width = canvas.width; height = canvas.height;
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
      if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('Chladni framebuffer is incomplete');
    }
    gl.viewport(0, 0, width, height);
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    gl.enable(gl.BLEND); gl.blendEquation(gl.MAX); gl.blendFunc(gl.ONE, gl.ONE);
    gl.useProgram(particles); gl.bindVertexArray(vao);
    gl.uniform2f(resolution, width, height); gl.uniform1f(dprUniform, lastDpr); gl.uniform1f(pulse, lastPulse);
    for (const [i, array] of attributes.entries()) {
      if (i === 2 && !grainDirty) continue;
      gl.bindBuffer(gl.ARRAY_BUFFER, buffers[i]!); gl.bufferSubData(gl.ARRAY_BUFFER, 0, array);
    }
    grainDirty = false;
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, count);
    gl.bindVertexArray(null); gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.disable(gl.BLEND);
    gl.useProgram(composite); gl.uniform3fv(colorUniform, colors);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
  function onLost(event: Event) { event.preventDefault(); lost = true; }
  function onRestored() {
    // Context loss already invalidated/freed the old GPU handles. Deleting them
    // against the restored context would itself produce INVALID_OPERATION.
    buffers = []; vao = null; texture = null; framebuffer = null; particles = null; composite = null;
    try { initialize(); lost = false; paint(); }
    catch (error) { lost = true; console.error('Unable to restore Chladni', error); }
  }
  initialize();
  canvas.addEventListener('webglcontextlost', onLost); canvas.addEventListener('webglcontextrestored', onRestored);
  return {
    draw(color, dpr, radialPulse) {
      if (lastColor !== color) {
        lastColor = color;
        for (const [i, shade] of [`color-mix(in srgb, ${color} 88%, #e6b98b)`, color, `color-mix(in srgb, ${color} 82%, #c4ddff)`].entries()) {
          colorContext.clearRect(0, 0, 1, 1); colorContext.fillStyle = shade; colorContext.fillRect(0, 0, 1, 1);
          const rgba = colorContext.getImageData(0, 0, 1, 1).data;
          for (let channel = 0; channel < 3; channel++) colors[i * 3 + channel] = rgba[channel]! / 255;
        }
      }
      lastDpr = dpr; lastPulse = radialPulse; paint();
    },
    reseed() { grainDirty = true; },
    dispose() {
      canvas.removeEventListener('webglcontextlost', onLost); canvas.removeEventListener('webglcontextrestored', onRestored); release();
    },
  };
}
