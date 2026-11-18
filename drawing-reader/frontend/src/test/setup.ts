import '@testing-library/jest-dom/vitest';

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
// Recharts / MUI rely on ResizeObserver and matchMedia which jsdom lacks.
globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
if (!window.matchMedia) {
  window.matchMedia = (query: string) =>
    ({ matches: false, media: query, onchange: null, addListener() {}, removeListener() {}, addEventListener() {},
      removeEventListener() {}, dispatchEvent: () => false }) as MediaQueryList;
}
