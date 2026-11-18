"""Label/value parsing and validation of title-block fields."""

from __future__ import annotations

import re
import statistics
from dataclasses import dataclass
from datetime import datetime

from app.services.ocr.layout import Line, Word, normalize_token

# Ordered by specificity: longer label sequences first so "DRAWING NO" beats "DRAWING".
LABELS: dict[str, list[str]] = {
    "drawing_number": [
        "DRAWING NUMBER",
        "DRAWING NO",
        "DRAWING NO.",
        "DWG NO",
        "DWG. NO",
        "DWG NO.",
        "DWG. NO.",
        "DRG NO",
        "DRG. NO",
        "DOCUMENT NO",
        "DOC NO",
        "DOC. NO",
        "DRAWING #",
        "DWG #",
    ],
    "title": ["DRAWING TITLE", "TITLE"],
    "revision": ["REVISION", "REV NO", "REV. NO", "REV", "REV."],
    "date": ["ISSUE DATE", "DATE"],
    "status": ["ISSUE STATUS", "PURPOSE OF ISSUE", "STATUS"],
    "scale": ["SCALE"],
    "sheet_number": ["SHEET NO", "SHEET NO.", "SHT NO", "SHEET", "SHT"],
    "drawn_by": ["DRAWN BY", "DRAWN", "DRN", "DRN."],
    "checker": ["CHECKED BY", "CHECKED", "CHKD", "CHK", "CHK."],
    "approver": ["APPROVED BY", "APPROVED", "APPD", "APP", "APPR", "APPR."],
    "vendor": ["VENDOR", "SUPPLIER", "MANUFACTURER", "CONTRACTOR", "SUBCONTRACTOR"],
    "project": ["PROJECT NAME", "PROJECT"],
    "client": ["CLIENT"],
}

FIELD_WEIGHTS = {
    "drawing_number": 3.0,
    "title": 2.0,
    "revision": 2.0,
    "date": 1.0,
    "status": 1.0,
    "scale": 1.0,
    "sheet_number": 0.5,
}

_LABEL_SEQS: list[tuple[str, tuple[str, ...]]] = sorted(
    ((field, tuple(normalize_token(t) for t in label.split())) for field, labels in LABELS.items() for label in labels),
    key=lambda item: -len(item[1]),
)
ALL_LABEL_TOKENS = {tok.rstrip(":.") for _, seq in _LABEL_SEQS for tok in seq}

STATUS_MAP = {
    "IFC": "IFC",
    "ISSUED FOR CONSTRUCTION": "IFC",
    "AFC": "AFC",
    "APPROVED FOR CONSTRUCTION": "AFC",
    "IFA": "IFA",
    "ISSUED FOR APPROVAL": "IFA",
    "IFR": "IFR",
    "ISSUED FOR REVIEW": "IFR",
    "IFI": "IFR",
    "ISSUED FOR INFORMATION": "IFR",
    "AS-BUILT": "AS-BUILT",
    "AS BUILT": "AS-BUILT",
    "ASBUILT": "AS-BUILT",
    "PRELIMINARY": "PRELIM",
    "PRELIM": "PRELIM",
    "SUPERSEDED": "SUPERSEDED",
    "VOID": "VOID",
}

DRAWING_NO_RE = re.compile(r"^(?=.*\d)[A-Z0-9][A-Z0-9_.]*(?:[-/][A-Z0-9_.]+){2,}$")
REVISION_RE = re.compile(r"^(?:[A-Z]{1,2}|[A-Z]?\d{1,3}|[A-Z]\d{1,2}[A-Z]?)$")
SCALE_RE = re.compile(r"^(?:1\s*:\s*\d+(?:\.\d+)?|\d+\s*:\s*1|NTS|N\.T\.S\.?|AS SHOWN|NONE|FULL)$")
SHEET_RE = re.compile(r"^\d{1,3}(?:\s*(?:OF|/)\s*\d{1,3})?$")
_DATE_FORMATS = (
    "%Y-%m-%d",
    "%d-%m-%Y",
    "%d/%m/%Y",
    "%d.%m.%Y",
    "%Y/%m/%d",
    "%d-%b-%Y",
    "%d %b %Y",
    "%d-%b-%y",
    "%d/%m/%y",
    "%b %d %Y",
    "%d %B %Y",
    "%m/%d/%Y",
)
DATE_TOKEN_RE = re.compile(
    r"\b(\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}|\d{1,2}[- ][A-Z]{3}[- ]\d{2,4})\b"
)


