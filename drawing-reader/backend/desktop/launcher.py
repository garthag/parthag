"""Drawing Reader desktop launcher (entry point of the Windows installer build).

Runs the whole application in one process on the user's PC:

* data lives in ``%LOCALAPPDATA%\\DrawingReader`` (SQLite database, uploaded PDFs, logs, secret key);
* the database is migrated with Alembic on every start, reference data (roles, admin user,
  disciplines, material rates) is created on first start;
* the FastAPI server listens on 127.0.0.1 only and also serves the built web UI;
* the bundled Tesseract OCR engine is put on PATH;
* a small window shows the address with "Open in browser" / "Quit" buttons.

Command line::

    DrawingReader.exe                     start (or focus the running instance) and open the browser
    DrawingReader.exe --seed-sample-data  load the 3 sample projects, then exit (used by the installer)
    DrawingReader.exe --self-test         start in a temporary folder, check API/UI/OCR, exit 0 on success
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import secrets
import socket
import sys
import tempfile
import threading
import time
import urllib.request
import webbrowser
from pathlib import Path

APP_NAME = "Drawing Reader"
DEFAULT_PORT = 8765
DEFAULT_LOGIN = ("admin", "Admin@12345")

log = logging.getLogger("launcher")


# --------------------------------------------------------------------------- paths & environment


def bundle_dir() -> Path:
    """Directory holding bundled resources (PyInstaller) or the backend source tree."""
    if getattr(sys, "frozen", False):
        return Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent))
    return Path(__file__).resolve().parents[1]


def frontend_dir(base: Path) -> Path:
    bundled = base / "frontend"
    return bundled if bundled.is_dir() else base.parent / "frontend" / "dist"


def default_data_dir() -> Path:
    if os.environ.get("DRAWING_READER_HOME"):
        return Path(os.environ["DRAWING_READER_HOME"])
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / "DrawingReader"
    return Path.home() / ".drawing-reader"


def configure_environment(data: Path, base: Path) -> None:
    """Set the app's settings through environment variables. Must run before importing ``app``."""
    for sub in ("storage", "logs"):
        (data / sub).mkdir(parents=True, exist_ok=True)
    key_file = data / "secret.key"
    if not key_file.exists():
        key_file.write_text(secrets.token_urlsafe(48), encoding="utf-8")
    db_path = (data / "drawing_reader.db").resolve().as_posix()
    os.environ.update(
        {
            "ENVIRONMENT": "production",
            "DATABASE_URL": f"sqlite:///{db_path}",
            "STORAGE_DIR": str(data / "storage"),
            "REDIS_URL": "",
            "OCR_EAGER": "true",
            "SECRET_KEY": key_file.read_text(encoding="utf-8").strip(),
            "FRONTEND_DIST": str(frontend_dir(base)),
            "CORS_ORIGINS": "http://127.0.0.1",
            "ACCESS_TOKEN_EXPIRE_MINUTES": os.environ.get("ACCESS_TOKEN_EXPIRE_MINUTES", "480"),
        }
    )
    tesseract = base / "tesseract"
    if (tesseract / "tesseract.exe").exists() or (tesseract / "tesseract").exists():
        os.environ["PATH"] = f"{tesseract}{os.pathsep}{os.environ.get('PATH', '')}"
        os.environ["TESSDATA_PREFIX"] = str(tesseract / "tessdata")


def redirect_output(data: Path) -> None:
    """A windowed (no console) build has no stdout/stderr: send all output to a log file."""
    if sys.stdout is None or sys.stderr is None:
        stream = open(data / "logs" / "drawing-reader.log", "a", buffering=1, encoding="utf-8")  # noqa: SIM115
        sys.stdout = sys.stdout or stream
        sys.stderr = sys.stderr or stream


# --------------------------------------------------------------------------- database


def migrate(base: Path) -> None:
    from alembic.config import Config

    from alembic import command

    cfg = Config()
    cfg.set_main_option("script_location", str(base / "alembic"))
    command.upgrade(cfg, "head")


