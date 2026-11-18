"""Locate and parse BOM tables from page words (native text or OCR output).

Strategy
--------
1. Group words into lines and find header lines naming >=4 known BOM columns.
2. Derive column boundaries from header word positions (midpoints between columns), merging
   stacked two-line headers such as ``UNIT`` / ``WT (KG)``.
3. Walk rows away from the header (downwards, or upwards for tables stacked over a title block)
   until a TOTAL row, a large vertical gap, or another table begins.
4. Resolve merged cells: wrapped descriptions (rows without item number/quantity) are appended
   to the previous row; blank or ``DITTO``/``"`` material/grade/spec cells inherit the value above.
5. Concatenate tables across pages (multi-sheet BOMs, ``(CONTINUED)`` sheets).
6. Reconcile weights (qty x unit weight, section library estimates) and check against the
   printed BOM total.
"""

from __future__ import annotations

import itertools
import re
import statistics
from dataclasses import dataclass, field
from decimal import Decimal, InvalidOperation

from app.services.bom.weights import q3, reconcile_weights
from app.services.ocr.backends import OCRBackend, recognize_region
from app.services.ocr.layout import BBox, Line, PageContent, Word, group_lines

COLUMN_SYNONYMS: dict[str, list[tuple[str, ...]]] = {
    "item_number": [("ITEM", "NO"), ("ITEM",), ("POS",), ("MARK",), ("SR", "NO"), ("S/N",)],
    "part_number": [("PART", "NO"), ("PART", "NUMBER"), ("PART",), ("TAG", "NO"), ("CODE",), ("PIECE", "MARK")],
    "description": [("DESCRIPTION",), ("DESC",)],
    "material": [("MATERIAL",), ("MATL",), ("MAT'L",), ("MAT",)],
    "grade": [("GRADE",), ("GR",)],
    "specification": [("SPEC",), ("SPECIFICATION",), ("STANDARD",), ("STD",)],
    "quantity": [("QTY",), ("QUANTITY",), ("NOS",), ("NO", "OFF")],
    "unit": [("UNIT",), ("UOM",)],
    "unit_weight": [("UNIT", "WT"), ("UNIT", "WEIGHT"), ("WT/UNIT",), ("UNIT", "MASS"), ("WT", "EACH")],
    "total_weight": [("TOTAL", "WT"), ("TOTAL", "WEIGHT"), ("TOT", "WT"), ("TOTAL", "MASS"), ("WEIGHT",), ("WT",)],
}
_SYN_SEQS = sorted(
    ((col, seq) for col, seqs in COLUMN_SYNONYMS.items() for seq in seqs), key=lambda item: -len(item[1])
)
REQUIRED_ANY = {"description", "quantity"}
DITTO = {'"', "DO", "DITTO", "''", "-DO-"}
NUMERIC_COLUMNS = {"quantity", "unit_weight", "total_weight"}


@dataclass
class ColumnSpec:
    name: str
    x0: float
    x1: float

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2


@dataclass
class ExtractedItem:
    line_no: int
    item_number: str | None = None
    part_number: str | None = None
    description: str = ""
    material: str | None = None
    grade: str | None = None
    specification: str | None = None
    quantity: Decimal = Decimal("0")
    unit: str = "EA"
    unit_weight_kg: Decimal | None = None
    total_weight_kg: Decimal = Decimal("0")
    sheet_no: int = 1
    confidence: float = 1.0
    weight_calculated: bool = False
    raw_total_weight_kg: Decimal | None = field(default=None, repr=False)

    def as_dict(self) -> dict:
        return {k: getattr(self, k) for k in self.__dataclass_fields__ if k != "raw_total_weight_kg"}


