import { describe, expect, it } from 'vitest';
import { fmtBytes, fmtInt, fmtKg, fmtMoney, fmtNum, fmtPct, fmtSigned, fmtWeight } from './format';

describe('format', () => {
  it('formats numbers and weights', () => {
    expect(fmtInt(1234.6)).toBe('1,235');
    expect(fmtNum(12.345)).toBe('12.3');
    expect(fmtKg(5)).toBe('5 kg');
    expect(fmtWeight(950)).toBe('950 kg');
    expect(fmtWeight(12500)).toBe('12.5 t');
    expect(fmtWeight(null)).toBe('–');
    expect(fmtInt(undefined)).toBe('–');
  });
  it('formats signed deltas, money, percent and bytes', () => {
    expect(fmtSigned(5.25, ' kg')).toBe('+5.3 kg');
    expect(fmtSigned(-2)).toBe('−2');
    expect(fmtSigned(0)).toBe('±0');
    expect(fmtMoney(1234.5)).toBe('$1,235');
    expect(fmtMoney(10, 'XXX1')).toBe('10.00 XXX1');
    expect(fmtMoney(null)).toBe('–');
    expect(fmtPct(0.953)).toBe('95.3%');
    expect(fmtPct(null)).toBe('–');
    expect(fmtBytes(10)).toBe('10 B');
    expect(fmtBytes(2048)).toBe('2 KB');
    expect(fmtBytes(5 * 1024 ** 2)).toBe('5 MB');
  });
});
