"""BOM extraction, merged cells, multi-page tables, number parsing and weight calculations."""

from __future__ import annotations

import shutil
from decimal import Decimal
from pathlib import Path

import pytest

from app.services.bom import weights
from app.services.bom.extractor import clean_code, extract_bom, parse_number
from app.services.ocr.backends import get_backend
from app.services.ocr.layout import PageContent, Word
from app.services.ocr.pdf_loader import load_document
from tests.conftest import make_bom, make_pdf


def _extract(tmp_path: Path, data: bytes, backend=None):
    path = tmp_path / "bom.pdf"
    path.write_bytes(data)
    return extract_bom(load_document(path, backend=backend, use_default_backend=False), backend)


@pytest.mark.parametrize("rotate", [0, 90])
def test_multi_page_bom_exact(tmp_path: Path, rotate: int) -> None:
    bom = make_bom(55)
    result = _extract(tmp_path, make_pdf(bom=bom, bom_rows_per_page=30, rotate=rotate))
    assert len(result.items) == 55
    assert (result.page_from, result.page_to) == (1, 2)
    for got, exp in zip(result.items, bom, strict=True):
        assert got.item_number == exp.item_number
        assert got.part_number == exp.part_number
        assert got.description == exp.description
        assert (got.material, got.grade, got.specification, got.unit) == ("CS", "S355JR", "EN 10025", "EA")
        assert got.quantity == exp.quantity
        assert got.total_weight_kg == exp.total_weight_kg
    expected_total = sum(b.total_weight_kg for b in bom)
    assert result.total_weight_kg == expected_total
    assert result.source_total_weight_kg == expected_total
    assert result.weight_check_passed is True
    assert result.confidence == 1.0
    assert {"quantity", "total_weight", "unit_weight"} <= set(result.columns)


def test_no_bom(tmp_path: Path) -> None:
    result = _extract(tmp_path, make_pdf(bom=[]))
    assert result.items == [] and result.confidence == 0.0 and result.weight_check_passed is None


def _w(text: str, x: float, y: float) -> Word:
    return Word(text, x, y, x + 0.01 * max(len(text), 2) * 0.5, y + 0.01)


def test_merged_cells_wrapped_description_and_ditto() -> None:
    header = [
        _w("ITEM", 0.10, 0.10),
        _w("PART", 0.15, 0.10),
        _w("NO", 0.18, 0.10),
        _w("DESCRIPTION", 0.25, 0.10),
        _w("MATERIAL", 0.50, 0.10),
        _w("GRADE", 0.58, 0.10),
        _w("QTY", 0.65, 0.10),
        _w("UNIT", 0.70, 0.10),
        _w("TOTAL", 0.78, 0.10),
        _w("WT", 0.81, 0.10),
    ]
    rows = [
        [
            _w("1", 0.10, 0.12),
            _w("P-1", 0.15, 0.12),
            _w("PLATE", 0.25, 0.12),
            _w("CS", 0.50, 0.12),
            _w("S355", 0.58, 0.12),
            _w("2", 0.65, 0.12),
            _w("EA", 0.70, 0.12),
            _w("10.00", 0.78, 0.12),
        ],
        [_w("CHEQUERED", 0.25, 0.14)],  # wrapped description line
        [
            _w("2", 0.10, 0.16),
            _w("P-2", 0.15, 0.16),
            _w("ANGLE", 0.25, 0.16),
            _w('"', 0.50, 0.16),
            _w("DITTO", 0.58, 0.16),
            _w("4", 0.65, 0.16),
            _w("EA", 0.70, 0.16),
            _w("1,200.50", 0.78, 0.16),
        ],
        [
            _w("3", 0.10, 0.18),
            _w("P-3", 0.15, 0.18),
            _w("BOLT", 0.25, 0.18),
            _w("8", 0.65, 0.18),
            _w("EA", 0.70, 0.18),
            _w("3,5", 0.78, 0.18),
        ],  # merged (blank) material/grade cells
        [_w("TOTAL", 0.25, 0.20), _w("1,214.00", 0.78, 0.20)],
    ]
    words = header + [w for r in rows for w in r]
    result = extract_bom([PageContent(0, 1, 1, words)])
    assert [i.description for i in result.items] == ["PLATE CHEQUERED", "ANGLE", "BOLT"]
    assert result.items[1].material == "CS" and result.items[1].grade == "S355"
    assert result.items[2].material == "CS" and result.items[2].grade == "S355"
    assert result.items[1].total_weight_kg == Decimal("1200.50")
    assert result.items[2].total_weight_kg == Decimal("3.5")
    assert result.items[1].unit_weight_kg == Decimal("300.125")
    assert result.source_total_weight_kg == Decimal("1214.00")
    assert result.weight_check_passed is True


