import type { CapacitorConfig } from '@capacitor/cli'

// Android app: the same web app, bundled inside the APK (works offline from the first start).
const config: CapacitorConfig = {
  appId: 'org.constructdpr.app',
  appName: 'CONSTRUCT DPR',
  webDir: 'dist',
  android: {
    // Site photos and voice notes are kept in app storage until uploaded.
    allowMixedContent: false,
  },
}

export default config
