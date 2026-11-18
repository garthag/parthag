import { Alert, Button, Card, Chip, InputAdornment, MenuItem, Stack, TextField, Typography } from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import UploadIcon from '@mui/icons-material/CloudUpload';
import DownloadIcon from '@mui/icons-material/Download';
import type { ColDef, ICellRendererParams } from 'ag-grid-community';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { downloadFile, errorMessage } from '../api/client';
import { useDisciplines, useDrawings } from '../api/hooks';
import type { Drawing } from '../api/types';
import { Grid } from '../components/Grid';
import { UploadDialog } from '../components/UploadDialog';
import { useAuthStore } from '../store/auth';
import { useUIStore } from '../store/ui';
import { STATUS_COLORS, fmtNum, fmtPct } from '../utils/format';

const STATUSES = ['PRELIM', 'IFR', 'IFA', 'IFC', 'AFC', 'AS-BUILT', 'SUPERSEDED', 'VOID'];

export function RegisterPage() {
  const projectId = useUIStore((s) => s.projectId);
  const can = useAuthStore((s) => s.can);
  const navigate = useNavigate();
  const disciplines = useDisciplines();
  const [search, setSearch] = useState('');
  const [debounced, setDebounced] = useState('');
  const [discipline, setDiscipline] = useState('');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [uploadOpen, setUploadOpen] = useState(false);
  const pageSize = 200;

  useEffect(() => {
    const t = setTimeout(() => {
      setDebounced(search);
      setPage(1);
    }, 250);
    return () => clearTimeout(t);
  }, [search]);

  const query = useDrawings({ project_id: projectId, search: debounced, discipline, status, page, page_size: pageSize,
    sort: 'drawing_number' });

  const columns = useMemo<ColDef<Drawing>[]>(
    () => [
      { field: 'drawing_number', headerName: 'Drawing No', pinned: 'left', minWidth: 210 },
      { field: 'title', headerName: 'Title', flex: 2, minWidth: 260 },
      { field: 'project_code', headerName: 'Project', width: 100 },
      { field: 'discipline_code', headerName: 'Disc.', width: 90 },
      { field: 'area_code', headerName: 'Area', width: 90 },
      { field: 'drawing_type', headerName: 'Type', width: 90 },
      { field: 'current_revision', headerName: 'Rev', width: 80 },
      {
        field: 'status',
        headerName: 'Status',
        width: 120,
        cellRenderer: (p: ICellRendererParams<Drawing, string>) =>
          p.value ? <Chip size="small" label={p.value} color={STATUS_COLORS[p.value] ?? 'default'} /> : null,
      },
      { field: 'revision_count', headerName: 'Revs', width: 80, type: 'numericColumn' },
      {
        field: 'current_weight_kg',
        headerName: 'Weight (kg)',
        width: 130,
        type: 'numericColumn',
        valueFormatter: (p) => fmtNum(p.value as number),
      },
      {
        field: 'ocr_confidence',
        headerName: 'OCR conf.',
        width: 110,
        type: 'numericColumn',
        valueFormatter: (p) => fmtPct(p.value as number | null),
      },
      { field: 'scale', headerName: 'Scale', width: 90 },
      { field: 'vendor', headerName: 'Vendor', width: 160 },
      { field: 'folder_path', headerName: 'Folder', width: 220 },
    ],
    [],
  );

  const meta = query.data?.meta;

  return (
    <Stack spacing={2}>
      <Stack direction="row" alignItems="center" spacing={1} flexWrap="wrap" useFlexGap>
        <Typography variant="h5" sx={{ mr: 'auto' }}>
          Drawing Register
        </Typography>
        <Button startIcon={<DownloadIcon />}
          onClick={() => downloadFile('/reports/drawing-register', { format: 'xlsx', project_id: projectId ?? undefined }, 'register.xlsx')}>
          Export Excel
        </Button>
        {can('drawings.upload') && (
          <Button variant="contained" startIcon={<UploadIcon />} onClick={() => setUploadOpen(true)}>
            Upload drawings
          </Button>
        )}
      </Stack>
      <Card sx={{ p: 1.5 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
          <TextField
            size="small"
            placeholder="Search drawing number, title, vendor or discipline"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            sx={{ flex: 1 }}
            slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment> } }}
          />
          <TextField select size="small" label="Discipline" value={discipline} sx={{ minWidth: 170 }}
            onChange={(e) => { setDiscipline(e.target.value); setPage(1); }}>
            <MenuItem value="">All</MenuItem>
            {(disciplines.data ?? []).map((d) => (
              <MenuItem key={d.id} value={d.code}>{d.code} – {d.name}</MenuItem>
            ))}
          </TextField>
          <TextField select size="small" label="Status" value={status} sx={{ minWidth: 140 }}
            onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <MenuItem value="">All</MenuItem>
            {STATUSES.map((s) => <MenuItem key={s} value={s}>{s}</MenuItem>)}
          </TextField>
        </Stack>
      </Card>
      {query.error && <Alert severity="error">{errorMessage(query.error)}</Alert>}
      <Grid<Drawing>
        rows={query.data?.data ?? []}
        columns={columns}
        loading={query.isFetching}
        height="calc(100vh - 290px)"
        onRowClicked={(d) => navigate(`/analysis?drawing=${d.id}`)}
      />
      <Stack direction="row" alignItems="center" spacing={1}>
        <Typography variant="body2" color="text.secondary" sx={{ mr: 'auto' }}>
          {meta ? `${meta.total.toLocaleString()} drawings · page ${meta.page} of ${Math.max(meta.pages, 1)}` : ''}
        </Typography>
        <Button size="small" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</Button>
        <Button size="small" disabled={!meta || page >= meta.pages} onClick={() => setPage((p) => p + 1)}>Next</Button>
      </Stack>
      <UploadDialog open={uploadOpen} onClose={() => setUploadOpen(false)} />
    </Stack>
  );
}
