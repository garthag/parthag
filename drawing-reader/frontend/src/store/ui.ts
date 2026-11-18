import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type ThemeMode = 'light' | 'dark';

interface UIState {
  mode: ThemeMode;
  projectId: number | null;
  toggleMode: () => void;
  setProjectId: (id: number | null) => void;
}

const prefersDark = typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches;

export const useUIStore = create<UIState>()(
  persist(
    (set) => ({
      mode: prefersDark ? 'dark' : 'light',
      projectId: null,
      toggleMode: () => set((s) => ({ mode: s.mode === 'light' ? 'dark' : 'light' })),
      setProjectId: (projectId) => set({ projectId }),
    }),
    { name: 'drawing-reader-ui' },
  ),
);
