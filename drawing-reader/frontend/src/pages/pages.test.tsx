import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { AppRoutes } from '../App';
import { DrawingTree } from '../components/DrawingTree';
import { useAuthStore } from '../store/auth';
import { useUIStore } from '../store/ui';
import { charts, drawing, summary, tree } from '../test/fixtures';
import { mockGet, ok, page, renderWithProviders } from '../test/utils';
import { AssistantPage } from './AssistantPage';
import { DashboardPage } from './DashboardPage';
import { LoginPage } from './LoginPage';
import { ReportsPage } from './ReportsPage';

vi.mock('../components/PdfViewer', () => ({
  PdfViewer: ({ label }: { label?: string }) => <div data-testid="pdf-viewer">{label}</div>,
}));

const user = { id: 1, email: 'a@b.c', username: 'admin', full_name: 'Admin', is_superuser: true, roles: [], permissions: ['*'] };

beforeEach(() => {
  useUIStore.setState({ projectId: null, mode: 'light' });
});
afterEach(() => {
  vi.restoreAllMocks();
  useAuthStore.getState().logout();
});

describe('LoginPage', () => {
  it('signs in and stores tokens', async () => {
    vi.spyOn(api, 'post').mockResolvedValue({ data: ok({ access_token: 'acc', refresh_token: 'ref', token_type: 'bearer', expires_in: 1800 }) });
    mockGet({ '/auth/me': () => ok(user) });
    renderWithProviders(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<div>home</div>} />
      </Routes>,
      { route: '/login' },
    );
    await userEvent.type(screen.getByLabelText(/username/i), 'admin');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@12345');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));
    expect(await screen.findByText('home')).toBeInTheDocument();
    expect(useAuthStore.getState().accessToken).toBe('acc');
    expect(useAuthStore.getState().user?.username).toBe('admin');
  });

  it('shows API errors', async () => {
    const { AxiosError, AxiosHeaders } = await import('axios');
    const err = new AxiosError('x');
    err.response = { data: { success: false, error: { code: 'unauthorized', message: 'Incorrect username or password' } },
      status: 401, statusText: '', headers: {}, config: { headers: new AxiosHeaders() } };
    vi.spyOn(api, 'post').mockRejectedValue(err);
    renderWithProviders(<LoginPage />, { route: '/login' });
    await userEvent.type(screen.getByLabelText(/username/i), 'admin');
    await userEvent.type(screen.getByLabelText(/password/i), 'wrongpass');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));
    expect(await screen.findByText('Incorrect username or password')).toBeInTheDocument();
  });
});

describe('routing', () => {
  it('redirects anonymous users to login', async () => {
    renderWithProviders(<AppRoutes />, { route: '/register' });
    expect(await screen.findByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });

  it('renders the layout and dashboard when authenticated', async () => {
    useAuthStore.setState({ accessToken: 'acc', refreshToken: 'ref', user });
    mockGet({
      '/auth/me': () => ok(user),
      '/projects': () => page([{ id: 1, code: 'NRP', name: 'North Refinery', status: 'active', drawing_count: 34 }]),
      '/dashboard/summary': () => ok(summary),
      '/dashboard/charts': () => ok(charts),
    });
    renderWithProviders(<AppRoutes />, { route: '/' });
    expect(await screen.findByRole('heading', { name: 'Dashboard' })).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText('open navigation'));
    expect(await screen.findByText('Drawing Register')).toBeInTheDocument();
    expect(await screen.findByText('999.8 t')).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText('toggle dark mode'));
    expect(useUIStore.getState().mode).toBe('dark');

    const post = vi.spyOn(api, 'post').mockResolvedValue({ data: { success: true, message: 'Password changed' } });
    fireEvent.click(screen.getByLabelText('account'));
    fireEvent.click(await screen.findByText('Change password'));
    await userEvent.type(screen.getByLabelText(/current password/i), 'Admin@12345');
    await userEvent.type(screen.getByLabelText(/^new password/i), 'NewPass@123');
    await userEvent.type(screen.getByLabelText(/confirm new password/i), 'Mismatch@1');
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText('The new passwords do not match')).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText(/confirm new password/i));
    await userEvent.type(screen.getByLabelText(/confirm new password/i), 'NewPass@123');
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));
    expect(await screen.findByText('Password changed.')).toBeInTheDocument();
    expect(post).toHaveBeenCalledWith('/auth/change-password', { current_password: 'Admin@12345', new_password: 'NewPass@123' });
  });
});

