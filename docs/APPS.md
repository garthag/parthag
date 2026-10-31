# CONSTRUCT DPR apps — Android, Windows and Web, one live system

| Where | What you install | Good for |
|---|---|---|
| **Android phone/tablet** | `CONSTRUCT-DPR-android-….apk` | Site engineers, supervisors, storekeepers: camera, voice, GPS, works offline |
| **Windows PC/laptop** | `CONSTRUCT-DPR-…-x64-nsis.exe` (installer) or `…-portable.exe` (no install) | Planners, project managers, approvers, commercial |
| **Any browser** | Nothing (or "Install app" from Chrome/Edge) | Anyone, any device, including iPhone and Mac |

All three are the **same application** connected to the **same live database**:
* **Live:** a report submitted on a phone appears in the Approvals list on every open PC within seconds (status bar shows **⚡ Live updates on**).
* **Who is here:** opening a report shows *"Also open now: Ravi (Android app)"* when someone else — or you on another device — has it open.
* **Changed elsewhere:** if a report you are editing is saved on another device, you are told immediately and can *Load latest* or *Keep editing mine*.
* **Never lost offline:** without network, everything is saved on the device and sent automatically when the connection returns, without duplicates; conflicts are shown for you to resolve.
* **Catch-up:** when a phone wakes up or a laptop comes back from sleep, every screen refreshes what it missed.

## Download

Open the repository on GitHub → **Releases** (right-hand side) → **CONSTRUCT DPR apps (latest build)**. Every update to the code rebuilds these files automatically.

## Install on Android
1. On the phone, open the release page and download **`CONSTRUCT-DPR-android-….apk`**.
2. Open the downloaded file. Android asks to allow installing from this source → **Settings → Allow from this source** → back → **Install**.
3. Open **CONSTRUCT DPR**. On first start paste the **connection code** (see below) → **Connect** → sign in.
4. The first time you take a photo, record a voice note or add a location, Android asks for that permission → **Allow** (only that one; you can change it in Android Settings → Apps → CONSTRUCT DPR → Permissions).

Updating: download and install the newer APK over the old one — your data and sign-in are kept.

> For a company roll-out through the Google Play Store (or Play's managed/private apps), add the signing secrets described below; the build then also produces a signed `…-playstore.aab`.

## Install on Windows
1. On the PC, download **`CONSTRUCT-DPR-…-x64-nsis.exe`** from the release page.
2. Run it. If **"Windows protected your PC"** appears: **More info → Run anyway** (the installer is not code-signed yet — see below).
3. Choose the folder (or keep the default) → **Install**. A desktop and Start-menu shortcut **CONSTRUCT DPR** are created.
4. On first start paste the **connection code** → **Connect** → sign in.

No admin rights? Use **`CONSTRUCT-DPR-…-portable.exe`**: double-click to run, nothing installed.
Updating: run the newer installer; it replaces the old version and keeps your data. Uninstall: Windows **Settings → Apps → CONSTRUCT DPR → Uninstall**.

## The connection code
Each app works with *your* company server. Any signed-in user can find the code in **Settings → Connect other devices** (web, Android or Windows) and send it by WhatsApp/e-mail. It contains only the server address and its **public** key — the same information every copy of the app needs — never a password. Each person still signs in with their own account and sees only their projects.

(Typing it instead: *Or enter the server address and public key* on the first-start screen. The app refuses secret/service-role keys.)

## Sign-in in the apps
Use **e-mail and password** (or set one up from the web version with *Create account*). "Continue with Google" is offered in the browser version.

## For administrators

**Photos / Drive uploads from the apps.** If you restricted the `drive` server function with `ALLOWED_ORIGINS`, add the app origins:
```
ALLOWED_ORIGINS=https://yourcompany-dpr.netlify.app,https://localhost,app://dpr
```
(`https://localhost` = Android app, `app://dpr` = Windows app.)

**Signing for Play Store / no Windows warning (optional).** In GitHub → *Settings → Secrets and variables → Actions* add:

| Secret | For |
|---|---|
| `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` | Signed release APK + Play Store bundle (`.aab`). Create the keystore once with Android Studio or `keytool`, base64-encode it, and **keep a backup** — losing it means you can never update the Play Store app |
| `WINDOWS_CERTIFICATE_BASE64`, `WINDOWS_CERTIFICATE_PASSWORD` | Code-signed Windows installer (removes the SmartScreen warning). Needs a code-signing certificate from a certificate authority (paid) |

The unsigned builds are fine for a pilot. The Android test builds share one fixed, deliberately public test key so each new build installs as an update.

## How it is built (developers)
* Android: Capacitor wraps `web/dist` (`CDPR_TARGET=app npm run build && npx cap sync android`, project in `web/android`).
* Windows: Electron (`native/desktop`) serves `web/dist` from a private `app://dpr` origin with context isolation, sandboxing and a permission handler that allows camera/microphone/location only for the app.
* CI: `.github/workflows/apps.yml` builds both on every change and updates the **apps-latest** release.
* Icons: `web/scripts/make-app-icons.cjs` renders all Android icons/splash screens and the Windows icon from `web/public/icon.svg`.
