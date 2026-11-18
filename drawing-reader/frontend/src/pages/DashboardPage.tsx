import { Alert, Grid2 as Grid, Stack, Typography } from '@mui/material';
import DescriptionIcon from '@mui/icons-material/DescriptionOutlined';
import HistoryIcon from '@mui/icons-material/History';
import ScaleIcon from '@mui/icons-material/Scale';
import FolderIcon from '@mui/icons-material/FolderOutlined';
import DocumentScannerIcon from '@mui/icons-material/DocumentScanner';
import { errorMessage } from '../api/client';
import { useDashboard } from '../api/hooks';
import { ChartCard, KpiCard, SimpleBar, TrendArea } from '../components/Charts';
import { useUIStore } from '../store/ui';
import { fmtInt, fmtNum } from '../utils/format';

export function DashboardPage() {
  const projectId = useUIStore((s) => s.projectId);
  const { summary, charts } = useDashboard(projectId);
  const s = summary.data;
  const c = charts.data;

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Dashboard</Typography>
      {(summary.error || charts.error) && <Alert severity="error">{errorMessage(summary.error ?? charts.error)}</Alert>}
      <Grid container spacing={2}>
        <Grid size={{ xs: 6, md: 4, lg: 2.4 }}>
          <KpiCard label="Total Drawings" icon={<DescriptionIcon fontSize="small" />} loading={summary.isLoading}
            value={fmtInt(s?.total_drawings)} hint={s ? `${fmtInt(s.ifc_drawings)} IFC / AFC` : undefined} />
        </Grid>
        <Grid size={{ xs: 6, md: 4, lg: 2.4 }}>
          <KpiCard label="Total Revisions" icon={<HistoryIcon fontSize="small" />} loading={summary.isLoading}
            value={fmtInt(s?.total_revisions)} hint={s ? `${fmtInt(s.bom_items)} BOM lines` : undefined} />
        </Grid>
        <Grid size={{ xs: 6, md: 4, lg: 2.4 }}>
          <KpiCard label="Total Weight" icon={<ScaleIcon fontSize="small" />} loading={summary.isLoading}
            value={s ? `${fmtNum(s.total_weight_t)} t` : '–'} hint="Current revisions" />
        </Grid>
        <Grid size={{ xs: 6, md: 6, lg: 2.4 }}>
          <KpiCard label="Projects" icon={<FolderIcon fontSize="small" />} loading={summary.isLoading} value={fmtInt(s?.projects)} />
        </Grid>
        <Grid size={{ xs: 12, md: 6, lg: 2.4 }}>
          <KpiCard label="Pending OCR" icon={<DocumentScannerIcon fontSize="small" />} loading={summary.isLoading}
            value={fmtInt(s?.pending_ocr)}
            hint={s ? `${fmtInt(s.ocr_needs_review)} need review · ${fmtInt(s.failed_ocr)} failed` : undefined} />
        </Grid>
        <Grid size={{ xs: 12, lg: 6 }}>
          <ChartCard title="Drawings by Discipline" loading={charts.isLoading} empty={!c?.drawings_by_discipline.length}>
            <SimpleBar data={c?.drawings_by_discipline ?? []} unit=" drawings" />
          </ChartCard>
        </Grid>
        <Grid size={{ xs: 12, lg: 6 }}>
          <ChartCard title="Weight by Discipline" subtitle="Tonnes, current revisions" loading={charts.isLoading}
            empty={!c?.weight_by_discipline.length}>
            <SimpleBar data={c?.weight_by_discipline ?? []} unit=" t" />
          </ChartCard>
        </Grid>
        <Grid size={{ xs: 12, lg: 6 }}>
          <ChartCard title="Revision Trend" subtitle="Revisions issued per month" loading={charts.isLoading}
            empty={!c?.revision_trend.length}>
            <TrendArea data={c?.revision_trend ?? []} name="Revisions" />
          </ChartCard>
        </Grid>
        <Grid size={{ xs: 12, lg: 6 }}>
          <ChartCard title="Monthly Upload Trend" subtitle="Drawing files uploaded per month" loading={charts.isLoading}
            empty={!c?.monthly_upload_trend.length}>
            <TrendArea data={c?.monthly_upload_trend ?? []} name="Uploads" />
          </ChartCard>
        </Grid>
        <Grid size={{ xs: 12 }}>
          <ChartCard title="Drawing Status" loading={charts.isLoading} height={220} empty={!c?.status_breakdown.length}>
            <SimpleBar data={c?.status_breakdown ?? []} unit=" drawings" horizontal />
          </ChartCard>
        </Grid>
      </Grid>
    </Stack>
  );
}
