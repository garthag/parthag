import { Box, Card, CardContent, Skeleton, Stack, Typography, useTheme } from '@mui/material';
import type { ReactNode } from 'react';
import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { chartColors } from '../theme';
import { fmtNum } from '../utils/format';

export function useChartColors() {
  return chartColors[useTheme().palette.mode];
}

export function KpiCard({ label, value, hint, icon, loading }: {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
  icon?: ReactNode;
  loading?: boolean;
}) {
  return (
    <Card sx={{ height: '100%' }}>
      <CardContent sx={{ py: 1.75, '&:last-child': { pb: 1.75 } }}>
        <Stack direction="row" alignItems="center" spacing={1} sx={{ color: 'text.secondary', mb: 0.5 }}>
          {icon}
          <Typography variant="body2">{label}</Typography>
        </Stack>
        <Typography variant="h5" component="div" sx={{ fontVariantNumeric: 'tabular-nums' }}>
          {loading ? <Skeleton width={90} /> : value}
        </Typography>
        {hint && (
          <Typography variant="caption" color="text.secondary">
            {hint}
          </Typography>
        )}
      </CardContent>
    </Card>
  );
}

export function ChartCard({ title, subtitle, children, height = 280, loading, empty, action }: {
  title: string;
  subtitle?: string;
  action?: ReactNode;
  children: ReactNode;
  height?: number;
  loading?: boolean;
  empty?: boolean;
}) {
  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack direction="row" alignItems="center" spacing={1}>
          <Typography variant="h6" sx={{ flex: 1 }}>{title}</Typography>
          {action}
        </Stack>
        {subtitle && (
          <Typography variant="caption" color="text.secondary">
            {subtitle}
          </Typography>
        )}
        <Box sx={{ height, mt: 1 }}>
          {loading ? (
            <Skeleton variant="rounded" height={height} />
          ) : empty ? (
            <Typography color="text.secondary" sx={{ pt: 8, textAlign: 'center' }}>
              No data yet
            </Typography>
          ) : (
            children
          )}
        </Box>
      </CardContent>
    </Card>
  );
}

interface Datum {
  label: string;
  value: number;
}

const tooltipStyle = (surface: string, axis: string) => ({
  contentStyle: { background: surface, border: `1px solid ${axis}33`, borderRadius: 6, fontSize: 12 },
  labelStyle: { fontWeight: 600 },
});

/** Single-series bar chart (categories named on the axis; one hue, no legend needed). */
export function SimpleBar({ data, unit = '', horizontal = false }: { data: Datum[]; unit?: string; horizontal?: boolean }) {
  const c = useChartColors();
  const axis = { stroke: c.axis, fontSize: 11, tickLine: false, axisLine: false };
  return (
    <ResponsiveContainer width="100%" height="100%">
      <BarChart data={data} layout={horizontal ? 'vertical' : 'horizontal'} margin={{ left: 4, right: 16, top: 4 }}>
        <CartesianGrid stroke={c.grid} vertical={horizontal} horizontal={!horizontal} />
        {horizontal ? (
          <>
            <XAxis type="number" {...axis} tickFormatter={(v: number) => fmtNum(v)} />
            <YAxis type="category" dataKey="label" width={90} {...axis} />
          </>
        ) : (
          <>
            <XAxis dataKey="label" {...axis} interval={0} />
            <YAxis {...axis} width={48} tickFormatter={(v: number) => fmtNum(v)} />
          </>
        )}
        <Tooltip
          cursor={{ fill: c.grid, opacity: 0.5 }}
          formatter={(v: number) => [`${fmtNum(v)}${unit}`, '']}
          separator=""
          {...tooltipStyle(c.surface, c.axis)}
        />
        <Bar dataKey="value" fill={c.series[0]} radius={horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0]} maxBarSize={36} />
      </BarChart>
    </ResponsiveContainer>
  );
}

/** Single-series trend (area under a 2px line). */
export function TrendArea({ data, unit = '', name }: { data: Datum[]; unit?: string; name: string }) {
  const c = useChartColors();
  const axis = { stroke: c.axis, fontSize: 11, tickLine: false, axisLine: false };
  return (
    <ResponsiveContainer width="100%" height="100%">
      <AreaChart data={data} margin={{ left: 4, right: 16, top: 8 }}>
        <CartesianGrid stroke={c.grid} vertical={false} />
        <XAxis dataKey="label" {...axis} minTickGap={16} />
        <YAxis {...axis} width={44} allowDecimals={false} />
        <Tooltip formatter={(v: number) => [`${fmtNum(v)}${unit}`, name]} {...tooltipStyle(c.surface, c.axis)} />
        <Area type="monotone" dataKey="value" name={name} stroke={c.series[0]} strokeWidth={2} fill={c.series[0]}
          fillOpacity={0.12} dot={false} activeDot={{ r: 4 }} />
      </AreaChart>
    </ResponsiveContainer>
  );
}

/** Up to three named series on one axis (legend always shown). */
export function MultiLine({ data, keys, unit = '' }: { data: Record<string, string | number>[]; keys: string[]; unit?: string }) {
  const c = useChartColors();
  const axis = { stroke: c.axis, fontSize: 11, tickLine: false, axisLine: false };
  const shown = keys.slice(0, 3);
  return (
    <ResponsiveContainer width="100%" height="100%">
      <LineChart data={data} margin={{ left: 4, right: 16, top: 8 }}>
        <CartesianGrid stroke={c.grid} vertical={false} />
        <XAxis dataKey="period" {...axis} minTickGap={16} />
        <YAxis {...axis} width={52} tickFormatter={(v: number) => fmtNum(v)} />
        <Tooltip formatter={(v: number, n: string) => [`${fmtNum(v)}${unit}`, n]} {...tooltipStyle(c.surface, c.axis)} />
        {shown.length > 1 && <Legend wrapperStyle={{ fontSize: 12 }} iconType="plainline" />}
        {shown.map((k, i) => (
          <Line key={k} type="monotone" dataKey={k} stroke={c.series[i]} strokeWidth={2} dot={false}
            activeDot={{ r: 4 }} connectNulls />
        ))}
      </LineChart>
    </ResponsiveContainer>
  );
}
