import { Alert, Box, Button, Card, CardContent, Stack, TextField, Typography } from '@mui/material';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api, errorMessage } from '../api/client';
import type { ApiResponse, TokenPair, User } from '../api/types';
import { useAuthStore } from '../store/auth';

export function LoginPage() {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const { setTokens, setUser } = useAuthStore();
  const navigate = useNavigate();

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const res = await api.post<ApiResponse<TokenPair>>('/auth/login', { username, password });
      setTokens(res.data.data.access_token, res.data.data.refresh_token);
      const me = await api.get<ApiResponse<User>>('/auth/me');
      setUser(me.data.data);
      navigate('/');
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', bgcolor: 'background.default', p: 2 }}>
      <Card sx={{ width: '100%', maxWidth: 400 }}>
        <CardContent sx={{ p: 4 }}>
          <Typography variant="h5" gutterBottom>
            Drawing Reader
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
            Engineering drawing register, BOM intelligence and revision analytics
          </Typography>
          <form onSubmit={submit}>
            <Stack spacing={2}>
              <TextField label="Username or e-mail" value={username} onChange={(e) => setUsername(e.target.value)}
                autoComplete="username" autoFocus required />
              <TextField label="Password" type="password" value={password} onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password" required />
              {error && <Alert severity="error">{error}</Alert>}
              <Button type="submit" variant="contained" size="large" disabled={busy}>
                {busy ? 'Signing in…' : 'Sign in'}
              </Button>
            </Stack>
          </form>
        </CardContent>
      </Card>
    </Box>
  );
}
