import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios';
import { useAuthStore } from '../store/auth';
import type { ApiErrorBody, ApiResponse, TokenPair } from './types';

export const API_BASE = import.meta.env.VITE_API_BASE ?? '/api';

export const api = axios.create({ baseURL: API_BASE, timeout: 120_000 });

api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().accessToken;
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

let refreshing: Promise<string | null> | null = null;

async function refreshAccessToken(): Promise<string | null> {
  const { refreshToken, setTokens, logout } = useAuthStore.getState();
  if (!refreshToken) return null;
  try {
    const res = await axios.post<ApiResponse<TokenPair>>(`${API_BASE}/auth/refresh`, { refresh_token: refreshToken });
    setTokens(res.data.data.access_token, res.data.data.refresh_token);
    return res.data.data.access_token;
  } catch {
    logout();
    return null;
  }
}

api.interceptors.response.use(
  (res) => res,
  async (error: AxiosError) => {
    const original = error.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;
    const isAuthCall = original?.url?.includes('/auth/login') || original?.url?.includes('/auth/refresh');
    if (error.response?.status === 401 && original && !original._retried && !isAuthCall) {
      original._retried = true;
      refreshing ??= refreshAccessToken().finally(() => {
        refreshing = null;
      });
      const token = await refreshing;
      if (token) {
        original.headers.Authorization = `Bearer ${token}`;
        return api(original);
      }
    }
    return Promise.reject(error);
  },
);

/** Human readable message from an API error envelope. */
export function errorMessage(error: unknown): string {
  if (axios.isAxiosError(error)) {
    const body = error.response?.data as ApiErrorBody | undefined;
    if (body?.error?.message) {
      const details = Array.isArray(body.error.details)
        ? body.error.details
            .map((d: { loc?: unknown[]; msg?: string }) => `${(d.loc ?? []).slice(1).join('.')}: ${d.msg}`)
            .join('; ')
        : '';
      return details ? `${body.error.message} (${details})` : body.error.message;
    }
    return error.message;
  }
  return error instanceof Error ? error.message : String(error);
}

/** Download a binary endpoint (reports) with auth and save it via a temporary link. */
export async function downloadFile(url: string, params: Record<string, unknown>, fallbackName: string): Promise<void> {
  const res = await api.get(url, { params, responseType: 'blob' });
  const disposition = String(res.headers['content-disposition'] ?? '');
  const name = /filename="?([^";]+)"?/.exec(disposition)?.[1] ?? fallbackName;
  const href = URL.createObjectURL(res.data as Blob);
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(href);
}

export function fileUrl(fileId: number): string {
  return `${API_BASE}/drawings/files/${fileId}/content`;
}