@dataclass
class FieldValue:
    value: str
    confidence: float
    words: list[Word]
    method: str  # label | pattern | filename


def parse_date(text: str) -> str | None:
    cleaned = text.strip().upper().replace(",", "")
    for fmt in _DATE_FORMATS:
        try:
            return datetime.strptime(cleaned.title() if "%b" in fmt or "%B" in fmt else cleaned, fmt).date().isoformat()
        except ValueError:
            continue
    return None


def normalize_status(text: str) -> str | None:
    up = re.sub(r"\s+", " ", text.upper().strip(" .:"))
    if up in STATUS_MAP:
        return STATUS_MAP[up]
    for key, value in STATUS_MAP.items():
        if re.search(rf"\b{re.escape(key)}\b", up):
            return value
    return None


def validate(field: str, value: str) -> tuple[str | None, bool]:
    """Return (normalised value, is_valid)."""
    v = value.strip(" :;,")
    up = v.upper()
    if not v:
        return None, False
    if field == "drawing_number":
        token = up.replace(" ", "")
        return token, bool(DRAWING_NO_RE.match(token))
    if field == "revision":
        token = re.sub(r"^REV(?:ISION)?\.?\s*", "", up).replace(" ", "")
        raw = re.sub(r"^REV(?:ISION)?\.?\s*", "", v, flags=re.I).replace(" ", "")
        # OCR often echoes a lone glyph as "Cc"/"Bb": drawings are upper-case, so collapse it.
        if len(raw) == 2 and raw[0].isupper() and raw[1].islower() and raw[0] == raw[1].upper():
            token = raw[0]
        return token, bool(REVISION_RE.match(token))
    if field == "scale":
        token = re.sub(r"\s*:\s*", ":", up)
        return token, bool(SCALE_RE.match(token))
    if field == "date":
        iso = parse_date(v)
        return (iso or v), iso is not None
    if field == "status":
        status = normalize_status(v)
        return (status or up), status is not None
    if field == "sheet_number":
        token = re.sub(r"(\d)\s*(OF|/)\s*(\d)", r"\1 \2 \3", re.sub(r"\s+", " ", up))
        return token, bool(SHEET_RE.match(token))
    if field == "title":
        return re.sub(r"\s+", " ", up), len(v) >= 4 and sum(c.isalpha() for c in v) >= 3
    return v, len(v) >= 1


def _match_label(line: Line, start: int) -> tuple[str, int] | None:
    tokens = [w.norm.rstrip(":") for w in line.words]
    for field, seq in _LABEL_SEQS:
        n = len(seq)
        if start + n > len(tokens):
            continue
        window = tokens[start : start + n]
        if all(a.rstrip(":.") == b.rstrip(":.") for a, b in zip(window, seq, strict=True)):
            return field, n
    return None


def _is_label_start(line: Line, index: int) -> bool:
    return _match_label(line, index) is not None


def _value_conf(words: list[Word]) -> float:
    return statistics.fmean(w.conf for w in words) if words else 0.0


def extract_labelled_fields(lines: list[Line], gap: float = 0.06) -> dict[str, list[FieldValue]]:
    """Find label tokens and read their values from the same line (right) or the cell below."""
    found: dict[str, list[FieldValue]] = {}
    for li, line in enumerate(lines):
        i = 0
        while i < len(line.words):
            match = _match_label(line, i)
            if match is None:
                i += 1
                continue
            field, n = match
            label_words = line.words[i : i + n]
            j = i + n
            value_words: list[Word] = []
            prev = label_words[-1]
            while j < len(line.words):
                w = line.words[j]
                if w.text in (":", "-", "=") and not value_words:
                    prev = w
                    j += 1
                    continue
                if w.x0 - prev.x1 > gap or _is_label_start(line, j):
                    break
                value_words.append(w)
                prev = w
                j += 1
            method = "label"
            if not value_words:
                value_words = _value_below(lines, li, label_words, field)
                method = "label_below"
            if value_words:
                text = " ".join(w.text for w in value_words).strip(" :")
                found.setdefault(field, []).append(FieldValue(text, _value_conf(value_words), value_words, method))
            i = j if j > i + n - 1 else i + n
    return found


