"""Generate realistic engineering drawing PDFs (title block, revision block, BOM, notes, geometry).

Used for seed data so the register is browsable immediately, and as deterministic fixtures for the
OCR / BOM / revision test-suites (native, rotated, multi-page and scanned variants).
"""

from __future__ import annotations

import itertools
import math
import random
from dataclasses import dataclass, field
from decimal import Decimal
from pathlib import Path

import pymupdf as fitz

A3_LANDSCAPE = (1190.0, 842.0)
FONT = "helv"
FONT_BOLD = "hebo"


@dataclass
class BOMLine:
    item_number: str
    part_number: str
    description: str
    material: str
    grade: str
    specification: str
    quantity: Decimal
    unit: str
    unit_weight_kg: Decimal | None
    total_weight_kg: Decimal | None


@dataclass
class RevisionEntry:
    revision: str
    date: str
    description: str
    drawn_by: str = "JD"
    checker: str = "MK"
    approver: str = "PS"


@dataclass
class DrawingSpec:
    drawing_number: str
    title: str
    revision: str
    date: str
    status: str = "IFC"
    scale: str = "1:50"
    sheet: str = "1 OF 1"
    drawn_by: str = "J.DOE"
    checker: str = "M.KHAN"
    approver: str = "P.SINGH"
    vendor: str | None = None
    project: str = "NORTH REFINERY EXPANSION"
    client: str = "ACME PETROLEUM"
    bom: list[BOMLine] = field(default_factory=list)
    revisions: list[RevisionEntry] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    clouds: list[tuple[float, float, float, float]] = field(default_factory=list)
    seed: int = 0


def _text(page: fitz.Page, x: float, y: float, text: str, size: float = 7.0, bold: bool = False) -> None:
    page.insert_text((x, y), text, fontsize=size, fontname=FONT_BOLD if bold else FONT, color=(0, 0, 0))


def _fmt(value: Decimal | None, places: int = 2) -> str:
    if value is None:
        return ""
    q = Decimal(1).scaleb(-places)
    return f"{Decimal(value).quantize(q):,}"


