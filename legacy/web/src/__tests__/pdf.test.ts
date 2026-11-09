import { describe, expect, it } from 'vitest'
import { buildDprPdf, pdfText } from '../lib/pdf/dprPdf'
import type { DprDocument } from '../lib/dprDoc'

const doc: DprDocument = {
  schema: 'construct-dpr/1',
  project: { id: 'p', code: 'BF2', name: 'Skip Bridge', client: 'Demo Steel', consultant: null, work_order_no: 'WO/17', site_address: null, timezone: 'Asia/Kolkata', currency: 'INR', report_header: 'Daily Progress Report', report_footer: null },
  header: { id: 'h1', dpr_no: 'BF2-DPR-0042', revision: 1, revision_reason: 'Re-measured', report_date: '2026-10-28', shift: 'day', status: 'approved', version: 3, seq: 42, prepared_by_name: 'Ravi', weather: 'Clear, 34 °C', work_completed: 'Trusses T1–T2 erected' },
  activities: [{ id: 'e1', activity_id: 'a', activity_code: 'A2010', activity_name: 'Erection — skip bridge', unit: 'MT', qty_today: 12, qty_accepted: 11.5, prev_cum_qty: 120, cum_qty: 131.5, balance_qty: 168.5, approved_total_qty: 300, planned_progress_pct: 55, actual_progress_pct: 43.833, variance_pct: -11.167, delay_reason: 'Crane breakdown' }],
  manpower: [{ trade: 'Fitters', headcount: 24, man_hours: 198, skilled: 20, unskilled: 4 }],
  equipment: [{ equipment_code: 'CR-01', equipment_type: 'Mobile crane', operating_hours: 7, idle_hours: 2, breakdown_hours: 0, utilization_pct: 77.78 }],
  materials: [{ material_code: 'CEM', material_description: 'Cement', unit: 'bags', opening_qty: 100, received_qty: 600, issued_qty: 220, consumed_qty: 210, returned_qty: 0, rejected_qty: 0, closing_qty: 480 }],
  safety: [], inspections: [], ncrs: [], constraints: [], actions: [], attachments: [],
  approval_history: [{ action: 'approve', comment: 'OK', from: 'under_review', to: 'approved', by: 'Asha (PM)', at: '2026-10-28T12:00:00Z' }],
}

describe('DPR PDF (Test G)', () => {
  it('contains the database quantities, totals and document control', () => {
    const pdf = buildDprPdf(doc, { orientation: 'landscape', reportRevisionId: 'rr-1' })
    const text = new TextDecoder('latin1').decode(pdf.output('arraybuffer'))
    for (const s of ['BF2-DPR-0042', 'Rev 1', 'A2010', '131.5', '168.5', '11.5', '480', '198', 'Asha \\(PM\\)', 'snapshot rr-1', 'Page 1 of'])
      expect(text, s).toContain(s)
    expect(pdf.getNumberOfPages()).toBeGreaterThan(1)
  })
  it('sanitises characters the standard PDF fonts cannot show', () => {
    expect(pdfText('₹ 1,000 → m³ — ok')).toBe('Rs. 1,000 -> m³ - ok')
    expect(pdfText(null)).toBe('-')
  })
})
