import type { DashboardCharts, DashboardSummary, Drawing, TreeNode } from '../api/types';

export const summary: DashboardSummary = {
  total_drawings: 100, total_revisions: 300, total_weight_kg: 999786.9, total_weight_t: 999.787, projects: 3,
  pending_ocr: 2, failed_ocr: 1, ocr_needs_review: 4, ifc_drawings: 53, bom_items: 3442,
};

export const charts: DashboardCharts = {
  drawings_by_discipline: [{ label: 'STR', value: 30 }, { label: 'PIP', value: 20 }],
  weight_by_discipline: [{ label: 'STR', value: 380 }],
  revision_trend: [{ label: '2026-01', value: 10 }, { label: '2026-02', value: 12 }],
  monthly_upload_trend: [{ label: '2026-02', value: 30 }],
  status_breakdown: [{ label: 'IFC', value: 53 }],
};

export const drawing: Drawing = {
  id: 7, project_id: 1, project_code: 'NRP', discipline_id: 2, discipline_code: 'STR', area_code: 'A01',
  drawing_number: 'NRP-ST-A01-GA-0001', title: 'PIPE RACK STEEL FRAMING', drawing_type: 'GA', status: 'IFC',
  current_revision: 'B', scale: '1:50', sheet_number: '1 OF 1', vendor: null, folder_path: 'NRP/Structural/A01',
  current_weight_kg: 14322.2, revision_count: 2, ocr_confidence: 0.97, updated_at: '2026-03-01T00:00:00Z',
};

export const tree: TreeNode[] = [
  {
    id: 'p-1', label: 'NRP – North Refinery', type: 'project', entity_id: 1, count: 1, meta: {},
    children: [
      {
        id: 'disc-1-2', label: 'STR – Structural', type: 'discipline', entity_id: 2, count: 1, meta: {},
        children: [
          {
            id: 'a-1', label: 'A01 – Process Unit', type: 'area', entity_id: 1, count: 1, meta: {},
            children: [
              { id: 'd-7', label: 'NRP-ST-A01-GA-0001', type: 'drawing', entity_id: 7, count: 0,
                meta: { revision: 'B', title: 'PIPE RACK' }, children: [] },
            ],
          },
        ],
      },
    ],
  },
];
