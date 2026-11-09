# REST API (integration with ERP, Power BI, Python, Java …)

Every app uses this same API, so anything the apps can do, other software can do too, subject to the same roles. JSON is camelCase, and enums are text (e.g. `"status": "Approved"`). Errors return `{"kind": "...", "message": "..."}` with HTTP 401, 403, 404, 409 (conflict / stale version) or 422 (rule violation).

For another program, create a dedicated account, add it to the project with the role **Integration (API account)**, and use its token.

## Sign in

```bash
curl -X POST https://your-server/api/identity/login -H "content-type: application/json" \
     -d '{"email":"erp@company.com","password":"…"}'
# → {"tokenType":"Bearer","accessToken":"…","expiresIn":28800,"refreshToken":"…"}
# Refresh before expiry: POST /api/identity/refresh {"refreshToken":"…"}
```

Send `Authorization: Bearer <accessToken>` with every call below.

## Endpoints

| Method & path | What |
|---|---|
| `GET /api/me` | Who am I |
| `GET /api/projects` | My projects with my role |
| `GET /api/projects/{p}` · `PUT` | Project details (incl. holidays) |
| `GET /api/projects/{p}/activities` · `/dependencies` | Activities with CPM results; predecessors |
| `POST /api/projects/{p}/activities/save` | `{upserts, deletes, linksFor, links}` (all-or-nothing, re-schedules) |
| `POST /api/projects/{p}/activities/import` | `{content: <xlsx as base64>}` |
| `POST /api/projects/{p}/schedule/run` | Run CPM (data date = today on site) |
| `POST /api/projects/{p}/baselines` | `{name}` freeze a baseline revision |
| `GET /api/projects/{p}/dashboard` | KPIs and S-curve points |
| `GET /api/projects/{p}/dprs` · `GET …/dprs/{d}` | Report list; one report with all lines |
| `POST /api/projects/{p}/dprs` | `{date, shift}` new report |
| `PUT …/dprs/{d}` · `POST …/submit` | Save / submit (send the whole report with its `version`) |
| `POST …/dprs/{d}/approve` | `{accepted: {activityId: qty}}` |
| `POST …/reject` · `…/revise` | `{reason}` |
| `GET …/dprs/{d}/verify` | `true` when the approved content still matches its SHA-256 snapshot |
| `GET /api/projects/{p}/audit?take=100` | Audit trail |
| `GET /api/projects/{p}/reports/{DprPdf\|DprExcel\|SchedulePdf\|ScheduleExcel\|ActivityTemplate}?dprId=` | File download |
| `GET /api/projects/{p}/members` · `POST {email, role}` · `DELETE /members/{m}` | People (admin portal) |
| WebSocket `/hubs/live` (SignalR) | Invoke `WatchProject(p)` to receive `changed` events (`LiveChange`) |

## Python example (standard library only)

```python
import json, urllib.request
S = "https://your-server"
def call(method, path, body=None, token=None):
    req = urllib.request.Request(S + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                 headers={"content-type": "application/json", **({"authorization": "Bearer " + token} if token else {})})
    with urllib.request.urlopen(req) as r: return json.load(r)

tok = call("POST", "/api/identity/login", {"email": "erp@company.com", "password": "…"})["accessToken"]
p = call("GET", "/api/projects", token=tok)[0]["id"]
for a in call("GET", f"/api/projects/{p}/activities", token=tok):
    print(a["code"], a["acceptedQty"], "/", a["totalQty"], a["unit"])
```

For C#, reference `ConstructDpr.Client` and use `RemoteApi` directly (typed). Excel and Power BI can read the JSON endpoints with a bearer token (Power Query: *Web → Advanced → Headers*).
