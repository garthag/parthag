"""Detection of title block, revision block and drawing notes regions."""

from __future__ import annotations

import re
from dataclasses import dataclass, field

import cv2
import numpy as np

from app.services.ocr.fields import ALL_LABEL_TOKENS, REVISION_RE, parse_date, validate
from app.services.ocr.layout import BBox, Line, Word, group_lines

TITLE_ANCHORS = {
    "DRAWING",
    "DWG",
    "DRG",
    "TITLE",
    "REV",
    "REVISION",
    "SCALE",
    "SHEET",
    "SHT",
    "DATE",
    "CHECKED",
    "CHKD",
    "CHK",
    "APPROVED",
    "APPD",
    "DRAWN",
    "PROJECT",
    "CLIENT",
    "STATUS",
    "DOCUMENT",
}


@dataclass
class RegionResult:
    title_block: BBox | None = None
    title_block_score: float = 0.0
    revision_block: BBox | None = None
    notes_block: BBox | None = None
    revision_rows: list[dict[str, str | None]] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "title_block": self.title_block.to_dict() if self.title_block else None,
            "title_block_score": round(self.title_block_score, 3),
            "revision_block": self.revision_block.to_dict() if self.revision_block else None,
            "notes_block": self.notes_block.to_dict() if self.notes_block else None,
        }


def _anchor_words(words: list[Word]) -> list[Word]:
    return [w for w in words if w.norm.rstrip(":.") in TITLE_ANCHORS]


def detect_title_block(words: list[Word], image: np.ndarray | None = None) -> tuple[BBox | None, float]:
    """Locate the title block by clustering label anchors, refined by ruled rectangles if an image exists.

    Title blocks of different formats (ISO 7200 bottom-right, ANSI strip along the bottom, vendor
    blocks on the right edge) all share a dense cluster of label keywords; the densest cluster wins
    with a mild bias towards the bottom-right corner where most standards place it.
    """
    anchors = _anchor_words(words)
    if not anchors:
        return None, 0.0
    clusters: list[list[Word]] = []
    for w in sorted(anchors, key=lambda a: (a.cy, a.cx)):
        for cluster in clusters:
            if any(abs(w.cx - o.cx) < 0.22 and abs(w.cy - o.cy) < 0.12 for o in cluster):
                cluster.append(w)
                break
        else:
            clusters.append([w])

    def score(cluster: list[Word]) -> float:
        distinct = len({w.norm.rstrip(":.") for w in cluster})
        cx = sum(w.cx for w in cluster) / len(cluster)
        cy = sum(w.cy for w in cluster) / len(cluster)
        return distinct + 1.5 * cx + 2.0 * cy

    best = max(clusters, key=score)
    distinct = len({w.norm.rstrip(":.") for w in best})
    if distinct < 2:
        return None, 0.0
    box = BBox(
        max(min(w.x0 for w in best) - 0.01, 0.0),
        max(min(w.y0 for w in best) - 0.01, 0.0),
        min(max(w.x1 for w in best) + 0.18, 1.0),
        min(max(w.y1 for w in best) + 0.05, 1.0),
    )
    if image is not None:
        ruled = _ruled_rectangle_containing(image, box)
        if ruled is not None:
            box = ruled
    confidence = min(1.0, 0.35 + 0.1 * distinct)
    return box, confidence


