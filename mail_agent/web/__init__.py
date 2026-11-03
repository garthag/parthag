"""Local web UI (Flask). Binds to 127.0.0.1 only."""

from __future__ import annotations

import hmac
import logging
import secrets
import threading
import time
import uuid
from datetime import date, datetime
from pathlib import Path

from flask import Flask, Response, abort, jsonify, render_template, request

from .. import config, exporter, share, textract
from ..autodetect import detect
from ..downloader import FOLDER_TEMPLATES, NAME_TEMPLATES, build_zip, save_all
from ..engine import Cancelled, SearchEngine
from ..index import Index, db_name
from ..models import SearchRequest
from ..smartfields import DOC_LABELS

SAFE_INLINE = {"application/pdf", "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"}
TEXTUAL = ("text/", "application/json", "application/xml", "message/rfc822")
ALLOWED_HOSTS = {"127.0.0.1", "localhost", "[::1]"}
# The app may only ever write these file types through "Save as" (never .exe/.bat/.lnk ...).
SAVE_EXTS = {".zip", ".xlsx", ".csv", ".json", ".txt", ".eml"}
CSP = ("default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
       "frame-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; "
       "frame-ancestors 'self'")


def safe_target(save_to: str, default_name: str) -> Path:
    """Validate a path chosen in the Save-as dialog: absolute, allowed file type, not a system folder."""
    target = Path(str(save_to)).expanduser()
    if not target.suffix and "." in default_name:
        target = target.with_suffix("." + default_name.rsplit(".", 1)[-1])
    if not target.is_absolute():
        raise ValueError("Choose a full folder path")
    if target.suffix.lower() not in SAVE_EXTS:
        raise ValueError(f"For safety the app only saves {', '.join(sorted(SAVE_EXTS))} files here")
    low = str(target.resolve()).lower().replace("\\", "/")
    if any(part in low for part in ("/windows/", "/program files", "/programdata/", "\\startup",
                                    "/start menu/programs/startup", "/system32")):
        raise ValueError("For safety the app does not write into system or start-up folders")
    return target
EXPORTS = {
    "xlsx": ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
    "csv": ("text/csv", ".csv"),
    "json": ("application/json", ".json"),
    "txt": ("text/plain; charset=utf-8", ".txt"),
}


def _list(v) -> list[str]:
    if isinstance(v, list):
        items = v
    else:
        items = str(v or "").replace(";", ",").replace("\n", ",").split(",")
    return [s.strip() for s in items if s and str(s).strip()]


def _date(v) -> date | None:
    if not v:
        return None
    return datetime.strptime(str(v)[:10], "%Y-%m-%d").date()


def _num(v) -> float | None:
    if v in (None, ""):
        return None
    return float(str(v).replace(",", ""))


def parse_request(j: dict) -> SearchRequest:
    mb = 1024 * 1024
    return SearchRequest(
        senders=_list(j.get("senders")), keywords=_list(j.get("keywords")),
        exclude_keywords=_list(j.get("exclude")), recipients=_list(j.get("recipients")),
        date_from=_date(j.get("date_from")), date_to=_date(j.get("date_to")),
        file_types=_list(j.get("file_types")), folders=_list(j.get("folders")),
        match_pdf_text=bool(j.get("match_pdf_text")), hide_duplicates=j.get("hide_duplicates", True) is not False,
        include_inline=bool(j.get("include_inline")), min_score=float(j.get("min_score") or 0),
        window_months=int(j.get("window_months") or 3),
        match_mode=j.get("match_mode") if j.get("match_mode") in ("any", "all", "phrase") else "any",
        fuzzy=j.get("fuzzy", True) is not False, filename_pattern=str(j.get("filename_pattern") or ""),
        size_min=int((_num(j.get("size_min_mb")) or 0) * mb), size_max=int((_num(j.get("size_max_mb")) or 0) * mb),
        amount_min=_num(j.get("amount_min")), amount_max=_num(j.get("amount_max")),
        doc_types=_list(j.get("doc_types")),
        text_state=j.get("text_state") if j.get("text_state") in ("any", "has_text", "scanned", "not_analyzed") else "any",
        source="index" if j.get("source") == "index" else "live",
    )


