"""Engineering weight calculations: densities, rolled-section tables and description parsing."""

from __future__ import annotations

import math
import re
from decimal import ROUND_HALF_UP, Decimal

DENSITY_KG_M3 = {
    "CS": 7850,
    "STEEL": 7850,
    "MS": 7850,
    "CARBON STEEL": 7850,
    "A36": 7850,
    "S275": 7850,
    "S355": 7850,
    "SS": 7930,
    "STAINLESS": 7930,
    "SS304": 7930,
    "SS316": 7980,
    "DUPLEX": 7800,
    "AL": 2700,
    "ALUMINIUM": 2700,
    "ALUMINUM": 2700,
    "CU": 8960,
    "COPPER": 8960,
    "BRASS": 8500,
    "GRP": 1900,
    "HDPE": 960,
    "PVC": 1400,
    "CONCRETE": 2400,
}

# Nominal linear masses (kg/m) for common rolled sections.
SECTION_KG_M: dict[str, float] = {
    # EU IPE / HEA / HEB
    "IPE100": 8.1,
    "IPE120": 10.4,
    "IPE140": 12.9,
    "IPE160": 15.8,
    "IPE180": 18.8,
    "IPE200": 22.4,
    "IPE220": 26.2,
    "IPE240": 30.7,
    "IPE270": 36.1,
    "IPE300": 42.2,
    "IPE330": 49.1,
    "IPE360": 57.1,
    "IPE400": 66.3,
    "IPE450": 77.6,
    "IPE500": 90.7,
    "IPE550": 106.0,
    "IPE600": 122.0,
    "HEA100": 16.7,
    "HEA120": 19.9,
    "HEA140": 24.7,
    "HEA160": 30.4,
    "HEA180": 35.5,
    "HEA200": 42.3,
    "HEA220": 50.5,
    "HEA240": 60.3,
    "HEA260": 68.2,
    "HEA280": 76.4,
    "HEA300": 88.3,
    "HEA400": 125.0,
    "HEB100": 20.4,
    "HEB120": 26.7,
    "HEB140": 33.7,
    "HEB160": 42.6,
    "HEB200": 61.3,
    "HEB240": 83.2,
    "HEB300": 117.0,
    # UK UB / UC (the trailing figure is the mass per metre)
    "UB203X133X25": 25.1,
    "UB254X146X31": 31.1,
    "UB305X165X40": 40.3,
    "UB356X171X51": 51.0,
    "UB406X178X60": 60.1,
    "UB457X191X74": 74.3,
    "UB533X210X92": 92.1,
    "UB610X229X113": 113.0,
    "UC152X152X23": 23.0,
    "UC203X203X46": 46.1,
    "UC254X254X73": 73.1,
    "UC305X305X97": 96.9,
    # US W shapes (Wd x lb/ft -> kg/m)
    "W8X31": 46.1,
    "W10X33": 49.1,
    "W12X26": 38.7,
    "W12X40": 59.5,
    "W14X30": 44.6,
    "W16X40": 59.5,
    "W18X50": 74.4,
    "W21X62": 92.3,
    "W24X76": 113.1,
    # Channels
    "UPN100": 10.6,
    "UPN140": 16.0,
    "UPN200": 25.3,
    "UPN300": 46.2,
    "PFC150X75": 17.9,
    "PFC200X90": 29.7,
}

_SECTION_RE = re.compile(
    r"\b(IPE|HEA|HEB|UPN)\s*-?\s*(\d{3})\b|\b(UB|UC|PFC)\s*(\d{2,3}\s*[X×]\s*\d{2,3}(?:\s*[X×]\s*\d{1,3})?)|\b(W)\s*(\d{1,2}\s*[X×]\s*\d{1,3})\b",
    re.IGNORECASE,
)
_LENGTH_RE = re.compile(r"\bL\s*=\s*(\d+(?:\.\d+)?)\s*(MM|M)?\b", re.IGNORECASE)
_PLATE_RE = re.compile(
    r"\b(?:PL|PLATE|FLAT|FB)\b\.?\s*(\d+(?:\.\d+)?)\s*(?:THK|MM)?\s*[X×]\s*(\d+(?:\.\d+)?)\s*(?:[X×]\s*(\d+(?:\.\d+)?))?",
    re.IGNORECASE,
)
_PIPE_RE = re.compile(
    r"\b(?:PIPE|TUBE|CHS)\b.*?(?:OD\s*)?(\d+(?:\.\d+)?)\s*(?:OD)?\s*[X×]\s*(\d+(?:\.\d+)?)\s*(?:WT|THK|MM)?",
    re.IGNORECASE,
)
_ROUND_RE = re.compile(r"\b(?:RB|ROUND\s*BAR|ROD|BAR)\b\.?\s*(?:DIA\.?|Ø)?\s*(\d+(?:\.\d+)?)", re.IGNORECASE)
_ANGLE_RE = re.compile(r"\b(?:L|ANGLE|EA)\s*(\d{2,3})\s*[X×]\s*(\d{2,3})\s*[X×]\s*(\d{1,2})\b", re.IGNORECASE)

