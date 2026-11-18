"""Seed realistic EPC sample data: 3 projects, 100 drawings, 300 revisions, 1000+ BOM lines.

Usage::

    python -m app.seed.seed            # seed if the database is empty
    python -m app.seed.seed --force    # wipe sample projects and re-seed
    python -m app.seed.seed --no-pdfs  # skip generating PDF files (faster)
"""

from __future__ import annotations

import argparse
import hashlib
import logging
import random
from dataclasses import dataclass
from datetime import date, timedelta
from decimal import Decimal

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.core.config import settings
from app.core.logging import configure_logging
from app.core.security import hash_password
from app.db.session import SessionLocal
from app.models import (
    Area,
    Discipline,
    Drawing,
    DrawingFile,
    DrawingRevision,
    FileStatus,
    MaterialRate,
    Permission,
    Project,
    Role,
    System,
    User,
)
from app.services import storage
from app.services.bom.weights import SECTION_KG_M, estimate_unit_weight, q3
from app.services.bom_service import rebuild_boq, save_bom
from app.services.categorize import revision_sequence
from app.services.drawing_service import set_current_revision
from app.services.pdf_generator import BOMLine, DrawingSpec, RevisionEntry, generate_drawing_pdf
from app.services.revision_engine import compare_consecutive

logger = logging.getLogger("seed")

