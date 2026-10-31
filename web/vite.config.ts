import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      // The Android and Windows apps ship the files inside the app, so no service worker there.
      disable: process.env.CDPR_TARGET === 'app',
      registerType: 'prompt',
      includeAssets: ['icon.svg'],
      manifest: {
        name: 'CONSTRUCT DPR',
        short_name: 'DPR',
        description: 'Construction daily progress reporting and site management',
        theme_color: '#0f3d5e',
        background_color: '#0b1520',
        display: 'standalone',
        orientation: 'any',
        start_url: '/',
        icons: [
          { src: 'icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: 'icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: 'icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        navigateFallback: '/index.html',
        // API calls are never served from cache: offline data lives in IndexedDB instead.
        runtimeCaching: [],
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
      },
    }),
  ],
  // The upload rules are shared with the Drive edge function (../supabase/functions/drive/core.ts).
  server: { fs: { allow: ['..'] } },
  build: { chunkSizeWarningLimit: 1200 },
  test: { environment: 'node' },
})