APP_PASSWORD_HOSTS = ("imap.gmail.com", "imap.mail.yahoo.com", "imap.mail.me.com")
OWN_PASSWORD_HOSTS = ("imap.titan.email", "imap.secureserver.net", "imap.hostinger.com")


def clean_login(host: str, user: str, pw: str) -> tuple[str, str]:
    """Fix the most common copy/paste mistakes before logging in."""
    user = (user or "").strip().strip("<>").strip()
    pw = (pw or "").strip()
    if host in APP_PASSWORD_HOSTS:
        # Google/Yahoo/Apple app passwords are shown as "abcd efgh ijkl mnop" but contain no spaces
        pw = "".join(pw.split())
    return user, pw


def login_help(host: str, user: str, ex: Exception) -> str:
    raw = ex.args[0] if getattr(ex, "args", None) else ex
    msg = raw.decode("utf-8", "replace") if isinstance(raw, bytes) else str(raw)
    low = msg.lower()
    if "authenticationfailed" in low or "invalid credentials" in low or "login failed" in low:
        if host == "imap.gmail.com":
            tips = ["Use a 16-letter App Password, NOT your normal password.",
                    "Create one at myaccount.google.com/apppasswords (2-Step Verification must be ON first; "
                    "if that page says 'not available', turn on 2-Step Verification and try again).",
                    f"Check the email is exactly right: {user}",
                    "Company (Google Workspace) accounts: the admin must allow IMAP and App Passwords "
                    "(Google Admin -> Apps -> Gmail -> End User Access, and Security -> 2-Step Verification)."]
        elif host == "outlook.office365.com":
            tips = ["Use an App Password from account.microsoft.com/security (2-step verification must be ON).",
                    "Work/school Microsoft 365 accounts often have IMAP basic login disabled by the admin."]
        elif host == "imap.mail.yahoo.com":
            tips = ["Use an App Password: Yahoo Account security -> Generate app password."]
        elif host in OWN_PASSWORD_HOSTS or host.startswith(("mail.", "imap.")) and "zoho" not in host:
            tips = ["Use the same password you use for webmail for this address.",
                    f"Check the email is exactly right: {user}",
                    "If you changed the password recently, sign in to webmail once, then try again."]
        elif "zoho" in host:
            tips = ["Enable IMAP: Zoho Mail -> Settings -> Mail Accounts -> IMAP Access.",
                    "If you use 2-factor login, create an App-specific password (accounts.zoho.in -> Security)."]
        else:
            tips = ["Check email and password. Many providers need an app-specific password for IMAP."]
        return "Login refused by the mail server.\n• " + "\n• ".join(tips)
    if "imap" in low and ("disabled" in low or "not enabled" in low):
        return "IMAP is turned off for this mailbox. Gmail: Settings -> See all settings -> Forwarding and POP/IMAP -> Enable IMAP."
    if "web browser" in low or "webloginrequired" in low:
        return "Google wants you to confirm this sign-in: open Gmail in your browser, approve the security alert, then try again."
    if any(k in low for k in ("getaddrinfo", "timed out", "connection refused", "network", "unreachable")):
        return f"Cannot reach {host}. Check your internet connection / firewall and try again. ({msg})"
    return f"Login failed: {msg}"


