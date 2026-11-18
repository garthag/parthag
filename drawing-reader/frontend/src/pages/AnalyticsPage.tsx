import { Alert, Card, Grid2 as MuiGrid, Stack, ToggleButton, ToggleButtonGroup, Typography } from '@mui/material';
import type { ColDef } from 'ag-grid-community';
import { useMemo, useState } from 'react';
import { errorMessage } from '../api/client';
import { useMaterialAnalysis, useWeightGrowth } from '../api/hooks';
import type { MaterialAnalysisRow } from '../api/types';
import { ChartCard, MultiLine, SimpleBar } from '../components/Charts';
import { Grid } from '../components/Grid';
import { useUIStore } from '../store/ui';
import { fmtMoney, fmtNum } from '../utils/format';

const MAT_COLUMNS: ColDef<MaterialAnalysisRow>[] = [
  { field: 'material', headerName: 'Material', width: 120 },
  { field: 'grade', headerName: 'Grade', width: 130 },
  { field: 'total_weight_kg', headerName: 'Weight (kg)', width: 140, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  { field: 'share_pct', headerName: 'Share %', width: 110, type: 'numericColumn', valueFormatter: (p) => `${fmtNum(p.value)}%` },
  { field: 'total_quantity', headerName: 'Quantity', width: 120, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  { field: 'estimated_cost', headerName: 'Est. cost', width: 140, type: 'numericColumn', valueFormatter: (p) => fmtMoney(p.value) },
  { field: 'drawing_count', headerName: 'Drawings', width: 110, type: 'numericColumn' },
];

export function AnalyticsPage() {
  const projectId = useUIStore((s) => s.projectId);
  const [groupBy, setGroupBy] = useState<'project' | 'discipline'>('project');
  const material = useMaterialAnalysis(projectId);
  const growth = useWeightGrowth(projectId, groupBy);

  // Wide table for the line chart; series beyond three are folded into "Other" to keep colours distinguishable.
  const { rows, keys } = useMemo(() => {
    const series = [...(growth.data ?? [])].sort(
      (a, b) => (b.points.at(-1)?.cumulative_kg ?? 0) - (a.points.at(-1)?.cumulative_kg ?? 0),
    );
    const top = series.slice(0, series.length > 3 ? 2 : 3);
    const rest = series.slice(top.length);
    const periods = Array.from(new Set(series.flatMap((s) => s.points.map((p) => p.period)))).sort();
    const cumulative = (s: (typeof series)[number], period: string) => {
      let v = 0;
      for (const p of s.points) if (p.period <= period) v = p.cumulative_kg;
      return v / 1000;
    };
    const out = periods.map((period) => {
      const row: Record<string, string | number> = { period };
      top.forEach((s) => (row[s.key] = Number(cumulative(s, period).toFixed(3))));
      if (rest.length) row.Other = Number(rest.reduce((sum, s) => sum + cumulative(s, period), 0).toFixed(3));
      return row;
    });
    return { rows: out, keys: [...top.map((s) => s.key), ...(rest.length ? ['Other'] : [])] };
  }, [growth.data]);

  const materialBars = (material.data ?? []).slice(0, 10).map((m) => ({
    label: `${m.material} ${m.grade ?? ''}`.trim(),
    value: Number((m.total_weight_kg / 1000).toFixed(2)),
  }));

  return (
    <Stack spacing={2}>
      <Typography variant="h5">Engineering Analytics</Typography>
      {(material.error || growth.error) && <Alert severity="error">{errorMessage(material.error ?? growth.error)}</Alert>}
      <MuiGrid container spacing={2}>
        <MuiGrid size={{ xs: 12, lg: 7 }}>
          <ChartCard
            title="Cumulative weight growth"
            subtitle="Tonnes, cumulative revision weight deltas per month"
            loading={growth.isLoading}
            empty={!rows.length}
            height={300}
            action={
              <ToggleButtonGroup size="small" exclusive value={groupBy} onChange={(_, v) => v && setGroupBy(v)}>
                <ToggleButton value="project">By project</ToggleButton>
                <ToggleButton value="discipline">By discipline</ToggleButton>
              </ToggleButtonGroup>
            }
          >
            <MultiLine data={rows} keys={keys} unit=" t" />
          </ChartCard>
        </MuiGrid>
        <MuiGrid size={{ xs: 12, lg: 5 }}>
          <ChartCard title="Top materials by weight" subtitle="Tonnes, current revisions" loading={material.isLoading}
            empty={!materialBars.length} height={348}>
            <SimpleBar data={materialBars} unit=" t" horizontal />
          </ChartCard>
        </MuiGrid>
        <MuiGrid size={{ xs: 12 }}>
          <Card sx={{ p: 1 }}>
            <Typography variant="h6" sx={{ px: 1, pb: 1 }}>Material analysis</Typography>
            <Grid<MaterialAnalysisRow> rows={material.data ?? []} columns={MAT_COLUMNS} loading={material.isFetching} height={360} />
          </Card>
        </MuiGrid>
      </MuiGrid>
    </Stack>
  );
}