def draw_cloud(page: fitz.Page, rect: tuple[float, float, float, float], bump: float = 12.0) -> None:
    """Draw a revision cloud (scalloped outline) around ``rect``."""
    x0, y0, x1, y1 = rect
    shape = page.new_shape()
    pts: list[fitz.Point] = []
    for (ax, ay), (bx, by) in (((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))):
        length = math.hypot(bx - ax, by - ay)
        n = max(int(length // bump), 2)
        for i in range(n):
            pts.append(fitz.Point(ax + (bx - ax) * i / n, ay + (by - ay) * i / n))
    pts.append(pts[0])
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    for a, b in itertools.pairwise(pts):
        mx, my = (a.x + b.x) / 2, (a.y + b.y) / 2
        dx, dy = mx - cx, my - cy
        norm = math.hypot(dx, dy) or 1
        bulge = bump * 0.6
        ctrl = fitz.Point(mx + dx / norm * bulge, my + dy / norm * bulge)
        shape.draw_bezier(a, ctrl, ctrl, b)
    shape.finish(color=(0, 0, 0), width=1.0, closePath=False)
    shape.commit()


def _geometry(page: fitz.Page, rng: random.Random) -> None:
    """Some plausible structural geometry: grid lines, members and dimension strings."""
    shape = page.new_shape()
    for i in range(6):
        x = 70 + i * 70
        shape.draw_line((x, 120), (x, 560))
    for j in range(5):
        y = 140 + j * 100
        shape.draw_line((50, y), (460, y))
    for _ in range(8):
        x = 70 + rng.randint(0, 5) * 70
        y = 140 + rng.randint(0, 4) * 100
        shape.draw_rect(fitz.Rect(x - 6, y - 6, x + 6, y + 6))
    shape.finish(color=(0.15, 0.15, 0.15), width=0.6)
    shape.commit()
    for i in range(6):
        _text(page, 66 + i * 70, 112, chr(65 + i), 8, True)
    for j in range(5):
        _text(page, 36, 143 + j * 100, str(j + 1), 8, True)
    _text(page, 200, 590, "PLAN VIEW", 9, True)


def _border(page: fitz.Page) -> None:
    w, h = page.rect.width, page.rect.height
    shape = page.new_shape()
    shape.draw_rect(fitz.Rect(20, 20, w - 20, h - 20))
    shape.finish(color=(0, 0, 0), width=1.2)
    shape.commit()


BOM_COLS = [
    ("ITEM", 465),
    ("PART NO", 492),
    ("DESCRIPTION", 566),
    ("MATERIAL", 758),
    ("GRADE", 812),
    ("SPEC", 858),
    ("QTY", 934),
    ("UNIT", 962),
    ("UNIT WT", 996),
    ("TOTAL WT", 1062),
]


def _bom_table(page: fitz.Page, rows: list[BOMLine], continued: bool, total: Decimal | None) -> None:
    top = 40.0
    row_h = 11.0
    _text(page, 465, top, "BILL OF MATERIALS" + (" (CONTINUED)" if continued else ""), 8, True)
    y = top + 14
    for label, x in BOM_COLS:
        _text(page, x, y, label, 6.5, True)
    shape = page.new_shape()
    shape.draw_line((460, y + 3), (1170, y + 3))
    y += row_h + 2
    for row in rows:
        values = [
            row.item_number,
            row.part_number,
            row.description[:48],
            row.material,
            row.grade,
            row.specification,
            _fmt(row.quantity, 0) if row.quantity == row.quantity.to_integral() else _fmt(row.quantity, 1),
            row.unit,
            _fmt(row.unit_weight_kg),
            _fmt(row.total_weight_kg),
        ]
        for (_, x), v in zip(BOM_COLS, values, strict=True):
            if v:
                _text(page, x, y, v, 6.5)
        y += row_h
    if total is not None:
        shape.draw_line((460, y - 7), (1170, y - 7))
        _text(page, 566, y + 2, "TOTAL WEIGHT (KG)", 6.5, True)
        _text(page, 1062, y + 2, _fmt(total), 6.5, True)
        y += row_h
    shape.draw_rect(fitz.Rect(460, top - 10, 1170, y - 4))
    shape.finish(color=(0, 0, 0), width=0.6)
    shape.commit()


def _revision_block(page: fitz.Page, revisions: list[RevisionEntry]) -> None:
    header_y = 668.0
    cols = [("REV", 795), ("DATE", 820), ("DESCRIPTION", 875), ("BY", 1085), ("CHK", 1112), ("APP", 1140)]
    for label, x in cols:
        _text(page, x, header_y, label, 6.5, True)
    y = header_y - 11
    for rev in revisions[-5:]:
        vals = [rev.revision, rev.date, rev.description[:40], rev.drawn_by, rev.checker, rev.approver]
        for (_, x), v in zip(cols, vals, strict=True):
            _text(page, x, y, v, 6.5)
        y -= 11
    shape = page.new_shape()
    shape.draw_rect(fitz.Rect(790, y + 2, 1170, header_y + 4))
    shape.finish(color=(0, 0, 0), width=0.6)
    shape.commit()


def _title_block(page: fitz.Page, spec: DrawingSpec, sheet: str) -> None:
    x0, y0, x1, y1 = 790.0, 674.0, 1170.0, 822.0
    shape = page.new_shape()
    shape.draw_rect(fitz.Rect(x0, y0, x1, y1))
    for y in (698, 744, 766, 786, 804):
        shape.draw_line((x0, y), (x1, y))
    shape.finish(color=(0, 0, 0), width=0.8)
    shape.commit()
    _text(page, 795, 688, "PROJECT:", 6.5, True)
    _text(page, 835, 688, spec.project, 7)
    _text(page, 1010, 688, "CLIENT:", 6.5, True)
    _text(page, 1045, 688, spec.client, 7)
    _text(page, 795, 708, "TITLE:", 6.5, True)
    words = spec.title.split()
    line1, line2 = [], []
    for word in words:
        (line1 if len(" ".join([*line1, word])) <= 52 else line2).append(word)
    _text(page, 795, 722, " ".join(line1), 9, True)
    if line2:
        _text(page, 795, 736, " ".join(line2), 9, True)
    _text(page, 795, 758, "DRAWING NO:", 6.5, True)
    _text(page, 850, 758, spec.drawing_number, 9, True)
    _text(page, 1060, 758, "REV:", 6.5, True)
    _text(page, 1085, 758, spec.revision, 9, True)
    _text(page, 795, 779, "SCALE:", 6.5, True)
    _text(page, 825, 779, spec.scale, 7)
    _text(page, 900, 779, "DATE:", 6.5, True)
    _text(page, 925, 779, spec.date, 7)
    _text(page, 1040, 779, "SHEET:", 6.5, True)
    _text(page, 1072, 779, sheet, 7)
    _text(page, 795, 798, "STATUS:", 6.5, True)
    _text(page, 832, 798, spec.status, 7)
    if spec.vendor:
        _text(page, 960, 798, "VENDOR:", 6.5, True)
        _text(page, 998, 798, spec.vendor, 7)
    _text(page, 795, 816, "DRAWN:", 6.5, True)
    _text(page, 828, 816, spec.drawn_by, 7)
    _text(page, 915, 816, "CHECKED:", 6.5, True)
    _text(page, 956, 816, spec.checker, 7)
    _text(page, 1040, 816, "APPROVED:", 6.5, True)
    _text(page, 1086, 816, spec.approver, 7)


def _notes(page: fitz.Page, notes: list[str]) -> None:
    if not notes:
        return
    _text(page, 40, 640, "NOTES:", 8, True)
    y = 652
    for i, note in enumerate(notes[:12], 1):
        _text(page, 40, y, f"{i}. {note}", 6.5)
        y += 10


def build_document(spec: DrawingSpec, bom_rows_per_page: int = 40) -> fitz.Document:
    rng = random.Random(spec.seed)
    doc = fitz.open()
    chunks = [spec.bom[i : i + bom_rows_per_page] for i in range(0, len(spec.bom), bom_rows_per_page)] or [[]]
    total = sum((b.total_weight_kg or Decimal(0) for b in spec.bom), Decimal(0)) if spec.bom else None
    for idx, chunk in enumerate(chunks):
        page = doc.new_page(width=A3_LANDSCAPE[0], height=A3_LANDSCAPE[1])
        sheet = f"{idx + 1} OF {len(chunks)}"
        _border(page)
        _geometry(page, rng)
        if chunk:
            _bom_table(page, chunk, continued=idx > 0, total=total if idx == len(chunks) - 1 else None)
        _revision_block(page, spec.revisions or [RevisionEntry(spec.revision, spec.date, "ISSUED")])
        _title_block(page, spec, sheet)
        if idx == 0:
            _notes(page, spec.notes)
        for rect in spec.clouds:
            draw_cloud(page, rect)
    return doc


def rotate_document(doc: fitz.Document, angle: int) -> fitz.Document:
    """Return a new document whose content is drawn rotated (simulates a sideways-scanned sheet)."""
    out = fitz.open()
    for page in doc:
        w, h = page.rect.width, page.rect.height
        new = out.new_page(width=h, height=w) if angle in (90, 270) else out.new_page(width=w, height=h)
        new.show_pdf_page(new.rect, doc, page.number, rotate=angle)
    return out


def rasterize_document(doc: fitz.Document, dpi: int = 200, rotate: int = 0) -> fitz.Document:
    """Convert to an image-only (scanned) PDF, optionally rotated by 90° steps."""
    out = fitz.open()
    for page in doc:
        pix = page.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY)
        w, h = page.rect.width, page.rect.height
        new = out.new_page(width=h, height=w) if rotate in (90, 270) else out.new_page(width=w, height=h)
        new.insert_image(new.rect, pixmap=pix, rotate=rotate)
    return out


def generate_drawing_pdf(
    spec: DrawingSpec,
    path: str | Path | None = None,
    *,
    rotate: int = 0,
    scanned: bool = False,
    bom_rows_per_page: int = 40,
    dpi: int = 200,
) -> bytes:
    doc = build_document(spec, bom_rows_per_page)
    if scanned:
        doc = rasterize_document(doc, dpi=dpi, rotate=rotate)
    elif rotate:
        doc = rotate_document(doc, rotate)
    data = doc.tobytes(garbage=3, deflate=True)
    if path is not None:
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        Path(path).write_bytes(data)
    return data
