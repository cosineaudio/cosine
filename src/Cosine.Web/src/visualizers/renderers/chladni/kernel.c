// Freestanding numerical kernel. Build with tools/build-chladni-wasm.mjs.
// Keep double intermediates / Float32 stores in sync with simulation.ts.
// Transcendentals import the same Math functions as JS, avoiding approximations.
#define COUNT 18000
#define GRID 97
#define MODES 16
#define FIELD (GRID * GRID * 3)
#define PI 3.141592653589793
#define EXPORT(name) __attribute__((export_name(name)))
#define MATH(name) __attribute__((import_module("math"), import_name(#name)))
MATH(sin) double js_sin(double);
MATH(cos) double js_cos(double);
MATH(exp) double js_exp(double);
MATH(atan2) double js_atan2(double, double);
MATH(hypot) double js_hypot(double, double);

typedef unsigned int u32;
static struct {
  float x[COUNT], y[COUNT], vx[COUNT], vy[COUNT], grain[COUNT], rest[COUNT], direction[COUNT];
  float weights[MODES];
  int order[MODES];
  float field[FIELD], surfaces[MODES][FIELD];
} state;
static u32 rng;
EXPORT("data") void *data(void) { return &state; }
EXPORT("set_seed") void set_seed(u32 seed) { rng = seed; }
EXPORT("random") double random_value(void) {
  rng += 0x6d2b79f5u;
  u32 v = (rng ^ (rng >> 15)) * (1u | rng);
  v ^= v + (v ^ (v >> 7)) * (61u | v);
  return (v ^ (v >> 14)) / 4294967296.0;
}
static double min(double a, double b) { return a < b ? a : b; }
static double max(double a, double b) { return a > b ? a : b; }
static double abs_value(double a) { return __builtin_fabs(a); }
static double sign(double a) { return a > 0 ? 1 : a < 0 ? -1 : 0; }
static void mix_field(double evolution) {
  for (int i = 0; i < FIELD; i++) state.field[i] = 0;
  int stage = (int)evolution;
  double fraction = evolution - stage;
  double blend = fraction * fraction * (3 - 2 * fraction);
  for (int band = 0; band < MODES; band++) {
    double weight = state.weights[band];
    if (weight < 0.0001) continue;
    const float *surface = state.surfaces[state.order[(band + stage * 5) % MODES]];
    const float *next = state.surfaces[state.order[(band + (stage + 1) * 5) % MODES]];
    for (int i = 0; i < FIELD; i++)
      state.field[i] = (double)state.field[i] + ((double)surface[i] * (1 - blend) + (double)next[i] * blend) * weight;
  }
}
static void advance(double dt, double drive, double scatter, double width, double phase) {
  double attraction = 1 - js_exp(-dt * (8 - width * 6));
  double drag = js_exp(-dt * (7 - width * 3));
  for (int i = 0; i < COUNT; i++) {
    double px = state.x[i], py = state.y[i];
    double grid_x = (px + 1) * (GRID - 1) * 0.5;
    double grid_y = (py + 1) * (GRID - 1) * 0.5;
    int col = (int)__builtin_floor(grid_x), row = (int)__builtin_floor(grid_y);
    double fx = grid_x - col, fy = grid_y - row;
    int offset = (row * GRID + col) * 3;
    double value = 0, gx = 0, gy = 0;
    for (int corner = 0; corner < 4; corner++) {
      int right = corner & 1, bottom = corner >> 1;
      double weight = (right ? fx : 1 - fx) * (bottom ? fy : 1 - fy);
      int index = offset + right * 3 + bottom * GRID * 3;
      value += state.field[index] * weight;
      gx += state.field[index + 1] * weight;
      gy += state.field[index + 2] * weight;
    }
    double gradient = __builtin_sqrt(gx * gx + gy * gy + 0.3);
    if (scatter > 0) {
      double angle = random_value() * PI * 2;
      double impulse = scatter * (0.18 + random_value() * 0.32);
      state.vx[i] = (double)state.vx[i] + js_cos(angle) * impulse;
      state.vy[i] = (double)state.vy[i] + js_sin(angle) * impulse;
      state.rest[i] = random_value() - 0.5;
    }
    double rest_distance = state.rest[i] * (0.032 + width * 0.09);
    if (drive != 0) rest_distance += drive * 0.004 * js_sin(phase + state.grain[i] * PI * 2);
    double distance = value / gradient - rest_distance;
    double step = max(-0.035, min(0.035, distance)) * attraction / gradient;
    state.vx[i] = state.vx[i] * drag;
    state.vy[i] = state.vy[i] * drag;
    double flow = drive * (0.035 + state.grain[i] * 0.035) * dt * state.direction[i] / gradient;
    double next_x = px - gx * step - gy * flow + state.vx[i] * dt;
    double next_y = py - gy * step + gx * flow + state.vy[i] * dt;
    double bounce_x = 0, bounce_y = 0;
    if (abs_value(next_x) > 0.975) {
      next_x = sign(next_x) * (1.95 - abs_value(next_x)); bounce_x = sign(next_x);
    }
    if (abs_value(next_y) > 0.975) {
      next_y = sign(next_y) * (1.95 - abs_value(next_y)); bounce_y = sign(next_y);
    }
    if (bounce_x || bounce_y) {
      state.direction[i] *= -1;
      double speed = max(0.025 + drive * 0.05, js_hypot(state.vx[i], state.vy[i]) * 0.65);
      double spread = bounce_x && bounce_y ? 0.45 : 0.9;
      double angle = js_atan2(-bounce_y, -bounce_x) + (random_value() - 0.5) * spread * 2;
      state.vx[i] = js_cos(angle) * speed; state.vy[i] = js_sin(angle) * speed;
    }
    state.x[i] = next_x; state.y[i] = next_y;
  }
}
EXPORT("seed") void seed(void) {
  for (int i = 0; i < COUNT; i++) {
    state.x[i] = random_value() * 1.96 - 0.98;
    state.y[i] = random_value() * 1.96 - 0.98;
    state.grain[i] = random_value(); state.rest[i] = random_value() - 0.5;
    state.direction[i] = i % 2 ? 1 : -1;
    state.vx[i] = 0; state.vy[i] = 0;
  }
  for (int i = 0; i < 28; i++) advance(1.0 / 60, 0, 0, 0, 0);
}
EXPORT("step") void step(double evolution, double dt, double drive, double scatter, double width, double phase, int mix, int moving) {
  if (mix) mix_field(evolution);
  if (moving) advance(dt, drive, scatter, width, phase);
}
