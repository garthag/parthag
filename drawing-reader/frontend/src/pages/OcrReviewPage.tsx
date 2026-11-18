import {
  Alert,
  Box,
  Button,
  Card,
  Chip,
  Grid2 as MuiGrid,
  List,
  ListItemButton,
  ListItemText,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { useEffect, useState } from 'react';
import { errorMessage } from '../api/client';
import { useOcrJobs, useOcrOverride, useReviewQueue } from '../api/hooks';
import type { OCRResult } from '../api/types';
import { PdfViewer } from '../components/PdfViewer';
import { useAuthStore } from '../store/auth';
import { fmtPct } from '../utils/format';

const FIELDS = [
  ['drawing_number', 'Drawing number'],
  ['title', 'Title'],
  ['revision', 'Revision'],
  ['date', 'Date'],
  ['status', 'Status'],
  ['scale', 'Scale'],
  ['sheet_number', 'Sheet'],
  ['checker', 'Checker'],
  ['approver', 'Approver'],
  ['vendor', 'Vendor'],
] as const;

export function OcrReviewPage() {
  const queue = useReviewQueue();
  const running = useOcrJobs('running');
  const queued = useOcrJobs('queued');
  const override = useOcrOverride();
  const canEdit = useAuthStore((s) => s.can('drawings.edit'));
  const [selected, setSelected] = useState<OCRResult | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [zoom, setZoom] = useState(1);

  useEffect(() => {
    if (!selected && queue.data?.length) setSelected(queue.data[0]);
  }, [queue.data, selected]);
  useEffect(() => {
    setValues(Object.fromEntries(FIELDS.map(([k]) => [k, selected?.effective?.[k] ?? ''])));
  }, [selected]);

  const save = async () => {
    if (!selected) return;
    await override.mutateAsync({ id: selected.id, values });
    setSelected(null);
  };

  const pending = (running.data?.meta.total ?? 0) + (queued.data?.meta.total ?? 0);

  return (
    <Stack spacing={2}>
      <Stack direction="row" spacing={1} alignItems="center">
        <Typography variant="h5" sx={{ mr: 'auto' }}>OCR Review</Typography>
        <Chip label={`${pending} job(s) in progress`} />
        <Chip label={`${queue.data?.length ?? 0} awaiting review`} color={queue.data?.length ? 'warning' : 'default'} />
      </Stack>
      {queue.error && <Alert severity="error">{errorMessage(queue.error)}</Alert>}
      <MuiGrid container spacing={2}>
        <MuiGrid size={{ xs: 12, md: 3 }}>
          <Card sx={{ maxHeight: 'calc(100vh - 170px)', overflow: 'auto' }}>
            <List dense>
              {(queue.data ?? []).map((r) => (
                <ListItemButton key={r.id} selected={selected?.id === r.id} onClick={() => setSelected(r)}>
                  <ListItemText
                    primary={r.extracted.drawing_number ?? `File #${r.drawing_file_id}`}
                    secondary={`${fmtPct(r.overall_confidence)} confidence${r.is_scanned ? ' · scanned' : ''}`}
                  />
                </ListItemButton>
              ))}
              {!queue.isLoading && !queue.data?.length && (
                <Typography color="text.secondary" sx={{ p: 2 }}>Nothing to review — all OCR results are above the 90% threshold.</Typography>
              )}
            </List>
          </Card>
        </MuiGrid>
        <MuiGrid size={{ xs: 12, md: 5 }}>
          <Card sx={{ height: 'calc(100vh - 170px)' }}>
            <PdfViewer fileId={selected?.drawing_file_id} zoom={zoom} onZoomChange={setZoom} label="Source drawing" />
          </Card>
        </MuiGrid>
        <MuiGrid size={{ xs: 12, md: 4 }}>
          <Card sx={{ p: 2 }}>
            {selected ? (
              <Stack spacing={1.5}>
                <Typography variant="h6">Extracted fields</Typography>
                {FIELDS.map(([key, label]) => {
                  const conf = selected.field_confidences[key] ?? 0;
                  return (
                    <TextField
                      key={key}
                      size="small"
                      label={label}
                      value={values[key] ?? ''}
                      onChange={(e) => setValues((v) => ({ ...v, [key]: e.target.value }))}
                      disabled={!canEdit}
                      helperText={`OCR: ${selected.extracted[key] ?? '—'} (${fmtPct(conf)})`}
                      color={conf >= 0.9 ? 'success' : 'warning'}
                      focused={conf < 0.9 ? true : undefined}
                    />
                  );
                })}
                {override.error && <Alert severity="error">{errorMessage(override.error)}</Alert>}
                <Box>
                  <Button variant="contained" onClick={save} disabled={!canEdit || override.isPending}>
                    Confirm &amp; apply to register
                  </Button>
                </Box>
              </Stack>
            ) : (
              <Typography color="text.secondary">Select a result to review.</Typography>
            )}
          </Card>
        </MuiGrid>
      </MuiGrid>
    </Stack>
  );
}
