import { Alert, Button, Card, CardActions, CardContent, Grid2 as MuiGrid, Stack, Typography } from '@mui/material';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { downloadFile, errorMessage } from '../api/client';
import { useUIStore } from '../store/ui';

export const REPORTS = [
  { key: 'drawing-register', title: 'Drawing Register', text: 'All drawings with revision, status, discipline, area and weight.' },
  { key: 'boq', title: 'BOQ Report', text: 'Bill of quantities by discipline, material and grade with costs.' },
  { key: 'mto', title: 'MTO Report', text: 'Material take-off of current revisions: quantity, weight and estimated cost.' },
  { key: 'cost-impact', title: 'Cost Impact Report', text: 'Every revision change ranked by weight and cost impact.' },
  { key: 'weight-growth', title: 'Weight Growth Report', text: 'Monthly weight deltas and cumulative growth per project.' },
] as const;

export function ReportsPage() {
  const projectId = useUIStore((s) => s.projectId);
  const navigate = useNavigate();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const download = async (key: string, format: string) => {
    setBusy(`${key}.${format}`);
    setError(null);
    try {
      await downloadFile(`/reports/${key}`, { format, project_id: projectId ?? undefined }, `${key}.${format}`);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(null);
    }
  };

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Reports</Typography>
      <Typography variant="body2" color="text.secondary">
        Reports use the same queries as the dashboard{projectId ? ' and are scoped to the selected project' : ''}.
      </Typography>
      {error && <Alert severity="error">{error}</Alert>}
      <MuiGrid container spacing={2}>
        {REPORTS.map((r) => (
          <MuiGrid key={r.key} size={{ xs: 12, sm: 6, lg: 4 }}>
            <Card sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
              <CardContent sx={{ flex: 1 }}>
                <Typography variant="h6">{r.title}</Typography>
                <Typography variant="body2" color="text.secondary">{r.text}</Typography>
              </CardContent>
              <CardActions>
                {['pdf', 'xlsx', 'csv'].map((f) => (
                  <Button key={f} size="small" disabled={busy !== null} onClick={() => void download(r.key, f)}>
                    {busy === `${r.key}.${f}` ? 'Preparing…' : f.toUpperCase()}
                  </Button>
                ))}
              </CardActions>
            </Card>
          </MuiGrid>
        ))}
        <MuiGrid size={{ xs: 12, sm: 6, lg: 4 }}>
          <Card sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
            <CardContent sx={{ flex: 1 }}>
              <Typography variant="h6">Revision Difference Report</Typography>
              <Typography variant="body2" color="text.secondary">
                Added, removed and modified BOM items between two revisions — export from the Revision Compare screen.
              </Typography>
            </CardContent>
            <CardActions>
              <Button size="small" onClick={() => navigate('/compare')}>Open Revision Compare</Button>
            </CardActions>
          </Card>
        </MuiGrid>
      </MuiGrid>
    </Stack>
  );
}