def _ruled_rectangle_containing(image: np.ndarray, hint: BBox) -> BBox | None:
    """Find the smallest ruled rectangle (title block frame) that contains the anchor cluster centre."""
    gray = image if image.ndim == 2 else cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    h, w = gray.shape
    _, binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    horiz = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (max(w // 30, 10), 1)))
    vert = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (1, max(h // 30, 10))))
    grid = cv2.add(horiz, vert)
    contours, _ = cv2.findContours(grid, cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
    cx, cy = (hint.x0 + hint.x1) / 2 * w, (hint.y0 + hint.y1) / 2 * h
    best: BBox | None = None
    for contour in contours:
        x, y, bw, bh = cv2.boundingRect(contour)
        area = bw * bh / (w * h)
        if area < 0.01 or area > 0.5:
            continue
        if x <= cx <= x + bw and y <= cy <= y + bh:
            candidate = BBox(x / w, y / h, (x + bw) / w, (y + bh) / h)
            if best is None or candidate.area < best.area:
                best = candidate
    return best


REV_HEADERS = {"REV", "REV.", "REVISION", "NO", "NO."}


def detect_revision_block(lines: list[Line]) -> tuple[BBox | None, list[dict[str, str | None]]]:
    """Find a revision table (REV | DATE | DESCRIPTION | BY | CHK | APP) and parse its rows.

    Revision tables are drawn either top-down or (very commonly, stacked above the title block)
    bottom-up, so rows are collected on both sides of the header.
    """
    for li, line in enumerate(lines):
        tokens = [w.norm.rstrip(":.") for w in line.words]
        if not tokens or tokens[0] not in {"REV", "REVISION", "NO"}:
            continue
        if not ({"DESCRIPTION", "DESC"} & set(tokens)) or not ({"DATE"} & set(tokens)):
            continue
        columns = dict(zip(tokens, line.words, strict=True))
        x_lo, x_hi = line.x0 - 0.01, line.x1 + 0.1
        rows: list[dict[str, str | None]] = []
        used: list[Line] = [line]
        for direction in (range(li + 1, len(lines)), range(li - 1, -1, -1)):
            last_y = line.cy
            for k in direction:
                cand = lines[k]
                if abs(cand.cy - last_y) > 0.035:
                    break
                ws = [w for w in cand.words if x_lo <= w.cx <= x_hi]
                if not ws:
                    continue
                first = ws[0].text.upper().strip(".")
                if not REVISION_RE.match(first) or first in ALL_LABEL_TOKENS:
                    break
                rows.append(_parse_revision_row(ws, columns))
                used.append(cand)
                last_y = cand.cy
            if rows:
                break
        box = BBox(min(u.x0 for u in used), min(u.y0 for u in used), max(u.x1 for u in used), max(u.y1 for u in used))
        return box, rows
    return None, []


def _parse_revision_row(words: list[Word], columns: dict[str, Word]) -> dict[str, str | None]:
    row: dict[str, str | None] = {"revision": validate("revision", words[0].text.strip("."))[0]}
    date_word = next((w for w in words[1:] if parse_date(w.text)), None)
    row["date"] = parse_date(date_word.text) if date_word else None
    desc_col = columns.get("DESCRIPTION") or columns.get("DESC")
    by_cols = [columns[c] for c in ("BY", "DRN", "CHK", "CHKD", "APP", "APPD") if c in columns]
    first_initial_x = min((c.x0 for c in by_cols), default=2.0)
    desc_words = [
        w
        for w in words[1:]
        if w is not date_word and (desc_col is None or w.x0 >= desc_col.x0 - 0.02) and w.x0 < first_initial_x - 0.005
    ]
    row["description"] = " ".join(w.text for w in desc_words) or None
    initials = [w for w in words if w.x0 >= first_initial_x - 0.005]
    for key, word in zip(("drawn_by", "checker", "approver"), initials, strict=False):
        row[key] = word.text
    return row


def detect_notes(lines: list[Line], exclude: list[BBox]) -> tuple[BBox | None, list[str]]:
    """Collect numbered notes following a ``NOTES`` / ``GENERAL NOTES`` heading."""
    for li, line in enumerate(lines):
        heads = [i for i, w in enumerate(line.words) if w.norm.rstrip(":.") == "NOTES"]
        if not heads:
            continue
        head = line.words[heads[0]]
        x_lo, x_hi = head.x0 - 0.02, head.x0 + 0.4
        notes: list[str] = []
        used = [head]
        last_y = line.y1
        for cand in lines[li + 1 :]:
            if cand.y0 - last_y > 0.04:
                break
            ws = [w for w in cand.words if x_lo <= w.x0 <= x_hi and not any(b.contains(w) for b in exclude)]
            if not ws:
                continue
            text = " ".join(w.text for w in ws)
            if re.match(r"^\(?\d{1,2}[.)]", text) or not notes:
                notes.append(re.sub(r"^\(?\d{1,2}[.)]\s*", "", text))
            else:
                notes[-1] = f"{notes[-1]} {text}"
            used.extend(ws)
            last_y = cand.y1
        if notes:
            box = BBox(
                min(w.x0 for w in used), min(w.y0 for w in used), max(w.x1 for w in used), max(w.y1 for w in used)
            )
            return box, notes
    return None, []


def analyse_regions(words: list[Word], image: np.ndarray | None = None) -> RegionResult:
    lines = group_lines(words)
    result = RegionResult()
    result.title_block, result.title_block_score = detect_title_block(words, image)
    result.revision_block, result.revision_rows = detect_revision_block(lines)
    excludes = [b for b in (result.title_block, result.revision_block) if b is not None]
    result.notes_block, result.notes = detect_notes(lines, excludes)
    return result