def _value_below(lines: list[Line], li: int, label_words: list[Word], field: str) -> list[Word]:
    lx0 = label_words[0].x0
    lx1 = max(label_words[-1].x1, lx0 + 0.04)
    label_h = max(w.height for w in label_words)
    collected: list[Word] = []
    max_lines = 3 if field == "title" else 1
    last_y = label_words[0].y1
    for line in lines[li + 1 :]:
        if line.y0 - last_y > max(label_h * 4, 0.03):
            break
        cell = [w for w in line.words if w.x1 >= lx0 - 0.01]
        if not cell or cell[0].x0 > lx1 + 0.12:
            continue
        # stop at the next row of labels
        if any(w.norm.rstrip(":.") in ALL_LABEL_TOKENS and w.x0 <= lx1 for w in cell[:1]):
            break
        # keep only contiguous words starting near the label's left edge
        segment: list[Word] = []
        for w in cell:
            if segment and w.x0 - segment[-1].x1 > 0.05:
                break
            segment.append(w)
        collected.extend(segment)
        last_y = line.y1
        if len(collected) and max_lines == 1:
            break
        max_lines -= 1
        if max_lines == 0:
            break
    return collected


def pattern_candidates(words: list[Word]) -> dict[str, list[FieldValue]]:
    """Regex-only fallbacks (no label) used when a field is not labelled in the title block."""
    found: dict[str, list[FieldValue]] = {}
    for w in words:
        token = w.text.upper().strip(" :;,")
        if DRAWING_NO_RE.match(token) and len(token) >= 8:
            found.setdefault("drawing_number", []).append(FieldValue(token, w.conf, [w], "pattern"))
        if re.fullmatch(r"1:\d+(?:\.\d+)?", token):
            found.setdefault("scale", []).append(FieldValue(token, w.conf, [w], "pattern"))
        if DATE_TOKEN_RE.fullmatch(token) and parse_date(token):
            found.setdefault("date", []).append(FieldValue(token, w.conf, [w], "pattern"))
        if token in STATUS_MAP:
            found.setdefault("status", []).append(FieldValue(token, w.conf, [w], "pattern"))
    for cands in found.values():
        cands.sort(key=lambda c: (-len(c.value), -c.confidence))
    return found


def choose(field: str, candidates: list[FieldValue]) -> tuple[str | None, float, FieldValue | None]:
    """Pick the best value for ``field``.

    Candidates are grouped by normalised value; independent sources that agree (e.g. the title
    block, the revision table and the file name) corroborate each other, combined as
    ``1 - prod(1 - c_i)`` over distinct extraction methods.
    """
    groups: dict[str, list[tuple[float, FieldValue]]] = {}
    valid_by_value: dict[str, bool] = {}
    for cand in candidates:
        value, valid = validate(field, cand.value)
        if value is None:
            continue
        method_factor = METHOD_FACTORS.get(cand.method, 0.7)
        conf = cand.confidence * method_factor * (1.0 if valid else 0.55)
        groups.setdefault(value, []).append((conf, cand))
        valid_by_value[value] = valid_by_value.get(value, False) or valid
    best: tuple[str | None, float, FieldValue | None] = (None, 0.0, None)
    for value, scored in groups.items():
        per_method: dict[str, float] = {}
        for conf, cand in scored:
            per_method[cand.method] = max(per_method.get(cand.method, 0.0), conf)
        if len(per_method) > 1 and valid_by_value[value]:
            miss = 1.0
            for conf in per_method.values():
                miss *= 1.0 - conf
            combined = min(1.0 - miss, 0.995)
        else:
            combined = max(per_method.values())
        top = max(scored, key=lambda item: item[0])[1]
        if combined > best[1]:
            best = (value, combined, top)
    return best


METHOD_FACTORS = {"label": 1.0, "label_below": 0.97, "revision_block": 0.9, "pattern": 0.8, "filename": 0.75}
