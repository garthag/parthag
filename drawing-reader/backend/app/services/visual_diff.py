"""Raster comparison of two drawing revisions: changed regions and revision-cloud detection."""

from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np
import pymupdf as fitz

from app.services.ocr.pdf_loader import render_page

DIFF_DPI = 100


def _render(path: str | Path, page: int, dpi: int = DIFF_DPI) -> np.ndarray:
    with fitz.open(path) as doc:
        if page >= doc.page_count:
            page = doc.page_count - 1
        return render_page(doc[page], dpi)


def _align(a: np.ndarray, b: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    h, w = min(a.shape[0], b.shape[0]), min(a.shape[1], b.shape[1])
    if a.shape != b.shape:
        b = cv2.resize(b, (a.shape[1], a.shape[0]))
        h, w = a.shape
    return a[:h, :w], b[:h, :w]


def changed_regions(a: np.ndarray, b: np.ndarray, min_area: float = 0.00002) -> tuple[list[dict], float]:
    """Bounding boxes (normalised) of pixel regions that differ between two renders."""
    a, b = _align(a, b)
    diff = cv2.absdiff(a, b)
    _, mask = cv2.threshold(diff, 40, 255, cv2.THRESH_BINARY)
    changed_ratio = float(np.count_nonzero(mask)) / mask.size
    mask = cv2.dilate(mask, cv2.getStructuringElement(cv2.MORPH_RECT, (15, 15)), iterations=2)
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    h, w = mask.shape
    regions = []
    for c in contours:
        x, y, bw, bh = cv2.boundingRect(c)
        if bw * bh / (w * h) < min_area:
            continue
        regions.append({"x": x / w, "y": y / h, "width": bw / w, "height": bh / h, "kind": "change", "score": 1.0})
    regions.sort(key=lambda r: -(r["width"] * r["height"]))
    return regions[:200], round(changed_ratio, 6)


def detect_revision_clouds(gray: np.ndarray) -> list[dict]:
    """Detect revision clouds: closed, thin outlines made of many similar convex arcs.

    1. Remove straight horizontal/vertical strokes (grid lines, members, frames) which would
       otherwise merge with a cloud outline.
    2. Close small gaps so the arcs form one contour.
    3. Keep contours whose boundary has >= 8 regularly spaced convexity defects (the cusps between
       scallops) and whose ink density is low (an outline, not a filled text block).
    """
    _, binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    h, w = gray.shape
    line_len = max(15, int(min(w, h) * 0.03))
    horiz = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (line_len, 1)))
    vert = cv2.morphologyEx(binary, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (1, line_len)))
    arcs = cv2.subtract(binary, cv2.add(horiz, vert))
    k = max(5, int(w * 0.008))
    closed = cv2.morphologyEx(arcs, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    contours, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    found: list[dict] = []
    for c in contours:
        x, y, bw, bh = cv2.boundingRect(c)
        if bw < 0.03 * w or bh < 0.03 * h or bw > 0.9 * w or bh > 0.9 * h:
            continue
        hull_idx = cv2.convexHull(c, returnPoints=False)
        if hull_idx is None or len(hull_idx) < 4:
            continue
        try:
            defects = cv2.convexityDefects(c, hull_idx)
        except cv2.error:
            continue
        if defects is None:
            continue
        depths = defects.reshape(-1, 4)[:, 3] / 256.0
        cusps = depths[depths > 1.5]
        if len(cusps) < 8:
            continue
        regularity = float(np.std(cusps) / (np.mean(cusps) + 1e-6))
        density = np.count_nonzero(arcs[y : y + bh, x : x + bw]) / float(bw * bh)
        if regularity > 0.5 or density > 0.08:
            continue
        found.append(
            {
                "x": x / w,
                "y": y / h,
                "width": bw / w,
                "height": bh / h,
                "kind": "cloud",
                "score": round(min(1.0, len(cusps) / 20) * (1 - regularity), 3),
            }
        )
    return found


def visual_diff(from_path: str | Path, to_path: str | Path, page: int = 0) -> dict:
    a = _render(from_path, page)
    b = _render(to_path, page)
    regions, ratio = changed_regions(a, b)
    clouds = detect_revision_clouds(b)
    return {"page": page, "regions": regions + clouds, "changed_ratio": ratio}