def initialise(base: Path, *, sample_data: bool = False) -> dict:
    """Migrate, create reference data on first start, optionally load the sample projects."""
    migrate(base)
    from sqlalchemy import func, select

    from app.db.session import SessionLocal
    from app.models import Drawing, User
    from app.seed.seed import _ensure_reference_data, seed

    with SessionLocal() as db:
        if not db.scalar(select(func.count(User.id))):
            _ensure_reference_data(db)
            db.commit()
            log.info("Created reference data and default users")
        stats: dict = {}
        if sample_data and not db.scalar(select(Drawing.id).limit(1)):
            stats = seed(db, with_pdfs=True)
            log.info("Loaded sample data: %s", stats)
        return stats


# --------------------------------------------------------------------------- server


def port_is_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        try:
            s.bind(("127.0.0.1", port))
            return True
        except OSError:
            return False


def pick_port(preferred: int) -> int:
    """``preferred`` if it is free, otherwise any free port (``0`` always means any)."""
    if preferred and port_is_free(preferred):
        return preferred
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def http_json(url: str, payload: dict | None = None, token: str | None = None, timeout: float = 30) -> dict:
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method="POST" if data else "GET")  # noqa: S310 - localhost only
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=timeout) as res:  # noqa: S310
        return json.loads(res.read().decode())


def running_instance(data: Path) -> str | None:
    port_file = data / "server.port"
    if not port_file.exists():
        return None
    url = f"http://127.0.0.1:{port_file.read_text().strip()}"
    try:
        if http_json(f"{url}/health", timeout=1.5).get("status") == "ok":
            return url
    except Exception:  # noqa: BLE001 - stale port file
        return None
    return None


class Server:
    def __init__(self, port: int) -> None:
        import uvicorn

        from app.main import app

        config = uvicorn.Config(app, host="127.0.0.1", port=port, log_config=None, access_log=False, workers=1)
        self.server = uvicorn.Server(config)
        self.thread = threading.Thread(target=self.server.run, name="uvicorn", daemon=True)
        self.url = f"http://127.0.0.1:{port}"

    def start(self, timeout: float = 60) -> None:
        self.thread.start()
        deadline = time.time() + timeout
        while not self.server.started:
            if not self.thread.is_alive() or time.time() > deadline:
                raise RuntimeError("The server did not start; see the log file for details")
            time.sleep(0.1)

    def stop(self) -> None:
        self.server.should_exit = True
        self.thread.join(timeout=10)


# --------------------------------------------------------------------------- window


def control_window(server: Server, data: Path) -> None:
    """Small always-available window; closing it stops the server."""
    try:
        import tkinter as tk
        from tkinter import ttk
    except ImportError:  # pragma: no cover - headless platforms
        log.info("tkinter unavailable; serving until interrupted")
        while server.thread.is_alive():
            time.sleep(1)
        return
    root = tk.Tk()
    root.title(APP_NAME)
    root.resizable(False, False)
    frame = ttk.Frame(root, padding=16)
    frame.grid()
    ttk.Label(frame, text=f"{APP_NAME} is running", font=("Segoe UI", 12, "bold")).grid(columnspan=2, sticky="w")
    ttk.Label(frame, text=server.url, foreground="#1f3a5f").grid(columnspan=2, sticky="w", pady=(4, 8))
    ttk.Label(
        frame,
        text=f"First sign-in: {DEFAULT_LOGIN[0]} / {DEFAULT_LOGIN[1]} (change it in the account menu).\n"
        f"Your data is stored in {data}",
        justify="left",
    ).grid(columnspan=2, sticky="w", pady=(0, 12))
    ttk.Button(frame, text="Open in browser", command=lambda: webbrowser.open(server.url)).grid(
        row=3, column=0, sticky="w"
    )

    def quit_app() -> None:
        root.destroy()

    ttk.Button(frame, text="Quit", command=quit_app).grid(row=3, column=1, sticky="e")
    root.protocol("WM_DELETE_WINDOW", quit_app)
    root.mainloop()


# --------------------------------------------------------------------------- self test


