import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField } from '@mui/material';
import { useState } from 'react';
import { api, errorMessage } from '../api/client';

export function ChangePasswordDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  const close = () => {
    setCurrent('');
    setNext('');
    setConfirm('');
    setError(null);
    setDone(false);
    onClose();
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (next !== confirm) {
      setError('The new passwords do not match');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api.post('/auth/change-password', { current_password: current, new_password: next });
      setDone(true);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onClose={close} maxWidth="xs" fullWidth>
      <form onSubmit={submit}>
        <DialogTitle>Change password</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            <TextField label="Current password" type="password" value={current} required autoComplete="current-password"
              onChange={(e) => setCurrent(e.target.value)} disabled={done} />
            <TextField label="New password" type="password" value={next} required autoComplete="new-password"
              helperText="At least 8 characters" onChange={(e) => setNext(e.target.value)} disabled={done} />
            <TextField label="Confirm new password" type="password" value={confirm} required autoComplete="new-password"
              onChange={(e) => setConfirm(e.target.value)} disabled={done} />
            {error && <Alert severity="error">{error}</Alert>}
            {done && <Alert severity="success">Password changed.</Alert>}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={close}>{done ? 'Close' : 'Cancel'}</Button>
          {!done && (
            <Button type="submit" variant="contained" disabled={busy || next.length < 8}>
              Change password
            </Button>
          )}
        </DialogActions>
      </form>
    </Dialog>
  );
}
