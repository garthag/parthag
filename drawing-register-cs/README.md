# Drawing Register (C# · WPF · SQLite)

A native Windows desktop app for keeping an engineering drawing register **fully offline**:
create projects, upload PDF drawings (single files, whole folder trees, or drag & drop),
track revisions, search, preview and export. No server, no internet, no administrator rights.

## Download & install

1. Download `DrawingRegisterSetup-1.0.x.exe` from the repository's **Releases** page
   (releases named *Drawing Register (C#) 1.0.x for Windows*).
2. Run it. Windows SmartScreen may say the publisher is unknown because the installer isn't
   code-signed: choose **More info → Run anyway**. It installs for your user account; no .NET
   installation is needed (the runtime is bundled).
3. Start **Drawing Register** from the Start menu or desktop.

## Using it

| Task | How |
|---|---|
| Add a project | **+ Add project** (Ctrl+N): code (e.g. `NRP-2026`), name, client, location, status, start/end dates, description |
| Edit / delete a project | Select it, then **Edit** / **Delete** under the project list (delete removes its drawings too) |
| Upload drawings | **Upload PDFs** (Ctrl+U, multi-select), **Upload folder** (all sub-folders), or drag PDFs/folders onto the drawing list |
| Revisions | `ABC-ST-0001_RevB.pdf` becomes drawing `ABC-ST-0001`, revision `B`; uploading `…_RevC.pdf` adds revision C and makes it current. Files without a revision code become `0`, then `U2`, `U3`… |
| Automatic categorisation | Discipline, area and drawing type are read from the number (`NRP-ST-A01-GA-0001` → Structural, A01, GA) and from folder names (`02_Piping/Area 03/Isometric`) |
| Duplicates | An identical PDF already in the project is skipped (SHA-256 check) |
| Search / filter | Search box matches number, title, discipline, area and folder; filter by discipline and status |
| Edit drawing details | Double-click a drawing or **Edit drawing**: title, discipline, area, type, status, remarks |
| View PDFs | Built-in preview (Microsoft Edge WebView2, part of Windows 10/11); **Open PDF** opens your default viewer; **Show in folder** |
| Export | **Export CSV** saves the visible register for Excel |

Data lives in `%LOCALAPPDATA%\DrawingRegister`: `drawing_register.db` (SQLite) and `drawings\`
(the uploaded PDFs, stored once per project by content hash). **Open data folder** in the status
bar opens it; copy that folder to back everything up. Uninstalling keeps your data.

## Build from source

Requirements: Windows, .NET 10 SDK, Inno Setup 6 (`choco install innosetup -y`).

```powershell
cd drawing-register-cs
powershell -ExecutionPolicy Bypass -File build-windows.ps1 -Version 1.0.0
```

This runs the unit tests, publishes a self-contained `win-x64` build, runs the app's smoke test
(`DrawingRegister.exe --smoke-test report.txt` opens the real window in a temporary data folder,
adds a project, uploads a folder of PDFs, checks revisions, search and the PDF preview, then
exits), and writes `dist\DrawingRegisterSetup-1.0.0.exe`.

The core library and tests also build and run on Linux/macOS: `dotnet test tests/DrawingRegister.Core.Tests`.

CI (`.github/workflows/drawing-register-cs.yml`) does the same on `windows-latest`, then silently
installs the produced installer, smoke-tests the installed app, uninstalls it, and publishes the
installer on the Releases page.

## Layout

```
src/DrawingRegister.Core/    SQLite schema & migrations, repositories, file-name parser,
                             revision ordering, import (validation, hashing, storage), CSV export
src/DrawingRegister.App/     WPF UI (MVVM): main window, project/drawing dialogs, upload results,
                             WebView2 PDF preview, smoke test
tests/                       xUnit tests for the core (22 tests)
installer/                   Inno Setup script
```
