import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  LinearProgress,
  MenuItem,
  Stack,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import UploadFileIcon from '@mui/icons-material/UploadFile';
import DriveFolderUploadIcon from '@mui/icons-material/DriveFolderUpload';
import { useRef, useState } from 'react';
import { errorMessage } from '../api/client';
import { useProjects, useUpload } from '../api/hooks';
import type { UploadSummary } from '../api/types';
import { useUIStore } from '../store/ui';
import { fmtBytes } from '../utils/format';

const BATCH = 100; // files per request keeps requests small for 1000+ drawing uploads

export function UploadDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const projects = useProjects();
  const current = useUIStore((s) => s.projectId);
  const [projectId, setProjectId] = useState<number | ''>(current ?? '');
  const [files, setFiles] = useState<File[]>([]);
  const [folder, setFolder] = useState(false);
  const [runOcr, setRunOcr] = useState(true);
  const [progress, setProgress] = useState(0);
  const [summary, setSummary] = useState<UploadSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const upload = useUpload();
  const fileInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);

  const pick = (list: FileList | null, isFolder: boolean) => {
    if (!list) return;
    const accepted = Array.from(list).filter((f) => /\.(pdf|zip)$/i.test(f.name));
    setFiles(accepted);
    setFolder(isFolder);
    setSummary(null);
    setError(null);
  };

  const start = async () => {
    if (!projectId || files.length === 0) return;
    setError(null);
    const merged: UploadSummary = { total: 0, succeeded: 0, failed: 0, duplicates: 0, success_rate: 0, items: [] };
    try {
      for (let i = 0; i < files.length; i += BATCH) {
        const batch = files.slice(i, i + BATCH);
        const res = await upload.mutateAsync({
          projectId: Number(projectId),
          files: batch,
          folder,
          runOcr,
          onProgress: (pct) => setProgress(Math.round(((i + (pct / 100) * batch.length) / files.length) * 100)),
        });
        merged.total += res.total;
        merged.succeeded += res.succeeded;
        merged.failed += res.failed;
        merged.duplicates += res.duplicates;
        merged.items.push(...res.items);
      }
      merged.success_rate = merged.total ? ((merged.succeeded + merged.duplicates) / merged.total) * 100 : 0;
      setSummary(merged);
      setProgress(100);
    } catch (e) {
      setError(errorMessage(e));
    }
  };

  const close = () => {
    setFiles([]);
    setSummary(null);
    setProgress(0);
    setError(null);
    onClose();
  };

  const totalSize = files.reduce((s, f) => s + f.size, 0);

  return (
    <Dialog open={open} onClose={close} maxWidth="md" fullWidth>
      <DialogTitle>Upload drawings</DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <TextField select label="Project" size="small" value={projectId} onChange={(e) => setProjectId(Number(e.target.value))}>
            {(projects.data ?? []).map((p) => (
              <MenuItem key={p.id} value={p.id}>
                {p.code} – {p.name}
              </MenuItem>
            ))}
          </TextField>
          <Stack direction="row" spacing={1}>
            <Button variant="outlined" startIcon={<UploadFileIcon />} onClick={() => fileInput.current?.click()}>
              Select PDFs / ZIP
            </Button>
            <Button variant="outlined" startIcon={<DriveFolderUploadIcon />} onClick={() => folderInput.current?.click()}>
              Select folder
            </Button>
            <input ref={fileInput} hidden type="file" multiple accept=".pdf,.zip" data-testid="file-input"
              onChange={(e) => pick(e.target.files, false)} />
            <input
              ref={folderInput}
              hidden
              type="file"
              multiple
              {...({ webkitdirectory: '', directory: '' } as Record<string, string>)}
              onChange={(e) => pick(e.target.files, true)}
            />
            <FormControlLabel control={<Switch checked={runOcr} onChange={(e) => setRunOcr(e.target.checked)} />}
              label="Run OCR & BOM extraction" sx={{ ml: 'auto' }} />
          </Stack>
          <Typography variant="body2" color="text.secondary">
            Sub-folders such as <code>02_Structural/Area 01</code> and drawing numbers like <code>NRP-ST-A01-GA-0001_RevB</code>{' '}
            are used to categorise discipline, area, type and revision automatically.
          </Typography>
          {files.length > 0 && (
            <Alert severity="info">
              {files.length} file{files.length > 1 ? 's' : ''} selected ({fmtBytes(totalSize)}){folder ? ' – folder structure preserved' : ''}
            </Alert>
          )}
          {(upload.isPending || progress > 0) && <LinearProgress variant="determinate" value={progress} />}
          {error && <Alert severity="error">{error}</Alert>}
          {summary && (
            <Box>
              <Stack direction="row" spacing={1} sx={{ mb: 1 }}>
                <Chip color="success" label={`${summary.succeeded} registered`} />
                <Chip label={`${summary.duplicates} duplicates`} />
                <Chip color={summary.failed ? 'error' : 'default'} label={`${summary.failed} failed`} />
                <Chip variant="outlined" label={`${summary.success_rate.toFixed(1)}% success`} />
              </Stack>
              <Box sx={{ maxHeight: 260, overflow: 'auto' }}>
                <Table size="small" stickyHeader>
                  <TableHead>
                    <TableRow>
                      <TableCell>File</TableCell>
                      <TableCell>Drawing</TableCell>
                      <TableCell>Rev</TableCell>
                      <TableCell>Discipline</TableCell>
                      <TableCell>Result</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {summary.items.map((it, i) => (
                      <TableRow key={i}>
                        <TableCell>{it.relative_path ?? it.filename}</TableCell>
                        <TableCell>{it.drawing_number ?? '–'}</TableCell>
                        <TableCell>{it.revision ?? '–'}</TableCell>
                        <TableCell>{it.discipline ?? '–'}</TableCell>
                        <TableCell>{it.error ?? it.status.replace('_', ' ')}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            </Box>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>{summary ? 'Close' : 'Cancel'}</Button>
        <Button variant="contained" onClick={start} disabled={!projectId || files.length === 0 || upload.isPending}>
          Upload {files.length || ''}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
