import { addDays, daysBetween, toTime } from './calendar'

/**
 * Planned vs actual S-curves (weighted physical progress, 0–100 %).
 * Planned: each activity's weight spread linearly over its baseline dates.
 * Actual: weight × min(accepted cumulative / approved quantity, 1).
 */
export interface CurveActivity {
  id: string
  weight: number
  start: string | null // baseline (or planned) start
  finish: string | null
  totalQty: number
}

export interface ProgressPoint {
  activityId: string
  date: string
  qty: number // accepted quantity reported on that date
}

export interface CurvePoint {
  date: string
  planned: number | null
  actual: number | null
}

export function sCurve(acts: CurveActivity[], progress: ProgressPoint[], from: string, to: string, actualUntil?: string): CurvePoint[] {
  const measurable = acts.filter((a) => a.weight > 0)
  const totalWeight = measurable.reduce((s, a) => s + a.weight, 0)
  if (!totalWeight || toTime(to) < toTime(from)) return []
  const byAct = new Map<string, ProgressPoint[]>()
  for (const p of progress) {
    if (!byAct.has(p.activityId)) byAct.set(p.activityId, [])
    byAct.get(p.activityId)!.push(p)
  }
  const out: CurvePoint[] = []
  const n = daysBetween(from, to)
  const step = Math.max(1, Math.ceil(n / 400)) // keep charts light
  for (let i = 0; i <= n; i += step) {
    const day = addDays(from, i)
    let planned = 0, actual = 0
    for (const a of measurable) {
      if (a.start && a.finish) {
        const span = daysBetween(a.start, a.finish) + 1
        const done = Math.min(Math.max(daysBetween(a.start, day) + 1, 0), span)
        planned += (a.weight * done) / span
      }
      if (a.totalQty > 0) {
        const cum = (byAct.get(a.id) ?? []).filter((p) => toTime(p.date) <= toTime(day)).reduce((s, p) => s + p.qty, 0)
        actual += a.weight * Math.min(cum / a.totalQty, 1)
      }
    }
    const showActual = !actualUntil || toTime(day) <= toTime(actualUntil)
    out.push({
      date: day,
      planned: Math.round((planned * 10000) / totalWeight) / 100,
      actual: showActual ? Math.round((actual * 10000) / totalWeight) / 100 : null,
    })
  }
  return out
}
