import { WorkCalendar, type CalendarDef, DEFAULT_CALENDAR } from './calendar'

/**
 * Critical Path Method scheduling engine (P6-style).
 *
 * Time is measured in working-day indices on a WorkCalendar. An activity
 * occupying [S, F) starts on day S and finishes at the end of day F-1, so a
 * 5-day task starting Monday finishes Friday. Milestones have zero duration.
 *
 * Relationship rules (lag in working days, may be negative = lead):
 *   FS: S(succ) ≥ F(pred) + lag      SS: S(succ) ≥ S(pred) + lag
 *   FF: F(succ) ≥ F(pred) + lag      SF: F(succ) ≥ S(pred) + lag
 *
 * Progress: activities with an actual start are fixed at that start; with an
 * actual finish they are fixed completely. In-progress work is forecast from
 * the data date with the remaining duration. Unstarted work cannot be
 * scheduled before the data date.
 */
export type RelType = 'FS' | 'SS' | 'FF' | 'SF'

export interface CpmActivity {
  id: string
  code: string
  duration: number
  isMilestone?: boolean
  actualStart?: string | null
  actualFinish?: string | null
  remainingDuration?: number | null
  constraintType?: 'SNET' | 'FNLT' | 'MSO' | 'MFO' | null
  constraintDate?: string | null
}

export interface CpmDependency {
  predecessorId: string
  successorId: string
  type: RelType
  lag: number
}

export interface CpmResult {
  id: string
  earlyStart: string
  earlyFinish: string
  lateStart: string
  lateFinish: string
  totalFloat: number
  freeFloat: number
  critical: boolean
  status: 'not_started' | 'in_progress' | 'completed'
}

export interface CpmOutput {
  activities: Map<string, CpmResult>
  projectFinish: string
  criticalPath: string[] // ids, in schedule order
}

export class ScheduleError extends Error {}

