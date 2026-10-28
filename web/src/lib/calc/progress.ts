import Decimal from 'decimal.js'

/**
 * Quantity / progress arithmetic for one DPR activity line.
 * Mirrors the authoritative server calculation (refresh_dpr_entries) so the
 * form can show results instantly — the server recomputes on save.
 *
 *   cumulative = previous cumulative (accepted) + today's quantity
 *   balance    = approved total quantity − cumulative
 *   progress % = cumulative / approved total × 100   (quantity method)
 */
export interface ProgressInput {
  approvedTotal: number | string
  previousCumulative: number | string
  today: number | string
  method?: 'quantity' | 'manual'
  manualPct?: number | null
  plannedPct?: number | null
  overrunAuthorized?: boolean
}

export interface ProgressResult {
  cumulative: number
  balance: number
  actualPct: number | null
  variancePct: number | null
  overrun: boolean
  warnings: string[]
}

const dec = (v: number | string | null | undefined) => {
  try {
    const d = new Decimal(v === '' || v == null ? 0 : v)
    return d.isFinite() ? d : new Decimal(0)
  } catch {
    return new Decimal(0)
  }
}

export function computeProgress(i: ProgressInput): ProgressResult {
  const total = dec(i.approvedTotal)
  const prev = dec(i.previousCumulative)
  const today = dec(i.today)
  const warnings: string[] = []
  if (today.isNegative()) warnings.push("Today's quantity cannot be negative.")
  const cum = prev.plus(today)
  const balance = total.minus(cum)
  let actualPct: Decimal | null = null
  if (i.method === 'manual') {
    if (i.manualPct == null) warnings.push('Manual progress method selected but no percentage entered.')
    else if (i.manualPct < 0 || i.manualPct > 100) warnings.push('Manual progress must be between 0 and 100 %.')
    else actualPct = new Decimal(i.manualPct)
  } else if (total.isZero()) {
    if (today.gt(0)) warnings.push('Approved quantity is zero — progress % cannot be calculated. Record an authorised overrun or use the manual method.')
  } else {
    actualPct = cum.div(total).times(100).toDecimalPlaces(3)
  }
  const overrun = i.method !== 'manual' && cum.gt(total) && !(total.isZero() && today.isZero())
  if (overrun && !i.overrunAuthorized) {
    warnings.push(`Cumulative ${cum.toString()} exceeds approved quantity ${total.toString()}. An authorised overrun record with a reason is required.`)
  }
  const variance = actualPct != null && i.plannedPct != null ? actualPct.minus(i.plannedPct).toDecimalPlaces(3).toNumber() : null
  return {
    cumulative: cum.toNumber(),
    balance: balance.toNumber(),
    actualPct: actualPct?.toNumber() ?? null,
    variancePct: variance,
    overrun,
    warnings,
  }
}

/** Linear planned % on a date between planned start and finish (calendar days). */
export function linearPlannedPct(start: string | null, finish: string | null, on: string): number | null {
  if (!start || !finish) return null
  const s = Date.parse(start), f = Date.parse(finish), d = Date.parse(on)
  if (d < s) return 0
  if (d >= f) return 100
  const days = (f - s) / 86400000 + 1
  return Math.round((((d - s) / 86400000 + 1) * 100 / days) * 1000) / 1000
}
