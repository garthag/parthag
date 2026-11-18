"""High-level OCR pipeline: PDF -> pages -> regions -> title-block fields (+ confidence)."""

from __future__ import annotations

import logging
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from app.core.config import settings
from app.services.ocr.backends import OCRBackend
from app.services.ocr.fields import (
    FIELD_WEIGHTS,
    LABELS,
    FieldValue,
    choose,
    extract_labelled_fields,
    pattern_candidates,
)
from app.services.ocr.layout import PageContent, group_lines, words_in
from app.services.ocr.pdf_loader import load_document
from app.services.ocr.regions import RegionResult, analyse_regions

logger = logging.getLogger(__name__)

OUTPUT_FIELDS = list(LABELS.keys())


@dataclass
class OCRDocumentResult:
    fields: dict[str, Any]
    confidences: dict[str, float]
    overall_confidence: float
    revision_block: list[dict[str, Any]]
    notes: list[str]
    regions: dict[str, Any]
    rotation: int
    is_scanned: bool
    engine: str
    raw_text: str
    pages: list[PageContent] = field(repr=False, default_factory=list)
    duration_ms: int = 0

    @property
    def needs_review(self) -> bool:
        return self.overall_confidence < settings.OCR_CONFIDENCE_THRESHOLD or not self.fields.get("drawing_number")

    def as_json(self) -> dict[str, Any]:
        return {k: self.fields.get(k) for k in OUTPUT_FIELDS}


FILENAME_RE = re.compile(
    r"^(?P<number>[A-Z0-9][A-Z0-9._-]*?[A-Z0-9])(?:[ _-]+(?:REV|R)[ ._-]?(?P<rev>[A-Z0-9]{1,3}))?$",
    re.IGNORECASE,
)


def parse_filename(filename: str) -> dict[str, str | None]:
    """Derive drawing number / revision hints from names like ``ABC-ST-001_Rev_B.pdf``."""
    stem = Path(filename).stem.strip()
    match = FILENAME_RE.match(stem)
    if not match:
        return {"drawing_number": stem.upper(), "revision": None}
    rev = match.group("rev")
    return {"drawing_number": match.group("number").upper(), "revision": rev.upper() if rev else None}


def _overall(confidences: dict[str, float]) -> float:
    total_w = sum(FIELD_WEIGHTS.values())
    score = sum(FIELD_WEIGHTS[f] * confidences.get(f, 0.0) for f in FIELD_WEIGHTS)
    return round(score / total_w, 4)


def extract_title_block(page: PageContent, regions: RegionResult, filename: str | None = None) -> tuple[dict, dict]:
    words = page.words
    box = regions.title_block
    tb_words = words_in(words, box, tol=0.005) if box else words
    if regions.revision_block is not None:
        # revision table headers (REV/DATE/CHK/APP) are labels of that table, not of the title block
        rev_box = regions.revision_block
        words = [w for w in words if not rev_box.contains(w, 0.003)]
        tb_words = [w for w in tb_words if not rev_box.contains(w, 0.003)]
    candidates: dict[str, list[FieldValue]] = extract_labelled_fields(group_lines(tb_words))
    if box is not None:
        # labels outside the detected block (e.g. a separate vendor box) are still valid but less trusted
        for f, cands in extract_labelled_fields(group_lines(words)).items():
            known = {c.value for c in candidates.get(f, [])}
            for c in cands:
                if c.value not in known:
                    candidates.setdefault(f, []).append(FieldValue(c.value, c.confidence * 0.9, c.words, c.method))
    for f, cands in pattern_candidates(tb_words).items():
        candidates.setdefault(f, []).extend(cands)
    if filename:
        hints = parse_filename(filename)
        for f, v in hints.items():
            if v:
                candidates.setdefault(f, []).append(FieldValue(v, 1.0, [], "filename"))
    if regions.revision_rows and regions.revision_rows[-1].get("revision"):
        latest = regions.revision_rows[-1]
        candidates.setdefault("revision", []).append(FieldValue(str(latest["revision"]), 0.95, [], "revision_block"))
    values: dict[str, Any] = {}
    confidences: dict[str, float] = {}
    for f in OUTPUT_FIELDS:
        value, conf, _ = choose(f, candidates.get(f, []))
        values[f] = value
        confidences[f] = round(conf, 4)
    return values, confidences


def process_pdf(
    path: str | Path,
    *,
    filename: str | None = None,
    backend: OCRBackend | None = None,
    use_default_backend: bool = True,
) -> OCRDocumentResult:
    """Run the full OCR pipeline on a PDF drawing."""
    started = time.perf_counter()
    pages = load_document(path, backend=backend, use_default_backend=use_default_backend)
    if not pages:
        raise ValueError("PDF has no pages")
    first = pages[0]
    regions = analyse_regions(first.words)
    values, confidences = extract_title_block(first, regions, filename)
    # Sheet count from page count when not printed
    if not values.get("sheet_number") and len(pages) > 1:
        values["sheet_number"] = f"1 OF {len(pages)}"
        confidences["sheet_number"] = 0.6
    duration = int((time.perf_counter() - started) * 1000)
    result = OCRDocumentResult(
        fields=values,
        confidences=confidences,
        overall_confidence=_overall(confidences),
        revision_block=regions.revision_rows,
        notes=regions.notes,
        regions=regions.to_dict(),
        rotation=first.rotation % 360,
        is_scanned=any(p.is_scanned for p in pages),
        engine=first.engine,
        raw_text="\n\f".join(p.text for p in pages)[:200_000],
        pages=pages,
        duration_ms=duration,
    )
    logger.info(
        "OCR %s: dwg=%s rev=%s conf=%.2f scanned=%s %dms",
        filename or path,
        values.get("drawing_number"),
        values.get("revision"),
        result.overall_confidence,
        result.is_scanned,
        duration,
    )
    return result
