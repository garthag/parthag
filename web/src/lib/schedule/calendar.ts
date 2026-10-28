/**
 * Working calendar. Dates are ISO strings (YYYY-MM-DD) handled in UTC so
 * results never shift with the device timezone.
 */
export interface CalendarDef {
  workDays: number[] // 0 = Sunday … 6 = Saturday
  holidays: string[]
}

export const DEFAULT_CALENDAR: CalendarDef = { workDays: [1, 2, 3, 4, 5, 6], holidays: [] }

const DAY = 86400000
export const toTime = (iso: string) => Date.parse(iso.slice(0, 10) + 'T00:00:00Z')
export const toIso = (t: number) => new Date(t).toISOString().slice(0, 10)
export const addDays = (iso: string, n: number) => toIso(toTime(iso) + n * DAY)
export const daysBetween = (a: string, b: string) => Math.round((toTime(b) - toTime(a)) / DAY)

export class WorkCalendar {
  private holidays: Set<string>
  private workDays: Set<number>
  private origin: string
  private forward: string[] = [] // index → date
  private backward: string[] = [] // index -(i+1) → date

  constructor(def: CalendarDef, origin: string) {
    if (!def.workDays.length) throw new Error('Calendar must have at least one working day')
    this.holidays = new Set(def.holidays.map((h) => h.slice(0, 10)))
    this.workDays = new Set(def.workDays)
    this.origin = this.nextWorkday(origin)
  }

  isWorkday(iso: string): boolean {
    return this.workDays.has(new Date(toTime(iso)).getUTCDay()) && !this.holidays.has(iso.slice(0, 10))
  }

  nextWorkday(iso: string): string {
    let d = iso.slice(0, 10)
    for (let i = 0; i < 3660 && !this.isWorkday(d); i++) d = addDays(d, 1)
    return d
  }

  /** Date of working-day index i (0 = first working day on/after origin). */
  dateAt(i: number): string {
    if (i >= 0) {
      if (!this.forward.length) this.forward.push(this.origin)
      while (this.forward.length <= i) {
        let d = addDays(this.forward[this.forward.length - 1], 1)
        while (!this.isWorkday(d)) d = addDays(d, 1)
        this.forward.push(d)
      }
      return this.forward[i]
    }
    const k = -i - 1
    while (this.backward.length <= k) {
      let d = addDays(this.backward.length ? this.backward[this.backward.length - 1] : this.origin, -1)
      while (!this.isWorkday(d)) d = addDays(d, -1)
      this.backward.push(d)
    }
    return this.backward[k]
  }

  /** Working-day index of a date; non-working dates snap forward to the next workday. */
  indexOf(iso: string): number {
    const target = this.nextWorkday(iso)
    if (toTime(target) >= toTime(this.origin)) {
      let i = 0
      while (this.dateAt(i) !== target) {
        if (toTime(this.dateAt(i)) > toTime(target)) return i
        i++
      }
      return i
    }
    let i = -1
    while (this.dateAt(i) !== target) {
      if (toTime(this.dateAt(i)) < toTime(target)) return i + 1
      i--
    }
    return i
  }
}
