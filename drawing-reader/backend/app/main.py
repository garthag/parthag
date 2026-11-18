"""FastAPI application factory."""

from __future__ import annotations

import logging
import time
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.gzip import GZipMiddleware
from sqlalchemy import text

from app.api.routes import auth, bom, drawings, ocr, projects, revisions
from app.api.routes.analytics import ai, analytics_router, dashboard, reports_router
from app.core.config import settings
from app.core.exceptions import register_exception_handlers
from app.core.logging import configure_logging, request_id_ctx
from app.db.session import engine

logger = logging.getLogger("app")

TAGS = [
    {"name": "Authentication", "description": "JWT login, refresh, logout and profile."},
    {"name": "Projects", "description": "Projects, the top of the drawing breakdown structure."},
    {"name": "Drawings", "description": "Drawing register: uploads, folder ingestion, search, tree, PDFs."},
    {"name": "OCR", "description": "Title block / revision block / BOM extraction jobs and manual overrides."},
    {"name": "BOM & Weights", "description": "BOM items, material and weight summaries, BOQ and rates."},
    {"name": "Revisions", "description": "Revision listing, BOM comparison and visual differences."},
    {"name": "Dashboard", "description": "KPI cards and chart series."},
    {"name": "Analytics", "description": "Material, weight growth and cost impact analytics."},
    {"name": "AI Assistant", "description": "Natural-language questions answered with SQL-backed analytics."},
    {"name": "Reports", "description": "Register, revision difference, BOQ, MTO, cost and weight reports."},
    {"name": "Lookups", "description": "Disciplines, areas and systems."},
]


@asynccontextmanager
async def lifespan(_: FastAPI):
    configure_logging(settings.LOG_LEVEL, settings.LOG_JSON)
    settings.STORAGE_DIR.mkdir(parents=True, exist_ok=True)
    logger.info("%s %s starting (%s)", settings.APP_NAME, settings.APP_VERSION, settings.ENVIRONMENT)
    yield
    engine.dispose()


def create_app() -> FastAPI:
    app = FastAPI(
        title=f"{settings.APP_NAME} API",
        version=settings.APP_VERSION,
        description=(
            "Engineering Drawing Register, OCR, BOM Intelligence, Revision Analytics, Weight & BOQ Management.\n\n"
            "Authenticate with `POST /api/auth/login` and send `Authorization: Bearer <access_token>`. List endpoints "
            "accept `page`, `page_size` and `sort` (prefix `-` for descending). Errors use a uniform envelope "
            "`{success: false, error: {code, message, details}, request_id}`."
        ),
        openapi_url=f"{settings.API_PREFIX}/openapi.json",
        docs_url=f"{settings.API_PREFIX}/docs",
        redoc_url=f"{settings.API_PREFIX}/redoc",
        openapi_tags=TAGS,
        lifespan=lifespan,
    )
    app.add_middleware(GZipMiddleware, minimum_size=1024)
    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.CORS_ORIGINS,
        allow_credentials=True,
        allow_methods=["*"],
        allow_headers=["*"],
        expose_headers=["Content-Disposition", "X-Request-ID"],
    )

    @app.middleware("http")
    async def request_context(request: Request, call_next):
        rid = request.headers.get("x-request-id") or uuid.uuid4().hex[:16]
        token = request_id_ctx.set(rid)
        started = time.perf_counter()
        try:
            response = await call_next(request)
        finally:
            request_id_ctx.reset(token)
        duration = (time.perf_counter() - started) * 1000
        response.headers["X-Request-ID"] = rid
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "SAMEORIGIN"
        response.headers["Referrer-Policy"] = "strict-origin-when-cross-origin"
        logger.info(
            "%s %s -> %s %.1fms",
            request.method,
            request.url.path,
            response.status_code,
            duration,
            extra={
                "path": request.url.path,
                "method": request.method,
                "status": response.status_code,
                "duration_ms": round(duration, 1),
            },
        )
        return response

    register_exception_handlers(app)
    for router in (
        auth.router,
        projects.router,
        drawings.router,
        ocr.router,
        bom.router,
        revisions.router,
        dashboard,
        analytics_router,
        ai,
        reports_router,
    ):
        app.include_router(router, prefix=settings.API_PREFIX)

    @app.get("/health", tags=["Health"], summary="Liveness probe")
    def health() -> dict[str, str]:
        return {"status": "ok"}

    @app.get("/health/ready", tags=["Health"], summary="Readiness probe (database)")
    def ready() -> dict[str, str]:
        with engine.connect() as conn:
            conn.execute(text("SELECT 1"))
        return {"status": "ready"}

    return app


app = create_app()
