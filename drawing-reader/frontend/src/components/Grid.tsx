import { Box, useTheme } from '@mui/material';
import type { ColDef, GridOptions, RowClickedEvent } from 'ag-grid-community';
import 'ag-grid-community/styles/ag-grid.css';
import 'ag-grid-community/styles/ag-theme-quartz.css';
import { AgGridReact } from 'ag-grid-react';

interface Props<T> {
  rows: T[];
  columns: ColDef<T>[];
  height?: number | string;
  onRowClicked?: (row: T) => void;
  getRowClass?: GridOptions<T>['getRowClass'];
  loading?: boolean;
  quickFilter?: string;
}

/** AG Grid wrapper that follows the MUI light/dark mode. */
export function Grid<T>({ rows, columns, height = 480, onRowClicked, getRowClass, loading, quickFilter }: Props<T>) {
  const dark = useTheme().palette.mode === 'dark';
  return (
    <Box className={dark ? 'ag-theme-quartz-dark' : 'ag-theme-quartz'} sx={{ height, width: '100%' }}>
      <AgGridReact<T>
        rowData={rows}
        columnDefs={columns}
        defaultColDef={{ sortable: true, filter: true, resizable: true, minWidth: 80 }}
        animateRows={false}
        rowHeight={34}
        headerHeight={36}
        loading={loading}
        quickFilterText={quickFilter}
        getRowClass={getRowClass}
        onRowClicked={(e: RowClickedEvent<T>) => e.data && onRowClicked?.(e.data)}
        enableCellTextSelection
        suppressDragLeaveHidesColumns
      />
    </Box>
  );
}
