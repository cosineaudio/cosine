# Visualizers

`registry.ts` is the picker’s source of truth. Add a definition there and a renderer
module exporting `create` (see `types.ts`). Remove the definition to remove an
option. Set `placement` to `full` for the entire viewport, or `floor` for the
original Spectrum placement and fade. Saved unknown IDs fall back to Spectrum. `off` is reserved for the picker.

The host lazy-loads only the selected module, owns the animation loop and analyser,
and reads only the audio buffers declared by the definition. Buffers are reused:
do not mutate or retain their contents without copying. `update(null, …)` means
paused; `settling` keeps frames running only until the effect has decayed. Honor
`reducedMotion` with a static frame. Use delta seconds for new time-based effects.

Each renderer owns its context and GPU resources. The host creates a fresh canvas
on selection changes, so Canvas 2D and WebGL renderers can coexist. `resize` receives
physical pixel dimensions and a capped device pixel ratio. `dispose` must release
all renderer resources; renderers must not create their own animation loops.

Spectrum retains the original math and frame-based smoothing intentionally. Its
regression fixture records original drawing commands during playback, pause, and
reduced motion at DPR 1 and 2. Visual approval belongs to the user; automated tests
check behavior and numerical compatibility only.
