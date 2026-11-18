import { fireEvent, screen, waitFor } from '@testing-library/react';
import { createRef } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../test/utils';
import { PdfViewer, type PdfViewerHandle } from './PdfViewer';

const render = vi.fn(() => ({ promise: Promise.resolve(), cancel: vi.fn() }));
vi.mock('pdfjs-dist', () => ({
  GlobalWorkerOptions: {},
  getDocument: vi.fn(() => ({
    promise: Promise.resolve({
      numPages: 2,
      getPage: async () => ({ getViewport: ({ scale }: { scale: number }) => ({ width: 100 * scale, height: 70 * scale }), render }),
    }),
    destroy: vi.fn(),
  })),
}));
vi.mock('pdfjs-dist/build/pdf.worker.min.mjs?url', () => ({ default: 'worker.js' }));

describe('PdfViewer', () => {
  it('loads pages, draws overlays and reports zoom/scroll', async () => {
    HTMLCanvasElement.prototype.getContext = vi.fn(() => ({})) as never;
    const onZoom = vi.fn();
    const onScroll = vi.fn();
    const ref = createRef<PdfViewerHandle>();
    renderWithProviders(
      <PdfViewer ref={ref} fileId={5} zoom={1} onZoomChange={onZoom} onScrollFraction={onScroll} label="Rev A"
        regions={[{ x: 0.1, y: 0.1, width: 0.2, height: 0.2, kind: 'cloud', score: 1 }]} />,
    );
    expect(await screen.findByText('2 pages')).toBeInTheDocument();
    expect(screen.getByTestId('region-cloud')).toBeInTheDocument();
    await waitFor(() => expect(render).toHaveBeenCalled());
    fireEvent.click(screen.getByLabelText('zoom in'));
    expect(onZoom).toHaveBeenCalledWith(1.25);
    fireEvent.click(screen.getByLabelText('zoom out'));
    fireEvent.click(screen.getByLabelText('fit width'));
    expect(onZoom).toHaveBeenLastCalledWith(1);
    const scroller = screen.getByText('Rev A').parentElement!.nextElementSibling as HTMLElement;
    fireEvent.wheel(scroller, { ctrlKey: true, deltaY: -1 });
    expect(onZoom).toHaveBeenCalledTimes(4);
    ref.current?.scrollToFraction(0.5, 0.5);
    fireEvent.scroll(scroller);
    expect(onScroll).not.toHaveBeenCalled(); // programmatic scroll suppressed
    fireEvent.scroll(scroller);
    expect(onScroll).toHaveBeenCalled();
  });

  it('shows a placeholder without a file', () => {
    renderWithProviders(<PdfViewer fileId={null} zoom={1} onZoomChange={() => {}} />);
    expect(screen.getByText('No drawing file selected')).toBeInTheDocument();
  });
});