@dataclass
class BOMExtraction:
    items: list[ExtractedItem] = field(default_factory=list)
    source_total_weight_kg: Decimal | None = None
    page_from: int | None = None
    page_to: int | None = None
    confidence: float = 0.0
    columns: list[str] = field(default_factory=list)

    @property
    def total_weight_kg(self) -> Decimal:
        return q3(sum((i.total_weight_kg for i in self.items), Decimal("0")))

    @property
    def weight_check_passed(self) -> bool | None:
        if self.source_total_weight_kg is None:
            return None
        src = self.source_total_weight_kg
        tolerance = max(abs(src) * Decimal("0.005"), Decimal("0.5"))
        return abs(self.total_weight_kg - src) <= tolerance


def parse_number(text: str | None) -> Decimal | None:
    """Parse engineering numbers: ``1,234.50``, ``12,5`` (decimal comma), ``45 kg``, ``(2)``."""
    if text is None:
        return None
    t = text.strip().upper().replace(" ", "")
    t = re.sub(r"(KG|KGS|T|NOS|NO|EA|M|PCS)$", "", t).strip("()")
    if not t or t in {"-", "--"}:
        return None
    if re.fullmatch(r"\d{1,3}(,\d{3})+(\.\d+)?", t):
        t = t.replace(",", "")
    elif re.fullmatch(r"\d+,\d{1,2}", t):
        t = t.replace(",", ".")
    t = t.replace(",", "")
    # common OCR confusions inside numeric cells
    t = t.translate(str.maketrans({"O": "0", "o": "0", "l": "1", "I": "1", "S": "5"}))
    try:
        return Decimal(t)
    except InvalidOperation:
        return None


def clean_code(text: str | None) -> str | None:
    """Normalise part/tag codes: upper-case and fix OCR letter-O / digit-0 confusion next to digits."""
    if not text:
        return text
    code = text.upper()
    if any(c.isdigit() for c in code):
        previous = None
        while previous != code:  # "BOO1" -> "BO01" -> "B001"
            previous, code = code, re.sub(r"O(?=\d)|(?<=\d)O", "0", code)
    return code


def _match_columns(line: Line) -> list[ColumnSpec]:
    tokens = [w.norm.rstrip(":.()") for w in line.words]
    cols: list[ColumnSpec] = []
    used: set[str] = set()
    i = 0
    while i < len(tokens):
        for name, seq in _SYN_SEQS:
            n = len(seq)
            if name in used or tuple(tokens[i : i + n]) != seq:
                continue
            ws = line.words[i : i + n]
            cols.append(ColumnSpec(name, ws[0].x0, ws[-1].x1))
            used.add(name)
            i += n
            break
        else:
            i += 1
    return cols


def _merge_stacked_header(header: Line, below: Line | None) -> list[ColumnSpec]:
    """Combine a two-row header (e.g. ``UNIT``/``WT``) into one synthetic line before matching."""
    if below is None or below.y0 - header.y1 > header.y1 - header.y0 + 0.01:
        return _match_columns(header)
    stacked: list[Word] = []
    lower_used: set[int] = set()
    for w in header.words:
        under = [
            (k, b)
            for k, b in enumerate(below.words)
            if k not in lower_used
            and b.x0 < w.x1 + 0.005
            and b.x1 > w.x0 - 0.005
            and b.norm.rstrip(":.()") in {"WT", "WEIGHT", "NO", "NUMBER", "KG", "(KG)", "MASS"}
        ]
        stacked.append(w)
        for k, b in under:
            lower_used.add(k)
            stacked.append(Word(b.text, w.x0, w.y0, max(w.x1, b.x1), w.y1, b.conf, b.page))
    merged = _match_columns(Line(stacked))
    return merged if len(merged) > len(_match_columns(header)) else _match_columns(header)


def find_header(lines: list[Line]) -> tuple[int, list[ColumnSpec]] | None:
    best: tuple[int, list[ColumnSpec]] | None = None
    for i, line in enumerate(lines):
        cols = _merge_stacked_header(line, lines[i + 1] if i + 1 < len(lines) else None)
        names = {c.name for c in cols}
        if len(cols) >= 4 and names & REQUIRED_ANY and (best is None or len(cols) > len(best[1])):
            best = (i, cols)
    return best


