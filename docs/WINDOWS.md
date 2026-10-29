# CONSTRUCT DPR on Windows

There is nothing to install from an `.exe`. CONSTRUCT DPR is an installable web app: Chrome or Microsoft Edge installs it like a normal Windows program. It gets a Start-menu entry, a desktop/taskbar icon and its own window, and updates itself.

Before you start, the app must be online. Finish **Part A (Supabase)** and **Part B (Netlify)** in [START_HERE.md](START_HERE.md) so you have an address like `https://yourcompany-dpr.netlify.app`.

## 1. Install the app on Windows (2 minutes)

**Google Chrome**
1. Open your app address in Chrome.
2. Click the **Install** icon at the right end of the address bar (a small monitor with a down-arrow).
   No icon? Use menu **⋮ → Cast, save and share → Install CONSTRUCT DPR…** (older Chrome: **⋮ → Install CONSTRUCT DPR…**).
3. Click **Install**. The app opens in its own window.

**Microsoft Edge** (already on every Windows PC)
1. Open your app address in Edge.
2. Click the **App available** icon in the address bar, or menu **⋯ → Apps → Install this site as an app**.
3. Click **Install**, then tick **Pin to taskbar** / **Create desktop shortcut** if offered.

**Open it later:** Start menu → type **CONSTRUCT DPR**, or use the desktop/taskbar icon.
**Updates:** automatic. When a new version is ready the app shows *"A new version is available"* → **Update**.
**Uninstall:** open the app → **⋮ (or ⋯) → Uninstall CONSTRUCT DPR**, or Windows **Settings → Apps → Installed apps**.

> **Camera and microphone on a laptop:** the first time you tap *Capture photo* or *Voice note*, Windows/Chrome asks for permission → **Allow**. If you blocked it: click the 🔒 padlock left of the address → *Site settings* → allow Camera / Microphone. Also check Windows **Settings → Privacy & security → Camera / Microphone** → allow desktop apps and browsers.

## 2. Google Workspace edition on Windows

The AppSheet app works in a browser too:
1. Open the app link that AppSheet e-mailed you (or AppSheet → **Share → Copy link**) in Chrome or Edge, and sign in with Google.
2. Chrome: **⋮ → Cast, save and share → Create shortcut… → tick "Open as window" → Create**. Edge: **⋯ → Apps → Install this site as an app**.
3. Planners and administrators use the **spreadsheet** (CONSTRUCT DPR menu) and **Looker Studio** in the browser; there is nothing else to install.

## 3. Download the project files (optional)

You need these only to look at files, such as the SQL setup files or `Code.gs`, or to hand the code to a developer. Copying from the GitHub website works too.
1. Open the repository on GitHub.
2. Switch the branch dropdown (top left) to **`claude/construct-dpr-app-z3h0j8`**.
3. Green **Code** button → **Download ZIP**.
4. In *Downloads*, right-click the ZIP → **Extract All…** → Extract.

## 4. Run it on your own Windows PC (developers / testing only)

Normal users do not need this. It runs the app on your PC for testing, still connected to your Supabase project.
1. Install **Node.js LTS** (version 22) from <https://nodejs.org> → *Windows Installer (.msi)* → Next … Finish.
2. Download and extract the ZIP (section 3).
3. In the extracted folder open **`web`**. Copy `.env.example`, rename the copy to `.env`, open it with Notepad and fill in `VITE_SUPABASE_URL` and `VITE_SUPABASE_ANON_KEY` (from Part A5).
   *(Windows may hide file extensions: in File Explorer choose **View → Show → File name extensions** so the file is really `.env`, not `.env.txt`.)*
4. In the `web` folder click the address bar, type `powershell`, press Enter, then run:
   ```powershell
   npm ci
   npm run dev
   ```
5. Open <http://localhost:5173> in Chrome or Edge. Press **Ctrl + C** in PowerShell to stop.

To produce the files for hosting: `npm run build`. The result is in `web\dist`.

## Troubleshooting

| Problem | Fix |
|---|---|
| No *Install* option | The address must start with **https://** (or be `localhost`). Reload once; the option appears after the page has fully loaded |
| "Setup required" screen | The Netlify environment variables are missing; see START_HERE.md, Part B3 |
| `npm` is not recognised | Close and reopen PowerShell after installing Node.js, or restart Windows |
| Script execution error in PowerShell | Run `npm.cmd ci` and `npm.cmd run dev` instead, or use **Command Prompt** |
| Camera not found | Another app (Teams/Zoom) is using it; close that app. Check Windows camera privacy settings |
