"""OCR engine: title block, revision block, notes, rotation, scanned drawings, validators."""

from __future__ import annotations

import shutil
from pathlib import Path

import pytest

from app.services.ocr import fields
from app.services.ocr.backends import get_backend
from app.services.ocr.engine import parse_filename, process_pdf
from app.services.ocr.layout import BBox, Line, PageContent, Word, group_lines, split_line_by_gaps
from app.services.ocr.pdf_loader import load_document, page_count, rotate_bbox
from app.services.ocr.regions import detect_title_block
from tests.conftest import make_bom, make_pdf

EXPECTED = {
    "drawing_number": "TST-ST-A01-GA-0001",
    "title": "PIPE RACK PR-01 GENERAL ARRANGEMENT",
    "revision": "C",
    "date": "2026-03-14",
    "status": "IFC",
    "scale": "1:50",
    "sheet_number": "1 OF 1",
    "drawn_by": "J.DOE",
    "checker": "M.KHAN",
    "approver": "P.SINGH",
    "vendor": "ACME STEEL",
}


def _write(tmp_path: Path, name: str, data: bytes) -> Path:
    path = tmp_path / name
    path.write_bytes(data)
    return path


@pytest.mark.parametrize("rotate", [0, 90, 180, 270])
def test_native_and_rotated_title_block(tmp_path: Path, rotate: int) -> None:
    path = _write(tmp_path, f"r{rotate}.pdf", make_pdf(revision="C", rotate=rotate))
    result = process_pdf(path, filename="scan.pdf", use_default_backend=False)
    for key, value in EXPECTED.items():
        assert result.fields[key] == value, key
    assert result.overall_confidence > 0.9
    assert result.rotation == rotate
    assert not result.is_scanned
    assert not result.needs_review
    assert result.revision_block[-1]["revision"] == "C"
    assert result.notes == ["ALL DIMENSIONS ARE IN MM."]
    assert result.regions["title_block"]["x"] > 0.5
    assert set(result.as_json()) >= {"drawing_number", "title", "revision", "status", "scale"}


def test_multi_sheet_sets_sheet_count(tmp_path: Path) -> None:
    path = _write(tmp_path, "multi.pdf", make_pdf(bom=make_bom(50), bom_rows_per_page=30))
    assert page_count(path) == 2
    result = process_pdf(path, use_default_backend=False)
    assert result.fields["sheet_number"] == "1 OF 2"


def test_scanned_pdf_without_backend_needs_review(tmp_path: Path) -> None:
    path = _write(tmp_path, "scan.pdf", make_pdf(scanned=True, dpi=72))
    result = process_pdf(path, filename="ABC-ST-0001_RevB.pdf", use_default_backend=False)
    assert result.is_scanned
    assert result.engine == "none"
    assert result.fields["drawing_number"] == "ABC-ST-0001"  # falls back to the file name
    assert result.needs_review


@pytest.mark.tesseract
@pytest.mark.skipif(shutil.which("tesseract") is None, reason="tesseract not installed")
@pytest.mark.parametrize("rotate", [0, 90])
def test_scanned_rotated_drawing_with_tesseract(tmp_path: Path, rotate: int) -> None:
    path = _write(tmp_path, "scan.pdf", make_pdf(revision="C", scanned=True, rotate=rotate, dpi=300))
    result = process_pdf(path, filename="upload.pdf", backend=get_backend("tesseract"))
    assert result.is_scanned and result.engine == "tesseract"
    assert result.rotation == rotate
    for key in ("drawing_number", "revision", "date", "status", "scale"):
        assert result.fields[key] == EXPECTED[key], key
    assert result.overall_confidence > 0.9


def test_parse_filename() -> None:
    assert parse_filename("ABC-ST-001_Rev_B.pdf") == {"drawing_number": "ABC-ST-001", "revision": "B"}
    assert parse_filename("abc-st-001 R2.pdf") == {"drawing_number": "ABC-ST-001", "revision": "2"}
    assert parse_filename("plain.pdf")["revision"] is None
    assert parse_filename("weird name!.pdf") == {"drawing_number": "WEIRD NAME!", "revision": None}


