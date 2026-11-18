export interface ApiResponse<T> {
  success: boolean;
  data: T;
  message?: string | null;
}

export interface PaginationMeta {
  page: number;
  page_size: number;
  total: number;
  pages: number;
}

export interface Page<T> {
  success: boolean;
  data: T[];
  meta: PaginationMeta;
}

export interface ApiErrorBody {
  success: false;
  error: { code: string; message: string; details?: unknown };
  request_id?: string;
}

export interface TokenPair {
  access_token: string;
  refresh_token: string;
  token_type: string;
  expires_in: number;
}

export interface User {
  id: number;
  email: string;
  username: string;
  full_name: string;
  job_title?: string | null;
  is_superuser: boolean;
  roles: { id: number; name: string }[];
  permissions: string[];
}

export interface Project {
  id: number;
  code: string;
  name: string;
  client?: string | null;
  status: string;
  drawing_count: number;
}

export interface Discipline {
  id: number;
  code: string;
  name: string;
  color?: string | null;
}

export interface Drawing {
  id: number;
  project_id: number;
  project_code?: string | null;
  discipline_id?: number | null;
  discipline_code?: string | null;
  area_code?: string | null;
  drawing_number: string;
  title: string;
  drawing_type?: string | null;
  status: string;
  current_revision?: string | null;
  scale?: string | null;
  sheet_number?: string | null;
  vendor?: string | null;
  folder_path?: string | null;
  current_weight_kg: number;
  revision_count: number;
  ocr_confidence?: number | null;
  updated_at: string;
}

export interface Revision {
  id: number;
  drawing_id: number;
  revision: string;
  revision_seq: number;
  revision_date?: string | null;
  status: string;
  description?: string | null;
  checker?: string | null;
  approver?: string | null;
  is_current: boolean;
  total_weight_kg: number;
  ocr_confidence?: number | null;
}

export interface DrawingFile {
  id: number;
  revision_id: number | null;
  original_filename: string;
  relative_path?: string | null;
  file_size: number;
  page_count?: number | null;
  is_scanned?: boolean | null;
  upload_status: string;
  error_message?: string | null;
}

export interface DrawingDetail extends Drawing {
  total_sheets?: number | null;
  originator?: string | null;
  remarks?: string | null;
  tags: string[];
  revisions: Revision[];
  files: DrawingFile[];
  latest_ocr_result_id?: number | null;
}

export interface TreeNode {
  id: string;
  label: string;
  type: 'project' | 'discipline' | 'area' | 'drawing';
  entity_id?: number | null;
  count: number;
  meta: Record<string, unknown>;
  children: TreeNode[];
}

export interface BOMItem {
  id: number;
  line_no: number;
  item_number?: string | null;
  part_number?: string | null;
  description: string;
  material?: string | null;
  grade?: string | null;
  specification?: string | null;
  quantity: number;
  unit: string;
  unit_weight_kg?: number | null;
  total_weight_kg: number;
  confidence?: number | null;
  weight_calculated: boolean;
  drawing_id?: number;
  drawing_number?: string;
  revision?: string;
}

export interface FieldChange {
  field: string;
  old: unknown;
  new: unknown;
}

export interface ItemDiff {
  key: string;
  change_type: 'added' | 'removed' | 'modified';
  item_number?: string | null;
  part_number?: string | null;
  description?: string | null;
  material?: string | null;
  grade?: string | null;
  old_quantity?: number | null;
  new_quantity?: number | null;
  quantity_delta: number;
  old_weight_kg?: number | null;
  new_weight_kg?: number | null;
  weight_delta_kg: number;
  cost_delta: number;
  categories: string[];
  changes: FieldChange[];
}

export interface RevisionComparison {
  drawing_id: number;
  drawing_number: string;
  from_revision: string;
  to_revision: string;
  from_revision_id: number;
  to_revision_id: number;
  from_weight_kg: number;
  to_weight_kg: number;
  weight_delta_kg: number;
  weight_delta_pct: number | null;
  cost_delta: number;
  currency: string;
  summary: Record<string, number>;
  added: ItemDiff[];
  removed: ItemDiff[];
  modified: ItemDiff[];
  unchanged_count: number;
  cost_impacts: { material: string; grade: string; weight_delta_kg: number; cost_delta: number; currency: string }[];
  metadata_changes: FieldChange[];
}

export interface Region {
  x: number;
  y: number;
  width: number;
  height: number;
  kind: 'change' | 'cloud';
  score: number;
}

export interface VisualDiff {
  page: number;
  regions: Region[];
  changed_ratio: number;
}

export interface DashboardSummary {
  total_drawings: number;
  total_revisions: number;
  total_weight_kg: number;
  total_weight_t: number;
  projects: number;
  pending_ocr: number;
  failed_ocr: number;
  ocr_needs_review: number;
  ifc_drawings: number;
  bom_items: number;
}

export interface ChartPoint {
  label: string;
  value: number;
  extra?: Record<string, unknown>;
}

export interface DashboardCharts {
  drawings_by_discipline: ChartPoint[];
  weight_by_discipline: ChartPoint[];
  revision_trend: ChartPoint[];
  monthly_upload_trend: ChartPoint[];
  status_breakdown: ChartPoint[];
}

export interface MaterialAnalysisRow {
  material: string;
  grade?: string | null;
  total_weight_kg: number;
  total_quantity: number;
  share_pct: number;
  estimated_cost: number;
  drawing_count: number;
}

export interface WeightGrowthSeries {
  key: string;
  points: { period: string; delta_kg: number; cumulative_kg: number; revisions: number }[];
}

export interface BOQRow {
  project_id: number;
  discipline?: string | null;
  material: string;
  grade: string;
  unit: string;
  total_quantity: number;
  total_weight_kg: number;
  total_cost: number;
  currency: string;
  drawing_count: number;
}

export interface OCRJob {
  id: number;
  drawing_file_id: number;
  status: 'queued' | 'running' | 'completed' | 'failed';
  progress: number;
  error?: string | null;
  result_id?: number | null;
  duration_ms?: number | null;
  created_at: string;
}

export interface OCRResult {
  id: number;
  job_id: number;
  drawing_file_id: number;
  extracted: Record<string, string | null>;
  overrides: Record<string, string | null>;
  effective: Record<string, string | null>;
  field_confidences: Record<string, number>;
  overall_confidence: number;
  revision_block: Record<string, string | null>[];
  notes: string[];
  rotation: number;
  is_scanned: boolean;
  engine?: string | null;
  is_overridden: boolean;
  needs_review: boolean;
  bom_item_count: number;
}

export interface UploadItem {
  filename: string;
  relative_path?: string | null;
  status: 'created' | 'new_revision' | 'duplicate' | 'failed';
  drawing_id?: number | null;
  drawing_number?: string | null;
  revision?: string | null;
  discipline?: string | null;
  ocr_job_id?: number | null;
  error?: string | null;
}

export interface UploadSummary {
  total: number;
  succeeded: number;
  failed: number;
  duplicates: number;
  success_rate: number;
  items: UploadItem[];
}

export interface AIQueryResponse {
  question: string;
  intent: string;
  answer: string;
  columns: string[];
  rows: Record<string, unknown>[];
  sql?: string | null;
  parameters: Record<string, unknown>;
  elapsed_ms: number;
  suggestions: string[];
}