def _boundaries(cols: list[ColumnSpec]) -> list[tuple[str, float, float]]:
    ordered = sorted(cols, key=lambda c: c.x0)
    bounds: list[tuple[str, float, float]] = []
    for k, col in enumerate(ordered):
        left = -1.0 if k == 0 else (ordered[k - 1].x1 + col.x0) / 2
        if k > 0 and ordered[k - 1].name in NUMERIC_COLUMNS | {"unit"}:
            # numeric cells are often right-aligned under their header -> favour the left edge
            left = col.x0 - 0.004
        right = 2.0 if k == len(ordered) - 1 else ordered[k + 1].x0 - 0.004
        bounds.append((col.name, left, right))
    return bounds


def _assign(line: Line, bounds: list[tuple[str, float, float]]) -> dict[str, list[Word]]:
    cells: dict[str, list[Word]] = {}
    for w in line.words:
        for name, left, right in bounds:
            if left <= w.x0 < right:
                cells.setdefault(name, []).append(w)
                break
    return cells


def _row_text(cells: dict[str, list[Word]], name: str) -> str | None:
    words = cells.get(name)
    return " ".join(w.text for w in words).strip() if words else None


def _is_total_row(line: Line) -> bool:
    return any(w.norm.rstrip(":.") in {"TOTAL", "GRAND", "SUBTOTAL"} for w in line.words)


def _parse_rows(
    lines: list[Line], start: int, step: int, cols: list[ColumnSpec], page_no: int, header: Line
) -> tuple[list[ExtractedItem], Decimal | None]:
    bounds = _boundaries(cols)
    x_lo = min(c.x0 for c in cols) - 0.02
    x_hi = max(c.x1 for c in cols) + 0.06
    spacing = [abs(lines[k + 1].cy - lines[k].cy) for k in range(max(start - 1, 0), min(start + 6, len(lines) - 1))]
    typical_gap = statistics.median(spacing) if spacing else 0.015
    max_gap = max(typical_gap * 2.6, header.y1 - header.y0 + 0.006)
    items: list[ExtractedItem] = []
    source_total: Decimal | None = None
    last_y = header.cy
    k = start
    while 0 <= k < len(lines):
        line = lines[k]
        k += step
        in_table = [w for w in line.words if x_lo <= w.cx <= x_hi]
        if not in_table:
            continue
        if abs(line.cy - last_y) > max_gap:
            break
        row_line = Line(in_table)
        last_y = line.cy
        if _is_total_row(row_line):
            cells = _assign(row_line, bounds)
            source_total = parse_number(_row_text(cells, "total_weight") or row_line.words[-1].text)
            break
        if _match_columns(row_line) and len(_match_columns(row_line)) >= 3:
            break  # another table header
        cells = _assign(row_line, bounds)
        item_no = _row_text(cells, "item_number")
        qty = parse_number(_row_text(cells, "quantity"))
        desc = _row_text(cells, "description") or ""
        if item_no is None and qty is None:
            if items and desc:
                # wrapped description (vertically merged cell) belongs to the previous row
                if step > 0:
                    items[-1].description = f"{items[-1].description} {desc}".strip()
                else:
                    items[-1].description = f"{desc} {items[-1].description}".strip()
                continue
            if not items:
                continue
            break
        conf = statistics.fmean(w.conf for w in in_table)
        item = ExtractedItem(
            line_no=0,
            item_number=item_no,
            part_number=clean_code(_row_text(cells, "part_number")),
            description=desc,
            material=_row_text(cells, "material"),
            grade=_row_text(cells, "grade"),
            specification=_row_text(cells, "specification"),
            quantity=qty if qty is not None else Decimal("1"),
            unit=(_row_text(cells, "unit") or "EA").upper(),
            unit_weight_kg=parse_number(_row_text(cells, "unit_weight")),
            total_weight_kg=parse_number(_row_text(cells, "total_weight")) or Decimal("0"),
            raw_total_weight_kg=parse_number(_row_text(cells, "total_weight")),
            sheet_no=page_no + 1,
            confidence=conf * (1.0 if qty is not None else 0.7),
        )
        items.append(item)
    if step < 0:
        items.reverse()
    return items, source_total


