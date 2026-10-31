import { describe, it, expect } from 'vitest'
import { toAudioAnalysisProgress } from '../useSignalR'

/**
 * A hub payload is untrusted input in the one way that matters: the server may be a version
 * this bundle has never seen. These tests exist because that actually happened - the audio
 * progress event changed from two positional integers to a single object under the same name,
 * and every tab that had not reloaded read `undefined` for its counts and threw during render.
 * Nothing that comes out of here may carry an undefined number.
 */
describe('toAudioAnalysisProgress', () => {
  const valid = {
    phase: 'analysing',
    step: 'analysing audio',
    processed: 309,
    total: 7011,
    failed: 2,
    tracksPerMinute: 45.4,
    etaSeconds: 8820,
    currentTrack: 'Green Day – Basket Case',
  }

  it('passes a well-formed update through', () => {
    expect(toAudioAnalysisProgress(valid)).toEqual(valid)
  })

  it('rejects payloads that are not objects at all', () => {
    // What an old-format sender looks like from here: a bare number as the first argument.
    expect(toAudioAnalysisProgress(309)).toBeNull()
    expect(toAudioAnalysisProgress(null)).toBeNull()
    expect(toAudioAnalysisProgress(undefined)).toBeNull()
    expect(toAudioAnalysisProgress('analysing')).toBeNull()
  })

  it('never yields an undefined count, whatever is missing', () => {
    const result = toAudioAnalysisProgress({ phase: 'analysing' })!

    expect(result.processed).toBe(0)
    expect(result.total).toBe(0)
    expect(result.failed).toBe(0)
    expect(result.tracksPerMinute).toBe(0)

    // The property access the crash happened on. It must be safe for every field the template
    // formats, not merely present on the happy path.
    expect(() => result.total.toLocaleString()).not.toThrow()
    expect(() => result.processed.toLocaleString()).not.toThrow()
    expect(() => result.failed.toLocaleString()).not.toThrow()
  })

  it('replaces non-finite and wrongly typed counts rather than propagating them', () => {
    const result = toAudioAnalysisProgress({
      ...valid,
      processed: NaN,
      total: '7011',
      failed: null,
      tracksPerMinute: Infinity,
    })!

    expect(result.processed).toBe(0)
    expect(result.total).toBe(0)
    expect(result.failed).toBe(0)
    expect(result.tracksPerMinute).toBe(0)
  })

  it('keeps the optional fields nullable so the template can branch on them', () => {
    const result = toAudioAnalysisProgress({ ...valid, etaSeconds: null, currentTrack: null })!

    expect(result.etaSeconds).toBeNull()
    expect(result.currentTrack).toBeNull()
  })

  it('falls back to the analysing phase rather than an unrenderable one', () => {
    expect(toAudioAnalysisProgress({ ...valid, phase: 'something-new' })!.phase).toBe('analysing')
    expect(toAudioAnalysisProgress({ ...valid, phase: 'normalizing' })!.phase).toBe('normalizing')
  })

  it('always has a step to show, so the panel is never blank while working', () => {
    expect(toAudioAnalysisProgress({ ...valid, step: '' })!.step).toBe('working')
    expect(toAudioAnalysisProgress({ ...valid, step: undefined })!.step).toBe('working')
  })
})
