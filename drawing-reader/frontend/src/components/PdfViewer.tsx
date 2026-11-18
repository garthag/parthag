import { Alert, Box, CircularProgress, IconButton, Stack, Tooltip, Typography } from '@mui/material';
import ZoomInIcon from '@mui/icons-material/ZoomIn';
import ZoomOutIcon from '@mui/icons-material/ZoomOut';
import FitScreenIcon from '@mui/icons-material/FitScreen';
import { GlobalWorkerOptions, getDocument, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';
import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { fileUrl } from '../api/client';
import type { Region } from '../api/types';
import { useAuthStore } from '../store/auth';

GlobalWorkerOptions.workerSrc = workerUrl;

export interface PdfViewerHandle {
  scrollToFraction: (fx: number, fy: number) => void;
}

interface Props {
  fileId: number | null | undefined;
  zoom: number;
  onZoomChange: (zoom: number) => void;
  onScrollFraction?: (fx: number, fy: number) => void;
  regions?: Region[];
  regionPage?: number;
  height?: number | string;
  label?: string;
}

const MIN_ZOOM = 0.25;
const MAX_ZOOM = 6;

/** PDF.js viewer: renders every page to canvas, supports zoom, scroll sync and highlight overlays. */
export const PdfViewer = forwardRef<PdfViewerHandle, Props>(function PdfViewer(
  { fileId, zoom, onZoomChange, onScrollFraction, regions = [], regionPage = 0, height = '100%', label },
  ref,
) {
  const token = useAuthStore((s) => s.accessToken);
  const scroller = useRef<HTMLDivElement>(null);
  const pagesRef = useRef<HTMLDivElement>(null);
  const suppress = useRef(false);
  const [doc, setDoc] = useState<PDFDocumentProxy | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [baseWidth, setBaseWidth] = useState(800);

  useImperativeHandle(ref, () => ({
    scrollToFraction: (fx, fy) => {
      const el = scroller.current;
      if (!el) return;
      suppress.current = true;
      el.scrollLeft = fx * Math.max(el.scrollWidth - el.clientWidth, 0);
      el.scrollTop = fy * Math.max(el.scrollHeight - el.clientHeight, 0);
    },
  }));

  useEffect(() => {
    if (!fileId) {
      setDoc(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    const task = getDocument({ url: fileUrl(fileId), httpHeaders: token ? { Authorization: `Bearer ${token}` } : {} });
    task.promise
      .then((pdf) => !cancelled && setDoc(pdf))
      .catch((e: Error) => !cancelled && setError(e.message))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
      void task.destroy();
    };
  }, [fileId, token]);

  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setBaseWidth(Math.max(el.clientWidth - 24, 200)));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    const container = pagesRef.current;
    if (!doc || !container) return;
    let cancelled = false;
    const tasks: RenderTask[] = [];
    container.querySelectorAll('canvas').forEach((c) => c.remove());
    (async () => {
      for (let n = 1; n <= doc.numPages && !cancelled; n++) {
        const page = await doc.getPage(n);
        const unscaled = page.getViewport({ scale: 1 });
        const scale = (baseWidth / unscaled.width) * zoom;
        const viewport = page.getViewport({ scale });
        const ratio = window.devicePixelRatio || 1;
        const holder = container.querySelector<HTMLDivElement>(`[data-page="${n - 1}"]`);
        if (!holder || cancelled) return;
        holder.style.width = `${viewport.width}px`;
        holder.style.height = `${viewport.height}px`;
        const canvas = document.createElement('canvas');
        canvas.width = Math.floor(viewport.width * ratio);
        canvas.height = Math.floor(viewport.height * ratio);
        canvas.style.width = `${viewport.width}px`;
        canvas.style.height = `${viewport.height}px`;
        holder.prepend(canvas);
        const ctx = canvas.getContext('2d');
        if (!ctx) continue;
        const task = page.render({ canvasContext: ctx, viewport, transform: ratio !== 1 ? [ratio, 0, 0, ratio, 0, 0] : undefined });
        tasks.push(task);
        await task.promise.catch(() => undefined);
      }
    })();
    return () => {
      cancelled = true;
      tasks.forEach((t) => t.cancel());
    };
  }, [doc, zoom, baseWidth]);

  const onScroll = () => {
    if (suppress.current) {
      suppress.current = false;
      return;
    }
    const el = scroller.current;
    if (!el || !onScrollFraction) return;
    const fx = el.scrollWidth > el.clientWidth ? el.scrollLeft / (el.scrollWidth - el.clientWidth) : 0;
    const fy = el.scrollHeight > el.clientHeight ? el.scrollTop / (el.scrollHeight - el.clientHeight) : 0;
    onScrollFraction(fx, fy);
  };

  const onWheel = (e: React.WheelEvent) => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    onZoomChange(Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, zoom * (e.deltaY < 0 ? 1.1 : 0.9))));
  };

  return (
    <Box sx={{ height, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <Stack direction="row" alignItems="center" spacing={0.5} sx={{ px: 1, py: 0.5, borderBottom: 1, borderColor: 'divider' }}>
        {label && (
          <Typography variant="body2" fontWeight={600} sx={{ mr: 1 }} noWrap>
            {label}
          </Typography>
        )}
        <Tooltip title="Zoom out">
          <IconButton size="small" aria-label="zoom out" onClick={() => onZoomChange(Math.max(MIN_ZOOM, zoom / 1.25))}>
            <ZoomOutIcon fontSize="small" />
          </IconButton>
        </Tooltip>
        <Typography variant="body2" sx={{ width: 48, textAlign: 'center' }}>
          {Math.round(zoom * 100)}%
        </Typography>
        <Tooltip title="Zoom in">
          <IconButton size="small" aria-label="zoom in" onClick={() => onZoomChange(Math.min(MAX_ZOOM, zoom * 1.25))}>
            <ZoomInIcon fontSize="small" />
          </IconButton>
        </Tooltip>
        <Tooltip title="Fit width">
          <IconButton size="small" aria-label="fit width" onClick={() => onZoomChange(1)}>
            <FitScreenIcon fontSize="small" />
          </IconButton>
        </Tooltip>
        {doc && (
          <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>
            {doc.numPages} page{doc.numPages > 1 ? 's' : ''}
          </Typography>
        )}
      </Stack>
      <Box
        ref={scroller}
        onScroll={onScroll}
        onWheel={onWheel}
        sx={{ flex: 1, overflow: 'auto', bgcolor: 'action.hover', p: 1.5, minHeight: 0, position: 'relative' }}
      >
        {!fileId && (
          <Typography color="text.secondary" sx={{ p: 4, textAlign: 'center' }}>
            No drawing file selected
          </Typography>
        )}
        {loading && <CircularProgress sx={{ position: 'absolute', top: '40%', left: '50%' }} />}
        {error && <Alert severity="error">Could not load PDF: {error}</Alert>}
        <Box ref={pagesRef}>
          {doc &&
            Array.from({ length: doc.numPages }, (_, i) => (
              <Box
                key={i}
                data-page={i}
                sx={{ position: 'relative', mx: 'auto', mb: 1.5, bgcolor: '#fff', boxShadow: 2 }}
              >
                {i === regionPage &&
                  regions.map((r, k) => (
                    <Box
                      key={k}
                      data-testid={`region-${r.kind}`}
                      title={r.kind === 'cloud' ? 'Revision cloud' : 'Changed region'}
                      sx={{
                        position: 'absolute',
                        left: `${r.x * 100}%`,
                        top: `${r.y * 100}%`,
                        width: `${r.width * 100}%`,
                        height: `${r.height * 100}%`,
                        border: 2,
                        borderStyle: r.kind === 'cloud' ? 'dashed' : 'solid',
                        borderColor: r.kind === 'cloud' ? '#6250d6' : '#e34948',
                        bgcolor: r.kind === 'cloud' ? 'rgba(98,80,214,0.08)' : 'rgba(227,73,72,0.12)',
                        pointerEvents: 'none',
                      }}
                    />
                  ))}
              </Box>
            ))}
        </Box>
      </Box>
    </Box>
  );
});
