import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Card,
  Chip,
  FormControlLabel,
  Grid2 as MuiGrid,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography,
  useTheme,
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import type { ColDef } from 'ag-grid-community';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { downloadFile, errorMessage } from '../api/client';
import { useCompare, useDrawing, useDrawings, useVisualDiff } from '../api/hooks';
import type { Drawing, ItemDiff } from '../api/types';
import { KpiCard } from '../components/Charts';
import { Grid } from '../components/Grid';
import { PdfViewer, type PdfViewerHandle } from '../components/PdfViewer';
import { chartColors } from '../theme';
import { useUIStore } from '../store/ui';
import { fmtMoney, fmtNum, fmtSigned, fmtWeight } from '../utils/format';

const DIFF_COLUMNS: ColDef<ItemDiff>[] = [
  { field: 'change_type', headerName: 'Change', width: 110, pinned: 'left' },
  { field: 'item_number', headerName: 'Item', width: 70 },
  { field: 'part_number', headerName: 'Part No', width: 130 },
  { field: 'description', headerName: 'Description', flex: 2, minWidth: 200 },
  { field: 'material', headerName: 'Material', width: 90 },
  { field: 'grade', headerName: 'Grade', width: 100 },
  { field: 'old_quantity', headerName: 'Old Qty', width: 90, type: 'numericColumn' },
  { field: 'new_quantity', headerName: 'New Qty', width: 90, type: 'numericColumn' },
  { field: 'weight_delta_kg', headerName: 'Δ Wt (kg)', width: 110, type: 'numericColumn', valueFormatter: (p) => fmtSigned(p.value) },
  { field: 'cost_delta', headerName: 'Δ Cost', width: 110, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  { headerName: 'Categories', width: 170, valueGetter: (p) => p.data?.categories.join(', ') },
  {
    headerName: 'Field changes',
    flex: 2,
    minWidth: 240,
    valueGetter: (p) => p.data?.changes.map((c) => `${c.field}: ${String(c.old ?? '–')} → ${String(c.new ?? '–')}`).join('; '),
  },
];

export function RevisionComparePage() {
  const projectId = useUIStore((s) => s.projectId);
  const mode = useTheme().palette.mode;
  const colors = chartColors[mode];
  const [params, setParams] = useSearchParams();
  const drawingId = params.get('drawing') ? Number(params.get('drawing')) : null;
  const [search, setSearch] = useState('');
  const options = useDrawings({ project_id: projectId, search, page_size: 50 });
  const drawing = useDrawing(drawingId);
  const revisions = useMemo(
    () => [...(drawing.data?.revisions ?? [])].sort((a, b) => a.revision_seq - b.revision_seq),
    [drawing.data],
  );
  const [fromId, setFromId] = useState<number | null>(null);
  const [toId, setToId] = useState<number | null>(null);
  const [zoom, setZoom] = useState(1);
  const [sync, setSync] = useState(true);
  const [highlight, setHighlight] = useState(true);
  const left = useRef<PdfViewerHandle>(null);
  const right = useRef<PdfViewerHandle>(null);

  useEffect(() => {
    if (revisions.length >= 2) {
      setFromId(revisions.at(-2)!.id);
      setToId(revisions.at(-1)!.id);
    } else {
      setFromId(null);
      setToId(null);
    }
  }, [revisions]);

  const cmp = useCompare(fromId, toId);
  const visual = useVisualDiff(fromId, toId, 0, highlight);
  const files = drawing.data?.files ?? [];
  const fileFor = (revId: number | null) => files.filter((f) => f.revision_id === revId).at(-1)?.id;
  const diffRows = useMemo(() => (cmp.data ? [...cmp.data.added, ...cmp.data.removed, ...cmp.data.modified] : []), [cmp.data]);
  const changeRegions = visual.data?.regions.filter((r) => r.kind === 'change') ?? [];
  const allRegions = visual.data?.regions ?? [];
  const clouds = allRegions.filter((r) => r.kind === 'cloud').length;
  const selected = options.data?.data.find((o) => o.id === drawingId) ?? (drawing.data as Drawing | undefined) ?? null;

  const exportReport = (format: string) =>
    downloadFile('/reports/revision-difference', {
      format, drawing_id: drawingId, from_revision: cmp.data?.from_revision, to_revision: cmp.data?.to_revision,
    }, `revision-difference.${format}`);

  const revSelect = (label: string, value: number | null, onChange: (v: number) => void) => (
    <TextField select size="small" label={label} value={value ?? ''} onChange={(e) => onChange(Number(e.target.value))} sx={{ minWidth: 200 }}>
      {revisions.map((r) => (
        <MenuItem key={r.id} value={r.id}>Rev {r.revision} · {r.status} · {r.revision_date}</MenuItem>
      ))}
    </TextField>
  );

  return (
    <Stack spacing={1.5}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems={{ md: 'center' }}>
        <Typography variant="h5" sx={{ mr: 1 }}>Revision Compare</Typography>
        <Autocomplete<Drawing>
          size="small"
          sx={{ minWidth: 320, flex: 1, maxWidth: 520 }}
          options={options.data?.data ?? []}
          value={selected}
          getOptionLabel={(o) => `${o.drawing_number} – ${o.title}`}
          isOptionEqualToValue={(a, b) => a.id === b.id}
          onInputChange={(_, v, reason) => reason === 'input' && setSearch(v)}
          onChange={(_, v) => v && setParams({ drawing: String(v.id) })}
          renderInput={(p) => <TextField {...p} label="Drawing" placeholder="Search drawings" />}
        />
        {revSelect('Previous revision', fromId, setFromId)}
        {revSelect('Current revision', toId, setToId)}
      </Stack>
      <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap" useFlexGap>
        <FormControlLabel control={<Switch checked={sync} onChange={(e) => setSync(e.target.checked)} />} label="Sync zoom & scroll" />
        <FormControlLabel control={<Switch checked={highlight} onChange={(e) => setHighlight(e.target.checked)} />} label="Highlight differences" />
        {highlight && visual.data && (
          <>
            <Chip size="small" label={`${changeRegions.length} changed regions`} sx={{ borderLeft: '4px solid #e34948' }} />
            <Chip size="small" label={`${clouds} revision cloud${clouds === 1 ? '' : 's'} detected`} sx={{ borderLeft: '4px dashed #6250d6' }} />
          </>
        )}
        <Box sx={{ flex: 1 }} />
        <Button size="small" startIcon={<DownloadIcon />} disabled={!cmp.data} onClick={() => exportReport('xlsx')}>Excel</Button>
        <Button size="small" startIcon={<DownloadIcon />} disabled={!cmp.data} onClick={() => exportReport('pdf')}>PDF</Button>
        <Button size="small" startIcon={<DownloadIcon />} disabled={!cmp.data} onClick={() => exportReport('csv')}>CSV</Button>
      </Stack>
      {revisions.length === 1 && <Alert severity="info">This drawing has only one revision.</Alert>}
      {cmp.error && <Alert severity="error">{errorMessage(cmp.error)}</Alert>}

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 1.5, height: { xs: 'auto', md: '52vh' } }}>
        <Card sx={{ height: { xs: 420, md: '100%' } }}>
          <PdfViewer ref={left} fileId={fileFor(fromId)} zoom={zoom} onZoomChange={setZoom}
            label={`Previous · Rev ${cmp.data?.from_revision ?? ''}`}
            regions={highlight ? changeRegions : []}
            onScrollFraction={(x, y) => sync && right.current?.scrollToFraction(x, y)} />
        </Card>
        <Card sx={{ height: { xs: 420, md: '100%' } }}>
          <PdfViewer ref={right} fileId={fileFor(toId)} zoom={zoom} onZoomChange={setZoom}
            label={`Current · Rev ${cmp.data?.to_revision ?? ''}`}
            regions={highlight ? allRegions : []}
            onScrollFraction={(x, y) => sync && left.current?.scrollToFraction(x, y)} />
        </Card>
      </Box>

      {cmp.data && (
        <>
          <MuiGrid container spacing={1.5}>
            <MuiGrid size={{ xs: 6, md: 3 }}>
              <KpiCard label="Weight impact" value={fmtSigned(cmp.data.weight_delta_kg, ' kg')}
                hint={`${fmtWeight(cmp.data.from_weight_kg)} → ${fmtWeight(cmp.data.to_weight_kg)}${cmp.data.weight_delta_pct != null ? ` (${fmtSigned(cmp.data.weight_delta_pct, '%')})` : ''}`} />
            </MuiGrid>
            <MuiGrid size={{ xs: 6, md: 3 }}>
              <KpiCard label="Cost impact" value={fmtMoney(cmp.data.cost_delta, cmp.data.currency)}
                hint={`${cmp.data.cost_impacts.length} material/grade lines`} />
            </MuiGrid>
            <MuiGrid size={{ xs: 12, md: 6 }}>
              <Card sx={{ p: 1.5, height: '100%' }}>
                <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
                  <Chip label={`${cmp.data.summary.added} added`} sx={{ borderLeft: `4px solid ${colors.added}` }} />
                  <Chip label={`${cmp.data.summary.removed} removed`} sx={{ borderLeft: `4px solid ${colors.removed}` }} />
                  <Chip label={`${cmp.data.summary.modified} modified`} sx={{ borderLeft: `4px solid ${colors.modified}` }} />
                  <Chip variant="outlined" label={`${cmp.data.summary.quantity_changes} quantity`} />
                  <Chip variant="outlined" label={`${cmp.data.summary.material_changes} material`} />
                  <Chip variant="outlined" label={`${cmp.data.summary.specification_changes} specification`} />
                  <Chip variant="outlined" label={`${cmp.data.unchanged_count} unchanged`} />
                </Stack>
              </Card>
            </MuiGrid>
          </MuiGrid>
          <Box sx={{
            '& .diff-added': { boxShadow: `inset 4px 0 0 ${colors.added}` },
            '& .diff-removed': { boxShadow: `inset 4px 0 0 ${colors.removed}`, textDecoration: 'line-through' },
            '& .diff-modified': { boxShadow: `inset 4px 0 0 ${colors.modified}` },
          }}>
            <Grid<ItemDiff> rows={diffRows} columns={DIFF_COLUMNS} height={360}
              getRowClass={(p) => (p.data ? `diff-${p.data.change_type}` : undefined)} />
          </Box>
        </>
      )}
    </Stack>
  );
}