PERMISSIONS = {
    "projects.manage": "Create/update/delete projects",
    "drawings.upload": "Upload drawings",
    "drawings.edit": "Edit drawing metadata and OCR overrides",
    "ocr.process": "Run OCR jobs",
    "bom.edit": "Edit BOMs and rebuild BOQ",
    "rates.manage": "Maintain material rates",
    "reports.export": "Export reports",
    "users.manage": "Manage users",
}
ROLES = {
    "Administrator": list(PERMISSIONS),
    "Document Controller": ["drawings.upload", "drawings.edit", "ocr.process", "reports.export"],
    "Design Engineer": ["drawings.edit", "bom.edit", "ocr.process", "reports.export"],
    "Mechanical Engineer": ["drawings.edit", "bom.edit", "reports.export"],
    "Structural Engineer": ["drawings.edit", "bom.edit", "reports.export"],
    "Electrical Engineer": ["drawings.edit", "bom.edit", "reports.export"],
    "Instrumentation Engineer": ["drawings.edit", "bom.edit", "reports.export"],
    "Procurement": ["rates.manage", "reports.export"],
    "Planning": ["reports.export"],
    "Project Manager": ["projects.manage", "reports.export"],
    "EPC Management": ["reports.export"],
    "Viewer": [],
}
USERS = [
    ("admin", "admin@drawingreader.local", "System Administrator", "Admin@12345", ["Administrator"], True),
    ("doccon", "doccon@drawingreader.local", "Priya Nair", "Doccon@12345", ["Document Controller"], False),
    ("structural", "structural@drawingreader.local", "Omar Haddad", "Struct@12345", ["Structural Engineer"], False),
    ("piping", "piping@drawingreader.local", "Lena Fischer", "Piping@12345", ["Mechanical Engineer"], False),
    ("procurement", "procurement@drawingreader.local", "Arjun Mehta", "Procure@12345", ["Procurement"], False),
    ("pm", "pm@drawingreader.local", "Grace Okafor", "Manager@12345", ["Project Manager"], False),
]
DISCIPLINES = [
    ("CIV", "Civil", "#8D6E63"),
    ("STR", "Structural", "#1E88E5"),
    ("MEC", "Mechanical", "#43A047"),
    ("PIP", "Piping", "#FB8C00"),
    ("ELE", "Electrical", "#FDD835"),
    ("INS", "Instrumentation", "#8E24AA"),
    ("ARC", "Architectural", "#00ACC1"),
    ("PRO", "Process", "#E53935"),
    ("HVAC", "HVAC", "#6D4C41"),
]
RATES = [
    ("CS", "S355JR", "2.65"),
    ("CS", "S275JR", "2.40"),
    ("CS", "A36", "2.30"),
    ("CS", "A572-50", "2.55"),
    ("SS", "316L", "9.80"),
    ("SS", "304", "7.90"),
    ("CS", "A106-B", "3.10"),
    ("CS", "API 5L X52", "3.40"),
    ("CS", "A105", "4.20"),
    ("CS", "A234 WPB", "4.60"),
    ("AL", "6061-T6", "6.50"),
    ("CU", "XLPE", "11.00"),
    ("GALV", "HDG", "2.95"),
    ("CONCRETE", "C35/45", "0.18"),
]
PROJECTS = [
    ("NRP", "North Refinery Expansion", "Acme Petroleum", "Ras Laffan, Qatar", date(2025, 3, 1)),
    ("LNG2", "LNG Train 2 Modules", "Blue Gas Ltd", "Darwin, Australia", date(2025, 6, 15)),
    ("SPS", "Solar Plant Substation 400kV", "Helio Power", "Atacama, Chile", date(2025, 9, 1)),
]
AREAS = [
    ("A01", "Process Unit"),
    ("A02", "Pipe Rack"),
    ("A03", "Utilities"),
    ("A04", "Substation"),
    ("A05", "Tank Farm"),
]
TYPES = {
    "STR": ["GA", "FAB", "ERE", "DET"],
    "CIV": ["FDN", "PLN", "SEC"],
    "PIP": ["ISO", "GA", "LAY"],
    "MEC": ["GA", "DS", "DET"],
    "ELE": ["SLD", "LAY", "CBL"],
    "INS": ["LOOP", "LAY", "DET"],
    "ARC": ["PLN", "SEC"],
    "PRO": ["PID"],
    "HVAC": ["LAY", "SCH"],
}
TITLES = {
    "STR": [
        "PIPE RACK {a} STEEL FRAMING",
        "COMPRESSOR SHELTER {a} STEELWORK",
        "ACCESS PLATFORMS {a}",
        "STAIR TOWER {a} FABRICATION",
        "EQUIPMENT SUPPORT FRAME {a}",
    ],
    "CIV": ["FOUNDATION LAYOUT {a}", "PILE CAP DETAILS {a}", "PAVING AND DRAINAGE {a}"],
    "PIP": ["COOLING WATER PIPING {a}", "STEAM HEADER ISOMETRIC {a}", "FLARE LINE ROUTING {a}"],
    "MEC": ["PUMP SKID {a} ARRANGEMENT", "HEAT EXCHANGER {a} SUPPORTS", "STORAGE TANK {a} NOZZLES"],
    "ELE": ["MAIN SUBSTATION {a} SINGLE LINE", "CABLE TRAY LAYOUT {a}", "EARTHING LAYOUT {a}"],
    "INS": ["INSTRUMENT LOOP {a}", "JUNCTION BOX LAYOUT {a}", "CONTROL ROOM CABLING {a}"],
    "ARC": ["CONTROL BUILDING {a} PLANS", "SUBSTATION BUILDING {a} ELEVATIONS"],
    "PRO": ["P&ID CRUDE UNIT {a}", "P&ID FLARE SYSTEM {a}"],
    "HVAC": ["HVAC LAYOUT CONTROL ROOM {a}", "DUCTING SCHEDULE {a}"],
}
DISCIPLINE_MIX = (
    ["STR"] * 30
    + ["PIP"] * 20
    + ["CIV"] * 12
    + ["MEC"] * 12
    + ["ELE"] * 10
    + ["INS"] * 8
    + ["ARC"] * 3
    + ["PRO"] * 3
    + ["HVAC"] * 2
)
NOTES = [
    "ALL DIMENSIONS ARE IN MILLIMETRES UNLESS NOTED OTHERWISE.",
    "STEEL GRADE S355JR TO EN 10025 UNLESS NOTED.",
    "ALL WELDS 6MM CONTINUOUS FILLET UNLESS NOTED.",
    "HOT DIP GALVANISE AFTER FABRICATION TO ISO 1461.",
    "BOLTS GRADE 8.8 HDG WITH NUTS AND WASHERS.",
    "REFER TO SPECIFICATION FOR PAINTING SYSTEM.",
]
REV_FLOW = [
    ("A", "IFR", "ISSUED FOR REVIEW"),
    ("B", "IFA", "ISSUED FOR APPROVAL"),
    ("0", "IFC", "ISSUED FOR CONSTRUCTION"),
    ("1", "IFC", "REVISED PER CLIENT COMMENTS"),
    ("2", "IFC", "REVISED FOR VENDOR DATA"),
    ("3", "AS-BUILT", "AS-BUILT"),
]


@dataclass
class Line:
    part: str
    desc: str
    material: str
    grade: str
    spec: str
    qty: Decimal
    unit: str
    uw: Decimal