class Jobs:
    """Tiny background job runner (sync, analyze) with progress polling and cancel."""

    def __init__(self):
        self.jobs: dict[str, dict] = {}
        self.lock = threading.Lock()

    def start(self, kind: str, fn) -> dict:
        with self.lock:
            for j in self.jobs.values():
                if j["kind"] == kind and j["state"] == "running":
                    return j                      # one sync / analyze at a time
            jid = uuid.uuid4().hex[:10]
            job = {"id": jid, "kind": kind, "state": "running", "progress": {}, "result": None, "error": None,
                   "started": time.time(), "_cancel": threading.Event()}
            self.jobs[jid] = job

        def progress(**kw):
            job["progress"] = kw

        def run():
            try:
                job["result"] = fn(progress, job["_cancel"])
                job["state"] = "cancelled" if job["_cancel"].is_set() else "done"
            except Cancelled:
                job["state"] = "cancelled"
            except Exception as ex:          # shown in the UI
                job["state"], job["error"] = "error", str(ex)
        threading.Thread(target=run, daemon=True).start()
        return job

    def public(self, job: dict) -> dict:
        return {k: v for k, v in job.items() if not k.startswith("_")}

    def get(self, jid: str) -> dict | None:
        return self.jobs.get(jid)

    def running(self) -> list[dict]:
        return [self.public(j) for j in self.jobs.values() if j["state"] == "running"]


