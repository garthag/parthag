"""Desktop app: the same UI in a native window (Edge WebView2 on Windows).

Runs the local server on a free port in a background thread and opens a
pywebview window on it. Gives the page native dialogs (Browse for folder,
Save ZIP as..., Open folder in Explorer). Falls back to the normal browser if
pywebview isn't available.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
import traceback
import urllib.request
import webbrowser
from pathlib import Path

from werkzeug.serving import make_server

from .config import app_dir, settings

TITLE = "Mail Attachment Agent"


def _start_server(app):
    srv = make_server("127.0.0.1", 0, app, threaded=True)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, f"http://127.0.0.1:{srv.server_port}/"


def _dialog_kind(webview, name: str):
    """pywebview 5 uses webview.FOLDER_DIALOG, 6 uses webview.FileDialog.FOLDER."""
    fd = getattr(webview, "FileDialog", None)
    if fd is not None:
        return getattr(fd, name)
    return getattr(webview, f"{name}_DIALOG")


class DesktopApi:
    """Methods callable from JavaScript as window.pywebview.api.<name>()."""

    def __init__(self, webview):
        self._webview = webview
        self._window = None

    def pick_folder(self, start: str = ""):
        start = os.path.expanduser(start or "")
        r = self._window.create_file_dialog(_dialog_kind(self._webview, "FOLDER"),
                                            directory=start if os.path.isdir(start) else "")
        return r[0] if r else None

    def save_zip_path(self, name: str = "attachments.zip", start: str = ""):
        start = os.path.expanduser(start or "")
        r = self._window.create_file_dialog(_dialog_kind(self._webview, "SAVE"),
                                            directory=start if os.path.isdir(start) else "",
                                            save_filename=name, file_types=("ZIP files (*.zip)",))
        if not r:
            return None
        return r if isinstance(r, str) else r[0]

    def open_folder(self, path: str):
        p = Path(os.path.expanduser(path or ""))
        if p.is_file():
            if sys.platform == "win32":
                subprocess.Popen(["explorer", "/select,", str(p)])
                return True
            p = p.parent
        if not p.is_dir():
            return False
        if sys.platform == "win32":
            os.startfile(str(p))  # noqa: S606 - opens Explorer on a folder the user chose
        elif sys.platform == "darwin":
            subprocess.Popen(["open", str(p)])
        else:
            subprocess.Popen(["xdg-open", str(p)])
        return True


def run(demo: bool = False) -> int:
    from .web import create_app
    cfg = settings()
    engine = None
    if demo:
        from .demo import DemoSource
        from .engine import SearchEngine
        engine = SearchEngine(DemoSource(), workers=cfg["workers"])
    app = create_app(cfg, engine=engine, demo=demo)
    srv, url = _start_server(app)

    try:
        import webview
    except Exception:
        print(f"{TITLE}: pywebview not installed - opening your browser at {url}")
        webbrowser.open(url)
        try:
            threading.Event().wait()
        except KeyboardInterrupt:
            pass
        srv.shutdown()
        return 0

    api = DesktopApi(webview)
    window = webview.create_window(TITLE, url, js_api=api, width=1440, height=900, min_size=(960, 620))
    api._window = window
    try:
        webview.settings["ALLOW_DOWNLOADS"] = True
    except Exception:
        pass
    storage = app_dir() / "webview"
    storage.mkdir(exist_ok=True)
    try:
        # keep localStorage (recent searches, chosen folder) between runs
        webview.start(private_mode=False, storage_path=str(storage))
    finally:
        srv.shutdown()
    return 0


def selftest(out: str | None = None) -> int:
    """Used by the Windows build: proves the packaged app starts and serves the UI."""
    lines, ok = [], True
    try:
        from .demo import DemoSource
        from .engine import SearchEngine
        from .web import create_app
        cfg = settings()
        app = create_app(cfg, engine=SearchEngine(DemoSource(), workers=2), demo=True)
        srv, url = _start_server(app)

        def get(path, body=None):
            req = urllib.request.Request(url + path.lstrip("/"), data=json.dumps(body).encode() if body else None,
                                         headers={"Content-Type": "application/json"} if body else {})
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status, r.read()

        for path in ("/", "/static/app.js", "/static/app.css", "/api/status"):
            st, data = get(path)
            lines.append(f"GET {path} -> {st} ({len(data)} bytes)")
            ok &= st == 200 and len(data) > 50
        st, data = get("/api/search", {"senders": "mck kutty", "keywords": "invoice", "date_from": "2024-01-01",
                                       "date_to": "2025-12-25", "file_types": ["pdf"], "match_pdf_text": True})
        n = len(json.loads(data)["results"])
        lines.append(f"search -> {n} results")
        ok &= n == 26
        srv.shutdown()
        try:
            import webview  # noqa: F401
            lines.append(f"pywebview {getattr(webview, '__version__', '?')} OK")
        except Exception as ex:
            lines.append(f"pywebview import FAILED: {ex!r}")
            ok &= sys.platform != "win32"      # required for the Windows app only
        if sys.platform == "win32":
            try:
                import webview.platforms.winforms  # noqa: F401  (pythonnet + WebView2 DLLs bundled?)
                lines.append("winforms/WebView2 backend OK")
            except Exception as ex:
                lines.append(f"winforms backend FAILED: {ex!r}")
                ok = False
        from .config import can_remember
        remember = can_remember()
        lines.append(f"remember-me (credential store): {'OK' if remember else 'not available'}")
        if sys.platform == "win32":
            ok &= remember
    except Exception:
        lines.append(traceback.format_exc())
        ok = False
    lines.append("SELFTEST OK" if ok else "SELFTEST FAILED")
    text = "\n".join(lines)
    if out:
        Path(out).write_text(text, "utf-8")
    if sys.stdout:
        print(text)
    return 0 if ok else 1