def _bom_template(rng: random.Random, disc: str, dn: str) -> list[Line]:
    lines: list[Line] = []
    if disc in ("STR", "CIV", "MEC", "ARC", "HVAC"):
        sections = [k for k in SECTION_KG_M if not k.startswith("W")]
        for _ in range(rng.randint(6, 14)):
            kind = rng.random()
            grade = rng.choice(["S355JR", "S355JR", "S275JR"])
            if kind < 0.55:
                sec = rng.choice(sections)
                length = rng.choice([1500, 2400, 3000, 4500, 6000, 7200, 9000])
                if sec.startswith(("UB", "UC", "PFC")):
                    pretty = sec[:2] + " " + sec[2:].lower() if not sec.startswith("PFC") else "PFC " + sec[3:].lower()
                else:
                    pretty = sec
                desc = f"{pretty} L={length}"
            elif kind < 0.85:
                t, w, length = (
                    rng.choice([8, 10, 12, 16, 20, 25]),
                    rng.choice([150, 200, 250, 300, 400]),
                    rng.choice([300, 450, 600, 800]),
                )
                desc = f"PL {t} x {w} x {length}"
            else:
                a, t = rng.choice([(50, 5), (65, 6), (75, 8), (100, 10)])
                desc = f"ANGLE {a}x{a}x{t} L={rng.choice([600, 1200, 2400])}"
            uw = estimate_unit_weight(desc, "CS", grade) or Decimal("10")
            lines.append(Line("", desc, "CS", grade, "EN 10025", Decimal(rng.randint(1, 12)), "EA", uw))
    elif disc in ("PIP", "PRO"):
        for _ in range(rng.randint(6, 12)):
            od, wt = rng.choice(
                [(60.3, 3.91), (88.9, 5.49), (114.3, 6.02), (168.3, 7.11), (219.1, 8.18), (273.0, 9.27)]
            )
            if rng.random() < 0.6:
                desc = f"PIPE OD {od} x {wt} WT SMLS BE"
                uw = estimate_unit_weight(desc, "CS", "A106-B", "M") or Decimal(10)
                lines.append(Line("", desc, "CS", "A106-B", "ASME B36.10", Decimal(rng.randint(3, 60)), "M", uw))
            else:
                item = rng.choice(["ELBOW 90 LR", "TEE EQUAL", "FLANGE WN 150#", "REDUCER CONC"])
                grade = "A105" if "FLANGE" in item else "A234 WPB"
                uw = q3(Decimal(str(od)) * Decimal("0.08"))
                lines.append(
                    Line("", f"{item} {od}OD", "CS", grade, "ASME B16.9", Decimal(rng.randint(1, 10)), "EA", uw)
                )
    else:  # ELE / INS
        for _ in range(rng.randint(5, 10)):
            choice = rng.random()
            if choice < 0.4:
                lines.append(
                    Line(
                        "",
                        f"CABLE TRAY LADDER {rng.choice([300, 450, 600])}W HDG",
                        "GALV",
                        "HDG",
                        "IEC 61537",
                        Decimal(rng.randint(6, 80)),
                        "M",
                        Decimal(rng.choice(["6.20", "7.80", "9.40"])),
                    )
                )
            elif choice < 0.75:
                lines.append(
                    Line(
                        "",
                        f"CABLE {rng.choice(['3C', '4C'])}x{rng.choice([16, 35, 70, 120])}SQMM XLPE/SWA",
                        "CU",
                        "XLPE",
                        "IEC 60502",
                        Decimal(rng.randint(20, 400)),
                        "M",
                        Decimal(rng.choice(["0.95", "1.80", "3.20", "5.10"])),
                    )
                )
            else:
                lines.append(
                    Line(
                        "",
                        "JUNCTION BOX SS316 IP66",
                        "SS",
                        "316L",
                        "IEC 60079",
                        Decimal(rng.randint(1, 6)),
                        "EA",
                        Decimal("14.500"),
                    )
                )
    for i, ln in enumerate(lines, 1):
        ln.part = f"{dn.split('-')[-1]}-{i:03d}"
    return lines


def _mutate(rng: random.Random, lines: list[Line], disc: str, dn: str) -> list[Line]:
    """Apply realistic revision changes: quantity edits, material/grade upgrades, additions, deletions."""
    out = [Line(**vars(ln)) for ln in lines]
    for ln in out:
        r = rng.random()
        if r < 0.25:
            ln.qty = max(Decimal(1), ln.qty + rng.choice([-2, -1, 1, 2, 3, 4]))
        elif r < 0.32 and ln.grade == "S275JR":
            ln.grade = "S355JR"
        elif r < 0.36:
            ln.spec = rng.choice(["EN 10025-2", "ASTM A6", "IEC 60502-1"]) if ln.material != "CS" else "EN 10025-2"
    if out and rng.random() < 0.35:
        out.pop(rng.randrange(len(out)))
    if rng.random() < 0.7:
        extra = _bom_template(rng, disc, dn)[: rng.randint(1, 4)]
        start = len(out) + 1
        for k, ln in enumerate(extra):
            ln.part = f"{dn.split('-')[-1]}-{start + k + 100:03d}"
        out.extend(extra)
    return out