def test_bom_above_header_is_read_bottom_up() -> None:
    header_y = 0.5
    header = [
        _w("ITEM", 0.1, header_y),
        _w("DESCRIPTION", 0.2, header_y),
        _w("MATERIAL", 0.45, header_y),
        _w("QTY", 0.6, header_y),
        _w("WEIGHT", 0.7, header_y),
    ]
    rows = [
        [_w("2", 0.1, 0.48), _w("NUT", 0.2, 0.48), _w("CS", 0.45, 0.48), _w("4", 0.6, 0.48), _w("1.0", 0.7, 0.48)],
        [_w("1", 0.1, 0.46), _w("BOLT", 0.2, 0.46), _w("CS", 0.45, 0.46), _w("4", 0.6, 0.46), _w("2.0", 0.7, 0.46)],
    ]
    words = header + [w for r in rows for w in r]
    result = extract_bom([PageContent(0, 1, 1, words)])
    assert [i.item_number for i in result.items] == ["1", "2"]
    assert result.total_weight_kg == Decimal("3.000")


@pytest.mark.tesseract
@pytest.mark.skipif(shutil.which("tesseract") is None, reason="tesseract not installed")
def test_scanned_bom_numeric_accuracy(tmp_path: Path) -> None:
    bom = make_bom(12)
    result = _extract(tmp_path, make_pdf(bom=bom, scanned=True, dpi=300), get_backend("tesseract"))
    assert len(result.items) == 12
    checked = correct = 0
    for got, exp in zip(result.items, bom, strict=True):
        for attr in ("item_number", "quantity", "total_weight_kg", "unit_weight_kg", "material", "grade", "unit"):
            checked += 1
            correct += str(getattr(got, attr)).upper() == str(getattr(exp, attr)).upper()
    assert correct / checked >= 0.95
    assert result.total_weight_kg == sum(b.total_weight_kg for b in bom)


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("1,234.50", Decimal("1234.50")),
        ("12,5", Decimal("12.5")),
        ("45 kg", Decimal("45")),
        ("(2)", Decimal("2")),
        ("-", None),
        (None, None),
        ("abc", None),
        ("1O", Decimal("10")),
        ("7NOS", Decimal("7")),
    ],
)
def test_parse_number(raw: str | None, expected: Decimal | None) -> None:
    assert parse_number(raw) == expected


def test_clean_code() -> None:
    assert clean_code("pro1-boo1") == "PR01-B001"
    assert clean_code("ABC") == "ABC"
    assert clean_code(None) is None


@pytest.mark.parametrize(
    ("desc", "material", "unit", "expected"),
    [
        ("PL 10 x 200 x 800", "CS", "EA", Decimal("12.560")),
        ("UB 305x165x40 L=6000", "CS", "EA", Decimal("241.800")),
        ("IPE300 L=2.5M", "CS", "EA", Decimal("105.500")),
        ("HEA 200", "CS", "M", Decimal("42.300")),
        ("W12X40 L=1000", "CS", "EA", Decimal("59.500")),
        ("UB 999x999x77 L=1000", "CS", "EA", Decimal("77.000")),
        ("PIPE OD 168.3 x 7.11 WT", "CS", "M", Decimal("28.264")),
        ("ANGLE 100x100x10 L=1000", "CS", "EA", Decimal("14.915")),
        ("RB 20 L=1000", "CS", "EA", Decimal("2.466")),
        ("FB 10 x 100", "SS 316", "M", Decimal("7.980")),
        ("PL 10 x 1000", "AL", "M2", Decimal("27.000")),
        ("GASKET", "CS", "EA", None),
        ("UB 305x165x40", "CS", "EA", None),
    ],
)
def test_estimate_unit_weight(desc: str, material: str, unit: str, expected: Decimal | None) -> None:
    assert weights.estimate_unit_weight(desc, material, None, unit) == expected


def test_reconcile_weights() -> None:
    assert weights.reconcile_weights(Decimal(2), Decimal(5), Decimal(10)) == (Decimal(5), Decimal(10), False)
    assert weights.reconcile_weights(Decimal(2), Decimal(5), None) == (Decimal(5), Decimal("10.000"), True)
    assert weights.reconcile_weights(Decimal(4), None, Decimal(10)) == (Decimal("2.500"), Decimal(10), False)
    assert weights.reconcile_weights(Decimal(0), None, Decimal(10)) == (None, Decimal(10), False)
    uw, tw, calc = weights.reconcile_weights(Decimal(2), None, None, "PL 10 x 200 x 800", "CS")
    assert (uw, tw, calc) == (Decimal("12.560"), Decimal("25.120"), True)
    assert weights.reconcile_weights(Decimal(2), None, None, "GASKET") == (None, Decimal(0), False)
    assert weights.density_for(None) == 7850
    assert weights.density_for("Aluminium") == 2700
