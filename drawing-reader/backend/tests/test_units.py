"""Unit tests: categorisation, security, storage, cache, visual diff, PDF generator."""

from __future__ import annotations

import io
import time
from pathlib import Path

import pytest

from app.core import security
from app.core.config import Settings
from app.core.exceptions import AuthenticationError, ValidationFailedError
from app.core.logging import JsonFormatter, configure_logging
from app.services import cache, storage
from app.services.categorize import categorize, revision_sequence, split_filename
from app.services.visual_diff import changed_regions, detect_revision_clouds, visual_diff
from tests.conftest import make_bom, make_pdf


def test_revision_sequence_order() -> None:
    codes = ["P1", "P2", "A", "B", "Z", "AA", "C01", "0", "1", "R2", "10"]
    assert sorted(codes, key=revision_sequence) == ["P1", "P2", "A", "B", "C01", "Z", "AA", "0", "1", "R2", "10"]
    assert revision_sequence(None) == 0
    assert revision_sequence("???") == 1


@pytest.mark.parametrize(
    ("name", "expected"),
    [
        ("NRP-ST-A01-GA-0001_RevB.pdf", ("NRP-ST-A01-GA-0001", "B")),
        ("X-ME-0001 (Rev 2).pdf", ("X-ME-0001", "2")),
        ("drawing.pdf", ("DRAWING", None)),
        ("ABC_R3.pdf", ("ABC", "3")),
    ],
)
def test_split_filename(name: str, expected: tuple[str, str | None]) -> None:
    assert split_filename(name) == expected


def test_categorize_from_number_and_folders() -> None:
    c = categorize("NRP-ST-A01-GA-0001_RevB.pdf")
    assert (c.discipline_code, c.area_code, c.drawing_type, c.revision) == ("STR", "A01", "GA", "B")
    c = categorize("12345.pdf", "Project X/03_Piping/Area 07/Isometric/12345.pdf")
    assert (c.discipline_code, c.area_code, c.drawing_type) == ("PIP", "AREA07", "ISO")
    assert c.folder_path == "Project X/03_Piping/Area 07/Isometric"
    assert categorize("E-001.pdf").discipline_code is None  # single letter at start is ambiguous


def test_tokens_and_blacklist() -> None:
    token = security.create_token(7, "access", {"roles": ["x"]})
    payload = security.decode_token(token, "access")
    assert payload["sub"] == "7" and payload["roles"] == ["x"]
    with pytest.raises(AuthenticationError):
        security.decode_token(token, "refresh")
    with pytest.raises(AuthenticationError):
        security.decode_token("garbage", "access")
    security.token_blacklist.revoke(payload["jti"], int(time.time()) + 60)
    with pytest.raises(AuthenticationError):
        security.decode_token(token, "access")
    security.token_blacklist.revoke("old", int(time.time()) - 10)
    assert not security.token_blacklist.is_revoked("old")
    hashed = security.hash_password("s3cret-pass")
    assert security.verify_password("s3cret-pass", hashed) and not security.verify_password("x", hashed)
    assert not security.verify_password("x", "not-a-hash")


def test_settings_parsing() -> None:
    s = Settings(CORS_ORIGINS="http://a, http://b", DATABASE_URL="sqlite://", REDIS_URL="redis://r:6379/1")
    assert s.CORS_ORIGINS == ["http://a", "http://b"]
    assert s.is_sqlite and s.broker_url == "redis://r:6379/1" and s.result_backend == "redis://r:6379/1"


def test_storage_save_and_validation() -> None:
    data = make_pdf()
    rel, sha, size = storage.save_stream(io.BytesIO(data), "TST")
    assert size == len(data) and rel.endswith(f"{sha}.pdf")
    assert storage.absolute_path(rel).read_bytes() == data
    rel2, sha2, _ = storage.save_stream(io.BytesIO(data), "TST")  # dedup
    assert (rel2, sha2) == (rel, sha)
    with pytest.raises(ValidationFailedError):
        storage.save_stream(io.BytesIO(b"not a pdf"), "TST")
    with pytest.raises(ValidationFailedError):
        storage.absolute_path("../../etc/passwd")


def test_cache_ttl() -> None:
    calls = []
    producer = lambda: calls.append(1) or len(calls)  # noqa: E731
    assert cache.cached("k", producer, ttl=60) == 1
    assert cache.cached("k", producer, ttl=60) == 1
    cache.invalidate("k")
    assert cache.cached("k", producer, ttl=60) == 2
    assert cache.cached("k", producer, ttl=0) == 3


def test_visual_diff_detects_changes_and_clouds(tmp_path: Path) -> None:
    a = tmp_path / "a.pdf"
    b = tmp_path / "b.pdf"
    a.write_bytes(make_pdf(revision="A", bom=make_bom(4)))
    b.write_bytes(make_pdf(revision="B", bom=make_bom(5), clouds=[(100, 200, 300, 350)]))
    result = visual_diff(a, b, page=3)  # page beyond range clamps to last page
    kinds = [r["kind"] for r in result["regions"]]
    assert "change" in kinds and kinds.count("cloud") == 1
    cloud = next(r for r in result["regions"] if r["kind"] == "cloud")
    assert abs(cloud["x"] - 100 / 1190) < 0.02 and abs(cloud["y"] - 200 / 842) < 0.03
    assert result["changed_ratio"] > 0
    same = visual_diff(a, a)
    assert same["regions"] == [] and same["changed_ratio"] == 0


def test_cloud_detector_ignores_plain_geometry() -> None:
    import numpy as np

    img = np.full((400, 600), 255, np.uint8)
    img[100:300, 100] = 0
    img[100:300, 400] = 0
    img[100, 100:400] = 0
    img[300, 100:401] = 0
    assert detect_revision_clouds(img) == []
    regions, ratio = changed_regions(img, img[:, :-1].copy())
    assert isinstance(regions, list) and ratio >= 0


def test_logging_json() -> None:
    import logging

    configure_logging("WARNING", json_logs=True)
    record = logging.LogRecord("x", logging.INFO, __file__, 1, "hello %s", ("w",), None)
    record.path = "/p"
    assert '"msg": "hello w"' in JsonFormatter().format(record)
    configure_logging("WARNING", json_logs=False)
