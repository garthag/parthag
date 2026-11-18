const nf0 = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });
const nf1 = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });
const nf2 = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export const fmtInt = (v: number | null | undefined) => (v == null ? '–' : nf0.format(v));
export const fmtNum = (v: number | null | undefined) => (v == null ? '–' : nf1.format(v));
export const fmtKg = (v: number | null | undefined) => (v == null ? '–' : `${nf1.format(v)} kg`);

/** Weight with automatic unit: tonnes above 1,000 kg. */
export function fmtWeight(kg: number | null | undefined): string {
  if (kg == null) return '–';
  return Math.abs(kg) >= 1000 ? `${nf1.format(kg / 1000)} t` : `${nf1.format(kg)} kg`;
}

export function fmtSigned(v: number, unit = ''): string {
  const s = nf1.format(Math.abs(v));
  return `${v > 0 ? '+' : v < 0 ? '−' : '±'}${s}${unit}`;
}

export function fmtMoney(v: number | null | undefined, currency = 'USD'): string {
  if (v == null) return '–';
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency, maximumFractionDigits: 0 }).format(v);
  } catch {
    return `${nf2.format(v)} ${currency}`;
  }
}

export const fmtPct = (v: number | null | undefined) => (v == null ? '–' : `${nf1.format(v * 100)}%`);

export function fmtBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 ** 2) return `${nf1.format(b / 1024)} KB`;
  return `${nf1.format(b / 1024 ** 2)} MB`;
}

export const STATUS_COLORS: Record<string, 'default' | 'primary' | 'success' | 'warning' | 'info' | 'error' | 'secondary'> = {
  PRELIM: 'default',
  IFR: 'info',
  IFA: 'warning',
  IFC: 'success',
  AFC: 'success',
  'AS-BUILT': 'primary',
  SUPERSEDED: 'default',
  VOID: 'error',
};
