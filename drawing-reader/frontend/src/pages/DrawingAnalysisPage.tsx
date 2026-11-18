import {
  Alert,
  Box,
  Button,
  Card,
  Chip,
  Divider,
  LinearProgress,
  MenuItem,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import CompareIcon from '@mui/icons-material/Compare';
import RefreshIcon from '@mui/icons-material/Refresh';
import type { ColDef } from 'ag-grid-community';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { errorMessage } from '../api/client';
import { useBom, useCompare, useDrawing, useOcrResult, useRunOcr } from '../api/hooks';
import type { BOMItem, DrawingDetail, Revision } from '../api/types';
import { ChartCard, TrendArea } from '../components/Charts';
import { DrawingTree } from '../components/DrawingTree';
import { Grid } from '../components/Grid';
import { PdfViewer } from '../components/PdfViewer';
import { useAuthStore } from '../store/auth';
import { useUIStore } from '../store/ui';
import { STATUS_COLORS, fmtKg, fmtMoney, fmtNum, fmtPct, fmtSigned, fmtWeight } from '../utils/format';

const BOM_COLUMNS: ColDef<BOMItem>[] = [
  { field: 'item_number', headerName: 'Item', width: 70 },
  { field: 'part_number', headerName: 'Part No', width: 130 },
  { field: 'description', headerName: 'Description', flex: 2, minWidth: 220 },
  { field: 'material', headerName: 'Material', width: 100 },
  { field: 'grade', headerName: 'Grade', width: 100 },
  { field: 'specification', headerName: 'Spec', width: 120 },
  { field: 'quantity', headerName: 'Qty', width: 80, type: 'numericColumn' },
  { field: 'unit', headerName: 'Unit', width: 70 },
  { field: 'unit_weight_kg', headerName: 'Unit Wt', width: 100, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  { field: 'total_weight_kg', headerName: 'Total Wt (kg)', width: 120, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  {
    field: 'weight_calculated',
    headerName: 'Calc.',
    width: 80,
    valueFormatter: (p) => (p.value ? 'yes' : ''),
    headerTooltip: 'Weight calculated from section tables / unit weight',
  },
];

function MetaRow({ label, value, confidence }: { label: string; value: React.ReactNode; confidence?: number }) {
  return (
    <Stack direction="row" spacing={1} sx={{ py: 0.5 }} alignItems="baseline">
      <Typography variant="body2" color="text.secondary" sx={{ width: 96, flexShrink: 0 }}>
        {label}
      </Typography>
      <Typography variant="body2" sx={{ flex: 1, wordBreak: 'break-word' }}>
        {value || '–'}
      </Typography>
      {confidence !== undefined && (
        <Tooltip title="OCR confidence">
          <Typography variant="caption" color={confidence >= 0.9 ? 'success.main' : 'warning.main'}>
            {fmtPct(confidence)}
          </Typography>
        </Tooltip>
      )}
    </Stack>
  );
}

function aiSummary(d: DrawingDetail, prev: Revision | undefined, cur: Revision | undefined, cmp: ReturnType<typeof useCompare>['data']) {
  const lines = [
    `${d.drawing_number} – ${d.title || 'untitled'} is a ${d.discipline_code ?? 'unclassified'} ${d.drawing_type ?? 'drawing'} ` +
      `currently at revision ${d.current_revision ?? '–'} (${d.status}), with ${d.revisions.length} revision(s) on record.`,
    `Current BOM weight is ${fmtWeight(d.current_weight_kg)}.`,
  ];
  if (prev && cur && cmp) {
    const s = cmp.summary;
    lines.push(
      `Since revision ${prev.revision}: ${s.added} item(s) added, ${s.removed} removed and ${s.modified} modified ` +
        `(${s.quantity_changes} quantity, ${s.material_changes} material and ${s.specification_changes} specification changes).`,
      `Weight changed by ${fmtSigned(cmp.weight_delta_kg, ' kg')}${cmp.weight_delta_pct != null ? ` (${fmtSigned(cmp.weight_delta_pct, '%')})` : ''}, ` +
        `with an estimated procurement impact of ${fmtMoney(cmp.cost_delta, cmp.currency)}.`,
    );
    const biggest = [...cmp.added, ...cmp.modified, ...cmp.removed].sort(
      (a, b) => Math.abs(b.weight_delta_kg) - Math.abs(a.weight_delta_kg),
    )[0];
    if (biggest) {
      lines.push(`Largest single change: ${biggest.change_type} ${biggest.part_number ?? biggest.description} ` +
        `(${fmtSigned(biggest.weight_delta_kg, ' kg')}).`);
    }
  } else if (d.revisions.length < 2) {
    lines.push('Only one revision exists, so no revision changes are available yet.');
  }
  return lines;
}

export function DrawingAnalysisPage() {
  const projectId = useUIStore((s) => s.projectId);
  const can = useAuthStore((s) => s.can);
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const drawingId = params.get('drawing') ? Number(params.get('drawing')) : null;
  const drawing = useDrawing(drawingId);
  const d = drawing.data;
  const [tab, setTab] = useState(0);
  const [zoom, setZoom] = useState(1);
  const [revisionId, setRevisionId] = useState<number | null>(null);
  const runOcr = useRunOcr();

  const revisions = useMemo(() => [...(d?.revisions ?? [])].sort((a, b) => a.revision_seq - b.revision_seq), [d]);
  useEffect(() => {
    setRevisionId(revisions.find((r) => r.is_current)?.id ?? revisions.at(-1)?.id ?? null);
  }, [revisions]);

  const selectedRev = revisions.find((r) => r.id === revisionId);
  const file = d?.files.filter((f) => f.revision_id === revisionId).at(-1) ?? d?.files.at(-1);
  const idx = revisions.findIndex((r) => r.id === revisionId);
  const prevRev = idx > 0 ? revisions[idx - 1] : undefined;
  const bom = useBom({ revision_id: revisionId }, !!revisionId);
  const cmp = useCompare(prevRev?.id ?? null, selectedRev?.id ?? null);
  const ocr = useOcrResult(d?.latest_ocr_result_id);
  const conf = ocr.data?.field_confidences ?? {};

  const weightTrend = revisions.map((r) => ({ label: `Rev ${r.revision}`, value: Number(r.total_weight_kg) }));

  return (
    <Box sx={{ display: 'grid', gap: 1.5, height: 'calc(100vh - 80px)',
      gridTemplateColumns: { xs: '1fr', lg: '280px minmax(0,1fr) 320px' },
      gridTemplateRows: { xs: 'auto', lg: 'minmax(0, 3fr) minmax(0, 2fr)' } }}>
      <Card sx={{ gridRow: { lg: '1 / span 2' }, minHeight: { xs: 300, lg: 0 }, display: 'flex', flexDirection: 'column' }}>
        <Typography variant="h6" sx={{ px: 1.5, pt: 1 }}>Drawings</Typography>
        <Box sx={{ flex: 1, minHeight: 0 }}>
          <DrawingTree projectId={projectId} selectedId={drawingId} onSelect={(id) => setParams({ drawing: String(id) })} />
        </Box>
      </Card>

      <Card sx={{ minHeight: { xs: 420, lg: 0 }, display: 'flex', flexDirection: 'column' }}>
        {drawing.isFetching && <LinearProgress sx={{ height: 2 }} />}
        <PdfViewer fileId={file?.id} zoom={zoom} onZoomChange={setZoom}
          label={d ? `${d.drawing_number} · Rev ${selectedRev?.revision ?? ''}` : undefined} />
      </Card>

      <Card sx={{ overflow: 'auto', p: 1.5, minHeight: { xs: 300, lg: 0 } }}>
        {!d && !drawing.isLoading && <Typography color="text.secondary">Select a drawing from the tree.</Typography>}
        {drawing.error && <Alert severity="error">{errorMessage(drawing.error)}</Alert>}
        {d && (
          <Stack spacing={0.5}>
            <Typography variant="subtitle1" fontWeight={700}>{d.drawing_number}</Typography>
            <Typography variant="body2">{d.title}</Typography>
            <Stack direction="row" spacing={1} sx={{ py: 1 }}>
              <Chip size="small" label={d.status} color={STATUS_COLORS[d.status] ?? 'default'} />
              {d.discipline_code && <Chip size="small" variant="outlined" label={d.discipline_code} />}
              {d.tags.map((t) => <Chip key={t} size="small" variant="outlined" label={t} />)}
            </Stack>
            <TextField select size="small" label="Revision" value={revisionId ?? ''} onChange={(e) => setRevisionId(Number(e.target.value))}>
              {[...revisions].reverse().map((r) => (
                <MenuItem key={r.id} value={r.id}>
                  {r.revision} · {r.status} · {r.revision_date ?? ''} {r.is_current ? '(current)' : ''}
                </MenuItem>
              ))}
            </TextField>
            <Divider sx={{ my: 1 }} />
            <MetaRow label="Project" value={d.project_code} />
            <MetaRow label="Area" value={d.area_code} />
            <MetaRow label="Type" value={d.drawing_type} />
            <MetaRow label="Revision" value={d.current_revision} confidence={conf.revision} />
            <MetaRow label="Scale" value={d.scale} confidence={conf.scale} />
            <MetaRow label="Sheet" value={d.sheet_number} confidence={conf.sheet_number} />
            <MetaRow label="Vendor" value={d.vendor} confidence={conf.vendor} />
            <MetaRow label="Checker" value={selectedRev?.checker} confidence={conf.checker} />
            <MetaRow label="Approver" value={selectedRev?.approver} confidence={conf.approver} />
            <MetaRow label="Weight" value={fmtKg(selectedRev?.total_weight_kg)} />
            <MetaRow label="Folder" value={d.folder_path} />
            {ocr.data && (
              <Alert severity={ocr.data.needs_review ? 'warning' : 'success'} sx={{ mt: 1 }}>
                OCR {fmtPct(ocr.data.overall_confidence)} via {ocr.data.engine}
                {ocr.data.is_scanned ? ' (scanned)' : ''}{ocr.data.rotation ? `, rotated ${ocr.data.rotation}°` : ''}
                {ocr.data.is_overridden ? ' · manually reviewed' : ''}
              </Alert>
            )}
            <Stack direction="row" spacing={1} sx={{ pt: 1 }}>
              <Button size="small" startIcon={<CompareIcon />} disabled={revisions.length < 2}
                onClick={() => navigate(`/compare?drawing=${d.id}`)}>Compare</Button>
              {can('ocr.process') && (
                <Button size="small" startIcon={<RefreshIcon />} disabled={runOcr.isPending} onClick={() => runOcr.mutate([d.id])}>
                  Re-run OCR
                </Button>
              )}
            </Stack>
          </Stack>
        )}
      </Card>

      <Card sx={{ gridColumn: { lg: '2 / span 2' }, display: 'flex', flexDirection: 'column', minHeight: { xs: 360, lg: 0 } }}>
        <Tabs value={tab} onChange={(_, v) => setTab(v)} variant="scrollable" sx={{ px: 1, borderBottom: 1, borderColor: 'divider' }}>
          <Tab label={`BOM${bom.data ? ` (${bom.data.meta.total})` : ''}`} />
          <Tab label="Revision History" />
          <Tab label="Weight Analysis" />
          <Tab label="BOQ Impact" />
          <Tab label="AI Summary" />
        </Tabs>
        <Box sx={{ flex: 1, minHeight: 0, p: 1, overflow: 'auto' }}>
          {tab === 0 && <Grid<BOMItem> rows={bom.data?.data ?? []} columns={BOM_COLUMNS} loading={bom.isFetching} height="100%" />}
          {tab === 1 && (
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Rev</TableCell><TableCell>Date</TableCell><TableCell>Status</TableCell>
                  <TableCell>Description</TableCell><TableCell>Checker</TableCell><TableCell>Approver</TableCell>
                  <TableCell align="right">Weight</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {[...revisions].reverse().map((r) => (
                  <TableRow key={r.id} hover selected={r.id === revisionId} onClick={() => setRevisionId(r.id)} sx={{ cursor: 'pointer' }}>
                    <TableCell>{r.revision}{r.is_current ? ' ★' : ''}</TableCell>
                    <TableCell>{r.revision_date}</TableCell>
                    <TableCell><Chip size="small" label={r.status} color={STATUS_COLORS[r.status] ?? 'default'} /></TableCell>
                    <TableCell>{r.description}</TableCell>
                    <TableCell>{r.checker}</TableCell>
                    <TableCell>{r.approver}</TableCell>
                    <TableCell align="right">{fmtKg(r.total_weight_kg)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
          {tab === 2 && (
            <ChartCard title="Weight by revision" subtitle="kg, total BOM weight" height={200} empty={!weightTrend.length}>
              <TrendArea data={weightTrend} unit=" kg" name="Weight" />
            </ChartCard>
          )}
          {tab === 3 && (
            !prevRev ? <Typography color="text.secondary">No previous revision to compare against.</Typography> :
            cmp.isLoading ? <LinearProgress /> : cmp.data && (
              <Stack spacing={1}>
                <Typography variant="body2">
                  Rev {cmp.data.from_revision} → {cmp.data.to_revision}: weight {fmtSigned(cmp.data.weight_delta_kg, ' kg')},
                  cost {fmtMoney(cmp.data.cost_delta, cmp.data.currency)}
                </Typography>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Material</TableCell><TableCell>Grade</TableCell>
                      <TableCell align="right">Δ Weight (kg)</TableCell><TableCell align="right">Δ Cost</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {cmp.data.cost_impacts.map((c) => (
                      <TableRow key={`${c.material}-${c.grade}`}>
                        <TableCell>{c.material}</TableCell><TableCell>{c.grade}</TableCell>
                        <TableCell align="right">{fmtSigned(c.weight_delta_kg)}</TableCell>
                        <TableCell align="right">{fmtMoney(c.cost_delta, c.currency)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Stack>
            )
          )}
          {tab === 4 && d && (
            <Stack spacing={1} sx={{ maxWidth: 900 }}>
              {aiSummary(d, prevRev, selectedRev, cmp.data).map((line, i) => (
                <Typography key={i} variant="body2">{line}</Typography>
              ))}
              {ocr.data?.notes?.length ? (
                <>
                  <Typography variant="subtitle2" sx={{ pt: 1 }}>Drawing notes (OCR)</Typography>
                  {ocr.data.notes.map((n, i) => <Typography key={i} variant="body2">{i + 1}. {n}</Typography>)}
                </>
              ) : null}
            </Stack>
          )}
        </Box>
      </Card>
    </Box>
  );
}
