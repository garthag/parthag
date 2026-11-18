import { CircularProgress, CssBaseline, ThemeProvider } from '@mui/material';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Suspense, lazy, useEffect, useMemo } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { useMe } from './api/hooks';
import { Layout } from './components/Layout';
import { DashboardPage } from './pages/DashboardPage';
import { LoginPage } from './pages/LoginPage';
import { useAuthStore } from './store/auth';
import { useUIStore } from './store/ui';
import { buildTheme } from './theme';

const RegisterPage = lazy(() => import('./pages/RegisterPage').then((m) => ({ default: m.RegisterPage })));
const DrawingAnalysisPage = lazy(() => import('./pages/DrawingAnalysisPage').then((m) => ({ default: m.DrawingAnalysisPage })));
const RevisionComparePage = lazy(() => import('./pages/RevisionComparePage').then((m) => ({ default: m.RevisionComparePage })));
const BomPage = lazy(() => import('./pages/BomPage').then((m) => ({ default: m.BomPage })));
const AnalyticsPage = lazy(() => import('./pages/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));
const OcrReviewPage = lazy(() => import('./pages/OcrReviewPage').then((m) => ({ default: m.OcrReviewPage })));
const AssistantPage = lazy(() => import('./pages/AssistantPage').then((m) => ({ default: m.AssistantPage })));
const ReportsPage = lazy(() => import('./pages/ReportsPage').then((m) => ({ default: m.ReportsPage })));

export const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 15_000, refetchOnWindowFocus: false, retry: 1 } },
});

function RequireAuth({ children }: { children: React.ReactNode }) {
  const token = useAuthStore((s) => s.accessToken);
  const setUser = useAuthStore((s) => s.setUser);
  const me = useMe(!!token);
  useEffect(() => {
    if (me.data) setUser(me.data);
  }, [me.data, setUser]);
  if (!token) return <Navigate to="/login" replace />;
  return <>{children}</>;
}

export function AppRoutes() {
  return (
    <Suspense fallback={<CircularProgress sx={{ position: 'fixed', top: '45%', left: '50%' }} />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<RequireAuth><Layout /></RequireAuth>}>
          <Route index element={<DashboardPage />} />
          <Route path="register" element={<RegisterPage />} />
          <Route path="analysis" element={<DrawingAnalysisPage />} />
          <Route path="compare" element={<RevisionComparePage />} />
          <Route path="bom" element={<BomPage />} />
          <Route path="analytics" element={<AnalyticsPage />} />
          <Route path="ocr" element={<OcrReviewPage />} />
          <Route path="assistant" element={<AssistantPage />} />
          <Route path="reports" element={<ReportsPage />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </Suspense>
  );
}

export default function App() {
  const mode = useUIStore((s) => s.mode);
  const theme = useMemo(() => buildTheme(mode), [mode]);
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </ThemeProvider>
    </QueryClientProvider>
  );
}
