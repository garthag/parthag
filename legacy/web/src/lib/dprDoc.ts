import type { ActionLine, ConstraintLine, DprActivityLine, DprDraft, EquipmentLine, InspectionLine, ManpowerLine, MaterialLine, NcrLine, SafetyLine } from './types'
import { uid } from './types'

/** Shape returned by the dpr_document() database function (also stored as approved snapshots). */
export interface DprDocument {
  schema: string
  project: { id: string; code: string; name: string; client: string | null; consultant: string | null; work_order_no: string | null; site_address: string | null; timezone: string; currency: string; report_header: string | null; report_footer: string | null }
  header: Record<string, unknown> & { id: string; dpr_no: string; revision: number; report_date: string; shift: string; status: string; version: number; seq: number; prepared_by_name?: string; approved_by_name?: string; area_name?: string }
  activities: Array<Record<string, unknown> & { id: string; activity_id: string; activity_code: string; activity_name: string; unit: string | null; qty_today: number; qty_accepted: number | null; prev_cum_qty: number; cum_qty: number; balance_qty: number; approved_total_qty: number; planned_progress_pct: number | null; actual_progress_pct: number | null; variance_pct: number | null }>
  manpower: Array<Record<string, unknown> & { trade: string; headcount: number; man_hours: number; contractor_name?: string }>
  equipment: Array<Record<string, unknown> & { equipment_code: string; equipment_type: string; operating_hours: number; idle_hours: number; breakdown_hours: number; utilization_pct: number | null }>
  materials: Array<Record<string, unknown> & { material_code: string; material_description: string; unit: string; opening_qty: number; received_qty: number; issued_qty: number; consumed_qty: number; returned_qty: number; rejected_qty: number; closing_qty: number }>
  safety: Array<Record<string, unknown>>
  inspections: Array<Record<string, unknown>>
  ncrs: Array<Record<string, unknown>>
  constraints: Array<Record<string, unknown>>
  actions: Array<Record<string, unknown>>
  attachments: Array<{ id: string; drive_file_id: string; category: string; caption: string | null; mime_type: string; original_filename: string; activity_id: string | null; captured_at: string | null; latitude: number | null; longitude: number | null; sha256: string | null; transcript: string | null }>
  approval_history: Array<{ action: string; comment: string | null; from: string | null; to: string | null; by: string; at: string }>
}

function pick<T>(o: Record<string, unknown>, keys: string[]): T {
  const r: Record<string, unknown> = { key: uid() }
  for (const k of keys) r[k] = o[k] ?? null
  return r as T
}

/** Converts a server document back into an editable draft. */
export function docToDraft(doc: DprDocument): DprDraft {
  const h = doc.header
  const H = ['report_date', 'shift', 'work_start', 'work_end', 'weather', 'site_conditions', 'site_id', 'area_id', 'toolbox_talks', 'toolbox_topics', 'permits_issued',
    'work_completed', 'achievements', 'work_not_completed', 'delay_reasons', 'weather_interruptions', 'material_shortages', 'equipment_constraints', 'labour_constraints',
    'design_issues', 'client_instructions', 'decisions_required', 'tomorrow_plan', 'required_manpower', 'required_equipment', 'required_materials', 'priority_actions']
  const header: Record<string, unknown> = {}
  for (const k of H) header[k] = h[k] ?? (k === 'toolbox_talks' || k === 'permits_issued' ? 0 : '')
  if (typeof header.work_start === 'string') header.work_start = (header.work_start as string).slice(0, 5)
  if (typeof header.work_end === 'string') header.work_end = (header.work_end as string).slice(0, 5)
  return {
    ...(header as Partial<DprDraft>),
    id: h.id, project_id: doc.project.id, report_date: h.report_date, shift: h.shift,
    dpr_no: h.dpr_no, status: h.status as DprDraft['status'], revision: h.revision, expected_version: h.version, seq: h.seq,
    activities: doc.activities.map((a) => pick<DprActivityLine>(a, ['activity_id', 'qty_today', 'qty_inspected', 'location', 'contractor_id', 'actual_start', 'actual_finish', 'progress_method',
      'manual_progress_pct', 'overrun_authorized', 'overrun_reason', 'delay_reason', 'constraints', 'next_day_target', 'responsible', 'remarks'])),
    manpower: doc.manpower.map((m) => pick<ManpowerLine>(m, ['contractor_id', 'area_id', 'trade', 'shift', 'skilled', 'semi_skilled', 'unskilled', 'supervisors', 'engineers', 'safety_staff', 'others', 'absent', 'hours_per_person', 'overtime_hours', 'remarks'])),
    equipment: doc.equipment.map((e) => pick<EquipmentLine>(e, ['equipment_id', 'equipment_code', 'equipment_type', 'owner', 'rated_capacity', 'location', 'operator', 'operating_hours', 'idle_hours', 'breakdown_hours', 'fuel_consumed', 'fuel_unit', 'certification_expiry', 'breakdown_reason', 'remarks'])),
    materials: doc.materials.map((m) => pick<MaterialLine>(m, ['material_id', 'received_qty', 'accepted_qty', 'rejected_qty', 'issued_qty', 'consumed_qty', 'returned_qty', 'challan_no', 'heat_batch_no', 'inspection_status', 'storage_location', 'supplier', 'remarks'])),
    safety: doc.safety.map((s) => pick<SafetyLine>(s, ['category', 'permit_type', 'permit_no', 'description', 'location', 'severity', 'corrective_action', 'preventive_action', 'responsible', 'target_date', 'status'])),
    inspections: doc.inspections.map((s) => pick<InspectionLine>(s, ['activity_id', 'request_no', 'inspection_type', 'description', 'is_hold_point', 'inspected_qty', 'outcome', 'inspector', 'remarks'])),
    ncrs: doc.ncrs.map((s) => pick<NcrLine>(s, ['activity_id', 'ncr_no', 'kind', 'description', 'rework_required', 'corrective_action', 'responsible', 'target_date', 'status'])),
    constraints: doc.constraints.map((s) => pick<ConstraintLine>(s, ['activity_id', 'category', 'description', 'responsible', 'target_date', 'status'])),
    actions: doc.actions.map((s) => pick<ActionLine>(s, ['description', 'responsible', 'target_date', 'priority', 'status'])),
  }
}
