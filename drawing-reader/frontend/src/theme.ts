import { createTheme, type Theme } from '@mui/material/styles';
import type { ThemeMode } from './store/ui';

/**
 * Chart palette (validated categorical slots; light and dark steps chosen separately, not flipped).
 * Bars that compare categories use a single series hue; categories are named on the axis.
 */
export const chartColors = {
  light: {
    surface: '#fcfcfb',
    series: ['#2a78d6', '#eb6834', '#1baf7a'],
    grid: '#e4e3df',
    axis: '#52514e',
    added: '#008300',
    removed: '#e34948',
    modified: '#eda100',
  },
  dark: {
    surface: '#1a1a19',
    series: ['#3987e5', '#d95926', '#199e70'],
    grid: '#33332f',
    axis: '#c3c2b7',
    added: '#0ca30c',
    removed: '#e66767',
    modified: '#c98500',
  },
} as const;

export function buildTheme(mode: ThemeMode): Theme {
  const dark = mode === 'dark';
  return createTheme({
    palette: {
      mode,
      primary: { main: dark ? '#6da7ec' : '#1f3a5f' },
      secondary: { main: dark ? '#f0a35e' : '#c25412' },
      background: dark ? { default: '#121211', paper: '#1a1a19' } : { default: '#f3f4f6', paper: '#fcfcfb' },
      success: { main: dark ? '#0ca30c' : '#008300' },
      error: { main: dark ? '#e66767' : '#c62828' },
      warning: { main: dark ? '#c98500' : '#b26a00' },
    },
    shape: { borderRadius: 8 },
    typography: {
      fontFamily: '"Inter", "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
      h5: { fontWeight: 600 },
      h6: { fontWeight: 600, fontSize: '1.05rem' },
      body2: { fontSize: '0.85rem' },
    },
    components: {
      MuiPaper: { defaultProps: { elevation: 0 }, styleOverrides: { root: { backgroundImage: 'none' } } },
      MuiCard: {
        styleOverrides: { root: { border: `1px solid ${dark ? '#2c2c29' : '#e4e3df'}` } },
      },
      MuiButton: { defaultProps: { disableElevation: true }, styleOverrides: { root: { textTransform: 'none' } } },
      MuiTab: { styleOverrides: { root: { textTransform: 'none', minHeight: 40 } } },
      MuiTabs: { styleOverrides: { root: { minHeight: 40 } } },
    },
  });
}
