import {
  AppBar,
  Avatar,
  Box,
  Drawer,
  IconButton,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Menu,
  MenuItem,
  TextField,
  Toolbar,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import DashboardIcon from '@mui/icons-material/SpaceDashboard';
import ListAltIcon from '@mui/icons-material/ListAlt';
import ArchitectureIcon from '@mui/icons-material/Architecture';
import CompareIcon from '@mui/icons-material/Compare';
import InventoryIcon from '@mui/icons-material/Inventory2';
import InsightsIcon from '@mui/icons-material/Insights';
import SmartToyIcon from '@mui/icons-material/SmartToy';
import AssessmentIcon from '@mui/icons-material/Assessment';
import DocumentScannerIcon from '@mui/icons-material/DocumentScanner';
import DarkModeIcon from '@mui/icons-material/DarkMode';
import LightModeIcon from '@mui/icons-material/LightMode';
import MenuIcon from '@mui/icons-material/Menu';
import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { useProjects } from '../api/hooks';
import { useAuthStore } from '../store/auth';
import { useUIStore } from '../store/ui';

const DRAWER = 220;

export const NAV = [
  { to: '/', label: 'Dashboard', icon: <DashboardIcon /> },
  { to: '/register', label: 'Drawing Register', icon: <ListAltIcon /> },
  { to: '/analysis', label: 'Drawing Analysis', icon: <ArchitectureIcon /> },
  { to: '/compare', label: 'Revision Compare', icon: <CompareIcon /> },
  { to: '/bom', label: 'BOM & BOQ', icon: <InventoryIcon /> },
  { to: '/analytics', label: 'Analytics', icon: <InsightsIcon /> },
  { to: '/ocr', label: 'OCR Review', icon: <DocumentScannerIcon /> },
  { to: '/assistant', label: 'AI Assistant', icon: <SmartToyIcon /> },
  { to: '/reports', label: 'Reports', icon: <AssessmentIcon /> },
];

export function Layout() {
  const theme = useTheme();
  const wide = useMediaQuery(theme.breakpoints.up('lg'));
  const [open, setOpen] = useState(false);
  const [menu, setMenu] = useState<HTMLElement | null>(null);
  const { mode, toggleMode, projectId, setProjectId } = useUIStore();
  const { user, refreshToken, logout } = useAuthStore();
  const projects = useProjects();
  const navigate = useNavigate();

  const signOut = async () => {
    setMenu(null);
    try {
      await api.post('/auth/logout', { refresh_token: refreshToken });
    } catch {
      /* token may already be expired */
    }
    logout();
    navigate('/login');
  };

  const nav = (
    <List dense sx={{ pt: 1 }}>
      {NAV.map((item) => (
        <ListItemButton
          key={item.to}
          component={NavLink}
          to={item.to}
          end={item.to === '/'}
          onClick={() => setOpen(false)}
          sx={{ mx: 1, borderRadius: 1, '&.active': { bgcolor: 'action.selected', fontWeight: 600 } }}
        >
          <ListItemIcon sx={{ minWidth: 36 }}>{item.icon}</ListItemIcon>
          <ListItemText primary={item.label} />
        </ListItemButton>
      ))}
    </List>
  );

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <AppBar position="fixed" color="default" sx={{ zIndex: (t) => t.zIndex.drawer + 1, borderBottom: 1, borderColor: 'divider' }}>
        <Toolbar variant="dense" sx={{ gap: 1.5 }}>
          {!wide && (
            <IconButton edge="start" aria-label="open navigation" onClick={() => setOpen(true)}>
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h6" noWrap sx={{ fontWeight: 700, letterSpacing: 0.3 }}>
            Drawing Reader
          </Typography>
          <TextField
            select
            size="small"
            label="Project"
            value={projectId ?? ''}
            onChange={(e) => setProjectId(e.target.value === '' ? null : Number(e.target.value))}
            sx={{ minWidth: 220, ml: 2 }}
            slotProps={{ select: { displayEmpty: true }, inputLabel: { shrink: true } }}
          >
            <MenuItem value="">All projects</MenuItem>
            {(projects.data ?? []).map((p) => (
              <MenuItem key={p.id} value={p.id}>
                {p.code} – {p.name}
              </MenuItem>
            ))}
          </TextField>
          <Box sx={{ flex: 1 }} />
          <Tooltip title={mode === 'dark' ? 'Light mode' : 'Dark mode'}>
            <IconButton onClick={toggleMode} aria-label="toggle dark mode">
              {mode === 'dark' ? <LightModeIcon /> : <DarkModeIcon />}
            </IconButton>
          </Tooltip>
          <Tooltip title={user ? `${user.full_name} (${user.roles.map((r) => r.name).join(', ')})` : ''}>
            <IconButton onClick={(e) => setMenu(e.currentTarget)} aria-label="account">
              <Avatar sx={{ width: 30, height: 30, fontSize: 14, bgcolor: 'primary.main' }}>
                {user?.full_name?.[0] ?? '?'}
              </Avatar>
            </IconButton>
          </Tooltip>
          <Menu anchorEl={menu} open={!!menu} onClose={() => setMenu(null)}>
            <MenuItem disabled>{user?.email}</MenuItem>
            <MenuItem onClick={signOut}>Sign out</MenuItem>
          </Menu>
        </Toolbar>
      </AppBar>
      <Drawer
        variant={wide ? 'permanent' : 'temporary'}
        open={wide || open}
        onClose={() => setOpen(false)}
        sx={{ width: DRAWER, flexShrink: 0, '& .MuiDrawer-paper': { width: DRAWER, boxSizing: 'border-box' } }}
      >
        <Toolbar variant="dense" />
        {nav}
      </Drawer>
      <Box component="main" sx={{ flex: 1, minWidth: 0, p: { xs: 1.5, md: 2 }, pt: { xs: 7, md: 8 } }}>
        <Outlet />
      </Box>
    </Box>
  );
}