def _ensure_reference_data(db: Session) -> User:
    perms = {}
    for code, desc in PERMISSIONS.items():
        p = db.scalar(select(Permission).where(Permission.code == code)) or Permission(code=code, description=desc)
        db.add(p)
        perms[code] = p
    db.flush()
    roles = {}
    for name, codes in ROLES.items():
        r = db.scalar(select(Role).where(Role.name == name)) or Role(name=name)
        r.permissions = [perms[c] for c in codes]
        db.add(r)
        roles[name] = r
    db.flush()
    admin = None
    for username, email, name, pw, role_names, superuser in USERS:
        u = db.scalar(select(User).where(User.username == username))
        if u is None:
            u = User(
                username=username,
                email=email,
                full_name=name,
                hashed_password=hash_password(pw),
                is_superuser=superuser,
                job_title=role_names[0],
            )
            db.add(u)
        u.roles = [roles[n] for n in role_names]
        if superuser:
            admin = u
    for code, name, color in DISCIPLINES:
        if db.scalar(select(Discipline).where(Discipline.code == code)) is None:
            db.add(Discipline(code=code, name=name, color=color))
    for material, grade, rate in RATES:
        if (
            db.scalar(select(MaterialRate).where(MaterialRate.material == material, MaterialRate.grade == grade))
            is None
        ):
            db.add(
                MaterialRate(
                    material=material,
                    grade=grade,
                    rate=Decimal(rate),
                    rate_basis="kg",
                    currency="USD",
                    effective_from=date(2025, 1, 1),
                )
            )
    db.flush()
    assert admin is not None
    return admin


