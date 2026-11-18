import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { User } from '../api/types';

interface AuthState {
  accessToken: string | null;
  refreshToken: string | null;
  user: User | null;
  setTokens: (access: string, refresh: string) => void;
  setUser: (user: User | null) => void;
  logout: () => void;
  can: (permission: string) => boolean;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set, get) => ({
      accessToken: null,
      refreshToken: null,
      user: null,
      setTokens: (accessToken, refreshToken) => set({ accessToken, refreshToken }),
      setUser: (user) => set({ user }),
      logout: () => set({ accessToken: null, refreshToken: null, user: null }),
      can: (permission) => {
        const user = get().user;
        return !!user && (user.is_superuser || user.permissions.includes(permission));
      },
    }),
    { name: 'drawing-reader-auth', storage: createJSONStorage(() => sessionStorage) },
  ),
);
