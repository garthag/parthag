import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import type { DrawingDetail, OCRResult, RevisionComparison } from '../api/types';
import { UploadDialog } from '../components/UploadDialog';
import { useAuthStore } from '../store/auth';
import { useUIStore } from '../store/ui';
import { drawing, tree } from '../test/fixtures';
import { mockGet, ok, page, renderWithProviders } from '../test/utils';
import { AnalyticsPage } from './AnalyticsPage';
import { BomPage } from './BomPage';
import { DrawingAnalysisPage } from './DrawingAnalysisPage';
import { OcrReviewPage } from './OcrReviewPage';
import { RegisterPage } from './RegisterPage';
import { RevisionComparePage } from './RevisionComparePage';

vi.mock('../components/PdfViewer', async () => {
  const React = await import('react');
  return {
    PdfViewer: React.forwardRef(function Mock(
      { label, regions = [], onScrollFraction, onZoomChange }: {
        label?: string; regions?: { kind: string }[]; onScrollFraction?: (x: number, y: number) => void; onZoomChange: (z: number) => void;
      },
      ref: React.Ref<{ scrollToFraction: () => void }>,
    ) {
      React.useImperativeHandle(ref, () => ({ scrollToFraction: () => undefined }));
      return (
        <div data-testid="pdf-viewer" onClick={() => { onScrollFraction?.(0.5, 0.5); onZoomChange(2); }}>
          {label} regions:{regions.length}
        </div>
      );
    }),
  };
});

const admin = { id: 1, email: 'a@b.c', username: 'admin', full_name: 'Admin', is_superuser: true, roles: [], permissions: [] };

const detail: DrawingDetail = {
  ...drawing,
  total_sheets: 1, originator: 'EPC', remarks: null, tags: ['critical'], latest_ocr_result_id: 11,
  revisions: [
    { id: 1, drawing_id: 7, revision: 'A', revision_seq: 20100, revision_date: '2026-01-01', status: 'IFR', description: 'ISSUED FOR REVIEW', is_current: false, total_weight_kg: 1000 },
    { id: 2, drawing_id: 7, revision: 'B', revision_seq: 20200, revision_date: '2026-02-01', status: 'IFC', description: 'IFC', checker: 'MK', approver: 'PS', is_current: true, total_weight_kg: 1500 },
  ],
  files: [
    { id: 21, revision_id: 1, original_filename: 'a.pdf', file_size: 100, upload_status: 'processed' },
    { id: 22, revision_id: 2, original_filename: 'b.pdf', file_size: 100, upload_status: 'processed' },
  ],
};

const comparison: RevisionComparison = {
  drawing_id: 7, drawing_number: drawing.drawing_number, from_revision: 'A', to_revision: 'B', from_revision_id: 1, to_revision_id: 2,
  from_weight_kg: 1000, to_weight_kg: 1500, weight_delta_kg: 500, weight_delta_pct: 50, cost_delta: 1250, currency: 'USD',
  summary: { added: 1, removed: 0, modified: 1, unchanged: 3, quantity_changes: 1, material_changes: 0, specification_changes: 0 },
  added: [{ key: 'P:X', change_type: 'added', part_number: 'X-1', description: 'PLATE', quantity_delta: 2, weight_delta_kg: 400, cost_delta: 1000, categories: [], changes: [] }],
  removed: [],
  modified: [{ key: 'P:Y', change_type: 'modified', part_number: 'Y-1', description: 'BEAM', quantity_delta: 1, weight_delta_kg: 100, cost_delta: 250, categories: ['quantity'], changes: [{ field: 'quantity', old: 1, new: 2 }] }],
  unchanged_count: 3,
  cost_impacts: [{ material: 'CS', grade: 'S355JR', weight_delta_kg: 500, cost_delta: 1250, currency: 'USD' }],
  metadata_changes: [],
};

const ocrResult: OCRResult = {
  id: 11, job_id: 5, drawing_file_id: 22, extracted: { drawing_number: 'NRP-ST-A01-GA-0001', title: 'PIPE RACK' },
  overrides: {}, effective: { drawing_number: 'NRP-ST-A01-GA-0001', title: 'PIPE RACK' },
  field_confidences: { drawing_number: 0.99, title: 0.6 }, overall_confidence: 0.82, revision_block: [], notes: ['ALL DIMENSIONS IN MM'],
  rotation: 90, is_scanned: true, engine: 'tesseract', is_overridden: false, needs_review: true, bom_item_count: 3,
};

