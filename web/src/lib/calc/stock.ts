/** Store ledger arithmetic (mirrors material_transactions.closing_qty). */
export interface StockLine {
  opening: number
  received: number
  accepted?: number | null
  rejected: number
  issued: number
  consumed: number
  returned: number
}

export function closingStock(l: StockLine): number {
  return round(l.opening + l.received + l.returned - l.issued - l.rejected)
}

export function stockWarnings(l: StockLine): string[] {
  const w: string[] = []
  for (const [k, v] of Object.entries(l)) if (typeof v === 'number' && v < 0) w.push(`${k} cannot be negative`)
  if (l.rejected > l.received) w.push('Rejected quantity cannot exceed quantity received.')
  if (l.accepted != null && l.accepted + l.rejected > l.received) w.push('Accepted + rejected cannot exceed quantity received.')
  if (closingStock(l) < 0) w.push(`Issue exceeds available stock (closing would be ${closingStock(l)}).`)
  return w
}

const round = (n: number) => Math.round(n * 10000) / 10000
