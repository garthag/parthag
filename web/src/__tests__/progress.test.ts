import { describe, expect, it } from 'vitest'
import { computeProgress, linearPlannedPct } from '../lib/calc/progress'
import { amount, sum, formatINR } from '../lib/calc/money'
import { closingStock, stockWarnings } from '../lib/calc/stock'

describe('DPR progress arithmetic', () => {
  it('computes cumulative, balance and % (quantity method)', () => {
    const r = computeProgress({ approvedTotal: 100, previousCumulative: 10, today: 15, plannedPct: 30 })
    expect(r).toMatchObject({ cumulative: 25, balance: 75, actualPct: 25, variancePct: -5, overrun: false })
    expect(r.warnings).toEqual([])
  })
  it('never divides by zero for zero approved quantity', () => {
    const r = computeProgress({ approvedTotal: 0, previousCumulative: 0, today: 0 })
    expect(r.actualPct).toBeNull()
    expect(r.overrun).toBe(false)
    const r2 = computeProgress({ approvedTotal: 0, previousCumulative: 0, today: 3 })
    expect(r2.actualPct).toBeNull()
    expect(r2.overrun).toBe(true)
    expect(r2.warnings.join(' ')).toMatch(/zero/)
  })
  it('warns on overrun unless authorised', () => {
    expect(computeProgress({ approvedTotal: 100, previousCumulative: 95, today: 10 }).warnings.join()).toMatch(/exceeds approved/)
    expect(computeProgress({ approvedTotal: 100, previousCumulative: 95, today: 10, overrunAuthorized: true }).warnings).toEqual([])
  })
  it('supports the manual progress method', () => {
    expect(computeProgress({ approvedTotal: 0, previousCumulative: 0, today: 0, method: 'manual', manualPct: 40, plannedPct: 50 }))
      .toMatchObject({ actualPct: 40, variancePct: -10 })
  })
  it('is decimal safe', () => {
    expect(computeProgress({ approvedTotal: '0.3', previousCumulative: '0.1', today: '0.2' }).balance).toBe(0)
  })
  it('linear planned %', () => {
    expect(linearPlannedPct('2026-10-01', '2026-10-31', '2026-10-10')).toBeCloseTo(32.258, 3)
    expect(linearPlannedPct('2026-10-01', '2026-10-31', '2026-09-01')).toBe(0)
    expect(linearPlannedPct('2026-10-01', '2026-10-31', '2026-11-30')).toBe(100)
    expect(linearPlannedPct(null, '2026-10-31', '2026-11-30')).toBeNull()
  })
})

describe('money', () => {
  it('multiplies exactly and rounds half-up', () => {
    expect(amount(120, '18250.75')).toBe('2190090.00')
    expect(amount('0.1', '0.2', 2)).toBe('0.02')
    expect(amount('1.005', 1, 2)).toBe('1.01')
    expect(sum(['0.1', '0.2'])).toBe('0.30')
  })
  it('formats rupees with Indian grouping', () => {
    expect(formatINR(2190090)).toBe('₹21,90,090.00')
  })
})

describe('store ledger', () => {
  it('closing = opening + received + returned − issued − rejected', () => {
    expect(closingStock({ opening: 100, received: 50, rejected: 5, issued: 60, consumed: 55, returned: 2 })).toBe(87)
  })
  it('flags issues beyond stock and impossible rejections', () => {
    const w = stockWarnings({ opening: 10, received: 5, rejected: 8, issued: 20, consumed: 0, returned: 0 })
    expect(w.join(' ')).toMatch(/Rejected quantity cannot exceed/)
    expect(w.join(' ')).toMatch(/exceeds available stock/)
  })
})