const bomRow = { id: 1, line_no: 1, item_number: '1', part_number: 'X-1', description: 'PLATE', material: 'CS', grade: 'S355JR',
  quantity: 2, unit: 'EA', unit_weight_kg: 10, total_weight_kg: 20, weight_calculated: false, drawing_number: 'D', revision: 'B' };

function apiRoutes() {
  return mockGet({
    '/drawings/tree': () => ok(tree),
    '/drawings/7': () => ok(detail),
    '/drawings': () => page([drawing]),
    '/disciplines': () => ok([{ id: 2, code: 'STR', name: 'Structural' }]),
    '/projects': () => page([{ id: 1, code: 'NRP', name: 'North Refinery', status: 'active', drawing_count: 1 }]),
    '/bom/boq': () => ok([{ project_id: 1, discipline: 'STR', material: 'CS', grade: 'S355JR', unit: 'EA', total_quantity: 10, total_weight_kg: 2000, total_cost: 5000, currency: 'USD', drawing_count: 3 }]),
    '/bom': () => page([bomRow]),
    '/revisions/compare': () => ok(comparison),
    '/revisions/visual-diff': () => ok({ page: 0, changed_ratio: 0.01, regions: [
      { x: 0.1, y: 0.1, width: 0.1, height: 0.1, kind: 'change', score: 1 },
      { x: 0.2, y: 0.2, width: 0.1, height: 0.1, kind: 'cloud', score: 0.8 }] }),
    '/ocr/result/11': () => ok(ocrResult),
    '/ocr/review-queue': () => page([ocrResult]),
    '/ocr/jobs': () => page([]),
    '/analytics/material-analysis': () => ok([{ material: 'CS', grade: 'S355JR', total_weight_kg: 5000, total_quantity: 10, share_pct: 100, estimated_cost: 12000, drawing_count: 3 }]),
    '/analytics/weight-growth': () => ok(['NRP', 'LNG2', 'SPS', 'X'].map((key, i) => ({
      key, points: [{ period: '2026-01', delta_kg: 100 * (i + 1), cumulative_kg: 100 * (i + 1), revisions: 1 },
        { period: '2026-02', delta_kg: 50, cumulative_kg: 100 * (i + 1) + 50, revisions: 1 }] }))),
  });
}

beforeEach(() => {
  useAuthStore.setState({ accessToken: 'acc', refreshToken: 'ref', user: admin });
  useUIStore.setState({ projectId: null, mode: 'light' });
});
afterEach(() => vi.restoreAllMocks());

describe('RegisterPage', () => {
  it('loads drawings, filters and paginates', async () => {
    const get = apiRoutes();
    renderWithProviders(<RegisterPage />);
    expect(await screen.findByText(/1 drawings · page 1 of 1/)).toBeInTheDocument();
    await userEvent.type(screen.getByPlaceholderText(/search drawing number/i), 'rack');
    await waitFor(() => expect(get).toHaveBeenCalledWith('/drawings', expect.objectContaining({ params: expect.objectContaining({ search: 'rack' }) })));
    expect(screen.getByRole('button', { name: /upload drawings/i })).toBeInTheDocument();
  });
});

describe('UploadDialog', () => {
  it('uploads selected files in batches and shows the summary', async () => {
    apiRoutes();
    useUIStore.setState({ projectId: 1 });
    const post = vi.spyOn(api, 'post').mockResolvedValue({ data: ok({ total: 1, succeeded: 1, failed: 0, duplicates: 0, success_rate: 100,
      items: [{ filename: 'a.pdf', status: 'created', drawing_number: 'A-1', revision: 'A', discipline: 'STR' }] }) });
    renderWithProviders(<UploadDialog open onClose={() => {}} />);
    const input = screen.getByTestId('file-input') as HTMLInputElement;
    const file = new File(['%PDF-1.7'], 'a.pdf', { type: 'application/pdf' });
    await act(async () => { fireEvent.change(input, { target: { files: [file, new File(['x'], 'notes.txt')] } }); });
    expect(screen.getByText(/1 file selected/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /upload 1/i }));
    expect(await screen.findByText('1 registered')).toBeInTheDocument();
    expect(screen.getByText('100.0% success')).toBeInTheDocument();
    expect(post.mock.calls[0][0]).toBe('/drawings/upload');
  });
});