def seed(
    db: Session, *, with_pdfs: bool = True, drawings: int = 100, revisions_total: int = 300, seed_value: int = 42
) -> dict:
    rng = random.Random(seed_value)
    admin = _ensure_reference_data(db)
    disciplines = {d.code: d for d in db.scalars(select(Discipline))}
    projects = []
    for code, name, client, location, start in PROJECTS:
        p = db.scalar(select(Project).where(Project.code == code))
        if p is None:
            p = Project(
                code=code,
                name=name,
                client=client,
                location=location,
                start_date=start,
                end_date=start + timedelta(days=900),
                created_by_id=admin.id,
                description=f"{name} for {client}",
            )
            db.add(p)
            db.flush()
            for a_code, a_name in AREAS:
                area = Area(project_id=p.id, code=a_code, name=a_name)
                db.add(area)
                db.flush()
                db.add(System(project_id=p.id, area_id=area.id, code=f"{a_code}-S1", name=f"{a_name} System"))
        projects.append(p)
    db.flush()
    areas = {(a.project_id, a.code): a for a in db.scalars(select(Area))}

    # Distribute 300 revisions over 100 drawings: each drawing gets 1-6, totalling exactly revisions_total.
    counts = [1] * drawings
    remaining = revisions_total - drawings
    while remaining > 0:
        i = rng.randrange(drawings)
        if counts[i] < len(REV_FLOW):
            counts[i] += 1
            remaining -= 1
    stats = {"projects": len(projects), "drawings": 0, "revisions": 0, "bom_items": 0, "pdfs": 0}
    today = date.today()
    for n in range(drawings):
        project = projects[n % len(projects)]
        disc = DISCIPLINE_MIX[n % len(DISCIPLINE_MIX)]
        area_code, _ = AREAS[rng.randrange(len(AREAS))]
        dtype = rng.choice(TYPES[disc])
        number = f"{project.code}-{disc[:2]}-{area_code}-{dtype}-{n + 1:04d}"
        if db.scalar(select(Drawing).where(Drawing.project_id == project.id, Drawing.drawing_number == number)):
            continue
        title = rng.choice(TITLES[disc]).format(a=f"{area_code}-{rng.randint(1, 9):02d}")
        drawing = Drawing(
            project_id=project.id,
            discipline_id=disciplines[disc].id,
            area_id=areas[(project.id, area_code)].id,
            drawing_number=number,
            title=title,
            drawing_type=dtype,
            scale=rng.choice(["1:20", "1:50", "1:100", "NTS"]),
            sheet_number="1 OF 1",
            total_sheets=1,
            vendor=rng.choice([None, None, "GULF STEEL FABRICATORS", "PACIFIC MODULES", "ANDES ELECTRIC"]),
            originator="DRAWING READER EPC",
            folder_path=f"{project.code}/{disciplines[disc].name}/{area_code}",
            created_by_id=admin.id,
        )
        db.add(drawing)
        db.flush()
        stats["drawings"] += 1
        lines = _bom_template(rng, disc, number)
        rev_date = project.start_date + timedelta(days=rng.randint(10, 120))
        history: list[RevisionEntry] = []
        for k in range(counts[n]):
            code, status, desc = REV_FLOW[k]
            if k > 0:
                lines = _mutate(rng, lines, disc, number)
                rev_date = min(rev_date + timedelta(days=rng.randint(14, 75)), today)
            history.append(RevisionEntry(code, rev_date.isoformat(), desc))
            rev = DrawingRevision(
                drawing_id=drawing.id,
                revision=code,
                revision_seq=revision_sequence(code),
                revision_date=rev_date,
                status=status,
                description=desc,
                drawn_by="J.DOE",
                checker="M.KHAN",
                approver="P.SINGH",
                ocr_confidence=round(rng.uniform(0.91, 0.995), 3),
                created_by_id=admin.id,
            )
            drawing.revisions.append(rev)
            db.flush()
            items = [
                {
                    "item_number": str(i),
                    "part_number": ln.part,
                    "description": ln.desc,
                    "material": ln.material,
                    "grade": ln.grade,
                    "specification": ln.spec,
                    "quantity": ln.qty,
                    "unit": ln.unit,
                    "unit_weight_kg": ln.uw,
                    "total_weight_kg": q3(ln.uw * ln.qty),
                    "sheet_no": 1,
                    "confidence": 1.0,
                }
                for i, ln in enumerate(lines, 1)
            ]
            stats["revisions"] += 1
            stats["bom_items"] += len(items)
            if with_pdfs:
                spec = DrawingSpec(
                    number,
                    title,
                    code,
                    rev_date.isoformat(),
                    status=status,
                    scale=drawing.scale or "1:50",
                    vendor=drawing.vendor,
                    project=project.name.upper(),
                    client=(project.client or "").upper(),
                    bom=[
                        BOMLine(
                            str(i),
                            ln.part,
                            ln.desc,
                            ln.material,
                            ln.grade,
                            ln.spec,
                            ln.qty,
                            ln.unit,
                            ln.uw,
                            q3(ln.uw * ln.qty),
                        )
                        for i, ln in enumerate(lines, 1)
                    ],
                    revisions=list(history),
                    notes=rng.sample(NOTES, 3),
                    seed=n,
                    clouds=[(120 + 20 * k, 180, 300 + 20 * k, 330)] if k > 0 else [],
                )
                data = generate_drawing_pdf(spec)
                sha = hashlib.sha256(data).hexdigest()
                rel = f"{project.code}/{sha[:2]}/{sha}.pdf"
                path = storage.storage_root() / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
                db.add(
                    DrawingFile(
                        drawing_id=drawing.id,
                        revision_id=rev.id,
                        original_filename=f"{number}_Rev{code}.pdf",
                        relative_path=f"{drawing.folder_path}/{number}_Rev{code}.pdf",
                        storage_path=rel,
                        file_size=len(data),
                        sha256=sha,
                        page_count=1,
                        is_scanned=False,
                        upload_status=FileStatus.PROCESSED,
                        created_by_id=admin.id,
                    )
                )
                stats["pdfs"] += 1
            set_current_revision(drawing)
            save_bom(db, rev, items, source="native", user_id=admin.id)
        set_current_revision(drawing)
        db.flush()
        compare_consecutive(db, drawing)
        if n % 20 == 19:
            db.commit()
            logger.info("seeded %d drawings", n + 1)
    for p in projects:
        rebuild_boq(db, p.id)
    db.commit()
    return stats


def main() -> None:
    configure_logging(settings.LOG_LEVEL)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--force", action="store_true", help="delete sample projects first")
    parser.add_argument("--no-pdfs", action="store_true", help="skip PDF generation")
    args = parser.parse_args()
    with SessionLocal() as db:
        existing = db.scalar(select(Drawing.id).limit(1))
        if existing and not args.force:
            _ensure_reference_data(db)
            db.commit()
            logger.info("Database already contains drawings; reference data ensured, skipping sample data")
            return
        if args.force:
            for code, *_ in PROJECTS:
                p = db.scalar(select(Project).where(Project.code == code))
                if p is not None:
                    db.delete(p)
            db.commit()
        stats = seed(db, with_pdfs=not args.no_pdfs)
        logger.info("Seed complete: %s", stats)


if __name__ == "__main__":
    main()
