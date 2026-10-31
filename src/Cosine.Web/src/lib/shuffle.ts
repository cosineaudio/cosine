/**
 * Fisher-Yates, returning a new array.
 *
 * The obvious `[...items].sort(() => Math.random() - 0.5)` is not a shuffle: a comparator that
 * answers differently every time it is asked violates what sort assumes, so the result is
 * biased towards the input order - visibly so in V8, where short arrays take an insertion-sort
 * path that barely moves anything. On a shuffled album that reads as "it played the tracks
 * almost in order again".
 */
export function shuffled<T>(items: readonly T[]): T[] {
  const out = [...items];
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}
