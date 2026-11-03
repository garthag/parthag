import zipfile

from mail_agent import config, desktop
from mail_agent.demo import DemoSource
from mail_agent.engine import SearchEngine
from mail_agent.web import create_app


def _client(tmp_path):
    app = create_app({"download_dir": str(tmp_path), "workers": 2, "host": "", "user": "", "password": ""},
                     engine=SearchEngine(DemoSource(), workers=2), demo=True)
    return app.test_client()


def test_selftest_passes(tmp_path):
    out = tmp_path / "self.txt"
    assert desktop.selftest(str(out)) == 0
    assert "SELFTEST OK" in out.read_text()


def test_zip_save_to_path(tmp_path):
    c = _client(tmp_path)
    ids = [r["id"] for r in c.post("/api/search", json={"senders": "mck kutty", "keywords": "invoice",
                                                         "file_types": ["pdf"]}).json["results"]][:3]
    r = c.post("/api/zip", json={"ids": ids, "save_to": str(tmp_path / "sub" / "pack")})
    assert r.status_code == 200 and r.json["added"] == 3
    assert r.json["path"].endswith("pack.zip")
    assert len(zipfile.ZipFile(r.json["path"]).namelist()) == 3


def test_saved_account_roundtrip(tmp_path, monkeypatch):
    store = {}

    class FakeKeyring:
        def set_password(self, s, u, p): store[(s, u)] = p
        def get_password(self, s, u): return store.get((s, u))
        def delete_password(self, s, u): store.pop((s, u))

    monkeypatch.setattr(config, "_keyring", lambda: FakeKeyring())
    assert config.save_account("imap.gmail.com", 993, "me@gmail.com", "app-pw")
    assert config.saved_account()["user"] == "me@gmail.com"
    assert config.saved_password("me@gmail.com") == "app-pw"
    assert "app-pw" not in (config.app_dir() / "account.json").read_text()   # password never on disk
    c = _client(tmp_path)
    assert c.get("/api/status").json["saved_account"] == "me@gmail.com"
    c.post("/api/forget", json={})
    assert config.saved_account() is None and not store
