import axios, { AxiosHeaders } from 'axios';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useAuthStore } from '../store/auth';
import { api, downloadFile } from './client';

afterEach(() => {
  vi.restoreAllMocks();
  useAuthStore.getState().logout();
});

describe('api interceptors', () => {
  it('adds the bearer token and refreshes once on 401', async () => {
    useAuthStore.setState({ accessToken: 'old', refreshToken: 'ref' });
    const seen: string[] = [];
    api.defaults.adapter = async (config) => {
      const auth = String(new AxiosHeaders(config.headers).get('Authorization'));
      seen.push(auth);
      if (auth === 'Bearer old') {
        const err = new axios.AxiosError('unauthorized', '401', config, null, { status: 401, data: {}, statusText: '', headers: {}, config });
        throw err;
      }
      return { data: { ok: true }, status: 200, statusText: 'OK', headers: {}, config };
    };
    vi.spyOn(axios, 'post').mockResolvedValue({ data: { data: { access_token: 'new', refresh_token: 'ref2' } } });
    const res = await api.get('/x');
    expect(res.data).toEqual({ ok: true });
    expect(seen).toEqual(['Bearer old', 'Bearer new']);
    expect(useAuthStore.getState().refreshToken).toBe('ref2');
  });

  it('logs out when refresh fails', async () => {
    useAuthStore.setState({ accessToken: 'old', refreshToken: 'ref' });
    api.defaults.adapter = async (config) => {
      throw new axios.AxiosError('unauthorized', '401', config, null, { status: 401, data: {}, statusText: '', headers: {}, config });
    };
    vi.spyOn(axios, 'post').mockRejectedValue(new Error('expired'));
    await expect(api.get('/x')).rejects.toBeTruthy();
    expect(useAuthStore.getState().accessToken).toBeNull();
  });

  it('downloads files via a temporary link', async () => {
    api.defaults.adapter = async (config) => ({ data: new Blob(['a']), status: 200, statusText: 'OK', headers: {}, config });
    Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:y'), revokeObjectURL: vi.fn() });
    await downloadFile('/reports/mto', { format: 'csv' }, 'mto.csv');
    expect(URL.createObjectURL).toHaveBeenCalled();
  });
});
