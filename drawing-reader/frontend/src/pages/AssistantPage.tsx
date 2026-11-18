import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  Box,
  Button,
  Card,
  Chip,
  CircularProgress,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import SendIcon from '@mui/icons-material/Send';
import type { ColDef } from 'ag-grid-community';
import { useState } from 'react';
import { errorMessage } from '../api/client';
import { useAiQuery } from '../api/hooks';
import type { AIQueryResponse } from '../api/types';
import { Grid } from '../components/Grid';
import { useUIStore } from '../store/ui';

export const SAMPLE_QUESTIONS = [
  'Show all IFC drawings',
  'Show drawings with weight increase over 5 tons',
  'Which drawing added most steel?',
  'Show procurement impact',
  'Total weight by discipline',
  'How many drawings per discipline?',
];

export function AssistantPage() {
  const projectId = useUIStore((s) => s.projectId);
  const [question, setQuestion] = useState('');
  const [history, setHistory] = useState<AIQueryResponse[]>([]);
  const ask = useAiQuery();

  const submit = async (q: string) => {
    if (q.trim().length < 3) return;
    const res = await ask.mutateAsync({ question: q.trim(), projectId });
    setHistory((h) => [res, ...h]);
    setQuestion('');
  };

  return (
    <Stack spacing={2} sx={{ maxWidth: 1200 }}>
      <Typography variant="h5">AI Engineering Assistant</Typography>
      <Typography variant="body2" color="text.secondary">
        Ask questions about drawings, revisions, weights and costs. Every answer is computed with SQL over the live register,
        so it matches the dashboard.
      </Typography>
      <Card sx={{ p: 2 }}>
        <form onSubmit={(e) => { e.preventDefault(); void submit(question); }}>
          <Stack direction="row" spacing={1}>
            <TextField fullWidth size="small" value={question} onChange={(e) => setQuestion(e.target.value)}
              placeholder="e.g. What changed between R2 and R3 on NRP-ST-A01-GA-0001?" inputProps={{ 'aria-label': 'question' }} />
            <Button type="submit" variant="contained" endIcon={ask.isPending ? <CircularProgress size={16} color="inherit" /> : <SendIcon />}
              disabled={ask.isPending || question.trim().length < 3}>
              Ask
            </Button>
          </Stack>
        </form>
        <Stack direction="row" spacing={1} sx={{ mt: 1.5 }} flexWrap="wrap" useFlexGap>
          {SAMPLE_QUESTIONS.map((q) => (
            <Chip key={q} label={q} variant="outlined" onClick={() => void submit(q)} />
          ))}
        </Stack>
      </Card>
      {ask.error && <Alert severity="error">{errorMessage(ask.error)}</Alert>}
      {history.map((r, i) => {
        const columns: ColDef<Record<string, unknown>>[] = r.columns.map((c) => ({
          field: c,
          headerName: c.replace(/_/g, ' '),
          flex: c === 'title' || c === 'description' ? 2 : 1,
          minWidth: 100,
          valueFormatter: (p) => (typeof p.value === 'number' ? p.value.toLocaleString('en-US', { maximumFractionDigits: 2 }) : String(p.value ?? '')),
        }));
        return (
          <Card key={`${r.question}-${history.length - i}`} sx={{ p: 2 }}>
            <Typography variant="subtitle2" color="text.secondary">{r.question}</Typography>
            <Typography sx={{ my: 1 }}>{r.answer}</Typography>
            <Stack direction="row" spacing={1} sx={{ mb: 1 }}>
              <Chip size="small" label={`intent: ${r.intent}`} />
              <Chip size="small" label={`${r.rows.length} rows`} />
              <Chip size="small" label={`${r.elapsed_ms} ms`} />
            </Stack>
            {r.suggestions.length > 0 && (
              <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
                {r.suggestions.map((s) => <Chip key={s} size="small" label={s} onClick={() => setQuestion(s)} />)}
              </Stack>
            )}
            {r.rows.length > 0 && (
              <Box sx={{ mt: 1 }}>
                <Grid<Record<string, unknown>> rows={r.rows} columns={columns} height={Math.min(80 + r.rows.length * 34, 380)} />
              </Box>
            )}
            {r.sql && (
              <Accordion disableGutters sx={{ mt: 1 }}>
                <AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography variant="body2">SQL</Typography></AccordionSummary>
                <AccordionDetails>
                  <Box component="pre" sx={{ m: 0, fontSize: 12, whiteSpace: 'pre-wrap', fontFamily: 'monospace' }}>{r.sql}</Box>
                </AccordionDetails>
              </Accordion>
            )}
          </Card>
        );
      })}
    </Stack>
  );
}
