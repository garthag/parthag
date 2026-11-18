import { CssBaseline, ThemeProvider } from '@mui/material';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { AxiosRequestConfig } from 'axios';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import { api } from '../api/client';
import { buildTheme } from '../theme';

export function renderWithProviders(ui: ReactElement, { route = '/', mode = 'light' as 'light' | 'dark' } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ThemeProvider theme={buildTheme(mode)}>
        <CssBaseline />
        <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
      </ThemeProvider>
    </QueryClientProvider>,
  );
}

type Handler = (params?: Record<string, unknown>) => unknown;

/** Route api.get calls to handlers keyed by URL prefix. */
export function mockGet(routes: Record<string, Handler>) {
  return vi.spyOn(api, 'get').mockImplementation(async (url: string, config?: AxiosRequestConfig) => {
    const key = Object.keys(routes)
      .sort((a, b) => b.length - a.length)
      .find((k) => url.startsWith(k));
    if (!key) throw new Error(`unmocked GET ${url}`);
    return { data: routes[key](config?.params as Record<string, unknown> | undefined), headers: {} } as never;
  });
}

export const page = <T,>(data: T[]) => ({ success: true, data, meta: { page: 1, page_size: 50, total: data.length, pages: 1 } });
export const ok = <T,>(data: T) => ({ success: true, data });
