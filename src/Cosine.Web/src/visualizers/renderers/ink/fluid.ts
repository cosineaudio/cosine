import { vertex, shaders } from './shaders';

type PassName = keyof typeof shaders;
interface Target { texture: WebGLTexture; framebuffer: WebGLFramebuffer }
interface Pair { read: Target; write: Target }
const uniformNames = ['field', 'velocity', 'auxiliary', 'texel', 'dt', 'emitters', 'directions', 'splats', 'seed', 'tint'] as const;
type UniformName = typeof uniformNames[number];
interface Pass { program: WebGLProgram; uniforms: Record<UniformName, WebGLUniformLocation | null> }

export function createFluid(canvas: HTMLCanvasElement) {
  const context = canvas.getContext('webgl2', { alpha: false, antialias: false, depth: false, stencil: false });
  if (!context) throw new Error('Ink requires WebGL 2');
  const gl = context;
  const passes = new Map<PassName, Pass>();
  const targets: Target[] = [];
  let velocity: Pair;
  let pigment: Pair;
  let pressure: Pair;
  let divergence: Target;
  let width = 1;
  let height = 1;
  let lost = false;
  let needsSeed = true;
  let emitters = new Float32Array(24);
  let directions = new Float32Array(12);
  let splats = new Float32Array(12);
  let tint = new Float32Array([0.35, 0.55, 0.65]);

  function compile(type: number, source: string) {
    const shader = gl.createShader(type);
    if (!shader) throw new Error('Unable to allocate Ink shader');
    gl.shaderSource(shader, source); gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const message = gl.getShaderInfoLog(shader);
      gl.deleteShader(shader); throw new Error(message ?? 'Ink shader compilation failed');
    }
    return shader;
  }
  function makePass(name: PassName) {
    let vs: WebGLShader | null = null;
    let fs: WebGLShader | null = null;
    let program: WebGLProgram | null = null;
    try {
      vs = compile(gl.VERTEX_SHADER, vertex); fs = compile(gl.FRAGMENT_SHADER, shaders[name]);
      program = gl.createProgram();
      if (!program) throw new Error('Unable to allocate Ink program');
      gl.attachShader(program, vs); gl.attachShader(program, fs); gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'Ink linking failed');
      const uniforms = {} as Pass['uniforms'];
      for (const key of uniformNames) uniforms[key] = gl.getUniformLocation(program, key === 'emitters' || key === 'directions' || key === 'splats' ? `${key}[0]` : key);
      gl.useProgram(program);
      gl.uniform1i(uniforms.field, 0); gl.uniform1i(uniforms.velocity, 1); gl.uniform1i(uniforms.auxiliary, 2);
      passes.set(name, { program, uniforms });
    } catch (error) { if (program) gl.deleteProgram(program); throw error; }
    finally { if (vs) gl.deleteShader(vs); if (fs) gl.deleteShader(fs); }
  }
  function makeTarget(): Target {
    const texture = gl.createTexture(); const framebuffer = gl.createFramebuffer();
    if (!texture || !framebuffer) {
      if (texture) gl.deleteTexture(texture);
      if (framebuffer) gl.deleteFramebuffer(framebuffer);
      throw new Error('Unable to allocate Ink simulation grid');
    }
    const target = { texture, framebuffer }; targets.push(target);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, width, height, 0, gl.RGBA, gl.HALF_FLOAT, null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('Ink simulation grid is incomplete');
    gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
    return target;
  }
  function clearTargets() {
    for (const target of targets) { gl.deleteTexture(target.texture); gl.deleteFramebuffer(target.framebuffer); }
    targets.length = 0;
  }
  function allocate() {
    clearTargets();
    velocity = { read: makeTarget(), write: makeTarget() };
    pigment = { read: makeTarget(), write: makeTarget() };
    pressure = { read: makeTarget(), write: makeTarget() };
    divergence = makeTarget();
    needsSeed = true;
  }
  function release() {
    clearTargets();
    for (const pass of passes.values()) gl.deleteProgram(pass.program);
    passes.clear();
  }
  function initialize() {
    if (!gl.getExtension('EXT_color_buffer_float')) throw new Error('Ink requires floating-point render targets');
    for (const name of Object.keys(shaders) as PassName[]) makePass(name);
    allocate();
  }
  function swap(pair: Pair) { const old = pair.read; pair.read = pair.write; pair.write = old; }
  function run(name: PassName, output: Target | null, field: Target, dt: number, velocityInput?: Target, auxiliary?: Target, seed = 0) {
    const pass = passes.get(name)!;
    const u = pass.uniforms;
    gl.bindFramebuffer(gl.FRAMEBUFFER, output?.framebuffer ?? null);
    gl.viewport(0, 0, output ? width : canvas.width, output ? height : canvas.height);
    gl.useProgram(pass.program);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, field.texture);
    gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, velocityInput?.texture ?? null);
    gl.activeTexture(gl.TEXTURE2); gl.bindTexture(gl.TEXTURE_2D, auxiliary?.texture ?? null);
    if (u.texel) gl.uniform2f(u.texel, 1 / width, 1 / height);
    if (u.dt) gl.uniform1f(u.dt, dt);
    if (u.emitters) gl.uniform4fv(u.emitters, emitters);
    if (u.directions) gl.uniform2fv(u.directions, directions);
    if (u.splats) gl.uniform4fv(u.splats, splats);
    if (u.tint) gl.uniform3fv(u.tint, tint);
    if (u.seed) gl.uniform1f(u.seed, seed);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
  function seed() {
    if (!needsSeed) return;
    run('pigment', pigment.write, pigment.read, 0, velocity.read, undefined, 1);
    swap(pigment); needsSeed = false;
  }
  function paint() {
    if (lost) return;
    seed(); run('display', null, pigment.read, 0);
  }
  function onLost(event: Event) { event.preventDefault(); lost = true; }
  function onRestored() {
    targets.length = 0; passes.clear();
    try { initialize(); lost = false; paint(); }
    catch (error) { release(); console.error('Unable to restore Ink', error); }
  }
  try { initialize(); } catch (error) { release(); throw error; }
  canvas.addEventListener('webglcontextlost', onLost); canvas.addEventListener('webglcontextrestored', onRestored);
  return {
    resize(w: number, h: number) {
      const longest = Math.max(w, h);
      const nextWidth = Math.max(32, Math.round(256 * w / longest));
      const nextHeight = Math.max(32, Math.round(256 * h / longest));
      if (width === nextWidth && height === nextHeight) return;
      width = nextWidth; height = nextHeight;
      if (!lost) allocate();
    },
    step(dt: number, nextEmitters: Float32Array<ArrayBuffer>, nextDirections: Float32Array<ArrayBuffer>, nextSplats: Float32Array<ArrayBuffer>) {
      emitters = nextEmitters; directions = nextDirections; splats = nextSplats;
      if (lost) return;
      seed();
      run('advect', velocity.write, velocity.read, dt, velocity.read, pigment.read); swap(velocity);
      run('divergence', divergence, velocity.read, dt);
      run('curl', velocity.write, velocity.read, dt, undefined, divergence); swap(velocity);
      run('divergence', divergence, velocity.read, dt);
      gl.bindFramebuffer(gl.FRAMEBUFFER, pressure.read.framebuffer);
      gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
      for (let i = 0; i < 14; i++) {
        run('pressure', pressure.write, pressure.read, dt, undefined, divergence); swap(pressure);
      }
      run('project', velocity.write, velocity.read, dt, undefined, pressure.read); swap(velocity);
      run('pigment', pigment.write, pigment.read, dt, velocity.read); swap(pigment);
    },
    draw(color: Float32Array<ArrayBuffer>, nextSplats: Float32Array<ArrayBuffer>) {
      tint = color; splats = nextSplats; paint();
    },
    dispose() {
      canvas.removeEventListener('webglcontextlost', onLost); canvas.removeEventListener('webglcontextrestored', onRestored);
      release();
    },
  };
}
