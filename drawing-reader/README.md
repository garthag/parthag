# Drawing Reader

Engineering Drawing Register, BOM Intelligence, Revision Analytics, Weight & BOQ Management and an
SQL-backed AI Engineering Assistant for EPC projects. It replaces Excel drawing registers: drop in
scanned or native PDF drawings (single files, nested folders or ZIP archives), and it reads the title
block, revision block, notes and BOM tables, calculates weights, compares revisions and rolls
everything up into BOQs, cost impact and dashboards.

| | |
|---|---|
| **Backend** | Python 3.12 · FastAPI · SQLAlchemy 2 · PostgreSQL 16 · Alembic · Redis · Celery · JWT |
| **OCR** | PyMuPDF (native text) · Tesseract (default) / PaddleOCR (optional) · OpenCV |
| **Frontend** | React 18 · TypeScript · MUI 6 · Zustand · React Query · AG Grid · PDF.js · Recharts |
| **Infra** | Docker · Docker Compose · Nginx · GitHub Actions |

---

## Quick start

### Windows installer (no Docker, no Python)

1. Download `DrawingReaderSetup-1.0.x.exe` from the repository's **Releases** page (releases named
   *Drawing Reader 1.0.x for Windows*) or from the `DrawingReaderSetup-windows` artifact of the
   *Drawing Reader Windows installer* workflow run.
2. Run it. No administrator rights are needed (it installs for the current user; pick "all users"
   in the first dialog if you prefer). Windows SmartScreen may say the publisher is unknown because
   the installer is not code-signed: choose **More info → Run anyway**.
3. Tick **Load sample projects** if you want the demo data (3 projects, 100 drawings, 300 revisions).
4. Drawing Reader starts and opens in your browser at `http://127.0.0.1:8765`. Sign in with
   `admin` / `Admin@12345`, then use **account menu → Change password**.

The installed app is a single local program: the FastAPI server, the web UI and the Tesseract OCR
engine are bundled; data is a SQLite database plus your PDFs in `%LOCALAPPDATA%\DrawingReader`
(kept on uninstall and upgrade). A small "Drawing Reader is running" window offers *Open in browser*
and *Quit*; starting it again while it runs just reopens the browser. It listens on 127.0.0.1 only.
`drawing-reader-cli.exe --self-test` in the install folder checks an installation.

Build it yourself on Windows (Python 3.12, Node 22, `choco install tesseract innosetup`):

```powershell
cd drawing-reader\backend
pip install -r requirements.txt -r requirements-desktop.txt
powershell -ExecutionPolicy Bypass -File desktop\build-windows.ps1 -Version 1.0.0
```

### Docker (single command)

```bash
docker compose -f docker-compose.dev.yml up --build
```

- UI: http://localhost:5173 (hot reload) · API docs: http://localhost:8000/api/docs · ReDoc: http://localhost:8000/api/redoc
- The API container migrates the database and seeds sample data on first start.

### Local (no Docker)

```bash
cd backend
pip install -r requirements-dev.txt          # plus: apt-get install tesseract-ocr
export DATABASE_URL=postgresql+psycopg://drawing:drawing@localhost:5432/drawing_reader
alembic upgrade head
python -m app.seed.seed
uvicorn app.main:app --reload
```

```bash
cd frontend
npm ci
npm run dev                                  # proxies /api to http://localhost:8000
```

`DATABASE_URL=sqlite:///./dev.db` also works for a quick look without PostgreSQL.

### Sample logins

| User | Password | Role |
|---|---|---|
| `admin` | `Admin@12345` | Administrator (all permissions) |
| `doccon` | `Doccon@12345` | Document Controller |
| `structural` | `Struct@12345` | Structural Engineer |
| `piping` | `Piping@12345` | Mechanical Engineer |
| `procurement` | `Procure@12345` | Procurement |
| `pm` | `Manager@12345` | Project Manager |

Change or remove these accounts before any real deployment (`SEED_ON_START=false` in production).

---

