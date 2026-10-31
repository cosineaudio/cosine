import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { effectScope, nextTick, ref, type EffectScope } from 'vue'
import { useDeferredLoading } from '../useDeferredLoading'

const DELAY = 220
const MIN_VISIBLE = 400

let scope: EffectScope

/**
 * The composable registers an `onScopeDispose` cleanup, which only has somewhere
 * to go inside an effect scope - the same guarantee a component's setup gives it.
 */
function inScope<T>(fn: () => T): T {
  scope = effectScope()
  return scope.run(fn)!
}

/** `performance.now()` is read directly, so it has to move with the fake clock. */
function advance(ms: number): Promise<void> {
  vi.advanceTimersByTime(ms)
  return nextTick()
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  scope?.stop()
  vi.useRealTimers()
})

describe('useDeferredLoading', () => {
  it('stays hidden for a load that resolves inside the delay', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading))

    await advance(DELAY - 20)
    loading.value = false
    await advance(1000)

    expect(visible.value).toBe(false)
  })

  it('shows once the load outlives the delay', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading))

    expect(visible.value).toBe(false)

    await advance(DELAY + 1)

    expect(visible.value).toBe(true)
  })

  it('holds for the minimum once shown, however soon the data lands', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading))

    await advance(DELAY + 1)
    // Resolves one tick after the skeleton appeared: without the floor this
    // would be a single-frame blink, which is worse than never showing it.
    loading.value = false
    await advance(MIN_VISIBLE - 50)

    expect(visible.value).toBe(true)

    await advance(100)

    expect(visible.value).toBe(false)
  })

  it('hides immediately when the minimum has already elapsed', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading))

    await advance(DELAY + MIN_VISIBLE + 100)
    loading.value = false
    await nextTick()

    expect(visible.value).toBe(false)
  })

  it('keeps a visible placeholder up across a second load rather than restarting the delay', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading))

    await advance(DELAY + 1)
    expect(visible.value).toBe(true)

    // A route param change: settles and immediately starts loading again.
    loading.value = false
    await nextTick()
    loading.value = true
    await advance(DELAY + 1)

    expect(visible.value).toBe(true)
  })

  it('honours custom delay and minimum', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading, { delay: 50, minVisible: 100 }))

    await advance(51)
    expect(visible.value).toBe(true)

    loading.value = false
    await advance(60)
    expect(visible.value).toBe(true)

    await advance(50)
    expect(visible.value).toBe(false)
  })

  it('never shows for a source that was already settled', async () => {
    const loading = ref(false)
    const visible = inScope(() => useDeferredLoading(loading))

    await advance(2000)

    expect(visible.value).toBe(false)
  })

  it('drops its pending timer when the scope is stopped', async () => {
    const loading = ref(true)
    const visible = inScope(() => useDeferredLoading(loading))

    scope.stop()
    await advance(DELAY + 100)

    expect(visible.value).toBe(false)
  })
})