export function schedule(
  activities: CpmActivity[],
  deps: CpmDependency[],
  opts: { projectStart: string; dataDate?: string | null; calendar?: CalendarDef },
): CpmOutput {
  const cal = new WorkCalendar(opts.calendar ?? DEFAULT_CALENDAR, opts.projectStart)
  const byId = new Map(activities.map((a) => [a.id, a]))
  const preds = new Map<string, CpmDependency[]>()
  const succs = new Map<string, CpmDependency[]>()
  for (const a of activities) { preds.set(a.id, []); succs.set(a.id, []) }
  for (const d of deps) {
    if (!byId.has(d.predecessorId) || !byId.has(d.successorId)) continue
    if (d.predecessorId === d.successorId) throw new ScheduleError('An activity cannot depend on itself')
    preds.get(d.successorId)!.push(d)
    succs.get(d.predecessorId)!.push(d)
  }

  // Topological order (Kahn). Anything left over is part of a loop.
  const indeg = new Map(activities.map((a) => [a.id, preds.get(a.id)!.length]))
  const queue = activities.filter((a) => indeg.get(a.id) === 0).map((a) => a.id)
  const order: string[] = []
  while (queue.length) {
    const id = queue.shift()!
    order.push(id)
    for (const d of succs.get(id)!) {
      indeg.set(d.successorId, indeg.get(d.successorId)! - 1)
      if (indeg.get(d.successorId) === 0) queue.push(d.successorId)
    }
  }
  if (order.length !== activities.length) {
    const loop = activities.filter((a) => !order.includes(a.id)).map((a) => a.code)
    throw new ScheduleError(`Circular dependency between: ${loop.join(', ')}`)
  }

  const dur = (a: CpmActivity) => (a.isMilestone ? 0 : Math.max(0, Math.round(a.duration)))
  const dataIdx = opts.dataDate ? cal.indexOf(opts.dataDate) : null
  const ES = new Map<string, number>(), EF = new Map<string, number>()

  for (const id of order) {
    const a = byId.get(id)!
    const d = dur(a)
    if (a.actualStart) {
      const s = cal.indexOf(a.actualStart)
      ES.set(id, s)
      if (a.actualFinish) {
        EF.set(id, Math.max(s + (d === 0 ? 0 : 1), cal.indexOf(a.actualFinish) + (d === 0 ? 0 : 1)))
      } else {
        const elapsed = dataIdx != null ? Math.max(0, dataIdx - s) : 0
        const remaining = a.remainingDuration ?? Math.max(d - elapsed, d === 0 ? 0 : 1)
        EF.set(id, Math.max(s + (d === 0 ? 0 : 1), (dataIdx ?? s) + remaining))
      }
      continue
    }
    let s = 0
    for (const p of preds.get(id)!) {
      const ps = ES.get(p.predecessorId)!, pf = EF.get(p.predecessorId)!
      const req =
        p.type === 'FS' ? pf + p.lag :
        p.type === 'SS' ? ps + p.lag :
        p.type === 'FF' ? pf + p.lag - d :
        ps + p.lag - d
      s = Math.max(s, req)
    }
    if (dataIdx != null) s = Math.max(s, dataIdx)
    if (a.constraintDate && (a.constraintType === 'SNET')) s = Math.max(s, cal.indexOf(a.constraintDate))
    if (a.constraintDate && a.constraintType === 'MSO') s = cal.indexOf(a.constraintDate)
    if (a.constraintDate && a.constraintType === 'MFO') s = cal.indexOf(a.constraintDate) + (d === 0 ? 0 : 1) - d
    ES.set(id, s)
    EF.set(id, s + d)
  }

  const finishIdx = Math.max(0, ...activities.map((a) => EF.get(a.id)!))
  const LS = new Map<string, number>(), LF = new Map<string, number>()
  for (const id of [...order].reverse()) {
    const a = byId.get(id)!
    const d = dur(a)
    const curDur = EF.get(id)! - ES.get(id)!
    let lf = finishIdx
    for (const s of succs.get(id)!) {
      const sls = LS.get(s.successorId)!, slf = LF.get(s.successorId)!
      const bound =
        s.type === 'FS' ? sls - s.lag :
        s.type === 'SS' ? sls - s.lag + curDur :
        s.type === 'FF' ? slf - s.lag :
        slf - s.lag + curDur
      lf = Math.min(lf, bound)
    }
    if (a.constraintDate && (a.constraintType === 'FNLT' || a.constraintType === 'MFO')) {
      lf = Math.min(lf, cal.indexOf(a.constraintDate) + (d === 0 ? 0 : 1))
    }
    LF.set(id, lf)
    LS.set(id, lf - curDur)
  }

  const results = new Map<string, CpmResult>()
  for (const a of activities) {
    const es = ES.get(a.id)!, ef = EF.get(a.id)!, ls = LS.get(a.id)!, lf = LF.get(a.id)!
    let free = finishIdx - ef
    for (const s of succs.get(a.id)!) {
      const ses = ES.get(s.successorId)!, sef = EF.get(s.successorId)!
      const slack =
        s.type === 'FS' ? ses - (ef + s.lag) :
        s.type === 'SS' ? ses - (es + s.lag) :
        s.type === 'FF' ? sef - (ef + s.lag) :
        sef - (es + s.lag)
      free = Math.min(free, slack)
    }
    const completed = !!a.actualFinish
    const tf = lf - ef
    const toDate = (start: number, finish: number) => ({
      s: cal.dateAt(start),
      f: finish > start ? cal.dateAt(finish - 1) : cal.dateAt(start),
    })
    const early = toDate(es, ef), late = toDate(ls, lf)
    results.set(a.id, {
      id: a.id,
      earlyStart: early.s, earlyFinish: early.f,
      lateStart: late.s, lateFinish: late.f,
      totalFloat: completed ? 0 : tf,
      freeFloat: completed ? 0 : Math.max(0, free),
      critical: !completed && tf <= 0,
      status: completed ? 'completed' : a.actualStart ? 'in_progress' : 'not_started',
    })
  }
  const criticalPath = order.filter((id) => results.get(id)!.critical)
  return { activities: results, projectFinish: finishIdx > 0 ? cal.dateAt(finishIdx - 1) : cal.dateAt(0), criticalPath }
}
