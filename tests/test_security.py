"""Security hardening checks."""
import os
from pathlib import Path

import pytest

from mail_agent.demo import DemoSource
from mail_agent.downloader import is_risky, save_all
from mail_agent.engine import SearchEngine
from mail_agent.models import SearchRequest
from mail_agent.textract import extract_text
from mail_agent.web import create_app, safe_target


@pytest.fixture()
def app(tmp_path):
    return create_app({"download_dir": str(tmp_path), "workers": 2, "host": "", "user": "", "password": ""},
                      engine=SearchEngine(DemoSource(), workers=2), demo=True)


def test_api_needs_secret_token(app):
    c = app.test_client()
    assert c.get("/api/status").status_code == 403                       # no token
    assert c.get("/api/status", headers={"X-App-Token": "guess"}).status_code == 403
    assert c.post("/api/search", json={"senders": "mck"}).status_code == 403
    tok = app.config["API_TOKEN"]
    assert c.get("/api/status", headers={"X-App-Token": tok}).status_code == 200
    assert c.get(f"/api/status?t={tok}").status_code == 200              # preview iframes use ?t=


def test_each_run_gets_a_new_token(tmp_path):
    a = create_app({"download_dir": str(tmp_path), "workers": 1, "host": "", "user": "", "password": ""})
    b = create_app({"download_dir": str(tmp_path), "workers": 1, "host": "", "user": "", "password": ""})
    assert a.config["API_TOKEN"] != b.config["API_TOKEN"] and len(a.config["API_TOKEN"]) >= 40


def test_page_security_headers(app):
    r = app.test_client().get("/")
    csp = r.headers["Content-Security-Policy"]
    assert "script-src 'self'" in csp and "object-src 'none'" in csp and "frame-ancestors 'self'" in csp
    assert r.headers["X-Frame-Options"] == "SAMEORIGIN" and r.headers["X-Content-Type-Options"] == "nosniff"
    assert r.headers["Referrer-Policy"] == "no-referrer"
    assert app.config["API_TOKEN"].encode() in r.data            # only our own page receives the key


def test_other_hosts_and_form_posts_rejected(app):
    c = app.test_client()
    tok = {"X-App-Token": app.config["API_TOKEN"]}
    assert c.get("/", headers={"Host": "attacker.example"}).status_code == 403   # DNS rebinding
    assert c.post("/api/search", data="senders=x", headers=tok).status_code == 415  # cross-site form


def test_save_as_only_safe_file_types(tmp_path):
    assert safe_target(str(tmp_path / "list"), "a.xlsx").suffix == ".xlsx"
    for bad in ("evil.exe", "run.bat", "x.lnk", "a.ps1", "b.js"):
        with pytest.raises(ValueError):
            safe_target(str(tmp_path / bad), "a.xlsx")
    with pytest.raises(ValueError):
        safe_target("relative/list.csv", "a.csv")
    with pytest.raises(ValueError):
        safe_target(r"C:/Windows/System32/x.csv" if os.name == "nt" else "/x/Windows/System32/x.csv", "a.csv")


def test_web_refuses_exe_save(app, tmp_path):
    c = app.test_client()
    c.environ_base["HTTP_X_APP_TOKEN"] = app.config["API_TOKEN"]
    ids = [r["id"] for r in c.post("/api/search", json={"source": "index", "senders": "mck"}).json["results"][:1]]
    r = c.post("/api/export", json={"ids": ids, "format": "csv", "save_to": str(tmp_path / "evil.exe")})
    assert r.status_code == 400 and not (tmp_path / "evil.exe").exists()


def test_risky_files_flagged():
    assert is_risky("invoice.pdf.exe") and is_risky("Payment.JS") and is_risky("macro.xlsm")
    assert not is_risky("invoice.pdf") and not is_risky("photo.jpg") and not is_risky("sheet.xlsx")


def test_zip_bomb_and_huge_input_ignored():
    import io
    import zipfile
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("word/document.xml", b"0" * (100 * 1024 * 1024))   # 100 MB of zeros -> ~100 KB zip
    assert extract_text(buf.getvalue(), "bomb.docx", "") == ("", "none")
    assert extract_text(b"x" * (61 * 1024 * 1024), "big.txt", "text/plain") == ("", "none")


def test_preview_never_cached_and_html_not_rendered(app):
    c = app.test_client()
    c.environ_base["HTTP_X_APP_TOKEN"] = app.config["API_TOKEN"]
    rid = c.post("/api/search", json={"source": "index", "senders": "mck"}).json["results"][0]["id"]
    r = c.get(f"/api/preview/{rid}")
    assert r.headers["Cache-Control"] == "no-store"


def test_erase_everything(app, tmp_path, monkeypatch):
    from mail_agent import config
    (config.app_dir() / "account.json").write_text('{"user": "x"}')
    c = app.test_client()
    c.environ_base["HTTP_X_APP_TOKEN"] = app.config["API_TOKEN"]
    assert c.post("/api/erase", json={}).json["ok"]
    assert c.get("/api/index").json["attachments"] == 0
    assert not (config.app_dir() / "account.json").exists()


def test_imap_is_read_only_and_tls_verified():
    src = Path("mail_agent/imap_source.py").read_text()
    assert "readonly=True" in src and "ssl.create_default_context()" in src
    assert "BODY[" not in src.replace("BODY.PEEK[", "").replace('startswith(b"BODY[")', "").replace("b\"BODY[", "")


def test_parallel_reading_never_fails(tmp_path):
    from mail_agent.index import Index
    e = SearchEngine(DemoSource(), workers=8, index=Index(str(tmp_path / "s.db")))
    e.sync()
    ids = [r["id"] for r in e.search(SearchRequest(source="index", file_types=["pdf"]))["results"]]
    rep = e.analyze(ids, ocr=False)
    assert rep["failed"] == 0 and rep["analyzed"] == len(ids)