KG = Decimal("0.001")


def q3(value: Decimal | float | int) -> Decimal:
    return Decimal(str(value)).quantize(KG, rounding=ROUND_HALF_UP)


def density_for(material: str | None, grade: str | None = None) -> int:
    key = "".join(filter(None, [material, grade])).upper().replace(" ", "")
    for token, density in sorted(DENSITY_KG_M3.items(), key=lambda kv: -len(kv[0])):
        if token.replace(" ", "") in key:
            return density
    return 7850


def _length_m(description: str) -> float | None:
    match = _LENGTH_RE.search(description)
    if not match:
        return None
    value = float(match.group(1))
    unit = (match.group(2) or "MM").upper()
    return value if unit == "M" else value / 1000.0


def section_mass_per_m(description: str) -> float | None:
    match = _SECTION_RE.search(description)
    if not match:
        return None
    if match.group(1):
        key = f"{match.group(1).upper()}{match.group(2)}"
    elif match.group(3):
        key = f"{match.group(3).upper()}{re.sub(r'[\s×]', '', match.group(4).upper()).replace('×', 'X')}"
    else:
        key = f"W{re.sub(r'[\s×]', '', match.group(6).upper()).replace('×', 'X')}"
    key = key.replace("×", "X")
    if key in SECTION_KG_M:
        return SECTION_KG_M[key]
    # UB/UC designations carry mass per metre as their last term
    if match.group(3) and key.count("X") == 2:
        return float(key.rsplit("X", 1)[1])
    return None


def estimate_unit_weight(
    description: str, material: str | None = None, grade: str | None = None, unit: str | None = None
) -> Decimal | None:
    """Estimate the weight (kg) of one unit of a BOM line from its description.

    Supports rolled sections (IPE/HEA/HEB/UB/UC/W/UPN/PFC), plates and flats, pipes/CHS, round
    bars and equal/unequal angles. When the unit is metres the mass per metre is returned;
    otherwise a piece length ``L=####`` is required for linear items.
    """
    desc = description or ""
    density = density_for(material, grade)
    per_metre_unit = (unit or "").upper() in {"M", "LM", "MTR", "METRE", "METER"}
    length = _length_m(desc)

    mass_m = section_mass_per_m(desc)
    if mass_m is None:
        if pipe := _PIPE_RE.search(desc):
            od, wt = float(pipe.group(1)), float(pipe.group(2))
            if 0 < wt < od / 2:
                mass_m = math.pi * (od - wt) * wt * 1e-6 * density
        elif angle := _ANGLE_RE.search(desc):
            a, b, t = (float(angle.group(i)) for i in (1, 2, 3))
            mass_m = (a + b - t) * t * 1e-6 * density
        elif plate := _PLATE_RE.search(desc):
            t, w = float(plate.group(1)), float(plate.group(2))
            third = plate.group(3)
            if third is not None and not per_metre_unit:
                return q3(t * w * float(third) * 1e-9 * density)
            if (unit or "").upper() in {"M2", "SQM"}:
                return q3(t * 1e-3 * density)
            mass_m = t * w * 1e-6 * density
        elif rnd := _ROUND_RE.search(desc):
            d = float(rnd.group(1))
            mass_m = math.pi * (d / 2) ** 2 * 1e-6 * density
    if mass_m is None:
        return None
    if per_metre_unit:
        return q3(mass_m)
    if length is not None:
        return q3(mass_m * length)
    return None


def reconcile_weights(
    quantity: Decimal,
    unit_weight: Decimal | None,
    total_weight: Decimal | None,
    description: str = "",
    material: str | None = None,
    grade: str | None = None,
    unit: str | None = None,
) -> tuple[Decimal | None, Decimal, bool]:
    """Fill in missing unit/total weights. Returns (unit_weight, total_weight, calculated?)."""
    if total_weight is not None and unit_weight is not None:
        return unit_weight, total_weight, False
    if total_weight is None and unit_weight is not None:
        return unit_weight, q3(unit_weight * quantity), True
    if total_weight is not None and unit_weight is None:
        uw = q3(total_weight / quantity) if quantity else None
        return uw, total_weight, False
    estimate = estimate_unit_weight(description, material, grade, unit)
    if estimate is not None:
        return estimate, q3(estimate * quantity), True
    return None, Decimal("0"), False