## Production deployment

```bash
cp .env.example .env            # set POSTGRES_PASSWORD, SECRET_KEY, CORS_ORIGINS, ...
docker compose -f docker-compose.prod.yml --env-file .env up -d --build
```

The production stack runs:

| Service | Purpose |
|---|---|
| `proxy` | Nginx edge: serves the SPA, proxies `/api`, 1 GB upload limit for folder/ZIP uploads, login rate limiting, security headers |
| `web` | Built SPA served by Nginx (immutable asset caching) |
| `api` | Gunicorn + Uvicorn workers; runs `alembic upgrade head` on start |
| `worker` | Celery OCR worker (queue `ocr`), shares the file-storage volume with `api` |
| `db` | PostgreSQL 16 (volume `pgdata`) |
| `redis` | Celery broker, JWT revocation list and dashboard cache (AOF persistence) |

Every service has a health check and `restart: unless-stopped`. Volumes: `pgdata`, `redisdata`,
`storage` (uploaded PDFs, content-addressed by SHA-256).

**CI/CD** (`.github/workflows/drawing-reader.yml`): backend lint → format → migrations against
PostgreSQL 16 (upgrade, `alembic check` drift detection, downgrade, upgrade) → seed smoke test →
tests with a 90% coverage gate → OpenAPI validation; frontend lint → typecheck → tests with coverage →
build; Docker images built on every run and pushed to GHCR on `main`. A separate workflow
(`.github/workflows/drawing-reader-windows.yml`) builds the Windows installer on `windows-latest`,
self-tests the bundle, silently installs it and self-tests the installed copy, then publishes it on
the Releases page. Deployment runs over SSH when the
`DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_PATH` and `DEPLOY_SSH_KEY` secrets are configured
(`docker compose pull && up -d` with the commit's image tag); without them the job is skipped.

Backups: `docker compose exec db pg_dump -U drawing drawing_reader > backup.sql` plus a copy of the
`storage` volume.

### Configuration

All settings are environment variables (`backend/app/core/config.py`):

| Variable | Default | Notes |
|---|---|---|
| `DATABASE_URL` | postgres on localhost | SQLAlchemy URL |
| `REDIS_URL` | `redis://localhost:6379/0` | Empty disables Redis (process-local fallbacks) |
| `SECRET_KEY` | dev value | **Required** in production, ≥ 32 chars |
| `OCR_EAGER` | `true` | `true`: OCR runs in-process after upload; `false`: queued to Celery |
| `OCR_ENGINE` | `auto` | `auto` (PaddleOCR if installed, else Tesseract), `tesseract`, `paddle`, `none` |
| `OCR_DPI` | `300` | Raster resolution for scanned pages |
| `OCR_CONFIDENCE_THRESHOLD` | `0.9` | Results below this go to the OCR review queue |
| `MAX_UPLOAD_MB` | `200` | Per file |
| `DEFAULT_STEEL_RATE_PER_KG` | `2.5` | Fallback rate when no `material_rates` row matches |
| `ANTHROPIC_API_KEY` | — | Optional LLM fallback for the AI assistant (see below) |
| `CORS_ORIGINS` | localhost | Comma separated |
| `LOG_JSON` | `false` | Structured JSON logs with request ids |

PaddleOCR is optional because of its size: `pip install -r requirements-paddle.txt`; `OCR_ENGINE=auto`
then prefers it.

---

## How it works

### Drawing register (Phase 1)

- `POST /api/drawings/upload` accepts PDFs and ZIP archives; `POST /api/drawings/folder-upload`
  accepts a browser folder selection plus each file's relative path. Files are streamed to disk,
  hashed (duplicates detected per project), and registered in batches.
- Categorisation from drawing numbers and folders: `NRP-ST-A01-GA-0001_RevB.pdf` → discipline STR,
  area A01, type GA, revision B; folders like `02_Piping/Area 03/Isometric` work too.
- Revisions are ordered with a revision-code ranking (`P1 < A < B … < Z < AA < 0 < 1 < 2`, `R2` = `2`);
  the highest becomes current and drives drawing status and weight.
- Search covers drawing number, title, vendor and discipline; filters for project, discipline, area,
  status, type, folder, tag and weight; every list endpoint supports `page`, `page_size`, `sort`.
- Navigation tree: Project → Discipline → Area → Drawing.

### OCR engine (Phase 2) — `backend/app/services/ocr/`

1. **Load** (`pdf_loader.py`): native text with positions from PyMuPDF; pages with no text layer are
   rasterised at 300 DPI, deskewed (OpenCV min-area-rect), orientation-corrected (Tesseract OSD or
   scoring the four rotations) and OCR'd. Rotated native drawings are normalised from the dominant
   text direction. All coordinates are normalised to 0..1, so downstream logic is engine-agnostic.
2. **Regions** (`regions.py`): the title block is the densest cluster of label keywords (DRAWING NO,
   TITLE, REV, SCALE, …), biased towards the bottom-right and snapped to a ruled rectangle when an
   image is available — this handles ISO, ANSI-strip and vendor layouts. The revision table
   (REV/DATE/DESCRIPTION/BY/CHK/APP) is parsed above or below its header; numbered notes follow a
   NOTES heading.
3. **Fields** (`fields.py`): label synonyms (e.g. `DWG NO`, `DRG. NO`, `DOCUMENT NO`), values read to the
   right of the label or from the cell below, validated per field (drawing number pattern, revision
   codes, ISO dates from many formats, `1:50`/`NTS`, status names → IFC/IFA/…). Confidence = OCR word
   confidence × extraction method × validity; independent sources that agree (title block, revision
   table, file name) corroborate each other.
4. Output is stored in `ocr_results` (extracted JSON, per-field and overall confidence, rotation,
   regions, raw text). Results under 90% go to the **OCR Review** screen, where users override fields;
   overrides are audited and written back to the register.

### BOM extraction (Phase 3) — `backend/app/services/bom/`

Header rows are detected from column synonyms (ITEM, PART NO, DESCRIPTION, MATERIAL, GRADE, SPEC, QTY,
UNIT, UNIT WT, TOTAL WT; stacked two-line headers are merged); column boundaries come from header
positions; rows are read down (or up, for tables stacked on a title block) until the TOTAL row.
Wrapped descriptions are merged into the previous row, blank/ditto material-grade cells inherit from
the row above, multi-sheet tables are concatenated. On scanned pages the table region is re-OCR'd in
block mode at 2× (sparse OCR drops short numeric cells). Weights are reconciled (qty × unit weight,
total ÷ qty) or estimated from the description with a section library (IPE/HEA/HEB/UB/UC/W/UPN/PFC,
plates, pipes, angles, round bars, material densities) and checked against the printed total.
Weights roll up to revision, drawing, discipline and project; every BOM save writes `weight_history`
and the BOQ is rebuilt per project with `material_rates`.

### Revision intelligence (Phase 4)

`GET /api/revisions/compare` matches BOM lines by part number, else description+material+grade, then
pairs leftovers by description similarity (≥ 0.85) so re-described items count as modified. It reports
added/removed/modified items, quantity/material/specification/weight/description changes, weight delta,
and cost delta per material/grade (stored in `revision_differences` and `cost_impacts`).
`GET /api/revisions/visual-diff` renders both PDFs and returns changed regions plus detected
**revision clouds** (closed outlines of regular scallops, found after removing straight lines).
The side-by-side viewer syncs zoom and scroll and overlays both. Difference reports export to PDF,
Excel and CSV.

### Analytics, AI assistant and reports (Phase 5)

- Dashboard KPIs and charts are aggregate SQL (cached 30 s, Redis when available).
- `POST /api/ai/query` maps a question to one of a fixed set of intents (compare revisions, list
  drawings, weight increase over N t, drawing that added most of a material, procurement impact,
  weight/drawings by discipline, MTO, pending OCR, total weight) with a deterministic parser and runs
  SQL. The response includes the rows and the SQL. If no rule matches and `ANTHROPIC_API_KEY` is set,
  Claude classifies the question into the same intent schema (structured JSON output, low effort,
  4 s timeout, no retries); the model never writes SQL, so answers always come from the database.
- Reports: drawing register, revision difference, BOQ, MTO, cost impact, weight growth — PDF (ReportLab),
  Excel (openpyxl write-only, streams large registers) and CSV (streamed). They use the same service
  functions as the dashboard, and tests assert the totals match.

---

## API

OpenAPI 3.1: `/api/openapi.json` (Swagger UI at `/api/docs`, ReDoc at `/api/redoc`); a validated copy is
committed at [`docs/openapi.json`](docs/openapi.json) (regenerate with
`python backend/scripts/export_openapi.py`). All endpoints except login/refresh require
`Authorization: Bearer <token>`; writes are permission-checked (roles → permissions). Responses use
`{success, data, meta?}`; errors use `{success: false, error: {code, message, details}, request_id}`.

| Area | Endpoints |
|---|---|
| Auth | `POST /api/auth/login` · `POST /api/auth/logout` · `POST /api/auth/refresh` · `GET /api/auth/me` · `POST /api/auth/users` |
| Projects | `GET/POST /api/projects` · `GET/PUT/DELETE /api/projects/{id}` · `GET /api/disciplines` · `GET /api/projects/{id}/areas`, `/systems` |
| Drawings | `POST /api/drawings/upload` · `POST /api/drawings/folder-upload` · `GET /api/drawings` · `GET /api/drawings/tree` · `GET/PUT/DELETE /api/drawings/{id}` · `POST /api/drawings/{id}/revisions` · `GET /api/drawings/files/{id}/content` |
| OCR | `POST /api/ocr/process` · `GET /api/ocr/job/{id}` · `GET /api/ocr/jobs` · `GET/PUT /api/ocr/result/{id}` · `GET /api/ocr/review-queue` |
| BOM | `GET /api/bom` · `PUT /api/bom/revisions/{id}` · `GET /api/bom/material-summary` · `GET /api/bom/weight-summary` · `GET /api/bom/boq` · `POST /api/bom/boq/rebuild` · `GET/POST /api/bom/rates` |
| Revisions | `GET /api/revisions` · `GET /api/revisions/compare` · `GET /api/revisions/visual-diff` |
| Dashboard | `GET /api/dashboard/summary` · `GET /api/dashboard/charts` |
| Analytics | `GET /api/analytics/material-analysis` · `GET /api/analytics/weight-growth` · `GET /api/analytics/cost-impact` · `GET /api/analytics/procurement-impact` |
| AI | `POST /api/ai/query` |
| Reports | `GET /api/reports/{drawing-register\|revision-difference\|boq\|mto\|cost-impact\|weight-growth}?format=pdf\|xlsx\|csv` |
| Health | `GET /health` · `GET /health/ready` |

---

## Database

29 tables (SQLAlchemy models in `backend/app/models/`, migration `backend/alembic/versions/0001_initial_schema.py`):
projects, disciplines, areas, systems, users, roles, permissions (+ user_roles, role_permissions),
drawings, drawing_files, drawing_revisions, bom_headers, bom_items, revision_differences,
weight_history, boq_summary, material_rates, cost_impacts, drawing_relationships, drawing_tags,
ocr_jobs, ocr_results, document_transmittals (+ transmittal_items), review_comments,
approval_workflows, notifications, audit_logs.

Conventions: `created_at`/`updated_at` on every table; soft delete (`is_deleted`, `deleted_at`) and audit
columns (`created_by_id`, `updated_by_id`) on business tables; unique constraints such as
(project, drawing number), (drawing, revision), (from, to revision); indexes on search and join
columns; JSONB on PostgreSQL. Every create/update/delete/upload/override writes `audit_logs` with
user, IP, user agent and request id.

## Seed data

`python -m app.seed.seed` creates roles/permissions, the users above, 9 disciplines, 14 material rates,
3 projects (refinery, LNG modules, solar substation) with areas and systems, **100 drawings, 300
revisions and ~3,400 BOM lines** with realistic sections, plates, piping, cable and fittings, a PDF for
every revision (with title block, revision table, BOM and revision clouds), revision differences, cost
impacts, weight history and BOQs. `--no-pdfs` skips PDF generation; `--force` re-seeds.

## Testing

```bash
cd backend && pytest --cov=app             # 143 tests, 95% coverage
cd frontend && npm run test:coverage       # 29 tests, 96% line coverage
```

Backend tests cover OCR (native, 90/180/270° rotated, scanned and scanned-rotated via Tesseract,
validators), BOM extraction (multi-page, merged cells, ditto marks, bottom-up tables, scanned
tables, section-weight library), revision comparison (classification, weight and cost deltas),
the full upload → OCR → BOM → compare flow, folder/ZIP uploads, auth/refresh/logout/permissions,
pagination/sorting/validation errors, dashboard and report values cross-checked against SQL, the AI
assistant (including the LLM fallback with a mocked client) and OpenAPI completeness.

## Measured results (this repository's test fixtures)

| Check | Result |
|---|---|
| Title-block OCR, native PDFs (0/90/180/270°) | all 11 fields correct, confidence 0.99 |
| Title-block OCR, scanned 300 DPI (upright and rotated) | all fields correct, confidence 0.95 |
| BOM extraction, native 55-row 2-sheet table | 55/55 rows exact, total weight matches source |
| BOM extraction, scanned | 100% of item/qty/weight/material/grade/spec/unit cells; part codes are the weakest field (O/0 confusions) |
| 1,000-drawing nested-folder ZIP upload with OCR + BOM | 1,000/1,000 registered and processed in ~30 s (SQLite, single process) |
| API latency (100-drawing seed) | dashboard < 100 ms, drawing search ~20 ms, AI queries < 50 ms |

These figures come from generated drawings. Real scans vary (paper quality, stamps, handwriting,
unusual title-block layouts), so check accuracy on a sample of your own drawings; low-confidence
results are routed to the review queue rather than written silently.

## Project layout

```
drawing-reader/
├── backend/
│   ├── app/
│   │   ├── api/            # routers + dependencies (auth, pagination, sorting)
│   │   ├── core/           # config, security (JWT/bcrypt), logging, error handling
│   │   ├── db/             # base, mixins, session
│   │   ├── models/         # SQLAlchemy models
│   │   ├── schemas/        # Pydantic schemas (+ envelopes, errors, examples)
│   │   ├── services/       # ocr/, bom/, revision engine, visual diff, analytics, AI, reports, storage
│   │   ├── workers/        # Celery app and tasks
│   │   └── seed/           # sample data
│   ├── alembic/            # migrations
│   ├── desktop/            # Windows desktop launcher, PyInstaller spec, Inno Setup installer
│   ├── scripts/            # entrypoint, OpenAPI export
│   └── tests/
├── frontend/src/
│   ├── api/                # axios client (token refresh), React Query hooks, types
│   ├── components/         # layout, PDF viewer, tree, charts, grid, upload dialog
│   ├── pages/              # dashboard, register, analysis, compare, BOM, analytics, OCR review, assistant, reports
│   └── store/              # Zustand (auth, theme, project)
├── nginx/                  # edge proxy config
├── docs/openapi.json
├── docker-compose.dev.yml
└── docker-compose.prod.yml
```

## Known limitations

- Visual diff compares page renders of the same sheet size; drawings re-laid-out between revisions
  produce broad change regions (the BOM comparison is unaffected).
- Revision-cloud detection is a geometric heuristic tuned on standard scalloped clouds.
- PaddleOCR support is implemented but not part of the default image; Tesseract is.
- The LLM fallback of the AI assistant is optional and was tested with a mocked client only.