def self_test(base: Path) -> int:
    """End-to-end check of a built bundle: migrations, API, UI files, PDF generation and OCR."""
    data = Path(tempfile.mkdtemp(prefix="drawing-reader-selftest-"))
    configure_environment(data, base)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    checks: list[tuple[str, bool, str]] = []

    def check(name: str, fn) -> None:
        try:
            detail = fn()
            checks.append((name, True, str(detail or "")))
        except Exception as exc:  # noqa: BLE001
            checks.append((name, False, f"{type(exc).__name__}: {exc}"))

    check("migrate + reference data", lambda: initialise(base))
    server = Server(pick_port(DEFAULT_PORT + 1))
    check("server start", server.start)
    url = server.url
    check("health", lambda: http_json(f"{url}/health")["status"])

    def ui() -> str:
        html = urllib.request.urlopen(f"{url}/register", timeout=10).read().decode()  # noqa: S310 - SPA deep link
        if 'id="root"' not in html:
            raise RuntimeError("index.html not served")
        return "ok"

    check("web UI served", ui)
    token: dict[str, str] = {}

    def login() -> str:
        token["t"] = http_json(f"{url}/api/auth/login", {"username": DEFAULT_LOGIN[0], "password": DEFAULT_LOGIN[1]})[
            "data"
        ]["access_token"]
        return "ok"

    check("login", login)
    check("dashboard", lambda: http_json(f"{url}/api/dashboard/summary", token=token.get("t"))["data"]["projects"])

    def ocr() -> str:
        from decimal import Decimal

        from app.services.bom.extractor import extract_bom
        from app.services.ocr.backends import get_backend
        from app.services.ocr.engine import process_pdf
        from app.services.pdf_generator import BOMLine, DrawingSpec, generate_drawing_pdf

        backend = get_backend("tesseract")
        if backend is None:
            raise RuntimeError("Tesseract not found")
        bom = [
            BOMLine(
                "1",
                "P-1",
                "PL 10 x 200 x 800",
                "CS",
                "S355JR",
                "EN 10025",
                Decimal(2),
                "EA",
                Decimal("12.56"),
                Decimal("25.12"),
            )
        ]
        path = data / "scan.pdf"
        generate_drawing_pdf(
            DrawingSpec("ST-A01-GA-0001", "SELF TEST", "B", "2026-01-01", bom=bom), path, scanned=True, dpi=300
        )
        result = process_pdf(path, filename="scan.pdf", backend=backend)
        if result.fields.get("drawing_number") != "ST-A01-GA-0001" or result.fields.get("revision") != "B":
            raise RuntimeError(f"unexpected OCR fields {result.fields}")
        items = extract_bom(result.pages, backend).items
        return f"conf={result.overall_confidence:.2f} bom_rows={len(items)}"

    check("scanned-drawing OCR (Tesseract)", ocr)
    server.stop()
    for name, ok, detail in checks:
        print(f"[{'PASS' if ok else 'FAIL'}] {name} {detail}")
    return 0 if all(ok for _, ok, _ in checks) else 1


# --------------------------------------------------------------------------- main


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="DrawingReader", description=APP_NAME)
    parser.add_argument("--seed-sample-data", action="store_true", help="load sample projects and exit")
    parser.add_argument("--self-test", action="store_true", help="verify the installation and exit")
    parser.add_argument("--no-browser", action="store_true", help="do not open the browser")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args(argv)
    base = bundle_dir()
    if str(base) not in sys.path:  # running from source
        sys.path.insert(0, str(base))

    if args.self_test:
        return self_test(base)

    data = default_data_dir()
    data.mkdir(parents=True, exist_ok=True)
    configure_environment(data, base)
    redirect_output(data)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

    if args.seed_sample_data:
        initialise(base, sample_data=True)
        return 0

    existing = running_instance(data)
    if existing:
        log.info("Already running at %s", existing)
        if not args.no_browser:
            webbrowser.open(existing)
        return 0

    initialise(base)
    server = Server(pick_port(args.port))
    server.start()
    (data / "server.port").write_text(server.url.rsplit(":", 1)[1])
    log.info("%s running at %s", APP_NAME, server.url)
    if not args.no_browser:
        webbrowser.open(server.url)
    try:
        control_window(server, data)
    finally:
        server.stop()
        (data / "server.port").unlink(missing_ok=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