describe('DashboardPage', () => {
  it('shows KPI cards from the API', async () => {
    const get = mockGet({ '/dashboard/summary': () => ok(summary), '/dashboard/charts': () => ok(charts) });
    useUIStore.setState({ projectId: 2 });
    renderWithProviders(<DashboardPage />);
    expect(await screen.findByText('100')).toBeInTheDocument();
    expect(screen.getByText('300')).toBeInTheDocument();
    expect(screen.getByText('53 IFC / AFC')).toBeInTheDocument();
    expect(screen.getByText('4 need review · 1 failed')).toBeInTheDocument();
    expect(screen.getByText('Drawings by Discipline')).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/dashboard/summary', { params: { project_id: 2 } });
  });

  it('renders empty chart states', async () => {
    const empty = { drawings_by_discipline: [], weight_by_discipline: [], revision_trend: [], monthly_upload_trend: [], status_breakdown: [] };
    mockGet({ '/dashboard/summary': () => ok(summary), '/dashboard/charts': () => ok(empty) });
    renderWithProviders(<DashboardPage />);
    await waitFor(() => expect(screen.getAllByText('No data yet')).toHaveLength(5));
  });
});

describe('DrawingTree', () => {
  it('expands nodes and selects drawings', async () => {
    mockGet({ '/drawings/tree': () => ok(tree) });
    const onSelect = vi.fn();
    renderWithProviders(<DrawingTree projectId={null} selectedId={null} onSelect={onSelect} />);
    fireEvent.click(await screen.findByText('STR – Structural'));
    fireEvent.click(await screen.findByText('A01 – Process Unit'));
    fireEvent.click(await screen.findByText('NRP-ST-A01-GA-0001'));
    expect(onSelect).toHaveBeenCalledWith(7);
  });

  it('searches the tree on the server', async () => {
    const get = mockGet({ '/drawings/tree': () => ok(tree) });
    renderWithProviders(<DrawingTree projectId={1} selectedId={7} onSelect={() => {}} />);
    await userEvent.type(screen.getByPlaceholderText(/search number/i), 'rack');
    await waitFor(() => expect(get).toHaveBeenCalledWith('/drawings/tree', { params: { project_id: 1, search: 'rack' } }));
    expect(await screen.findByText('NRP-ST-A01-GA-0001')).toBeInTheDocument();
  });
});

describe('AssistantPage', () => {
  it('asks a sample question and renders the SQL-backed answer', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue({
      data: ok({ question: 'Show all IFC drawings', intent: 'list_drawings', answer: 'Found 1 drawing(s) (status=IFC).',
        columns: ['drawing_number', 'weight_kg'], rows: [{ drawing_number: drawing.drawing_number, weight_kg: 1234.5 }],
        sql: 'SELECT * FROM drawings', parameters: {}, elapsed_ms: 12, suggestions: [] }),
    });
    renderWithProviders(<AssistantPage />);
    fireEvent.click(screen.getByText('Show all IFC drawings'));
    expect(await screen.findByText('Found 1 drawing(s) (status=IFC).')).toBeInTheDocument();
    expect(screen.getByText('intent: list_drawings')).toBeInTheDocument();
    expect(post).toHaveBeenCalledWith('/ai/query', { question: 'Show all IFC drawings', project_id: null });
  });
});

describe('ReportsPage', () => {
  it('downloads reports with the selected project', async () => {
    useUIStore.setState({ projectId: 1 });
    const get = vi.spyOn(api, 'get').mockResolvedValue({ data: new Blob(['x']), headers: { 'content-disposition': 'attachment; filename="boq.pdf"' } });
    const createUrl = vi.fn(() => 'blob:x');
    Object.assign(URL, { createObjectURL: createUrl, revokeObjectURL: vi.fn() });
    renderWithProviders(<ReportsPage />);
    fireEvent.click(screen.getAllByRole('button', { name: 'PDF' })[1]);
    await waitFor(() => expect(get).toHaveBeenCalledWith('/reports/boq', { params: { format: 'pdf', project_id: 1 }, responseType: 'blob' }));
    await waitFor(() => expect(createUrl).toHaveBeenCalled());
  });
});
