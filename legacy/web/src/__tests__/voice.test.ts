import { describe, expect, it } from 'vitest'
import { parseVoiceReport } from '../lib/voice/parse'
import { wordsToNumber } from '../lib/voice/numbers'

const ACTS = [
  { id: '1', code: 'A2010', name: 'Erection — skip bridge structure', unit: 'MT', location: 'Skip bridge' },
  { id: '2', code: 'A1010', name: 'RCC foundations — skip bridge', unit: 'm³', location: 'Grid A–D' },
  { id: '3', code: 'A2000', name: 'Fabrication — skip bridge trusses', unit: 'MT', location: 'Fabrication yard' },
]

describe('spoken numbers', () => {
  it.each([
    [['twelve'], 12], [['twenty', 'four'], 24], [['one', 'hundred', 'and', 'five'], 105],
    [['two', 'point', 'five'], 2.5], [['1,250'], 1250], [['three', 'lakh'], 300000], [['bridge'], null],
  ])('%j → %s', (w, n) => expect(wordsToNumber(w as string[])).toBe(n))
})

describe('voice report parser', () => {
  it('parses the reference sentence into a structured proposal', () => {
    const p = parseVoiceReport(
      'At the skip bridge, erected twelve metric tonnes of structural steel during day shift using one mobile crane and twenty-four workers.',
      ACTS)
    expect(p.location?.value).toBe('skip bridge')
    expect(p.quantity).toEqual({ value: 12, confidence: 'high' })
    expect(p.unit?.value).toBe('MT')
    expect(p.shift?.value).toBe('day')
    expect(p.equipment).toEqual([{ count: 1, type: 'mobile crane' }])
    expect(p.workers?.value).toBe(24)
    expect(p.activity?.value.code).toBe('A2010')
    expect(p.uncertain).toContain('location') // always confirmed by the user
  })
  it('flags ambiguity instead of guessing', () => {
    const p = parseVoiceReport('Did some work, about 40 today', ACTS)
    expect(p.activity).toBeUndefined()
    expect(p.quantity?.confidence).toBe('low')
    expect(p.uncertain).toEqual(expect.arrayContaining(['activity', 'quantity', 'shift']))
  })
  it('handles digits, night shift and m³', () => {
    const p = parseVoiceReport('In grid C4, poured 32.5 cubic metres of RCC foundation in night shift with 18 masons', ACTS)
    expect(p.quantity?.value).toBe(32.5)
    expect(p.unit?.value).toBe('m³')
    expect(p.shift?.value).toBe('night')
    expect(p.workers?.value).toBe(18)
    expect(p.activity?.value.code).toBe('A1010')
  })
})
