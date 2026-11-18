import { describe, expect, it } from 'vitest';
import { useAuthStore } from './auth';
import { useUIStore } from './ui';

describe('stores', () => {
  it('auth store handles tokens and permissions', () => {
    const s = useAuthStore.getState();
    s.setTokens('a', 'r');
    expect(useAuthStore.getState().accessToken).toBe('a');
    expect(s.can('drawings.upload')).toBe(false);
    s.setUser({ id: 1, email: 'x', username: 'x', full_name: 'X', is_superuser: false, roles: [], permissions: ['drawings.upload'] });
    expect(useAuthStore.getState().can('drawings.upload')).toBe(true);
    expect(useAuthStore.getState().can('users.manage')).toBe(false);
    useAuthStore.getState().setUser({ id: 1, email: 'x', username: 'x', full_name: 'X', is_superuser: true, roles: [], permissions: [] });
    expect(useAuthStore.getState().can('anything')).toBe(true);
    useAuthStore.getState().logout();
    expect(useAuthStore.getState().accessToken).toBeNull();
  });

  it('ui store toggles theme and project', () => {
    const before = useUIStore.getState().mode;
    useUIStore.getState().toggleMode();
    expect(useUIStore.getState().mode).not.toBe(before);
    useUIStore.getState().setProjectId(3);
    expect(useUIStore.getState().projectId).toBe(3);
  });
});
