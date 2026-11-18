"""Load native or scanned PDF drawings into normalised ``PageContent`` objects.

* Native PDFs: words and positions come straight from the PDF text layer (PyMuPDF).
* Scanned PDFs: pages are rasterised, deskewed, orientation-corrected and OCR'd.
* Rotated drawings: both page ``/Rotate`` and rotated text content are normalised so that text
  reads left-to-right for downstream parsing.
"""

from __future__ import annotations

import logging
import math
from collections import Counter
from pathlib import Path

import cv2
import numpy as np
import pymupdf as fitz

from app.core.config import settings
from app.services.ocr.backends import OCRBackend, get_backend
from app.services.ocr.layout import PageContent, Word

logger = logging.getLogger(__name__)


def rotate_bbox(x0: float, y0: float, x1: float, y1: float, angle: int) -> tuple[float, float, float, float]:
    """Rotate a normalised bbox so that content written at ``angle`` degrees reads horizontally.

    ``angle`` is the counter-clockwise direction of the text baseline (0, 90, 180, 270).
    """
    angle %= 360
    if angle == 0:
        return x0, y0, x1, y1
    corners = [(x0, y0), (x1, y0), (x0, y1), (x1, y1)]
    if angle == 90:  # text runs bottom->top
        pts = [(1 - y, x) for x, y in corners]
    elif angle == 180:
        pts = [(1 - x, 1 - y) for x, y in corners]
    elif angle == 270:  # text runs top->bottom
        pts = [(y, 1 - x) for x, y in corners]
    else:  # pragma: no cover - only right angles supported
        return x0, y0, x1, y1
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def dominant_text_angle(page: fitz.Page) -> int:
    """Return the dominant text direction in degrees (CCW) weighted by characters."""
    counts: Counter[int] = Counter()
    for block in page.get_text("dict").get("blocks", []):
        for line in block.get("lines", []):
            dx, dy = line.get("dir", (1, 0))
            angle = round(math.degrees(math.atan2(-dy, dx)) / 90.0) * 90 % 360
            chars = sum(len(s.get("text", "")) for s in line.get("spans", []))
            counts[angle] += chars
    return counts.most_common(1)[0][0] if counts else 0


def _native_words(page: fitz.Page, index: int) -> tuple[list[Word], int]:
    rect = page.rect
    angle = dominant_text_angle(page)
    words: list[Word] = []
    for x0, y0, x1, y1, text, *_ in page.get_text("words"):
        nx0, ny0, nx1, ny1 = x0 / rect.width, y0 / rect.height, x1 / rect.width, y1 / rect.height
        nx0, ny0, nx1, ny1 = rotate_bbox(nx0, ny0, nx1, ny1, angle)
        words.append(Word(text, nx0, ny0, nx1, ny1, 1.0, index))
    return words, angle


def deskew(gray: np.ndarray) -> tuple[np.ndarray, float]:
    """Correct small skew (< 10°) of a scanned page using the min-area rectangle of ink pixels."""
    inv = cv2.bitwise_not(gray)
    _, thresh = cv2.threshold(inv, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
    coords = np.column_stack(np.where(thresh > 0))
    if coords.shape[0] < 500:
        return gray, 0.0
    angle = cv2.minAreaRect(coords.astype(np.float32))[-1]
    if angle > 45:
        angle -= 90
    if abs(angle) < 0.3 or abs(angle) > 10:
        return gray, 0.0
    h, w = gray.shape
    matrix = cv2.getRotationMatrix2D((w / 2, h / 2), angle, 1.0)
    rotated = cv2.warpAffine(gray, matrix, (w, h), flags=cv2.INTER_CUBIC, borderValue=255)
    return rotated, float(angle)


def _rotate_image(image: np.ndarray, angle: int) -> np.ndarray:
    """Rotate image clockwise by ``angle`` (multiple of 90)."""
    angle %= 360
    if angle == 90:
        return cv2.rotate(image, cv2.ROTATE_90_CLOCKWISE)
    if angle == 180:
        return cv2.rotate(image, cv2.ROTATE_180)
    if angle == 270:
        return cv2.rotate(image, cv2.ROTATE_90_COUNTERCLOCKWISE)
    return image


def _score(words: list[Word]) -> float:
    good = [w for w in words if len(w.text) >= 2 and any(c.isalnum() for c in w.text)]
    return sum(w.conf for w in good)


def correct_orientation(gray: np.ndarray, backend: OCRBackend) -> tuple[np.ndarray, int, list[Word] | None]:
    """Find the 0/90/180/270 rotation giving readable text. Returns (image, rotation, words?)."""
    osd = backend.detect_orientation(gray)
    if osd is not None:
        return _rotate_image(gray, osd), osd, None
    # Score candidate orientations on a downscaled copy to keep this cheap.
    scale = 1600 / max(gray.shape)
    small = cv2.resize(gray, None, fx=scale, fy=scale) if scale < 1 else gray
    best_angle, best_score = 0, -1.0
    for angle in (0, 90, 270, 180):
        score = _score(backend.recognize(_rotate_image(small, angle)))
        if score > best_score:
            best_angle, best_score = angle, score
        if angle == 0 and score > 40:  # clearly upright; skip the remaining passes
            break
    return _rotate_image(gray, best_angle), best_angle, None


def render_page(page: fitz.Page, dpi: int) -> np.ndarray:
    pix = page.get_pixmap(dpi=dpi, colorspace=fitz.csGRAY, alpha=False)
    return np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width).copy()


def load_document(
    path: str | Path,
    *,
    backend: OCRBackend | None = None,
    use_default_backend: bool = True,
    dpi: int | None = None,
    max_pages: int = 50,
) -> list[PageContent]:
    """Read every page of ``path`` into normalised words, OCR'ing pages without a text layer."""
    dpi = dpi or settings.OCR_DPI
    if backend is None and use_default_backend:
        backend = get_backend(settings.OCR_ENGINE)
    pages: list[PageContent] = []
    with fitz.open(path) as doc:
        for index, page in enumerate(doc):
            if index >= max_pages:
                logger.warning("%s: page limit %s reached", path, max_pages)
                break
            words, angle = _native_words(page, index)
            native_chars = sum(len(w.text) for w in words)
            if native_chars >= settings.OCR_MIN_NATIVE_CHARS:
                pages.append(PageContent(index, page.rect.width, page.rect.height, words, False, angle + page.rotation))
                continue
            if backend is None:
                pages.append(PageContent(index, page.rect.width, page.rect.height, words, True, page.rotation, "none"))
                continue
            gray = render_page(page, dpi)
            gray, skew = deskew(gray)
            gray, rotation, _ = correct_orientation(gray, backend)
            ocr_words = backend.recognize(gray)
            for w in ocr_words:
                w.page = index
            pages.append(
                PageContent(
                    index,
                    gray.shape[1],
                    gray.shape[0],
                    ocr_words,
                    True,
                    rotation,
                    backend.name,
                    {"skew": skew, "dpi": dpi},
                    gray,
                )
            )
    return pages


def page_count(path: str | Path) -> int:
    with fitz.open(path) as doc:
        return doc.page_count
