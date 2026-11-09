import { describe, expect, it } from 'vitest'
import { schedule, ScheduleError, type CpmActivity, type CpmDependency } from '../lib/schedule/cpm'
import { WorkCalendar } from '../lib/schedule/calendar'
import { sCurve } from '../lib/schedule/scurve'

const MON_FRI = { workDays: [1, 2, 3, 4, 5], holidays: [] as string[] }
const act = (id: string, duration: number, extra: Partial<CpmActivity> = {}): CpmActivity => ({ id, code: id, duration, ...extra })
const dep = (p: string, s: string, type: CpmDependency['type'] = 'FS', lag = 0): CpmDependency => ({ predecessorId: p, successorId: s, type, lag })

describe('working calendar', () => {
  it('skips weekends and holidays', () => {
    const cal = new WorkCalendar({ workDays: [1, 2, 3, 4, 5], holidays: ['2026-10-02'] }, '2026-09-28') // Mon
    expect(cal.dateAt(0)).toBe('2026-09-28')
    expect(cal.dateAt(4)).toBe('2026-10-05') // Thu 1st → (Fri 2nd holiday) → Mon 5th
    expect(cal.indexOf('2026-10-03')).toBe(4) // Saturday snaps to Monday
    expect(cal.dateAt(-1)).toBe('2026-09-25')
  })
})

describe('CPM engine', () => {
  // Classic network:  A(3) → B(2) → D(1);  A → C(4) → D
  const acts = [act('A', 3), act('B', 2), act('C', 4), act('D', 1)]
  const deps = [dep('A', 'B'), dep('A', 'C'), dep('B', 'D'), dep('C', 'D')]

  it('forward/backward pass, floats and critical path', () => {
    const r = schedule(acts, deps, { projectStart: '2026-10-05', calendar: MON_FRI }) // Monday
    const g = (id: string) => r.activities.get(id)!
    expect(g('A')).toMatchObject({ earlyStart: '2026-10-05', earlyFinish: '2026-10-07', totalFloat: 0, critical: true })
    expect(g('C')).toMatchObject({ earlyStart: '2026-10-08', earlyFinish: '2026-10-13', totalFloat: 0, critical: true })
    expect(g('B')).toMatchObject({ earlyStart: '2026-10-08', earlyFinish: '2026-10-09', totalFloat: 2, freeFloat: 2, critical: false })
    expect(g('D')).toMatchObject({ earlyStart: '2026-10-14', earlyFinish: '2026-10-14', critical: true })
    expect(r.projectFinish).toBe('2026-10-14')
    expect(r.criticalPath).toEqual(['A', 'C', 'D'])
  })

  it('handles SS, FF, SF relationships and lags/leads', () => {
    const r = schedule(
      [act('A', 10), act('B', 5), act('C', 4), act('D', 2)],
      [dep('A', 'B', 'SS', 3), dep('A', 'C', 'FF', 2), dep('A', 'D', 'SF', 1)],
      { projectStart: '2026-10-05', calendar: MON_FRI },
    )
    const s = (id: string) => r.activities.get(id)!
    expect(s('B').earlyStart).toBe('2026-10-08') // A start + 3 workdays
    expect(s('C').earlyFinish).toBe('2026-10-20') // A finish (16th) + 2 workdays
    // F(D) ≥ S(A)+1 would allow an earlier start, but nothing starts before the project start
    expect(s('D').earlyFinish).toBe('2026-10-06')
    const lead = schedule([act('A', 5), act('B', 3)], [dep('A', 'B', 'FS', -2)], { projectStart: '2026-10-05', calendar: MON_FRI })
    expect(lead.activities.get('B')!.earlyStart).toBe('2026-10-08') // 2-day lead
  })

  it('detects circular logic', () => {
    expect(() => schedule([act('A', 1), act('B', 1)], [dep('A', 'B'), dep('B', 'A')], { projectStart: '2026-10-05' }))
      .toThrow(ScheduleError)
  })

  it('respects actuals and the data date (delay propagates)', () => {
    const r = schedule(
      [act('A', 5, { actualStart: '2026-10-05' }), act('B', 3)],
      [dep('A', 'B')],
      { projectStart: '2026-10-05', dataDate: '2026-10-14', calendar: MON_FRI },
    )
    // A started on time but is still running on the data date: 5-day job, 7 days elapsed → at least 1 day left
    expect(r.activities.get('A')!.earlyFinish).toBe('2026-10-14')
    expect(r.activities.get('B')!.earlyStart).toBe('2026-10-15')
    expect(r.activities.get('A')!.status).toBe('in_progress')
  })

  it('milestones have zero duration and constraints apply', () => {
    const r = schedule([act('A', 5), act('M', 0, { isMilestone: true }), act('X', 2, { constraintType: 'SNET', constraintDate: '2026-10-20' })],
      [dep('A', 'M')], { projectStart: '2026-10-05', calendar: MON_FRI })
    expect(r.activities.get('M')!.earlyStart).toBe('2026-10-12')
    expect(r.activities.get('M')!.earlyFinish).toBe('2026-10-12')
    expect(r.activities.get('X')!.earlyStart).toBe('2026-10-20')
  })
})

describe('S-curve', () => {
  it('planned reaches 100 % at the last finish; actual follows accepted quantities', () => {
    const pts = sCurve(
      [{ id: 'a', weight: 1, start: '2026-10-01', finish: '2026-10-10', totalQty: 100 },
       { id: 'b', weight: 3, start: '2026-10-06', finish: '2026-10-15', totalQty: 50 }],
      [{ activityId: 'a', date: '2026-10-02', qty: 50 }, { activityId: 'b', date: '2026-10-07', qty: 25 }],
      '2026-10-01', '2026-10-15', '2026-10-08')
    expect(pts[0].planned).toBe(2.5)
    expect(pts.at(-1)!.planned).toBe(100)
    expect(pts.find((p) => p.date === '2026-10-08')!.actual).toBe(50) // (1×0.5 + 3×0.5)/4
    expect(pts.at(-1)!.actual).toBeNull() // no actuals after the data date
  })
})
