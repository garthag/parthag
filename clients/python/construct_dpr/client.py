"""
CONSTRUCT DPR API client.

Talks to the same Supabase REST API as the web app, signed in as a normal
user account. For unattended systems create a dedicated account and give it
the project role ``integration`` (read/export everything, sync masters, never
approve). Row Level Security applies to every call exactly as in the app.

Never use the Supabase service-role key here: it bypasses all permissions.
"""
from __future__ import annotations

import json
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Iterator, Mapping, Sequence


class ConstructDPRError(Exception):
    """An API error with the HTTP status and the server's message."""

    def __init__(self, status: int, message: str, code: str | None = None):
        super().__init__(f"{status}: {message}")
        self.status = status
        self.message = message
        self.code = code

    @property
    def is_conflict(self) -> bool:
        return self.status == 409 or "CONFLICT" in self.message


class ConstructDPR:
    """Minimal, dependency-free client.

    >>> api = ConstructDPR("https://xyz.supabase.co", "public-anon-key")
    >>> api.sign_in("erp-bot@company.com", "…")
    >>> for p in api.projects(): print(p["code"], p["name"])
    """

    def __init__(self, url: str, anon_key: str, timeout: float = 30.0, retries: int = 3):
        self.url = url.rstrip("/")
        self.anon_key = anon_key
        self.timeout = timeout
        self.retries = retries
        self.access_token: str | None = None
        self.refresh_token: str | None = None
        self.expires_at: float = 0.0

    # ------------------------------------------------------------------ auth
    def sign_in(self, email: str, password: str) -> dict:
        data = self._request("POST", "/auth/v1/token", query={"grant_type": "password"},
                             body={"email": email, "password": password}, auth=False)
        self._store_session(data)
        return data.get("user", {})

    def _refresh(self) -> None:
        if not self.refresh_token:
            raise ConstructDPRError(401, "Session expired and no refresh token; sign in again")
        data = self._request("POST", "/auth/v1/token", query={"grant_type": "refresh_token"},
                             body={"refresh_token": self.refresh_token}, auth=False)
        self._store_session(data)

    def _store_session(self, data: Mapping[str, Any]) -> None:
        self.access_token = data["access_token"]
        self.refresh_token = data.get("refresh_token")
        self.expires_at = time.time() + float(data.get("expires_in", 3600))

    # ------------------------------------------------------------- transport
    def _request(self, method: str, path: str, *, query: Mapping[str, Any] | None = None,
                 body: Any = None, headers: Mapping[str, str] | None = None, auth: bool = True) -> Any:
        if auth and self.access_token and time.time() > self.expires_at - 60:
            self._refresh()
        url = self.url + path
        if query:
            url += "?" + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None}, safe=",.*()")
        h = {"apikey": self.anon_key, "Accept": "application/json"}
        if body is not None:
            h["Content-Type"] = "application/json"
        if auth:
            if not self.access_token:
                raise ConstructDPRError(401, "Call sign_in() first")
            h["Authorization"] = f"Bearer {self.access_token}"
        h.update(headers or {})
        data = json.dumps(body).encode() if body is not None else None

        for attempt in range(self.retries + 1):
            req = urllib.request.Request(url, data=data, method=method, headers=h)
            try:
                with urllib.request.urlopen(req, timeout=self.timeout) as res:
                    raw = res.read()
                    return json.loads(raw) if raw else None
            except urllib.error.HTTPError as e:
                raw = e.read().decode("utf-8", "replace")
                e.close()
                try:
                    info = json.loads(raw)
                except ValueError:
                    info = {"message": raw}
                msg = info.get("message") or info.get("msg") or info.get("error_description") or info.get("error") or raw
                if e.code == 401 and auth and self.refresh_token and attempt == 0 and "JWT" in str(msg):
                    self._refresh()
                    h["Authorization"] = f"Bearer {self.access_token}"
                    continue
                if e.code in (429, 502, 503, 504) and attempt < self.retries:
                    time.sleep(2 ** attempt)
                    continue
                raise ConstructDPRError(e.code, str(msg), info.get("code")) from None
            except urllib.error.URLError as e:
                if attempt < self.retries:
                    time.sleep(2 ** attempt)
                    continue
                raise ConstructDPRError(0, f"Network error: {e.reason}") from None
        raise ConstructDPRError(0, "unreachable")

    # --------------------------------------------------------------- generic
    def select(self, table: str, filters: Mapping[str, str] | None = None, *, columns: str = "*",
               order: str | None = None, limit: int | None = None, offset: int | None = None) -> list[dict]:
        """PostgREST read. ``filters`` use PostgREST syntax, e.g. {"status": "eq.approved"}."""
        q: dict[str, Any] = {"select": columns, **(filters or {})}
        if order:
            q["order"] = order
        if limit is not None:
            q["limit"] = limit
        if offset is not None:
            q["offset"] = offset
        return self._request("GET", f"/rest/v1/{table}", query=q)

    def select_all(self, table: str, filters: Mapping[str, str] | None = None, *, columns: str = "*",
                   order: str = "id", page: int = 1000) -> Iterator[dict]:
        """Iterates over every row, page by page."""
        offset = 0
        while True:
            rows = self.select(table, filters, columns=columns, order=order, limit=page, offset=offset)
            yield from rows
            if len(rows) < page:
                return
            offset += page

    def upsert(self, table: str, rows: Sequence[Mapping[str, Any]], on_conflict: str) -> list[dict]:
        return self._request("POST", f"/rest/v1/{table}", query={"on_conflict": on_conflict}, body=list(rows),
                             headers={"Prefer": "resolution=merge-duplicates,return=representation"})

    def rpc(self, function: str, **args: Any) -> Any:
        return self._request("POST", f"/rest/v1/rpc/{function}", body=args)

    # ------------------------------------------------------------ convenience
    def projects(self) -> list[dict]:
        return self.select("projects", order="code")

    def project_by_code(self, code: str) -> dict:
        rows = self.select("projects", {"code": f"eq.{code}"})
        if not rows:
            raise ConstructDPRError(404, f"Project {code} not found or not accessible")
        return rows[0]

    def activities(self, project_id: str) -> list[dict]:
        return self.select("activities", {"project_id": f"eq.{project_id}"}, order="code")

    def activity_progress(self, project_id: str) -> list[dict]:
        return self.select("activity_progress", {"project_id": f"eq.{project_id}"}, order="code")

    def boq_status(self, project_id: str) -> list[dict]:
        return self.select("boq_status", {"project_id": f"eq.{project_id}"}, order="item_no")

    def upsert_activities(self, project_id: str, rows: Sequence[Mapping[str, Any]]) -> list[dict]:
        """Create or update activities by code (e.g. from ERP or a P6 export)."""
        return self.upsert("activities", [{**r, "project_id": project_id} for r in rows], on_conflict="project_id,code")

    def dprs(self, project_id: str, *, status: str | None = None, date_from: str | None = None,
             date_to: str | None = None) -> list[dict]:
        f = {"project_id": f"eq.{project_id}"}
        if status:
            f["status"] = f"eq.{status}"
        if date_from and date_to:
            f["and"] = f"(report_date.gte.{date_from},report_date.lte.{date_to})"
        elif date_from:
            f["report_date"] = f"gte.{date_from}"
        elif date_to:
            f["report_date"] = f"lte.{date_to}"
        return self.select("dpr_headers", f, order="report_date.desc,revision.desc")

    def dpr_document(self, dpr_id: str) -> dict:
        """Full report: header, activities, manpower, equipment, materials, registers, history."""
        return self.rpc("dpr_document", p_id=dpr_id)

    def approved_reports(self, project_id: str, since: str | None = None) -> list[dict]:
        """Immutable approved snapshots (``snapshot`` + ``snapshot_sha256``)."""
        f = {"project_id": f"eq.{project_id}"}
        if since:
            f["approved_at"] = f"gte.{since}"
        return self.select("report_revisions", f, order="approved_at")

    def export_project(self, project_id: str) -> dict:
        return self.rpc("export_project", p_project=project_id)

    # ----------------------------------------------------------------- events
    def events(self, after: int = 0, *, project_id: str | None = None, limit: int = 500) -> list[dict]:
        f = {"id": f"gt.{after}"}
        if project_id:
            f["project_id"] = f"eq.{project_id}"
        return self.select("integration_events", f, order="id", limit=limit)

    def follow_events(self, after: int = 0, *, project_id: str | None = None, interval: float = 30.0,
                      stop_when_idle: bool = False) -> Iterator[dict]:
        """Yields events in order, forever (or until idle). Persist ``event['id']`` as your cursor."""
        cursor = after
        while True:
            batch = self.events(cursor, project_id=project_id)
            for ev in batch:
                cursor = ev["id"]
                yield ev
            if len(batch) < 500:
                if stop_when_idle:
                    return
                time.sleep(interval)