def _inherit_merged_cells(items: list[ExtractedItem]) -> None:
    for prev, cur in itertools.pairwise(items):
        for attr in ("material", "grade", "specification"):
            value = getattr(cur, attr)
            if value is None or value.upper() in DITTO:
                inherited = getattr(prev, attr)
                if inherited is not None and value is not None:
                    setattr(cur, attr, inherited)  # explicit ditto mark
                elif inherited is not None and cur.part_number and prev.part_number:
                    setattr(cur, attr, inherited)  # vertically merged cell
                    cur.confidence *= 0.95


def _table_extent(lines: list[Line], hi: int, cols: list[ColumnSpec]) -> BBox:
    """Approximate table area: from the header to the last contiguous line in the column span."""
    x0 = max(min(c.x0 for c in cols) - 0.03, 0.0)
    x1 = min(max(c.x1 for c in cols) + 0.08, 1.0)
    header = lines[hi]
    y_end = header.y1
    for line in lines[hi + 1 :]:
        if not any(x0 <= w.cx <= x1 for w in line.words):
            continue
        if line.y0 - y_end > 0.06:
            break
        y_end = line.y1
    return BBox(x0, max(header.y0 - 0.01, 0.0), x1, min(y_end + 0.04, 1.0))


def refine_scanned_page(page: PageContent, backend: OCRBackend | None) -> PageContent:
    """Re-OCR a scanned page's BOM table in block mode at 2x — sparse OCR drops short numeric cells."""
    if backend is None or page.image is None or not page.is_scanned:
        return page
    lines = group_lines(page.words)
    found = find_header(lines)
    if not found:
        return page
    box = _table_extent(lines, found[0], found[1])
    table_words = recognize_region(backend, page.image, (box.x0, box.y0, box.x1, box.y1))
    if not table_words:
        return page
    kept = [w for w in page.words if not box.contains(w)]
    for w in table_words:
        w.page = page.index
    return PageContent(
        page.index,
        page.width,
        page.height,
        kept + table_words,
        True,
        page.rotation,
        page.engine,
        {**page.meta, "table_refined": True},
        page.image,
    )


def extract_bom(pages: list[PageContent], backend: OCRBackend | None = None) -> BOMExtraction:
    result = BOMExtraction()
    confs: list[float] = []
    for page in pages:
        page = refine_scanned_page(page, backend)
        lines = group_lines(page.words)
        found = find_header(lines)
        if not found:
            continue
        hi, cols = found
        header = lines[hi]
        body_start = hi + 1
        # skip the second line of a stacked header
        if (
            body_start < len(lines)
            and len(_match_columns(lines[body_start])) == 0
            and all(
                w.norm.rstrip(":.()") in {"WT", "WEIGHT", "NO", "KG", "(KG)", "MASS"} for w in lines[body_start].words
            )
        ):
            body_start += 1
        items, total = _parse_rows(lines, body_start, 1, cols, page.index, header)
        if not items:
            items, total = _parse_rows(lines, hi - 1, -1, cols, page.index, header)
        if not items:
            continue
        result.items.extend(items)
        result.columns = sorted({c.name for c in cols} | set(result.columns))
        if total is not None:
            result.source_total_weight_kg = total
        result.page_from = page.index + 1 if result.page_from is None else result.page_from
        result.page_to = page.index + 1
    _inherit_merged_cells(result.items)
    for n, item in enumerate(result.items, 1):
        item.line_no = n
        raw_total = item.raw_total_weight_kg
        uw, tw, calculated = reconcile_weights(
            item.quantity, item.unit_weight_kg, raw_total, item.description, item.material, item.grade, item.unit
        )
        item.unit_weight_kg, item.total_weight_kg, item.weight_calculated = uw, tw, calculated
        confs.append(item.confidence)
    result.confidence = round(statistics.fmean(confs), 4) if confs else 0.0
    return result