def create_app(settings: dict, engine: SearchEngine | None = None, demo: bool = False) -> Flask:
    app = Flask(__name__)
    token = secrets.token_urlsafe(32)          # per-run secret: only our own window/page can use the API
    app.config["API_TOKEN"] = token
    logging.getLogger("werkzeug").setLevel(logging.WARNING)   # no request URLs in logs
    state = {"engine": engine, "account": "Demo mailbox" if demo else "", "demo": demo, "host": ""}
    lock = threading.Lock()
    jobs = Jobs()
    if demo and engine is not None and not engine.index.stats()["attachments"]:
        engine.sync()                      # demo mailbox is tiny - index it right away

    @app.before_request
    def _guard():
        # Block DNS-rebinding and cross-site POSTs: only local host names, JSON bodies.
        host = (request.host or "").rsplit(":", 1)[0].lower()
        if host not in ALLOWED_HOSTS:
            abort(403)
        if request.method == "POST" and not request.is_json:
            abort(415)
        if request.path.startswith("/api/"):
            sent = request.headers.get("X-App-Token") or request.args.get("t") or ""
            if not hmac.compare_digest(sent, token):
                abort(403)

    @app.after_request
    def _headers(resp):
        resp.headers["X-Content-Type-Options"] = "nosniff"
        resp.headers["X-Frame-Options"] = "SAMEORIGIN"
        resp.headers["Referrer-Policy"] = "no-referrer"
        resp.headers["Cross-Origin-Resource-Policy"] = "same-origin"
        resp.headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()"
        if request.path == "/":
            resp.headers["Content-Security-Policy"] = CSP
        if request.path.startswith("/api/"):
            resp.headers.setdefault("Cache-Control", "no-store")
        return resp

    def eng() -> SearchEngine:
        if state["engine"] is None:
            abort(409, "Not connected")
        return state["engine"]

    @app.errorhandler(409)
    @app.errorhandler(400)
    def _err(e):
        return jsonify(error=getattr(e, "description", str(e))), e.code

    def _file(data: bytes, mime: str, name: str, save_to: str | None = None):
        """Send a file to the browser, or (desktop app) write it where the user chose."""
        if save_to:
            try:
                target = safe_target(save_to, name)
            except ValueError as ex:
                return jsonify(error=str(ex)), 400
            try:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            except OSError as ex:
                return jsonify(error=f"Cannot write file: {ex}"), 400
            return jsonify(path=str(target), bytes=len(data))
        return Response(data, mimetype=mime, headers={"Content-Disposition": f'attachment; filename="{name}"'})

    def _stamp() -> str:
        return datetime.now().strftime("%Y%m%d-%H%M")

    # ------------------------------------------------------------- basics
    @app.get("/")
    def index():
        return render_template("index.html", folder_templates=FOLDER_TEMPLATES, name_templates=NAME_TEMPLATES,
                               doc_labels=DOC_LABELS, api_token=token)

    @app.get("/api/status")
    def status():
        e = state["engine"]
        saved = config.saved_account()
        return jsonify(connected=e is not None, account=state["account"], demo=state["demo"],
                       gmail=bool(e and getattr(e.source, "is_gmail", False)),
                       download_dir=settings["download_dir"],
                       env_login=bool(settings.get("host") and settings.get("user") and settings.get("password")),
                       saved_account=saved["user"] if saved else "", can_remember=config.can_remember(),
                       ocr_engine=textract.ocr_engine(),
                       index=e.index.stats() if e else None, jobs=jobs.running())

    @app.post("/api/forget")
    def forget():
        config.forget_account()
        return status()

    @app.post("/api/connect")
    def connect():
        from ..imap_source import PROVIDERS, IMAPSource
        j = request.get_json()
        if j.get("use_env"):
            host, port, user, pw = settings["host"], settings["port"], settings["user"], settings["password"]
        elif j.get("use_saved"):
            acc = config.saved_account() or {}
            host, port, user = acc.get("host", ""), int(acc.get("port") or 993), acc.get("user", "")
            pw = config.saved_password(user) if user else None
            if not pw:
                return jsonify(error="Saved login not found - please sign in again"), 400
        else:
            prov = j.get("provider") or "auto"
            if prov == "auto":
                d = detect(j.get("user", ""))
                if not d["found"]:
                    return jsonify(error=d["reason"] + "\nChoose 'Other (IMAP server)' and type the server name."), 400
                host, port = d["host"], d["port"]
            else:
                host, port = PROVIDERS.get(prov, (j.get("host", "").strip(), int(j.get("port") or 993)))
            user, pw = j.get("user", ""), j.get("password", "")
        user, pw = clean_login(host, user, pw)
        if not (host and user and pw):
            return jsonify(error="Host, email and password are required"), 400
        try:
            src = IMAPSource(host, user, pw, port=port, pool_size=settings["workers"])
        except Exception as ex:
            return jsonify(error=login_help(host, user, ex)), 400
        idx_dir = config.app_dir() / "index"
        idx_dir.mkdir(exist_ok=True)
        with lock:
            if state["engine"]:
                state["engine"].close()
            state["engine"] = SearchEngine(src, workers=settings["workers"],
                                           index=Index(str(idx_dir / db_name(f"{user}@{host}"))))
            state["account"], state["demo"], state["host"] = user, False, host
        if j.get("remember") and not j.get("use_saved"):
            config.save_account(host, port, user, pw)
        return status()

    @app.post("/api/disconnect")
    def disconnect():
        with lock:
            if state["engine"]:
                state["engine"].close()
            state["engine"], state["account"] = None, ""
        return status()

    @app.get("/api/detect")
    def detect_provider():
        return jsonify(detect(request.args.get("email", "")))

    @app.get("/api/folders")
    def folders():
        e = eng()
        return jsonify(folders=e.source.list_folders(), default=e.source.default_folders())

    # -------------------------------------------------------------- search
    @app.post("/api/search")
    def search():
        try:
            req = parse_request(request.get_json())
        except ValueError as ex:
            return jsonify(error=f"Bad input: {ex}"), 400
        has_filter = (req.senders or req.keywords or req.date_from or req.date_to or req.recipients
                      or req.filename_pattern or req.doc_types or req.amount_min is not None
                      or req.amount_max is not None)
        if req.source == "live" and not has_filter:
            return jsonify(error="Give at least a sender, a keyword or a date range"), 400
        try:
            return jsonify(eng().search(req))
        except Exception as ex:
            return jsonify(error=f"Search failed: {ex}"), 502

    # --------------------------------------------------------------- index
    @app.get("/api/index")
    def index_stats():
        return jsonify(eng().index.stats())

    @app.post("/api/index/sync")
    def index_sync():
        e, j = eng(), request.get_json()
        flds, since, full = _list(j.get("folders")) or None, _date(j.get("since")), bool(j.get("full"))
        job = jobs.start("sync", lambda progress, cancel: e.sync(flds, since, progress, cancel, full=full))
        return jsonify(jobs.public(job))

    @app.post("/api/index/clear")
    def index_clear():
        eng().index.clear()
        return jsonify(eng().index.stats())

    @app.post("/api/erase")
    def erase_everything():
        """Privacy: remove everything this app stored on the PC (mail itself is untouched)."""
        e = state["engine"]
        if e is not None:
            e.index.wipe()
            e.cache.clear()
            e.registry.clear()
        config.forget_account()
        removed = config.erase_local_data()
        return jsonify(ok=True, removed=removed)

    @app.post("/api/analyze")
    def analyze():
        e, j = eng(), request.get_json()
        ocr, ids = j.get("ocr", True) is not False, _list(j.get("ids"))
        if j.get("all"):
            job = jobs.start("analyze", lambda progress, cancel: e.analyze_all(ocr, progress, cancel))
        else:
            if not ids:
                return jsonify(error="Nothing selected"), 400
            job = jobs.start("analyze", lambda progress, cancel: e.analyze(ids, ocr, progress, cancel,
                                                                            force=bool(j.get("force"))))
        return jsonify(jobs.public(job))

    @app.get("/api/jobs/<jid>")
    def job(jid):
        j = jobs.get(jid)
        return (jsonify(jobs.public(j)) if j else (jsonify(error="no such job"), 404))

    @app.post("/api/jobs/<jid>/cancel")
    def job_cancel(jid):
        j = jobs.get(jid)
        if j:
            j["_cancel"].set()
        return jsonify(ok=bool(j))

    # ----------------------------------------------------- preview / reader
    @app.get("/api/preview/<att_id>")
    def preview(att_id):
        e = eng()
        try:
            a = e.get(att_id)
            data = e.fetch_bytes(att_id)
        except KeyError as ex:
            return jsonify(error=str(ex)), 404
        except Exception as ex:
            return jsonify(error=f"Could not fetch from mail server: {ex}"), 502
        mime = a.mime.lower()
        if mime == "application/octet-stream" and a.ext == "pdf":
            mime = "application/pdf"
        headers = {"X-Content-Type-Options": "nosniff", "Cache-Control": "no-store"}   # never cached on disk
        if mime in SAFE_INLINE:
            headers["Content-Disposition"] = "inline"
            return Response(data, mimetype=mime, headers=headers)
        if mime.startswith(TEXTUAL) or a.ext in ("txt", "csv", "log", "json", "xml", "eml", "html", "htm"):
            # Shown as plain text (never rendered) so attachments can't run scripts here.
            headers["Content-Security-Policy"] = "sandbox"
            return Response(data[:400_000], mimetype="text/plain; charset=utf-8", headers=headers)
        return jsonify(error="No in-browser preview for this file type"), 415

    @app.get("/api/doc/<att_id>")
    def document(att_id):
        try:
            return jsonify(eng().document(att_id, ocr=request.args.get("ocr", "1") != "0"))
        except KeyError as ex:
            return jsonify(error=str(ex)), 404
        except Exception as ex:
            return jsonify(error=f"Could not read file: {ex}"), 502

    @app.post("/api/doc/<att_id>/reread")
    def reread(att_id):
        e = eng()
        try:
            e.analyze([att_id], ocr=True, force=True)
            return jsonify(e.document(att_id))
        except KeyError as ex:
            return jsonify(error=str(ex)), 404

    @app.post("/api/fields")
    def set_field():
        j = request.get_json()
        try:
            return jsonify(fields=eng().set_field(str(j.get("id")), str(j.get("field")), j.get("value")))
        except (KeyError, ValueError) as ex:
            return jsonify(error=str(ex)), 400

    @app.post("/api/tables/<att_id>")
    def tables_export(att_id):
        j = request.get_json()
        e = eng()
        try:
            doc = e.document(att_id)
            a = e.get(att_id)
        except KeyError as ex:
            return jsonify(error=str(ex)), 404
        tables = doc["tables"]
        if j.get("n") is not None:
            tables = tables[int(j["n"]):int(j["n"]) + 1]
        stem = a.filename.rsplit(".", 1)[0]
        if j.get("format") == "xlsx":
            return _file(exporter.tables_xlsx(tables), EXPORTS["xlsx"][0], f"{stem}-tables.xlsx", j.get("save_to"))
        return _file(exporter.tables_csv(tables), "text/csv", f"{stem}-tables.csv", j.get("save_to"))

    # ------------------------------------------------- download / export
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
        if j.get("save_to"):
            try:
                target = safe_target(str(j["save_to"]), "x.zip")
                if target.suffix.lower() != ".zip":
                    target = target.with_suffix(".zip")
            except ValueError as ex:
                return jsonify(error=str(ex)), 400
            try:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            except OSError as ex:
                return jsonify(error=f"Cannot write ZIP: {ex}"), 400
            return jsonify(path=str(target), **rep)
        name = f"mail-attachments-{_stamp()}.zip"
        return Response(data, mimetype="application/zip",
                        headers={"Content-Disposition": f'attachment; filename="{name}"',
                                 "X-Zip-Report": f"{rep['added']} added, {rep['skipped']} duplicate, "
                                                 f"{rep['failed']} failed"})

    @app.post("/api/export")
    def export():
        j = request.get_json()
        fmt = j.get("format", "xlsx")
        if fmt not in EXPORTS:
            return jsonify(error="Unknown format"), 400
        e = eng()
        ids = _list(j.get("ids"))
        if not ids:
            return jsonify(error="Nothing selected"), 400
        atts = [e.get(i) for i in ids]
        if fmt == "txt":
            missing = [a for a in atts if not a.text_source]
            if missing:
                e.analyze(missing, ocr=j.get("ocr", True) is not False)
            data = exporter.text_bundle(atts)
        elif fmt == "xlsx":
            data = exporter.to_xlsx(atts)
        elif fmt == "csv":
            data = exporter.to_csv(atts)
        else:
            data = exporter.to_json(atts)
        mime, ext = EXPORTS[fmt]
        return _file(data, mime, f"attachments-{_stamp()}{ext}", j.get("save_to"))

    # ---------------------------------------------------------------- share
    def _files(ids):
        files, failed = [], []
        for a, data in eng().fetch_many(ids):
            if isinstance(data, Exception):
                failed.append(a.filename)
            else:
                files.append((a.filename, data, a.mime))
        return files, failed

    @app.post("/api/share/text")
    def share_text():
        e = eng()
        return jsonify(text=exporter.share_text([e.get(i) for i in _list(request.get_json().get("ids"))]))

    @app.post("/api/share/eml")
    def share_eml():
        j = request.get_json()
        ids = _list(j.get("ids"))
        if not ids:
            return jsonify(error="Nothing selected"), 400
        files, _ = _files(ids)
        data = exporter.eml_draft(files, j.get("to", ""), j.get("subject", ""), j.get("message", ""),
                                  "" if state["demo"] else state["account"])
        return _file(data, "message/rfc822", f"draft-{_stamp()}.eml", j.get("save_to"))

    @app.post("/api/share/email")
    def share_email():
        j = request.get_json()
        ids, to = _list(j.get("ids")), share.parse_recipients(str(j.get("to", "")))
        if not ids:
            return jsonify(error="Nothing selected"), 400
        if not to:
            return jsonify(error="Enter at least one valid email address"), 400
        files, failed = _files(ids)
        if state["demo"]:
            return jsonify(sent_to=to, files=len(files), demo=True,
                           note="Demo mailbox - nothing was really sent.")
        src = eng().source
        try:
            rep = share.send(src.host, src.user, src.password, to, j.get("subject", ""), j.get("message", ""), files)
        except ValueError as ex:
            return jsonify(error=str(ex)), 400
        except Exception as ex:
            return jsonify(error=f"Sending failed: {ex}"), 502
        rep["failed"] = failed
        return jsonify(rep)

    return app
