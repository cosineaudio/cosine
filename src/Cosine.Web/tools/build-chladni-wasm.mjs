// Requires clang with wasm32 support and wasm-ld. Run npm run build:chladni-wasm after kernel.c changes.
// Commit the regenerated kernel.wasm; normal frontend/Docker builds use it without a C toolchain.
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../src/visualizers/renderers/chladni/', import.meta.url));
execFileSync(process.env.CLANG ?? 'clang', [
  '--target=wasm32', '-O3', '-msimd128', '-ffp-contract=off', '-fno-builtin', '-nostdlib',
  '-Wl,--no-entry', '-Wl,--export-memory', '-Wl,--initial-memory=3145728', '-Wl,--max-memory=3145728',
  '-Wl,--strip-all', root+'kernel.c', '-o', root+'kernel.wasm',
], { stdio: 'inherit' });
