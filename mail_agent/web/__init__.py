"""Local web UI (Flask). Binds to 127.0.0.1 only."""

from __future__ import annotations

import threading
from datetime import date, datetime

from flask import Flask, Response, abort, jsonify, render_template, request

from ..downloader import FOLDER_TEMPLATES, NAME_TEMPLATES, build_zip, save_all
from ..engine import SearchEngine, pdf_text
from ..models import SearchRequest

SAFE_INLINE = {"application/pdf", "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"}
TEXTUAL = ("text/", "application/json", "application/xml", "message/rfc822")
ALLOWED_HOSTS = {"127.0.0.1", "localhost", "[::1]"}


def _list(v) -> list[str]:
    if isinstance(v, list):
        items = v
    else:
        items = str(v or "").replace(";", ",").replace("\n", ",").split(",")
    return [s.strip() for s in items if s and s.strip()]


def _date(v) -> date | None:
    if not v:
        return None
    return datetime.strptime(str(v)[:10], "%Y-%m-%d").date()


def parse_request(j: dict) -> SearchRequest:
    return SearchRequest(
        senders=_list(j.get("senders")), keywords=_list(j.get("keywords")),
        exclude_keywords=_list(j.get("exclude")),
        date_from=_date(j.get("date_from")), date_to=_date(j.get("date_to")),
        file_types=_list(j.get("file_types")), folders=_list(j.get("folders")),
        match_pdf_text=bool(j.get("match_pdf_text")), hide_duplicates=j.get("hide_duplicates", True) is not False,
        include_inline=bool(j.get("include_inline")), min_score=float(j.get("min_score") or 0),
        window_months=int(j.get("window_months") or 3),
    )


def create_app(settings: dict, engine: SearchEngine | None = None, demo: bool = False) -> Flask:
    app = Flask(__name__)
    state = {"engine": engine, "account": "Demo mailbox" if demo else "", "demo": demo}
    lock = threading.Lock()

    @app.before_request
    def _guard():
        # Block DNS-rebinding and cross-site POSTs: only local host names, JSON bodies.
        host = (request.host or "").rsplit(":", 1)[0].lower()
        if host not in ALLOWED_HOSTS:
            abort(403)
        if request.method == "POST" and not request.is_json:
            abort(415)

    def eng() -> SearchEngine:
        if state["engine"] is None:
            abort(409, "Not connected")
        return state["engine"]

    @app.errorhandler(409)
    @app.errorhandler(400)
    def _err(e):
        return jsonify(error=getattr(e, "description", str(e))), e.code

    @app.get("/")
    def index():
        return render_template("index.html", folder_templates=FOLDER_TEMPLATES, name_templates=NAME_TEMPLATES)

    @app.get("/api/status")
    def status():
        e = state["engine"]
        return jsonify(connected=e is not None, account=state["account"], demo=state["demo"],
                       gmail=bool(e and getattr(e.source, "is_gmail", False)),
                       download_dir=settings["download_dir"],
                       env_login=bool(settings.get("host") and settings.get("user") and settings.get("password")))

    @app.post("/api/connect")
    def connect():
        from ..imap_source import PROVIDERS, IMAPSource
        j = request.get_json()
        if j.get("use_env"):
            host, port, user, pw = settings["host"], settings["port"], settings["user"], settings["password"]
        else:
            prov = j.get("provider") or "custom"
            host, port = PROVIDERS.get(prov, (j.get("host", ""), int(j.get("port") or 993)))
            user, pw = j.get("user", ""), j.get("password", "")
        if not (host and user and pw):
            return jsonify(error="Host, email and password are required"), 400
        try:
            src = IMAPSource(host, user, pw, port=port, pool_size=settings["workers"])
        except Exception as ex:
            msg = str(ex)
            if "AUTHENTICATIONFAILED" in msg.upper() or "Invalid credentials" in msg:
                msg += " - for Gmail/Yahoo/Outlook use an App Password, not your normal password."
            return jsonify(error=f"Login failed: {msg}"), 400
        with lock:
            if state["engine"]:
                state["engine"].close()
            state["engine"] = SearchEngine(src, workers=settings["workers"])
            state["account"], state["demo"] = user, False
        return status()

    @app.post("/api/disconnect")
    def disconnect():
        with lock:
            if state["engine"]:
                state["engine"].close()
            state["engine"], state["account"] = None, ""
        return status()

    @app.get("/api/folders")
    def folders():
        e = eng()
        return jsonify(folders=e.source.list_folders(), default=e.source.default_folders())

    @app.post("/api/search")
    def search():
        try:
            req = parse_request(request.get_json())
        except ValueError as ex:
            return jsonify(error=f"Bad input: {ex}"), 400
        if not (req.senders or req.keywords or req.date_from or req.date_to):
            return jsonify(error="Give at least a sender, a keyword or a date range"), 400
        try:
            return jsonify(eng().search(req))
        except Exception as ex:
            return jsonify(error=f"Search failed: {ex}"), 502

    @app.get("/api/preview/<att_id>")
    def preview(att_id):
        e = eng()
        try:
            a = e.get(att_id)
            data = e.fetch_bytes(att_id)
        except KeyError as ex:
            return jsonify(error=str(ex)), 404
        mime = a.mime.lower()
        if mime == "application/octet-stream" and a.ext == "pdf":
            mime = "application/pdf"
        headers = {"X-Content-Type-Options": "nosniff", "Cache-Control": "private, max-age=600"}
        if mime in SAFE_INLINE:
            headers["Content-Disposition"] = "inline"
            return Response(data, mimetype=mime, headers=headers)
        if mime.startswith(TEXTUAL) or a.ext in ("txt", "csv", "log", "json", "xml", "eml", "html", "htm"):
            # Shown as plain text (never rendered) so attachments can't run scripts here.
            headers["Content-Security-Policy"] = "sandbox"
            return Response(data[:400_000], mimetype="text/plain; charset=utf-8", headers=headers)
        return jsonify(error="No in-browser preview for this file type"), 415

    @app.get("/api/pdftext/<att_id>")
    def pdftext(att_id):
        e = eng()
        try:
            return jsonify(text=pdf_text(e.fetch_bytes(att_id), max_pages=20)[:100_000])
        except KeyError as ex:
            return jsonify(error=str(ex)), 404

    @app.post("/api/download")
    def download():
        j = request.get_json()
        ids = _list(j.get("ids"))
        if not ids:
            return jsonify(error="Nothing selected"), 400
        try:
            rep = save_all(eng(), ids, j.get("dest") or settings["download_dir"],
                           j.get("folder_template", "sender/year-month"), j.get("name_template", "original"))
        except OSError as ex:
            return jsonify(error=f"Cannot write to folder: {ex}"), 400
        return jsonify(rep)

    @app.post("/api/zip")
    def make_zip():
        j = request.get_json()
        ids = _list(j.get("ids"))
        if not ids:
            return jsonify(error="Nothing selected"), 400
        data, rep = build_zip(eng(), ids, j.get("folder_template", "sender/year-month"),
                              j.get("name_template", "original"))
        name = f"mail-attachments-{datetime.now():%Y%m%d-%H%M}.zip"
        return Response(data, mimetype="application/zip",
                        headers={"Content-Disposition": f'attachment; filename="{name}"',
                                 "X-Zip-Report": f"{rep['added']} added, {rep['skipped']} duplicate, "
                                                 f"{rep['failed']} failed"})

    return app
