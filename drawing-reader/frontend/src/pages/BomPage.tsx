import { Alert, Card, Grid2 as MuiGrid, Stack, Tab, Tabs, TextField, Typography } from '@mui/material';
import type { ColDef } from 'ag-grid-community';
import { useMemo, useState } from 'react';
import { errorMessage } from '../api/client';
import { useBom, useBoq } from '../api/hooks';
import type { BOMItem, BOQRow } from '../api/types';
import { KpiCard } from '../components/Charts';
import { Grid } from '../components/Grid';
import { useUIStore } from '../store/ui';
import { fmtMoney, fmtNum, fmtWeight } from '../utils/format';

const BOQ_COLUMNS: ColDef<BOQRow>[] = [
  { field: 'discipline', headerName: 'Discipline', width: 120, rowGroup: false },
  { field: 'material', headerName: 'Material', width: 130 },
  { field: 'grade', headerName: 'Grade', width: 130 },
  { field: 'unit', headerName: 'Unit', width: 80 },
  { field: 'total_quantity', headerName: 'Quantity', width: 120, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  { field: 'total_weight_kg', headerName: 'Weight (kg)', width: 140, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
  { field: 'total_cost', headerName: 'Cost', width: 140, type: 'numericColumn', valueFormatter: (p) => fmtMoney(p.value, p.data?.currency) },
  { field: 'drawing_count', headerName: 'Drawings', width: 110, type: 'numericColumn' },
];

const ITEM_COLUMNS: ColDef<BOMItem>[] = [
  { field: 'drawing_number', headerName: 'Drawing', width: 200, pinned: 'left' },
  { field: 'revision', headerName: 'Rev', width: 70 },
  { field: 'item_number', headerName: 'Item', width: 70 },
  { field: 'part_number', headerName: 'Part No', width: 130 },
  { field: 'description', headerName: 'Description', flex: 2, minWidth: 220 },
  { field: 'material', headerName: 'Material', width: 100 },
  { field: 'grade', headerName: 'Grade', width: 100 },
  { field: 'specification', headerName: 'Spec', width: 120 },
  { field: 'quantity', headerName: 'Qty', width: 80, type: 'numericColumn' },
  { field: 'unit', headerName: 'Unit', width: 70 },
  { field: 'total_weight_kg', headerName: 'Weight (kg)', width: 120, type: 'numericColumn', valueFormatter: (p) => fmtNum(p.value) },
];

export function BomPage() {
  const projectId = useUIStore((s) => s.projectId);
  const [tab, setTab] = useState(0);
  const [filter, setFilter] = useState('');
  const boq = useBoq(projectId);
  const items = useBom({ project_id: projectId, sort: '-total_weight_kg' }, tab === 1);
  const totals = useMemo(() => {
    const rows = boq.data ?? [];
    return {
      weight: rows.reduce((s, r) => s + r.total_weight_kg, 0),
      cost: rows.reduce((s, r) => s + r.total_cost, 0),
      currency: rows[0]?.currency ?? 'USD',
      lines: rows.length,
    };
  }, [boq.data]);

  return (
    <Stack spacing={2}>
      <Typography variant="h5">BOM &amp; BOQ</Typography>
      <MuiGrid container spacing={2}>
        <MuiGrid size={{ xs: 12, sm: 4 }}><KpiCard label="BOQ weight" value={fmtWeight(totals.weight)} loading={boq.isLoading} /></MuiGrid>
        <MuiGrid size={{ xs: 12, sm: 4 }}><KpiCard label="Estimated cost" value={fmtMoney(totals.cost, totals.currency)} loading={boq.isLoading} /></MuiGrid>
        <MuiGrid size={{ xs: 12, sm: 4 }}><KpiCard label="BOQ lines" value={totals.lines} loading={boq.isLoading} /></MuiGrid>
      </MuiGrid>
      {(boq.error || items.error) && <Alert severity="error">{errorMessage(boq.error ?? items.error)}</Alert>}
      <Card>
        <Stack direction="row" alignItems="center" sx={{ px: 1, borderBottom: 1, borderColor: 'divider' }}>
          <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ flex: 1 }}>
            <Tab label="BOQ summary" />
            <Tab label="BOM items (current revisions)" />
          </Tabs>
          <TextField size="small" placeholder="Quick filter" value={filter} onChange={(e) => setFilter(e.target.value)} sx={{ width: 240 }} />
        </Stack>
        <Stack sx={{ p: 1 }}>
          {tab === 0 ? (
            <Grid<BOQRow> rows={boq.data ?? []} columns={BOQ_COLUMNS} loading={boq.isFetching} quickFilter={filter}
              height="calc(100vh - 360px)" />
          ) : (
            <Grid<BOMItem> rows={items.data?.data ?? []} columns={ITEM_COLUMNS} loading={items.isFetching} quickFilter={filter}
              height="calc(100vh - 360px)" />
          )}
        </Stack>
      </Card>
    </Stack>
  );
}
