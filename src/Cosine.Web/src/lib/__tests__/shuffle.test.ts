import { describe, it, expect, vi, afterEach } from 'vitest'
import { shuffled } from '../shuffle'

afterEach(() => {
  vi.restoreAllMocks()
})

describe('shuffled', () => {
  it('returns a permutation of the input', () => {
    const input = Array.from({ length: 50 }, (_, i) => i)

    const out = shuffled(input)

    expect(out).toHaveLength(input.length)
    expect([...out].sort((a, b) => a - b)).toEqual(input)
  })

  it('does not mutate the input', () => {
    const input = [1, 2, 3, 4, 5]

    shuffled(input)

    expect(input).toEqual([1, 2, 3, 4, 5])
  })

  it('walks the whole array', () => {
    // Math.random() === 0 makes every j land on 0, so each pass swaps the current tail element
    // with the head: d->front, then c->front, then b->front, leaving 'a' pushed to the end.
    // A loop that skipped an index would not produce this exact sequence.
    vi.spyOn(Math, 'random').mockReturnValue(0)

    expect(shuffled(['a', 'b', 'c', 'd'])).toEqual(['b', 'c', 'd', 'a'])
  })

  it('can move the first element, which a sort-comparator shuffle rarely does', () => {
    const input = Array.from({ length: 8 }, (_, i) => i)

    // 200 runs of an 8-element array: P(element 0 never moves) is (1/8)^200.
    const moved = Array.from({ length: 200 }, () => shuffled(input)).some((out) => out[0] !== 0)

    expect(moved).toBe(true)
  })

  it('handles empty and single-element inputs', () => {
    expect(shuffled([])).toEqual([])
    expect(shuffled([7])).toEqual([7])
  })
})
