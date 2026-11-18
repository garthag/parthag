"""Desktop mode: serving the built UI, password change and launcher helpers."""

from __future__ import annotations

from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.main import mount_frontend
from desktop import launcher


def test_spa_is_served_with_deep_link_fallback(tmp_path: Path) -> None:
    (tmp_path / "assets").mkdir()
    (tmp_path / "index.html").write_text('<div id="root"></div>')
    (tmp_path / "assets" / "app.js").write_text("console.log(1)")
    (tmp_path / "favicon.svg").write_text("<svg/>")
    app = FastAPI()

    @app.get("/api/ping")
    def ping() -> dict:
        return {"ok": True}

    mount_frontend(app, tmp_path)
    client = TestClient(app)
    assert client.get("/").text == '<div id="root"></div>'
    assert client.get("/compare?drawing=7").text == '<div id="root"></div>'  # client-side route
    assert client.get("/assets/app.js").text == "console.log(1)"
    assert client.get("/favicon.svg").text == "<svg/>"
    assert client.get("/api/ping").json() == {"ok": True}
    assert client.get("/api/unknown").status_code == 404
    assert client.get("/../../etc/passwd").text == '<div id="root"></div>'  # no path traversal


def test_change_password(client: TestClient) -> None:
    res = client.post("/api/auth/login", json={"username": "pm", "password": "Manager@12345"})
    assert res.status_code == 200, res.text
    login = res.json()["data"]
    headers = {"Authorization": f"Bearer {login['access_token']}"}
    url = "/api/auth/change-password"
    assert (
        client.post(url, headers=headers, json={"current_password": "wrong", "new_password": "NewPass@123"}).status_code
        == 401
    )
    same = {"current_password": "Manager@12345", "new_password": "Manager@12345"}
    assert client.post(url, headers=headers, json=same).status_code == 422
    assert (
        client.post(
            url, headers=headers, json={"current_password": "Manager@12345", "new_password": "short"}
        ).status_code
        == 422
    )
    ok = client.post(url, headers=headers, json={"current_password": "Manager@12345", "new_password": "NewPass@123"})
    assert ok.status_code == 200
    assert client.post("/api/auth/login", json={"username": "pm", "password": "NewPass@123"}).status_code == 200
    assert client.post("/api/auth/login", json={"username": "pm", "password": "Manager@12345"}).status_code == 401


def test_launcher_helpers(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("DRAWING_READER_HOME", str(tmp_path / "home"))
    assert launcher.default_data_dir() == tmp_path / "home"
    port = launcher.pick_port(0)
    assert isinstance(port, int) and port > 0
    assert launcher.running_instance(tmp_path) is None
    (tmp_path / "server.port").write_text(str(port))
    assert launcher.running_instance(tmp_path) is None  # stale port file: nothing listening
    base = launcher.bundle_dir()
    assert (base / "alembic").is_dir()
    assert launcher.frontend_dir(base).name == "dist"
