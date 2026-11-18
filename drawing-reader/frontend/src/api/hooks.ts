import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import type {
  AIQueryResponse,
  ApiResponse,
  BOMItem,
  BOQRow,
  DashboardCharts,
  DashboardSummary,
  Discipline,
  Drawing,
  DrawingDetail,
  MaterialAnalysisRow,
  OCRJob,
  OCRResult,
  Page,
  Project,
  Revision,
  RevisionComparison,
  TreeNode,
  UploadSummary,
  User,
  VisualDiff,
  WeightGrowthSeries,
} from './types';

type Params = Record<string, string | number | boolean | null | undefined>;

const clean = (p: Params) => Object.fromEntries(Object.entries(p).filter(([, v]) => v !== undefined && v !== null && v !== ''));

async function get<T>(url: string, params: Params = {}): Promise<T> {
  const res = await api.get<T>(url, { params: clean(params) });
  return res.data;
}

export const useMe = (enabled: boolean) =>
  useQuery({ queryKey: ['me'], queryFn: () => get<ApiResponse<User>>('/auth/me').then((r) => r.data), enabled });

export const useProjects = () =>
  useQuery({
    queryKey: ['projects'],
    queryFn: () => get<Page<Project>>('/projects', { page_size: 200 }).then((r) => r.data),
    staleTime: 60_000,
  });

export const useDisciplines = () =>
  useQuery({
    queryKey: ['disciplines'],
    queryFn: () => get<ApiResponse<Discipline[]>>('/disciplines').then((r) => r.data),
    staleTime: 300_000,
  });

export const useDrawings = (params: Params) =>
  useQuery({
    queryKey: ['drawings', params],
    queryFn: () => get<Page<Drawing>>('/drawings', params),
    placeholderData: keepPreviousData,
  });

export const useDrawing = (id: number | null) =>
  useQuery({
    queryKey: ['drawing', id],
    queryFn: () => get<ApiResponse<DrawingDetail>>(`/drawings/${id}`).then((r) => r.data),
    enabled: !!id,
  });

export const useTree = (projectId: number | null, search: string) =>
  useQuery({
    queryKey: ['tree', projectId, search],
    queryFn: () => get<ApiResponse<TreeNode[]>>('/drawings/tree', { project_id: projectId, search }).then((r) => r.data),
    placeholderData: keepPreviousData,
  });

export const useRevisions = (drawingId: number | null) =>
  useQuery({
    queryKey: ['revisions', drawingId],
    queryFn: () => get<Page<Revision>>('/revisions', { drawing_id: drawingId, page_size: 200 }).then((r) => r.data),
    enabled: !!drawingId,
  });

export const useBom = (params: Params, enabled = true) =>
  useQuery({
    queryKey: ['bom', params],
    queryFn: () => get<Page<BOMItem>>('/bom', { page_size: 1000, ...params }),
    enabled,
  });

export const useCompare = (fromId: number | null, toId: number | null) =>
  useQuery({
    queryKey: ['compare', fromId, toId],
    queryFn: () =>
      get<ApiResponse<RevisionComparison>>('/revisions/compare', { from_revision_id: fromId, to_revision_id: toId }).then(
        (r) => r.data,
      ),
    enabled: !!fromId && !!toId && fromId !== toId,
  });

export const useVisualDiff = (fromId: number | null, toId: number | null, page: number, enabled: boolean) =>
  useQuery({
    queryKey: ['visual-diff', fromId, toId, page],
    queryFn: () =>
      get<ApiResponse<VisualDiff>>('/revisions/visual-diff', { from_revision_id: fromId, to_revision_id: toId, page }).then(
        (r) => r.data,
      ),
    enabled: enabled && !!fromId && !!toId && fromId !== toId,
    retry: false,
  });

export const useDashboard = (projectId: number | null) => {
  const summary = useQuery({
    queryKey: ['dashboard', 'summary', projectId],
    queryFn: () => get<ApiResponse<DashboardSummary>>('/dashboard/summary', { project_id: projectId }).then((r) => r.data),
    refetchInterval: 30_000,
  });
  const charts = useQuery({
    queryKey: ['dashboard', 'charts', projectId],
    queryFn: () => get<ApiResponse<DashboardCharts>>('/dashboard/charts', { project_id: projectId }).then((r) => r.data),
    refetchInterval: 60_000,
  });
  return { summary, charts };
};

export const useMaterialAnalysis = (projectId: number | null) =>
  useQuery({
    queryKey: ['material-analysis', projectId],
    queryFn: () =>
      get<ApiResponse<MaterialAnalysisRow[]>>('/analytics/material-analysis', { project_id: projectId }).then((r) => r.data),
  });

export const useWeightGrowth = (projectId: number | null, groupBy: 'project' | 'discipline') =>
  useQuery({
    queryKey: ['weight-growth', projectId, groupBy],
    queryFn: () =>
      get<ApiResponse<WeightGrowthSeries[]>>('/analytics/weight-growth', { project_id: projectId, group_by: groupBy }).then(
        (r) => r.data,
      ),
  });

export const useBoq = (projectId: number | null) =>
  useQuery({
    queryKey: ['boq', projectId],
    queryFn: () => get<ApiResponse<BOQRow[]>>('/bom/boq', { project_id: projectId }).then((r) => r.data),
  });

export const useOcrResult = (id: number | null | undefined) =>
  useQuery({
    queryKey: ['ocr-result', id],
    queryFn: () => get<ApiResponse<OCRResult>>(`/ocr/result/${id}`).then((r) => r.data),
    enabled: !!id,
  });

export const useReviewQueue = () =>
  useQuery({
    queryKey: ['ocr-review'],
    queryFn: () => get<Page<OCRResult>>('/ocr/review-queue', { page_size: 200 }).then((r) => r.data),
  });

export const useOcrJobs = (status?: string) =>
  useQuery({
    queryKey: ['ocr-jobs', status],
    queryFn: () => get<Page<OCRJob>>('/ocr/jobs', { status, page_size: 100 }),
    refetchInterval: 5_000,
  });

export function useUpload() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (args: {
      projectId: number;
      files: File[];
      folder: boolean;
      runOcr: boolean;
      onProgress?: (pct: number) => void;
    }) => {
      const form = new FormData();
      form.append('project_id', String(args.projectId));
      form.append('run_ocr', String(args.runOcr));
      for (const f of args.files) {
        form.append('files', f, f.name);
        if (args.folder) form.append('paths', (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name);
      }
      const res = await api.post<ApiResponse<UploadSummary>>(
        args.folder ? '/drawings/folder-upload' : '/drawings/upload',
        form,
        { onUploadProgress: (e) => e.total && args.onProgress?.(Math.round((e.loaded / e.total) * 100)), timeout: 0 },
      );
      return res.data.data;
    },
    onSuccess: () => qc.invalidateQueries(),
  });
}

export function useOcrOverride() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, values }: { id: number; values: Record<string, string | null> }) =>
      (await api.put<ApiResponse<OCRResult>>(`/ocr/result/${id}`, { ...values, apply_to_register: true })).data.data,
    onSuccess: () => qc.invalidateQueries(),
  });
}

export function useRunOcr() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (drawingIds: number[]) =>
      (await api.post<ApiResponse<OCRJob[]>>('/ocr/process', { drawing_ids: drawingIds })).data.data,
    onSuccess: () => qc.invalidateQueries(),
  });
}

export function useAiQuery() {
  return useMutation({
    mutationFn: async (args: { question: string; projectId: number | null }) =>
      (await api.post<ApiResponse<AIQueryResponse>>('/ai/query', { question: args.question, project_id: args.projectId }))
        .data.data,
  });
}