describe('DrawingAnalysisPage', () => {
  it('shows tree, viewer, metadata and the bottom tabs', async () => {
    apiRoutes();
    renderWithProviders(<DrawingAnalysisPage />, { route: '/analysis?drawing=7' });
    expect(await screen.findByText('PIPE RACK STEEL FRAMING')).toBeInTheDocument();
    expect(await screen.findByText(/OCR 82% via tesseract \(scanned\), rotated 90°/)).toBeInTheDocument();
    expect(screen.getByTestId('pdf-viewer')).toHaveTextContent('Rev B');
    fireEvent.click(screen.getByRole('tab', { name: 'Revision History' }));
    expect(await screen.findByText('ISSUED FOR REVIEW')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Weight Analysis' }));
    expect(screen.getByText('Weight by revision')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'BOQ Impact' }));
    expect(await screen.findByText('S355JR')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'AI Summary' }));
    expect(await screen.findByText(/1 item\(s\) added, 0 removed and 1 modified/)).toBeInTheDocument();
    expect(screen.getByText('1. ALL DIMENSIONS IN MM')).toBeInTheDocument();
    const post = vi.spyOn(api, 'post').mockResolvedValue({ data: ok([]) });
    fireEvent.click(screen.getByRole('button', { name: /re-run ocr/i }));
    await waitFor(() => expect(post).toHaveBeenCalledWith('/ocr/process', { drawing_ids: [7] }));
  });

  it('asks the user to pick a drawing', async () => {
    apiRoutes();
    renderWithProviders(<DrawingAnalysisPage />, { route: '/analysis' });
    expect(await screen.findByText('Select a drawing from the tree.')).toBeInTheDocument();
  });
});

describe('RevisionComparePage', () => {
  it('compares the last two revisions with highlights and impact', async () => {
    apiRoutes();
    renderWithProviders(<RevisionComparePage />, { route: '/compare?drawing=7' });
    expect(await screen.findByText('+500 kg')).toBeInTheDocument();
    expect(screen.getByText('$1,250')).toBeInTheDocument();
    expect(await screen.findByText('1 revision cloud detected')).toBeInTheDocument();
    expect(screen.getByText('1 changed regions')).toBeInTheDocument();
    const viewers = screen.getAllByTestId('pdf-viewer');
    expect(viewers[1]).toHaveTextContent('regions:2');
    fireEvent.click(viewers[0]);
    fireEvent.click(screen.getByLabelText('Highlight differences'));
    await waitFor(() => expect(screen.getAllByTestId('pdf-viewer')[1]).toHaveTextContent('regions:0'));
  });
});

describe('BomPage, AnalyticsPage and OcrReviewPage', () => {
  it('renders BOQ totals and switches tabs', async () => {
    apiRoutes();
    renderWithProviders(<BomPage />);
    expect(await screen.findByText('2 t')).toBeInTheDocument();
    expect(screen.getAllByText("$5,000").length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('tab', { name: /bom items/i }));
    await userEvent.type(screen.getByPlaceholderText('Quick filter'), 'plate');
  });

  it('renders analytics and folds extra series into Other', async () => {
    apiRoutes();
    renderWithProviders(<AnalyticsPage />);
    expect(await screen.findByText('Cumulative weight growth')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'By discipline' }));
    expect(screen.getByText('Material analysis')).toBeInTheDocument();
  });

  it('reviews and overrides an OCR result', async () => {
    apiRoutes();
    const put = vi.spyOn(api, 'put').mockResolvedValue({ data: ok(ocrResult) });
    renderWithProviders(<OcrReviewPage />);
    const title = await screen.findByLabelText('Title');
    await userEvent.clear(title);
    await userEvent.type(title, 'PIPE RACK PR-01');
    fireEvent.click(screen.getByRole('button', { name: /confirm/i }));
    await waitFor(() => expect(put).toHaveBeenCalledWith('/ocr/result/11', expect.objectContaining({ title: 'PIPE RACK PR-01', apply_to_register: true })));
  });
});
