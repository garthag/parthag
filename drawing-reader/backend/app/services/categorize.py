"""Infer discipline / area / type / revision from drawing numbers, file names and folder paths."""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import PurePosixPath

DISCIPLINE_ALIASES: dict[str, list[str]] = {
    "CIV": ["CIV", "CV", "CIVIL", "C"],
    "STR": ["STR", "ST", "STRUCTURAL", "STRUCT", "S"],
    "MEC": ["MEC", "ME", "MECH", "MECHANICAL", "M"],
    "PIP": ["PIP", "PI", "PIPING", "PIPE", "P"],
    "ELE": ["ELE", "EL", "ELEC", "ELECTRICAL", "E"],
    "INS": ["INS", "IN", "INST", "INSTRUMENTATION", "INSTRUMENT", "I"],
    "ARC": ["ARC", "AR", "ARCH", "ARCHITECTURAL", "A"],
    "PRO": ["PRO", "PR", "PROC", "PROCESS"],
    "HVAC": ["HVAC", "HV", "HV-AC"],
}
_ALIAS_TO_CODE = {alias: code for code, aliases in DISCIPLINE_ALIASES.items() for alias in aliases}
# single letters are ambiguous in folder names: only trust them inside drawing numbers
_FOLDER_ALIASES = {a: c for a, c in _ALIAS_TO_CODE.items() if len(a) > 1}

DRAWING_TYPES = {
    "GA": "General Arrangement",
    "PID": "P&ID",
    "P&ID": "P&ID",
    "ISO": "Isometric",
    "LAY": "Layout",
    "SLD": "Single Line Diagram",
    "DET": "Detail",
    "FDN": "Foundation",
    "PLN": "Plan",
    "SEC": "Section",
    "LOOP": "Loop Diagram",
    "DS": "Datasheet",
    "SCH": "Schematic",
    "CBL": "Cable Schedule",
    "STD": "Standard",
    "FAB": "Fabrication",
    "ERE": "Erection",
}

AREA_RE = re.compile(r"^(?:A|AREA|U|UNIT)[-_ ]?(\d{1,3}[A-Z]?)$", re.IGNORECASE)


@dataclass
class Categorization:
    discipline_code: str | None = None
    area_code: str | None = None
    drawing_type: str | None = None
    drawing_number: str | None = None
    revision: str | None = None
    folder_path: str | None = None


def revision_sequence(revision: str | None) -> int:
    """Sortable rank for revision codes: P1 < A < B < … < Z < AA < 0 < 1 < 2 (IFC numeric series)."""
    if not revision:
        return 0
    rev = revision.strip().upper()
    if m := re.fullmatch(r"P(\d{1,3})", rev):
        return 10_000 + int(m.group(1))
    if m := re.fullmatch(r"(?:R|REV)?(\d{1,3})", rev):
        return 30_000 + int(m.group(1))
    if m := re.fullmatch(r"([A-Z]{1,2})(\d{0,2})", rev):
        letters = m.group(1)
        index = 0
        for ch in letters:
            index = index * 26 + (ord(ch) - 64)
        return 20_000 + index * 100 + int(m.group(2) or 0)
    return 1


def split_filename(filename: str) -> tuple[str, str | None]:
    """``ABC-ST-0001_RevB.pdf`` -> (``ABC-ST-0001``, ``B``)."""
    stem = PurePosixPath(filename).stem.strip()
    m = re.match(r"^(.*?)[\s_\-.]*(?:\(|\[)?(?:REV|R)[\s_.\-]?([A-Z0-9]{1,3})(?:\)|\])?$", stem, re.IGNORECASE)
    if m and m.group(1) and len(m.group(1)) >= 3:
        return m.group(1).strip(" _-.").upper(), m.group(2).upper()
    return stem.upper(), None


def categorize(filename: str, relative_path: str | None = None) -> Categorization:
    number, revision = split_filename(filename)
    result = Categorization(drawing_number=number, revision=revision)
    parts = [p for p in re.split(r"[-_ ]", number) if p]
    for part in parts:
        token = part.upper()
        if result.discipline_code is None and token in _ALIAS_TO_CODE and parts.index(part) > 0:
            result.discipline_code = _ALIAS_TO_CODE[token]
        elif result.area_code is None and AREA_RE.match(token):
            result.area_code = token
        elif result.drawing_type is None and token in DRAWING_TYPES:
            result.drawing_type = token
    if relative_path:
        folders = list(PurePosixPath(relative_path.replace("\\", "/")).parent.parts)
        result.folder_path = "/".join(folders) or None
        for folder in folders:
            clean = re.sub(r"^\d+[\s_.-]*", "", folder).upper().strip()
            if result.discipline_code is None and clean in _FOLDER_ALIASES:
                result.discipline_code = _FOLDER_ALIASES[clean]
            if result.area_code is None and AREA_RE.match(clean.replace(" ", "")):
                result.area_code = clean.replace(" ", "")
            if result.drawing_type is None:
                for key in DRAWING_TYPES:
                    if clean == key or clean == DRAWING_TYPES[key].upper():
                        result.drawing_type = key
    return result