@pytest.mark.parametrize(
    ("field", "raw", "expected", "valid"),
    [
        ("drawing_number", "nrp-st-a01-ga-0001", "NRP-ST-A01-GA-0001", True),
        ("drawing_number", "TITLE", "TITLE", False),
        ("revision", "Rev. B", "B", True),
        ("revision", "Cc", "C", True),
        ("revision", "P1", "P1", True),
        ("revision", "ABCDE", "ABCDE", False),
        ("scale", "1 : 50", "1:50", True),
        ("scale", "nts", "NTS", True),
        ("date", "14/03/2026", "2026-03-14", True),
        ("date", "14-Mar-2026", "2026-03-14", True),
        ("date", "someday", "someday", False),
        ("status", "Issued for Construction", "IFC", True),
        ("status", "AS BUILT", "AS-BUILT", True),
        ("status", "unknown", "UNKNOWN", False),
        ("sheet_number", "1OF 2", "1 OF 2", True),
        ("title", "ab", "AB", False),
        ("vendor", "Acme", "Acme", True),
    ],
)
def test_validate(field: str, raw: str, expected: str, valid: bool) -> None:
    assert fields.validate(field, raw) == (expected, valid)


def test_validate_empty() -> None:
    assert fields.validate("title", " : ") == (None, False)


def test_choose_corroboration_beats_single_source() -> None:
    w = Word("C", 0, 0, 0.1, 0.1, 0.7)
    candidates = [
        fields.FieldValue("C", 0.7, [w], "label"),
        fields.FieldValue("C", 0.9, [], "revision_block"),
        fields.FieldValue("D", 0.8, [], "label"),
    ]
    value, conf, _ = fields.choose("revision", candidates)
    assert value == "C"
    assert conf > 0.9


def test_layout_helpers() -> None:
    words = [
        Word("A", 0.1, 0.1, 0.12, 0.12),
        Word("B", 0.13, 0.1, 0.15, 0.12),
        Word("C", 0.5, 0.1, 0.52, 0.12),
        Word("D", 0.1, 0.3, 0.12, 0.32),
    ]
    lines = group_lines(words)
    assert [ln.text for ln in lines] == ["A B C", "D"]
    assert [s.text for s in split_line_by_gaps(lines[0], 0.1)] == ["A B", "C"]
    assert group_lines([]) == []
    box = BBox(0, 0, 0.2, 0.2)
    assert box.contains(words[0]) and not box.contains(words[2])
    assert box.to_dict()["width"] == 0.2
    assert Line(words[:2]).conf == 1.0
    assert PageContent(0, 1, 1, words).text.startswith("A B C")


def test_rotate_bbox_round_trip() -> None:
    assert rotate_bbox(0.1, 0.2, 0.3, 0.4, 0) == (0.1, 0.2, 0.3, 0.4)
    x0, y0, x1, y1 = rotate_bbox(0.1, 0.2, 0.3, 0.4, 180)
    assert (round(x0, 3), round(y0, 3), round(x1, 3), round(y1, 3)) == (0.7, 0.6, 0.9, 0.8)


def test_title_block_not_found_without_anchors() -> None:
    assert detect_title_block([Word("HELLO", 0.1, 0.1, 0.2, 0.2)]) == (None, 0.0)
    assert detect_title_block([]) == (None, 0.0)


def test_load_document_without_backend_marks_scanned(tmp_path: Path) -> None:
    path = _write(tmp_path, "s.pdf", make_pdf(scanned=True, dpi=72))
    pages = load_document(path, use_default_backend=False)
    assert pages[0].is_scanned and pages[0].words == []


def test_get_backend_none() -> None:
    assert get_backend("none") is None
