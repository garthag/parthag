"""Authentication, authorisation, projects CRUD, pagination/sorting and error envelopes."""

from __future__ import annotations

from fastapi.testclient import TestClient


def test_login_failures(client: TestClient) -> None:
    res = client.post("/api/auth/login", json={"username": "admin", "password": "wrong-password"})
    assert res.status_code == 401
    body = res.json()
    assert body["success"] is False and body["error"]["code"] == "unauthorized"
    assert res.headers["www-authenticate"] == "Bearer"
    res = client.post("/api/auth/login", json={"username": "a"})  # password missing
    assert res.status_code == 422 and res.json()["error"]["code"] == "validation_error"


def test_login_by_email_me_refresh_logout(client: TestClient) -> None:
    res = client.post("/api/auth/login", json={"username": "doccon@drawingreader.local", "password": "Doccon@12345"})
    assert res.status_code == 200
    tokens = res.json()["data"]
    headers = {"Authorization": f"Bearer {tokens['access_token']}"}
    me = client.get("/api/auth/me", headers=headers).json()["data"]
    assert me["username"] == "doccon" and "drawings.upload" in me["permissions"]

    refreshed = client.post("/api/auth/refresh", json={"refresh_token": tokens["refresh_token"]})
    assert refreshed.status_code == 200
    # refresh tokens are single use
    assert client.post("/api/auth/refresh", json={"refresh_token": tokens["refresh_token"]}).status_code == 401
    # access token can't be used as refresh token
    assert client.post("/api/auth/refresh", json={"refresh_token": tokens["access_token"]}).status_code == 401

    new = refreshed.json()["data"]
    new_headers = {"Authorization": f"Bearer {new['access_token']}"}
    out = client.post("/api/auth/logout", headers=new_headers, json={"refresh_token": new["refresh_token"]})
    assert out.status_code == 200
    assert client.get("/api/auth/me", headers=new_headers).status_code == 401
    assert client.post("/api/auth/refresh", json={"refresh_token": new["refresh_token"]}).status_code == 401


def test_requires_auth_and_permissions(client: TestClient, viewer: dict) -> None:
    assert client.get("/api/projects").status_code == 401
    assert client.get("/api/projects", headers={"Authorization": "Bearer nope"}).status_code == 401
    assert client.get("/api/projects", headers=viewer).status_code == 200
    res = client.post("/api/projects", headers=viewer, json={"code": "VIEW", "name": "Nope"})
    assert res.status_code == 403 and res.json()["error"]["code"] == "forbidden"


def test_create_user(client: TestClient, auth: dict, viewer: dict) -> None:
    body = {
        "email": "planner@example.com",
        "username": "planner",
        "full_name": "Plan Ner",
        "password": "Planner@123",
        "roles": ["Planning"],
    }
    res = client.post("/api/auth/users", headers=auth, json=body)
    assert res.status_code == 201 and res.json()["data"]["roles"][0]["name"] == "Planning"
    assert client.post("/api/auth/users", headers=auth, json=body).status_code == 409
    assert client.post("/api/auth/users", headers=viewer, json=body).status_code == 403


def test_project_crud(client: TestClient, auth: dict) -> None:
    res = client.post(
        "/api/projects",
        headers=auth,
        json={"code": "crud-1", "name": "CRUD Project", "start_date": "2026-01-01", "end_date": "2026-12-31"},
    )
    assert res.status_code == 201
    pid = res.json()["data"]["id"]
    assert res.json()["data"]["code"] == "CRUD-1"
    assert client.post("/api/projects", headers=auth, json={"code": "CRUD-1", "name": "Dup"}).status_code == 409
    bad = client.post(
        "/api/projects",
        headers=auth,
        json={"code": "BAD", "name": "Bad", "start_date": "2026-02-01", "end_date": "2026-01-01"},
    )
    assert bad.status_code == 422
    upd = client.put(f"/api/projects/{pid}", headers=auth, json={"name": "Renamed", "status": "on_hold"})
    assert upd.status_code == 200 and upd.json()["data"]["name"] == "Renamed"
    assert client.put(f"/api/projects/{pid}", headers=auth, json={"end_date": "2025-01-01"}).status_code == 422
    assert client.get(f"/api/projects/{pid}", headers=auth).json()["data"]["status"] == "on_hold"
    assert client.delete(f"/api/projects/{pid}", headers=auth).status_code == 200
    assert client.get(f"/api/projects/{pid}", headers=auth).status_code == 404
    assert client.put("/api/projects/999999", headers=auth, json={"name": "x y"}).status_code == 404


def test_project_list_pagination_sort_filter(client: TestClient, auth: dict) -> None:
    res = client.get("/api/projects?page=1&page_size=2&sort=-code", headers=auth).json()
    assert res["meta"]["page_size"] == 2 and res["meta"]["total"] >= 3
    codes = [p["code"] for p in res["data"]]
    assert codes == sorted(codes, reverse=True)
    assert all(p["drawing_count"] >= 0 for p in res["data"])
    found = client.get("/api/projects?search=refinery", headers=auth).json()["data"]
    assert [p["code"] for p in found] == ["NRP"]
    assert client.get("/api/projects?status=active", headers=auth).status_code == 200
    bad = client.get("/api/projects?sort=password", headers=auth)
    assert bad.status_code == 422 and "allowed" in bad.json()["error"]["details"]
    assert client.get("/api/projects?page=0", headers=auth).status_code == 422


def test_lookups(client: TestClient, auth: dict) -> None:
    disciplines = client.get("/api/disciplines", headers=auth).json()["data"]
    assert {"STR", "PIP", "ELE"} <= {d["code"] for d in disciplines}
    areas = client.get("/api/projects/1/areas", headers=auth).json()["data"]
    assert len(areas) >= 5
    systems = client.get("/api/projects/1/systems", headers=auth).json()["data"]
    assert systems and systems[0]["project_id"] == 1


def test_health_and_openapi(client: TestClient) -> None:
    assert client.get("/health").json() == {"status": "ok"}
    assert client.get("/health/ready").json() == {"status": "ready"}
    spec = client.get("/api/openapi.json").json()
    assert spec["openapi"].startswith("3.1")
    paths = spec["paths"]
    for p in (
        "/api/auth/login",
        "/api/auth/logout",
        "/api/auth/refresh",
        "/api/auth/me",
        "/api/projects",
        "/api/projects/{project_id}",
        "/api/drawings/upload",
        "/api/drawings/folder-upload",
        "/api/drawings",
        "/api/drawings/{drawing_id}",
        "/api/ocr/process",
        "/api/ocr/job/{job_id}",
        "/api/ocr/result/{result_id}",
        "/api/bom",
        "/api/bom/material-summary",
        "/api/bom/weight-summary",
        "/api/revisions",
        "/api/revisions/compare",
        "/api/dashboard/summary",
        "/api/dashboard/charts",
        "/api/analytics/material-analysis",
        "/api/analytics/weight-growth",
        "/api/ai/query",
    ):
        assert p in paths, p
    assert "HTTPBearer" in spec["components"]["securitySchemes"]
    assert "ValidationErrorResponse" in spec["components"]["schemas"]
    assert client.get("/api/docs").status_code == 200
    assert client.get("/api/redoc").status_code == 200
    assert client.get("/nope").json()["error"]["code"] == "http_error"
