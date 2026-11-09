import Decimal from 'decimal.js'

/** Decimal-safe money helpers. Never use binary floating point for amounts. */
export type RoundingMode = 'half_up' | 'half_even' | 'down'
const MODES: Record<RoundingMode, Decimal.Rounding> = {
  half_up: Decimal.ROUND_HALF_UP,
  half_even: Decimal.ROUND_HALF_EVEN,
  down: Decimal.ROUND_DOWN,
}

export function amount(qty: number | string, rate: number | string, decimals = 2, mode: RoundingMode = 'half_up'): string {
  return new Decimal(qty || 0).times(new Decimal(rate || 0)).toDecimalPlaces(decimals, MODES[mode]).toFixed(decimals)
}

export function sum(values: Array<number | string | null | undefined>, decimals = 2): string {
  return values.reduce<Decimal>((acc, v) => acc.plus(new Decimal(v ?? 0)), new Decimal(0)).toFixed(decimals)
}

/** ₹ with Indian digit grouping (12,34,567.89). */
export function formatINR(v: number | string | null | undefined, decimals = 2, currency = 'INR'): string {
  if (v == null || v === '') return '—'
  const n = typeof v === 'string' ? Number(v) : v
  return new Intl.NumberFormat('en-IN', { style: 'currency', currency, minimumFractionDigits: decimals, maximumFractionDigits: decimals }).format(n)
}

export function formatQty(v: number | string | null | undefined, decimals = 3): string {
  if (v == null || v === '') return '—'
  const n = typeof v === 'string' ? Number(v) : v
  return new Intl.NumberFormat('en-IN', { maximumFractionDigits: decimals }).format(n)
}
