import { jsPDF } from 'jspdf'
import { autoTable, type RowInput } from 'jspdf-autotable'
import type { DprDocument } from '../dprDoc'

/**
 * Builds the printable DPR from a DprDocument — for approved reports this is
 * the immutable snapshot stored at approval, so the PDF always matches what
 * was approved, even if masters or BOQ change later.
 *
 * Runs in the browser (no server needed); the result is uploaded to Drive
 * and its checksum recorded against the report revision.
 */
export interface PdfOptions {
  orientation?: 'portrait' | 'landscape'
  watermark?: string // e.g. "DRAFT — NOT APPROVED"
  photos?: Array<{ caption: string; dataUrl: string; width: number; height: number }>
  reportRevisionId?: string
  generatedBy?: string
}

// Standard PDF fonts cover Latin-1 only; map common symbols and drop the rest.
const MAP: Record<string, string> = { '₹': 'Rs.', '→': '->', '←': '<-', '≥': '>=', '≤': '<=', '−': '-', '–': '-', '—': '-', '…': '...', '“': '"', '”': '"', '‘': "'", '’': "'", '✓': 'Y', '★': '*' }
export const pdfText = (v: unknown): string => {
  if (v == null || v === '') return '-'
  return String(v).replace(/[^\x20-\x7E\xA0-\xFF\n]/g, (c) => MAP[c] ?? '')
}
const n = (v: unknown, d = 3) => (v == null || v === '' ? '-' : Number(v).toLocaleString('en-IN', { maximumFractionDigits: d }))
const pct = (v: unknown) => (v == null ? '-' : `${Number(v).toFixed(1)}%`)
const sum = (rows: Array<Record<string, unknown>>, k: string) => rows.reduce((s, r) => s + Number(r[k] ?? 0), 0)

