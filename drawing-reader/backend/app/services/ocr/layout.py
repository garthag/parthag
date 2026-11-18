"""Layout primitives shared by the OCR and BOM engines.

All coordinates are normalised to the page (0..1, origin top-left) so that native PDF text and
OCR output from raster images can be processed by the same downstream logic.
"""

from __future__ import annotations

import re
import statistics
from dataclasses import dataclass, field
from typing import Any


@dataclass(slots=True)
class Word:
    text: str
    x0: float
    y0: float
    x1: float
    y1: float
    conf: float = 1.0
    page: int = 0

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2

    @property
    def cy(self) -> float:
        return (self.y0 + self.y1) / 2

    @property
    def height(self) -> float:
        return self.y1 - self.y0

    @property
    def norm(self) -> str:
        return normalize_token(self.text)


@dataclass(slots=True)
class Line:
    words: list[Word]

    @property
    def text(self) -> str:
        return " ".join(w.text for w in self.words)

    @property
    def x0(self) -> float:
        return min(w.x0 for w in self.words)

    @property
    def x1(self) -> float:
        return max(w.x1 for w in self.words)

    @property
    def y0(self) -> float:
        return min(w.y0 for w in self.words)

    @property
    def y1(self) -> float:
        return max(w.y1 for w in self.words)

    @property
    def cy(self) -> float:
        return (self.y0 + self.y1) / 2

    @property
    def conf(self) -> float:
        return statistics.fmean(w.conf for w in self.words) if self.words else 0.0


@dataclass(slots=True)
class BBox:
    x0: float
    y0: float
    x1: float
    y1: float

    def contains(self, w: Word, tol: float = 0.0) -> bool:
        return w.cx >= self.x0 - tol and w.cx <= self.x1 + tol and w.cy >= self.y0 - tol and w.cy <= self.y1 + tol

    def to_dict(self) -> dict[str, float]:
        return {
            "x": round(self.x0, 4),
            "y": round(self.y0, 4),
            "width": round(self.x1 - self.x0, 4),
            "height": round(self.y1 - self.y0, 4),
        }

    @property
    def area(self) -> float:
        return max(self.x1 - self.x0, 0) * max(self.y1 - self.y0, 0)


@dataclass
class PageContent:
    index: int
    width: float
    height: float
    words: list[Word]
    is_scanned: bool = False
    rotation: int = 0
    engine: str = "native"
    meta: dict[str, Any] = field(default_factory=dict)
    image: Any = field(default=None, repr=False)  # grayscale ndarray for scanned pages

    @property
    def text(self) -> str:
        return "\n".join(line.text for line in group_lines(self.words))


_TOKEN_STRIP = re.compile(r"[^A-Z0-9/\-.:#()']")


def normalize_token(text: str) -> str:
    return _TOKEN_STRIP.sub("", text.upper().replace("’", "'"))


def group_lines(words: list[Word], tolerance: float | None = None) -> list[Line]:
    """Cluster words into text lines by vertical centre, then sort each line left-to-right."""
    if not words:
        return []
    heights = [w.height for w in words if w.height > 0]
    median_h = statistics.median(heights) if heights else 0.01
    tol = tolerance if tolerance is not None else max(median_h * 0.5, 0.002)
    ordered = sorted(words, key=lambda w: (w.cy, w.x0))
    lines: list[list[Word]] = []
    centres: list[float] = []
    for word in ordered:
        if lines and abs(word.cy - centres[-1]) <= tol:
            lines[-1].append(word)
            centres[-1] = statistics.fmean(w.cy for w in lines[-1])
        else:
            lines.append([word])
            centres.append(word.cy)
    return [Line(sorted(ln, key=lambda w: w.x0)) for ln in lines]


def split_line_by_gaps(line: Line, gap: float) -> list[Line]:
    """Split a line into segments where the horizontal gap between words exceeds ``gap``."""
    segments: list[list[Word]] = [[line.words[0]]]
    for prev, cur in zip(line.words, line.words[1:], strict=False):
        if cur.x0 - prev.x1 > gap:
            segments.append([cur])
        else:
            segments[-1].append(cur)
    return [Line(s) for s in segments]


def words_in(words: list[Word], box: BBox, tol: float = 0.0) -> list[Word]:
    return [w for w in words if box.contains(w, tol)]
