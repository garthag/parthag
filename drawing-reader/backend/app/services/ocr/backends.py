"""OCR backends (PaddleOCR, Tesseract) behind a common interface.

Each backend receives a grayscale/BGR ``numpy`` image and returns ``Word`` objects with
coordinates normalised to the image size and confidences in 0..1.
"""

from __future__ import annotations

import logging
import shutil
from functools import lru_cache
from typing import Protocol

import numpy as np

from app.services.ocr.layout import Word

logger = logging.getLogger(__name__)


class OCRBackend(Protocol):
    name: str

    def recognize(self, image: np.ndarray, mode: str = "sparse") -> list[Word]: ...

    def detect_orientation(self, image: np.ndarray) -> int | None: ...


class TesseractBackend:
    name = "tesseract"

    def __init__(self) -> None:
        import pytesseract

        self._tess = pytesseract

    def recognize(self, image: np.ndarray, mode: str = "sparse") -> list[Word]:
        """``sparse`` (psm 11) suits whole drawings; ``block`` (psm 6) suits table crops."""
        h, w = image.shape[:2]
        psm = 6 if mode == "block" else 11
        data = self._tess.image_to_data(image, output_type=self._tess.Output.DICT, config=f"--psm {psm} --oem 1")
        words: list[Word] = []
        for i, text in enumerate(data["text"]):
            text = (text or "").strip()
            conf = float(data["conf"][i])
            if not text or conf < 0:
                continue
            x, y, bw, bh = (data[k][i] for k in ("left", "top", "width", "height"))
            words.append(Word(text, x / w, y / h, (x + bw) / w, (y + bh) / h, conf / 100.0))
        return words

    def detect_orientation(self, image: np.ndarray) -> int | None:
        try:
            osd = self._tess.image_to_osd(image, output_type=self._tess.Output.DICT)
        except Exception:  # noqa: BLE001 - OSD fails on sparse drawings; fall back to scoring
            return None
        if float(osd.get("orientation_conf", 0)) < 1.5:
            return None
        return int(osd.get("rotate", 0)) % 360


class PaddleBackend:
    name = "paddle"

    def __init__(self) -> None:
        from paddleocr import PaddleOCR  # type: ignore[import-not-found]

        self._ocr = PaddleOCR(use_angle_cls=True, lang="en", show_log=False)

    def recognize(self, image: np.ndarray, mode: str = "sparse") -> list[Word]:
        h, w = image.shape[:2]
        result = self._ocr.ocr(image, cls=True) or []
        words: list[Word] = []
        for page in result:
            for box, (text, conf) in page or []:
                xs = [p[0] for p in box]
                ys = [p[1] for p in box]
                x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
                tokens = text.split()
                if not tokens:
                    continue
                # Paddle returns line-level boxes; distribute width proportionally to characters.
                total_chars = sum(len(t) for t in tokens) + len(tokens) - 1
                cursor = x0
                for tok in tokens:
                    tw = (x1 - x0) * len(tok) / max(total_chars, 1)
                    words.append(Word(tok, cursor / w, y0 / h, (cursor + tw) / w, y1 / h, float(conf)))
                    cursor += tw + (x1 - x0) / max(total_chars, 1)
        return words

    def detect_orientation(self, image: np.ndarray) -> int | None:
        return None  # handled by the angle classifier inside recognize()


def recognize_region(
    backend: OCRBackend, image: np.ndarray, box: tuple[float, float, float, float], upscale: float = 2.0
) -> list[Word]:
    """OCR a normalised sub-region in block mode at higher magnification; map words back to page."""
    import cv2

    h, w = image.shape[:2]
    x0, y0, x1, y1 = (max(0.0, min(1.0, v)) for v in box)
    px0, py0, px1, py1 = int(x0 * w), int(y0 * h), int(x1 * w), int(y1 * h)
    if px1 - px0 < 10 or py1 - py0 < 10:
        return []
    crop = image[py0:py1, px0:px1]
    if upscale != 1.0:
        crop = cv2.resize(crop, None, fx=upscale, fy=upscale, interpolation=cv2.INTER_CUBIC)
    cw, ch = x1 - x0, y1 - y0
    words = backend.recognize(crop, mode="block")
    for word in words:
        word.x0, word.x1 = x0 + word.x0 * cw, x0 + word.x1 * cw
        word.y0, word.y1 = y0 + word.y0 * ch, y0 + word.y1 * ch
    return words


@lru_cache(maxsize=4)
def get_backend(preference: str = "auto") -> OCRBackend | None:
    """Return the best available backend for ``preference`` (auto|paddle|tesseract|none)."""
    if preference == "none":
        return None
    candidates = ["paddle", "tesseract"] if preference == "auto" else [preference]
    for name in candidates:
        try:
            if name == "paddle":
                return PaddleBackend()
            if name == "tesseract" and shutil.which("tesseract"):
                return TesseractBackend()
        except Exception as exc:  # noqa: BLE001 - optional dependency
            logger.info("OCR backend %s unavailable: %s", name, exc)
    logger.warning("No OCR backend available; scanned drawings cannot be read")
    return None
