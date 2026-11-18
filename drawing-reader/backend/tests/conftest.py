"""Test fixtures: in-memory SQLite, temporary storage, seeded sample data and authenticated clients."""

from __future__ import annotations

import os
import shutil
import tempfile
from collections.abc import Iterator
from decimal import Decimal

_STORAGE = tempfile.mkdtemp(prefix="drawing-reader-test-")
os.environ.update(
    {
        "ENVIRONMENT": "test",
        "DATABASE_URL": "sqlite://",
        "STORAGE_DIR": _STORAGE,
        "REDIS_URL": "",
        "OCR_EAGER": "true",
        "OCR_ENGINE": "none",
        "CACHE_TTL_SECONDS": "0",
        "LOG_LEVEL": "WARNING",
        "ANTHROPIC_API_KEY": "",
    }
)

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402
from sqlalchemy.orm import Session  # noqa: E402

from app.core.security import hash_password  # noqa: E402
from app.db.session import SessionLocal, engine  # noqa: E402
from app.main import app  # noqa: E402
from app.models import Base, Role, User  # noqa: E402
from app.seed.seed import seed  # noqa: E402
from app.services.pdf_generator import BOMLine, DrawingSpec, RevisionEntry, generate_drawing_pdf  # noqa: E402


@pytest.fixture(scope="session", autouse=True)
def database() -> Iterator[None]:
    Base.metadata.create_all(engine)
    with SessionLocal() as db:
        seed(db, with_pdfs=True, drawings=12, revisions_total=30, seed_value=7)
        viewer_role = db.query(Role).filter_by(name="Viewer").one()
        db.add(
            User(
                username="viewer",
                email="viewer@example.com",
                full_name="Read Only",
                hashed_password=hash_password("Viewer@12345"),
                roles=[viewer_role],
            )
        )
        db.commit()
    yield
    shutil.rmtree(_STORAGE, ignore_errors=True)


@pytest.fixture
def db() -> Iterator[Session]:
    session = SessionLocal()
    try:
        yield session
        session.commit()
    finally:
        session.close()


@pytest.fixture(scope="session")
def client() -> TestClient:
    return TestClient(app, raise_server_exceptions=False)


def _login(client: TestClient, username: str, password: str) -> dict[str, str]:
    res = client.post("/api/auth/login", json={"username": username, "password": password})
    assert res.status_code == 200, res.text
    return {"Authorization": f"Bearer {res.json()['data']['access_token']}"}


@pytest.fixture(scope="session")
def auth(client: TestClient) -> dict[str, str]:
    return _login(client, "admin", "Admin@12345")


@pytest.fixture(scope="session")
def viewer(client: TestClient) -> dict[str, str]:
    return _login(client, "viewer", "Viewer@12345")


@pytest.fixture(scope="session")
def project_id(client: TestClient, auth: dict[str, str]) -> int:
    res = client.post("/api/projects", headers=auth, json={"code": "TST", "name": "Upload Test Project"})
    assert res.status_code == 201, res.text
    return res.json()["data"]["id"]


def make_bom(n: int, *, grade: str = "S355JR", qty: int = 2, start: int = 1) -> list[BOMLine]:
    rows = []
    for i in range(start, start + n):
        uw = Decimal("241.80") if i % 2 else Decimal("12.56")
        desc = "BEAM UB 305x165x40 L=6000" if i % 2 else "PL 10 x 200 x 800"
        rows.append(BOMLine(str(i), f"PR01-B{i:03d}", desc, "CS", grade, "EN 10025", Decimal(qty), "EA", uw, uw * qty))
    return rows


def make_pdf(number: str = "TST-ST-A01-GA-0001", revision: str = "A", bom: list[BOMLine] | None = None, **kw) -> bytes:
    spec = DrawingSpec(
        number,
        kw.pop("title", "PIPE RACK PR-01 GENERAL ARRANGEMENT"),
        revision,
        kw.pop("date", "2026-03-14"),
        bom=bom if bom is not None else make_bom(5),
        revisions=[RevisionEntry(revision, "2026-03-14", "ISSUED FOR CONSTRUCTION")],
        notes=["ALL DIMENSIONS ARE IN MM."],
        status=kw.pop("status", "IFC"),
        vendor=kw.pop("vendor", "ACME STEEL"),
        clouds=kw.pop("clouds", []),
    )
    return generate_drawing_pdf(spec, **kw)
