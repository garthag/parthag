import { AxiosError, AxiosHeaders } from 'axios';
import { describe, expect, it } from 'vitest';
import { errorMessage, fileUrl } from './client';

function axiosError(data: unknown) {
  const err = new AxiosError('Request failed');
  err.response = { data, status: 422, statusText: '', headers: {}, config: { headers: new AxiosHeaders() } };
  return err;
}

describe('api client helpers', () => {
  it('extracts envelope messages and validation details', () => {
    expect(errorMessage(axiosError({ success: false, error: { code: 'x', message: 'Nope' } }))).toBe('Nope');
    expect(
      errorMessage(axiosError({ success: false, error: { code: 'v', message: 'Invalid', details: [{ loc: ['body', 'code'], msg: 'required' }] } })),
    ).toBe('Invalid (code: required)');
    expect(errorMessage(axiosError(undefined))).toBe('Request failed');
    expect(errorMessage(new Error('boom'))).toBe('boom');
    expect(errorMessage('plain')).toBe('plain');
  });
  it('builds file urls', () => {
    expect(fileUrl(5)).toBe('/api/drawings/files/5/content');
  });
});