export function buildDprPdf(doc: DprDocument, opt: PdfOptions = {}): jsPDF {
  const pdf = new jsPDF({ orientation: opt.orientation ?? 'portrait', unit: 'mm', format: 'a4' })
  const W = pdf.internal.pageSize.getWidth()
  const H = pdf.internal.pageSize.getHeight()
  const M = 12
  const h = doc.header
  let y = 30

  const brand: [number, number, number] = [15, 61, 94]
  const section = (num: number, title: string) => {
    if (y > H - 30) { pdf.addPage(); y = 30 }
    pdf.setFont('helvetica', 'bold').setFontSize(11).setTextColor(...brand)
    pdf.text(`${num}. ${pdfText(title)}`, M, y)
    pdf.setTextColor(0)
    y += 2
  }
  const table = (head: string[], body: RowInput[], opts: Record<string, unknown> = {}) => {
    autoTable(pdf, {
      startY: y, head: [head.map(pdfText)], body: body.length ? body.map((r) => (r as unknown[]).map((c) => (typeof c === 'object' && c !== null ? c : pdfText(c)))) as RowInput[] : [[{ content: 'None recorded', colSpan: head.length, styles: { fontStyle: 'italic', textColor: 120 } }]],
      margin: { left: M, right: M, top: 28, bottom: 16 }, theme: 'grid', showHead: 'everyPage',
      styles: { fontSize: 8, cellPadding: 1.5, overflow: 'linebreak' }, headStyles: { fillColor: brand, textColor: 255, fontStyle: 'bold' },
      ...opts,
    })
    y = (pdf as unknown as { lastAutoTable: { finalY: number } }).lastAutoTable.finalY + 6
  }
  const para = (label: string, text: unknown) => {
    if (text == null || text === '') return
    pdf.setFont('helvetica', 'bold').setFontSize(9)
    const lines = pdf.splitTextToSize(pdfText(text), W - 2 * M - 45) as string[]
    if (y + lines.length * 4 > H - 18) { pdf.addPage(); y = 30 }
    pdf.text(pdfText(label), M, y)
    pdf.setFont('helvetica', 'normal')
    pdf.text(lines, M + 45, y)
    y += Math.max(5, lines.length * 4 + 1)
  }

  // 1–2. Project / contract details and report identity
  section(1, 'Project and contract details')
  table(['Project', 'Client', 'Consultant', 'Work order', 'Site'], [[`${doc.project.code} - ${doc.project.name}`, doc.project.client, doc.project.consultant, doc.project.work_order_no, doc.project.site_address]])
  section(2, 'Report')
  table(['DPR no.', 'Revision', 'Date', 'Shift', 'Working hours', 'Area', 'Status'],
    [[h.dpr_no, String(h.revision), h.report_date, h.shift, `${String(h.work_start ?? '-').slice(0, 5)} to ${String(h.work_end ?? '-').slice(0, 5)}`, h.area_name ?? 'Whole project', String(h.status).toUpperCase()]])

  // 3. Executive summary (computed from the document itself)
  const head = sum(doc.manpower, 'headcount')
  const behind = doc.activities.filter((a) => a.variance_pct != null && a.variance_pct < -5).length
  section(3, 'Executive summary')
  para('Summary', `${doc.activities.length} activities reported; ${head} people (${n(sum(doc.manpower, 'man_hours'), 1)} man-hours); ${doc.equipment.length} equipment deployed; ${behind} activities behind plan by more than 5%; ${doc.safety.length} safety records; ${doc.ncrs.length} NCRs/observations.`)
  para('Work completed', h.work_completed)
  para('Achievements', h.achievements)

  // 4. Weather / site
  section(4, 'Weather and site conditions')
  para('Weather', h.weather)
  para('Site conditions', h.site_conditions)
  para('Weather interruptions', h.weather_interruptions)

  // 5–7. Activity progress, planned vs actual, quantities
  section(5, 'Activity-wise progress, planned vs actual, quantities')
  table(['Activity', 'Location', 'Unit', 'Approved', 'Previous', 'Today (rep.)', 'Accepted', 'Cumulative', 'Balance', 'Plan %', 'Actual %', 'Var.'],
    doc.activities.map((a) => [`${a.activity_code} ${a.activity_name}`, a.location as string, a.unit, n(a.approved_total_qty), n(a.prev_cum_qty), n(a.qty_today), n(a.qty_accepted), n(a.cum_qty), n(a.balance_qty), pct(a.planned_progress_pct), pct(a.actual_progress_pct), pct(a.variance_pct)]),
    { columnStyles: { 0: { cellWidth: opt.orientation === 'landscape' ? 60 : 38 }, 3: { halign: 'right' }, 4: { halign: 'right' }, 5: { halign: 'right' }, 6: { halign: 'right' }, 7: { halign: 'right' }, 8: { halign: 'right' }, 9: { halign: 'right' }, 10: { halign: 'right' }, 11: { halign: 'right' } } })
  const delays = doc.activities.filter((a) => a.delay_reason || a.constraints)
  if (delays.length) table(['Activity', 'Delay / deviation reason', 'Constraints', 'Next-day target', 'Responsible'], delays.map((a) => [a.activity_code, a.delay_reason as string, a.constraints as string, n(a.next_day_target), a.responsible as string]))

  // 8. Manpower
  section(8, 'Manpower')
  table(['Agency', 'Trade', 'Shift', 'Skilled', 'Semi', 'Unskilled', 'Sup.', 'Engg.', 'Safety', 'Other', 'Total', 'Man-h', 'OT h', 'Absent'],
    [...doc.manpower.map((m) => [m.contractor_name as string, m.trade, m.shift as string, m.skilled, m.semi_skilled, m.unskilled, m.supervisors, m.engineers, m.safety_staff, m.others, m.headcount, n(m.man_hours, 1), n(m.overtime_hours, 1), m.absent] as RowInput),
      ...(doc.manpower.length ? [[{ content: 'Total', colSpan: 10, styles: { fontStyle: 'bold' } }, head, n(sum(doc.manpower, 'man_hours'), 1), n(sum(doc.manpower, 'overtime_hours'), 1), sum(doc.manpower, 'absent')] as RowInput] : [])])

  // 9. Equipment
  section(9, 'Equipment deployment')
  table(['ID', 'Type', 'Location', 'Operator', 'Working h', 'Idle h', 'Breakdown h', 'Util. %', 'Fuel', 'Breakdown reason'],
    doc.equipment.map((e) => [e.equipment_code, e.equipment_type, e.location as string, e.operator as string, n(e.operating_hours, 1), n(e.idle_hours, 1), n(e.breakdown_hours, 1), pct(e.utilization_pct), e.fuel_consumed != null ? `${n(e.fuel_consumed, 1)} ${pdfText(e.fuel_unit ?? '')}` : '-', e.breakdown_reason as string]))

  // 10. Materials
  section(10, 'Material receipt and consumption')
  table(['Material', 'Unit', 'Opening', 'Received', 'Rejected', 'Issued', 'Consumed', 'Returned', 'Closing', 'Challan', 'Heat/batch', 'Inspection'],
    doc.materials.map((m) => [`${m.material_code} ${m.material_description}`, m.unit, n(m.opening_qty), n(m.received_qty), n(m.rejected_qty), n(m.issued_qty), n(m.consumed_qty), n(m.returned_qty), n(m.closing_qty), m.challan_no as string, m.heat_batch_no as string, m.inspection_status as string]))

  // 11. Safety
  section(11, 'Safety statistics and observations')
  para('Toolbox talks', `${h.toolbox_talks ?? 0} held. Topics: ${h.toolbox_topics ?? '-'}`)
  para('Permits issued', h.permits_issued)
  table(['Type', 'Permit', 'Description', 'Severity', 'Corrective / preventive action', 'Responsible', 'Target', 'Status'],
    doc.safety.map((s) => [s.category, [s.permit_type, s.permit_no].filter(Boolean).join(' '), s.description, s.severity, [s.corrective_action, s.preventive_action].filter(Boolean).join(' / '), s.responsible, s.target_date, s.status] as RowInput))

  // 12. QA/QC
  section(12, 'QA/QC inspections and NCRs')
  table(['Request', 'Type', 'Hold pt', 'Inspected qty', 'Outcome', 'Inspector', 'Remarks'],
    doc.inspections.map((i) => [i.request_no, i.inspection_type, i.is_hold_point ? 'Yes' : 'No', n(i.inspected_qty), i.outcome, i.inspector, i.remarks] as RowInput))
  if (doc.ncrs.length) table(['NCR', 'Kind', 'Description', 'Rework', 'Corrective action', 'Responsible', 'Target', 'Status'],
    doc.ncrs.map((x) => [x.ncr_no, x.kind, x.description, x.rework_required ? 'Yes' : 'No', x.corrective_action, x.responsible, x.target_date, x.status] as RowInput))

  // 13. Photographs
  section(13, 'Photographic evidence')
  if (!opt.photos?.length) para('Photos', doc.attachments.filter((a) => a.category === 'photo').length ? `${doc.attachments.filter((a) => a.category === 'photo').length} photos stored in the project Drive (not embedded in this copy).` : 'None attached.')
  else {
    const cols = opt.orientation === 'landscape' ? 3 : 2
    const cw = (W - 2 * M - (cols - 1) * 4) / cols
    let col = 0
    for (const p of opt.photos) {
      const ih = Math.min(cw * (p.height / p.width), 70)
      const iw = ih * (p.width / p.height)
      if (y + ih + 10 > H - 16) { pdf.addPage(); y = 30; col = 0 }
      const x = M + col * (cw + 4)
      pdf.addImage(p.dataUrl, 'JPEG', x, y, iw, ih)
      pdf.setFont('helvetica', 'normal').setFontSize(7.5)
      pdf.text(pdf.splitTextToSize(pdfText(p.caption), cw) as string[], x, y + ih + 3.5)
      col++
      if (col === cols) { col = 0; y += ih + 11 }
    }
    if (col) y += 81
  }

  // 14–16. Delays, actions, next-day plan
  section(14, 'Delays and constraints')
  para('Reasons for delay', h.delay_reasons)
  para('Material shortages', h.material_shortages)
  para('Equipment constraints', h.equipment_constraints)
  para('Labour constraints', h.labour_constraints)
  para('Design / drawing issues', h.design_issues)
  para('Client instructions', h.client_instructions)
  table(['Category', 'Constraint', 'Responsible', 'Needed by', 'Status'], doc.constraints.map((c) => [c.category, c.description, c.responsible, c.target_date, c.status] as RowInput))
  section(15, 'Required actions and responsible persons')
  para('Decisions required', h.decisions_required)
  table(['Action', 'Responsible', 'Target', 'Priority', 'Status'], doc.actions.map((a) => [a.description, a.responsible, a.target_date, a.priority, a.status] as RowInput))
  section(16, 'Next-day work plan')
  para('Planned activities', h.tomorrow_plan)
  para('Manpower required', h.required_manpower)
  para('Equipment required', h.required_equipment)
  para('Materials required', h.required_materials)
  para('Priority actions', h.priority_actions)

  // 17. Sign-off
  section(17, 'Prepared, checked and approved by')
  const submit = doc.approval_history.filter((x) => x.action === 'submit').at(-1)
  const approve = doc.approval_history.filter((x) => x.action === 'approve').at(-1)
  const review = doc.approval_history.filter((x) => x.action === 'start_review').at(-1)
  table(['Prepared by', 'Checked / reviewed by', 'Approved by'], [[
    `${pdfText(h.prepared_by_name)}\n${submit ? new Date(submit.at).toLocaleString('en-IN') : ''}`,
    review ? `${pdfText(review.by)}\n${new Date(review.at).toLocaleString('en-IN')}` : '-',
    approve ? `${pdfText(approve.by)}\n${new Date(approve.at).toLocaleString('en-IN')}` : 'Not approved',
  ]], { bodyStyles: { minCellHeight: 16 } })

  // 18. Revision history / document control
  section(18, 'Revision history and document control')
  table(['When', 'Action', 'By', 'From -> To', 'Comment'], doc.approval_history.map((x) => [new Date(x.at).toLocaleString('en-IN'), x.action, x.by, `${x.from ?? ''} -> ${x.to ?? ''}`, x.comment] as RowInput))
  para('Document control', `Report ID ${h.id}; revision ${h.revision}${h.revision_reason ? ` (${pdfText(h.revision_reason)})` : ''}; status ${h.status}; generated ${new Date().toLocaleString('en-IN')}${opt.generatedBy ? ` by ${pdfText(opt.generatedBy)}` : ''}${opt.reportRevisionId ? `; snapshot ${opt.reportRevisionId}` : ''}.`)

  // Header, footer, page numbers, watermark on every page
  const pages = pdf.getNumberOfPages()
  for (let i = 1; i <= pages; i++) {
    pdf.setPage(i)
    pdf.setFillColor(...brand).rect(0, 0, W, 20, 'F')
    pdf.setTextColor(255).setFont('helvetica', 'bold').setFontSize(12)
    pdf.text(pdfText(doc.project.report_header || 'Daily Progress Report'), M, 9)
    pdf.setFont('helvetica', 'normal').setFontSize(9)
    pdf.text(pdfText(`${doc.project.code} - ${doc.project.name}`), M, 15)
    pdf.text(pdfText(`${h.dpr_no}  Rev ${h.revision}  |  ${h.report_date}  |  ${h.shift} shift`), W - M, 9, { align: 'right' })
    pdf.text(pdfText(String(h.status).toUpperCase()), W - M, 15, { align: 'right' })
    pdf.setTextColor(110).setFontSize(7.5)
    pdf.text(pdfText(doc.project.report_footer || 'Generated by CONSTRUCT DPR'), M, H - 6)
    pdf.text(`Page ${i} of ${pages}`, W - M, H - 6, { align: 'right' })
    if (opt.watermark) {
      pdf.setTextColor(200, 40, 40).setFontSize(40)
      pdf.saveGraphicsState()
      pdf.setGState(new (pdf as unknown as { GState: new (o: object) => unknown }).GState({ opacity: 0.15 }) as never)
      pdf.text(pdfText(opt.watermark), W / 2, H / 2, { align: 'center', angle: 30 })
      pdf.restoreGraphicsState()
    }
    pdf.setTextColor(0)
  }
  return pdf
}

/** Loads an image Blob into a JPEG data URL scaled for the PDF. */
export async function photoForPdf(blob: Blob, caption: string, maxPx = 1000) {
  const bmp = await createImageBitmap(blob)
  const s = Math.min(1, maxPx / Math.max(bmp.width, bmp.height))
  const c = Object.assign(document.createElement('canvas'), { width: Math.round(bmp.width * s), height: Math.round(bmp.height * s) })
  c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height)
  bmp.close()
  return { caption, dataUrl: c.toDataURL('image/jpeg', 0.8), width: c.width, height: c.height }
}
