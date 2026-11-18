# PyInstaller spec for the Drawing Reader desktop build.
#
#   cd drawing-reader/frontend && npm ci && npm run build
#   cd ../backend && pyinstaller --noconfirm desktop/drawing_reader.spec
#
# Optional: set TESSERACT_DIR to a Tesseract-OCR install folder (containing tesseract(.exe) and
# tessdata/) to bundle the OCR engine. Output: dist/DrawingReader/ with
#   DrawingReader.exe       - windowed launcher (starts the server, opens the browser)
#   drawing-reader-cli.exe  - console variant (--self-test, --seed-sample-data, logs to the console)
# ruff: noqa
import os
import sys
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files, collect_submodules

desktop = Path(SPECPATH)
backend = desktop.parent
frontend_dist = backend.parent / "frontend" / "dist"
if not (frontend_dist / "index.html").exists():
    raise SystemExit(f"Frontend not built: {frontend_dist} (run npm run build first)")

datas = [
    (str(backend / "alembic"), "alembic"),
    (str(frontend_dist), "frontend"),
]
datas += collect_data_files("reportlab")
tesseract_dir = os.environ.get("TESSERACT_DIR")
if tesseract_dir:
    datas.append((tesseract_dir, "tesseract"))

hiddenimports = [m for m in collect_submodules("app") if not m.startswith("app.workers")]
hiddenimports += collect_submodules("uvicorn")
hiddenimports += ["email_validator", "multipart", "python_multipart", "sqlalchemy.dialects.sqlite"]

excludes = ["celery", "kombu", "billiard", "paddleocr", "paddle", "matplotlib", "IPython", "pytest", "notebook",
            "psycopg", "psycopg_binary", "pandas", "pymupdf4llm", "pymupdf_layout"]

icon = str(desktop / "icon.ico") if sys.platform == "win32" else None

a = Analysis(
    [str(desktop / "launcher.py")],
    pathex=[str(backend)],
    datas=datas,
    hiddenimports=hiddenimports,
    excludes=excludes,
    noarchive=False,
)
pyz = PYZ(a.pure)

gui = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="DrawingReader",
    console=False,
    icon=icon,
    upx=False,
)
cli = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="drawing-reader-cli",
    console=True,
    icon=icon,
    upx=False,
)
coll = COLLECT(gui, cli, a.binaries, a.datas, name="DrawingReader", upx=False)
