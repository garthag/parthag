export type AppRole =
  | 'org_admin' | 'project_admin' | 'project_manager' | 'construction_manager' | 'planning_engineer'
  | 'site_engineer' | 'supervisor' | 'storekeeper' | 'qaqc_engineer' | 'safety_engineer'
  | 'commercial_engineer' | 'client_reviewer' | 'auditor'

export const ROLE_LABELS: Record<AppRole, string> = {
  org_admin: 'Organisation administrator', project_admin: 'Project administrator', project_manager: 'Project manager',
  construction_manager: 'Construction manager', planning_engineer: 'Planning engineer', site_engineer: 'Site engineer',
  supervisor: 'Supervisor', storekeeper: 'Storekeeper', qaqc_engineer: 'QA/QC engineer', safety_engineer: 'Safety engineer',
  commercial_engineer: 'Commercial engineer', client_reviewer: 'Client reviewer', auditor: 'Read-only auditor',
}

export type Module = 'project' | 'members' | 'masters' | 'schedule' | 'boq' | 'commercial' | 'dpr' | 'resources' | 'safety' | 'quality' | 'documents' | 'reports'
export type Action = 'create' | 'view' | 'edit' | 'submit' | 'approve' | 'reject' | 'export' | 'delete'

export type DprStatus = 'draft' | 'submitted' | 'under_review' | 'approved' | 'rejected'

export interface Project {
  id: string; organization_id: string; code: string; name: string; client_id: string | null; consultant: string | null
  work_order_no: string | null; site_address: string | null; start_date: string | null; completion_date: string | null
  timezone: string; currency: string; units: string[]; money_decimals: number; qty_decimals: number
  drive_root_folder_id: string | null; report_header: string | null; report_footer: string | null
  allow_self_approval: boolean; approved_baseline_id: string | null; status: string; version: number
}

export interface Activity {
  id: string; project_id: string; wbs_id: string | null; area_id: string | null; boq_item_id: string | null
  contractor_id: string | null; calendar_id: string | null; code: string; name: string; location: string | null
  unit: string | null; total_qty: number; weight: number; original_duration: number; remaining_duration: number | null
  planned_start: string | null; planned_finish: string | null; actual_start: string | null; actual_finish: string | null
  is_milestone: boolean; constraint_type: 'SNET' | 'FNLT' | 'MSO' | 'MFO' | null; constraint_date: string | null
  responsible: string | null; status: string; version: number
}

export interface DprHeader {
  id: string; project_id: string; seq: number; dpr_no: string; revision: number; previous_revision_id: string | null
  revision_reason: string | null; report_date: string; shift: string; status: DprStatus; prepared_by: string
  area_id: string | null; version: number; updated_at: string; approved_at: string | null; submitted_at: string | null
  [k: string]: unknown
}

/** The editable DPR document as held in the wizard and the offline queue. */
export interface DprDraft {
  id: string
  project_id: string
  report_date: string
  shift: string
  work_start?: string; work_end?: string
  weather?: string; site_conditions?: string
  site_id?: string | null; area_id?: string | null
  toolbox_talks?: number; toolbox_topics?: string; permits_issued?: number
  work_completed?: string; achievements?: string; work_not_completed?: string; delay_reasons?: string
  weather_interruptions?: string; material_shortages?: string; equipment_constraints?: string
  labour_constraints?: string; design_issues?: string; client_instructions?: string; decisions_required?: string
  tomorrow_plan?: string; required_manpower?: string; required_equipment?: string; required_materials?: string
  priority_actions?: string
  activities: DprActivityLine[]
  manpower: ManpowerLine[]
  equipment: EquipmentLine[]
  materials: MaterialLine[]
  safety: SafetyLine[]
  inspections: InspectionLine[]
  ncrs: NcrLine[]
  constraints: ConstraintLine[]
  actions: ActionLine[]
  // client-side bookkeeping
  expected_version?: number | null
  dpr_no?: string
  status?: DprStatus
  revision?: number
  seq?: number
}

export interface DprActivityLine {
  key: string; activity_id: string; qty_today: number | null; qty_inspected?: number | null
  location?: string; contractor_id?: string | null; actual_start?: string | null; actual_finish?: string | null
  progress_method?: 'quantity' | 'manual'; manual_progress_pct?: number | null
  overrun_authorized?: boolean; overrun_reason?: string; delay_reason?: string; constraints?: string
  next_day_target?: number | null; responsible?: string; remarks?: string
}
export interface ManpowerLine {
  key: string; contractor_id?: string | null; area_id?: string | null; trade: string; shift?: string
  skilled: number; semi_skilled: number; unskilled: number; supervisors: number; engineers: number
  safety_staff: number; others: number; absent: number; hours_per_person: number; overtime_hours: number; remarks?: string
}
export interface EquipmentLine {
  key: string; equipment_id?: string | null; equipment_code: string; equipment_type: string; owner?: string
  rated_capacity?: string; location?: string; operator?: string; operating_hours: number; idle_hours: number
  breakdown_hours: number; fuel_consumed?: number | null; fuel_unit?: string; certification_expiry?: string | null
  breakdown_reason?: string; remarks?: string
}
export interface MaterialLine {
  key: string; material_id: string; received_qty: number; accepted_qty?: number | null; rejected_qty: number
  issued_qty: number; consumed_qty: number; returned_qty: number; challan_no?: string; heat_batch_no?: string
  inspection_status: string; storage_location?: string; supplier?: string; remarks?: string
}
export interface SafetyLine {
  key: string; category: string; permit_type?: string | null; permit_no?: string; description: string; location?: string
  severity?: string | null; corrective_action?: string; preventive_action?: string; responsible?: string
  target_date?: string | null; status: string
}
export interface InspectionLine {
  key: string; activity_id?: string | null; request_no?: string; inspection_type: string; description?: string
  is_hold_point: boolean; inspected_qty?: number | null; outcome: string; inspector?: string; remarks?: string
}
export interface NcrLine {
  key: string; activity_id?: string | null; ncr_no?: string; kind: 'ncr' | 'observation'; description: string
  rework_required: boolean; corrective_action?: string; responsible?: string; target_date?: string | null; status: string
}
export interface ConstraintLine {
  key: string; activity_id?: string | null; category: string; description: string; responsible?: string
  target_date?: string | null; status: string
}
export interface ActionLine {
  key: string; description: string; responsible?: string; target_date?: string | null; priority: string; status: string
}

export interface Attachment {
  id: string; project_id: string; dpr_id: string | null; activity_id: string | null; drive_file_id: string | null
  parent_folder_id: string | null; web_view_link: string | null; original_filename: string; mime_type: string
  size_bytes: number; sha256: string | null; category: 'photo' | 'video' | 'audio' | 'document' | 'drawing' | 'report'
  caption: string | null; transcript: string | null; captured_at: string | null; latitude: number | null
  longitude: number | null; upload_status: 'pending' | 'uploading' | 'uploaded' | 'failed'; upload_error: string | null
  retention_status: string; created_at: string; created_by: string
}

export const uid = () => crypto.randomUUID()
export const today = () => new Date().toLocaleDateString('en-CA') // YYYY-MM-DD in device local time
