"""Text reader + scanner (OCR) + table finder. All local, no cloud.

Text sources, fastest first:
  PDF with a text layer  -> pypdf
  Scanned PDF / image    -> OCR: Windows built-in OCR (winocr) or Tesseract if installed
  DOCX / XLSX / CSV / TXT / HTML / EML -> read directly
"""

from __future__ import annotations

import csv
import html as htmlmod
import io
import os
import re
import shutil
import sys
import threading
import zipfile

MAX_TEXT = 200_000
MAX_INPUT = 60 * 1024 * 1024          # don't parse attachments bigger than 60 MB
MAX_UNZIPPED = 80 * 1024 * 1024       # zip-bomb guard for .docx/.xlsx
OCR_LOCK = threading.Semaphore(max(1, (os.cpu_count() or 2) // 2))   # OCR is CPU heavy
IMAGE_EXT = {"png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "webp"}


# ------------------------------------------------------------------- OCR
_tess_cmd: str | None = None


def _find_tesseract() -> str | None:
    global _tess_cmd
    if _tess_cmd is None:
        cand = shutil.which("tesseract")
        if not cand and sys.platform == "win32":
            for base in (os.environ.get("ProgramFiles", r"C:\Program Files"),
                         os.environ.get("LOCALAPPDATA", "")):
                p = os.path.join(base, "Tesseract-OCR", "tesseract.exe") if base else ""
                if p and os.path.isfile(p):
                    cand = p
                    break
                p = os.path.join(base, "Programs", "Tesseract-OCR", "tesseract.exe") if base else ""
                if p and os.path.isfile(p):
                    cand = p
                    break
        _tess_cmd = cand or ""
    return _tess_cmd or None


def _windows_ocr(img) -> str | None:
    if sys.platform != "win32":
        return None
    try:
        import winocr
    except Exception:
        return None
    for lang in ("en-US", "en"):
        try:
            res = winocr.recognize_pil_sync(img.convert("RGBA"), lang)
            text = res["text"] if isinstance(res, dict) else getattr(res, "text", "")
            lines = res.get("lines") if isinstance(res, dict) else None
            if lines:   # keep line structure (better for tables & fields)
                text = "\n".join(l.get("text", "") if isinstance(l, dict) else str(l) for l in lines)
            return text
        except Exception:
            continue
    return None


def _tesseract_ocr(img) -> str | None:
    cmd = _find_tesseract()
    if not cmd:
        return None
    try:
        import pytesseract
        pytesseract.pytesseract.tesseract_cmd = cmd
        # psm 6 keeps rows aligned -> tables survive OCR better
        return pytesseract.image_to_string(img, config="--psm 6")
    except Exception:
        return None


def ocr_engine() -> str:
    if sys.platform == "win32":
        try:
            import winocr  # noqa: F401
            return "Windows OCR"
        except Exception:
            pass
    return "Tesseract" if _find_tesseract() else ""


def ocr_image(img) -> str:
    with OCR_LOCK:
        if img.mode not in ("RGB", "L", "RGBA"):
            img = img.convert("RGB")
        # small scans OCR badly - upscale
        if max(img.size) < 1400:
            f = 1400 / max(img.size)
            img = img.resize((int(img.width * f), int(img.height * f)))
        for fn in (_windows_ocr, _tesseract_ocr):
            t = fn(img)
            if t and t.strip():
                return t
    return ""


def render_pdf_pages(data: bytes, max_pages: int = 5, scale: float = 2.0):
    try:
        import pypdfium2 as pdfium
    except ImportError:
        return []
    out = []
    try:
        pdf = pdfium.PdfDocument(data)
        for i in range(min(len(pdf), max_pages)):
            out.append(pdf[i].render(scale=scale).to_pil())
        pdf.close()
    except Exception:
        pass
    return out


# ------------------------------------------------------------- extractors
def pdf_text(data: bytes, max_pages: int = 20, layout: bool = False) -> str:
    try:
        from pypdf import PdfReader
        r = PdfReader(io.BytesIO(data))
        if r.is_encrypted:
            try:
                r.decrypt("")
            except Exception:
                return ""
        parts = []
        for p in r.pages[:max_pages]:
            try:
                parts.append((p.extract_text(extraction_mode="layout") if layout else p.extract_text()) or "")
            except Exception:
                parts.append(p.extract_text() or "")
        return "\n".join(parts)
    except Exception:
        return ""


def _zip_is_sane(data: bytes) -> bool:
    """Reject zip bombs: total uncompressed size and compression ratio must be reasonable."""
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            total = sum(i.file_size for i in z.infolist())
            return total <= MAX_UNZIPPED and total <= max(len(data), 1) * 200
    except Exception:
        return False


def _docx_text(data: bytes) -> str:
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            xml = z.read("word/document.xml").decode("utf-8", "replace")
    except Exception:
        return ""
    xml = re.sub(r"</w:p>", "\n", xml)
    xml = re.sub(r"<w:tab/>", "\t", xml)
    xml = re.sub(r"</w:tc>", "  |  ", xml)
    return htmlmod.unescape(re.sub(r"<[^>]+>", "", xml))


def _xlsx_rows(data: bytes) -> list[tuple[str, list[list[str]]]]:
    try:
        import openpyxl
        wb = openpyxl.load_workbook(io.BytesIO(data), read_only=True, data_only=True)
    except Exception:
        return []
    sheets = []
    for ws in wb.worksheets[:10]:
        rows = []
        for r in ws.iter_rows(values_only=True, max_row=2000):
            cells = ["" if v is None else str(v) for v in r]
            if any(c.strip() for c in cells):
                rows.append(cells)
        sheets.append((ws.title, rows))
    wb.close()
    return sheets


def _decode(data: bytes) -> str:
    for enc in ("utf-8", "cp1252", "latin-1"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", "replace")


def extract_text(data: bytes, filename: str, mime: str, ocr: bool = True) -> tuple[str, str]:
    """Return (text, source) where source is pdf | ocr | office | plain | none."""
    ext = filename.rsplit(".", 1)[-1].lower() if "." in filename else ""
    mime = (mime or "").lower()
    if len(data) > MAX_INPUT:
        return "", "none"
    if ext in ("docx", "docm", "xlsx", "xlsm") and not _zip_is_sane(data):
        return "", "none"
    if ext == "pdf" or mime == "application/pdf":
        t = pdf_text(data)
        if len(re.sub(r"\s", "", t)) >= 25:
            return t[:MAX_TEXT], "pdf"
        if ocr:
            pages = render_pdf_pages(data)
            t2 = "\n\n".join(ocr_image(p) for p in pages)
            if t2.strip():
                return t2[:MAX_TEXT], "ocr"
        return t[:MAX_TEXT], ("pdf" if t.strip() else "none")
    if ext in IMAGE_EXT or mime.startswith("image/"):
        if not ocr:
            return "", "none"
        try:
            from PIL import Image
            img = Image.open(io.BytesIO(data))
            img.load()
        except Exception:
            return "", "none"
        t = ocr_image(img)
        return t[:MAX_TEXT], ("ocr" if t.strip() else "none")
    if ext in ("docx", "docm"):
        t = _docx_text(data)
        return t[:MAX_TEXT], ("office" if t.strip() else "none")
    if ext in ("xlsx", "xlsm"):
        sheets = _xlsx_rows(data)
        t = "\n\n".join(f"[{name}]\n" + "\n".join("\t".join(r) for r in rows) for name, rows in sheets)
        return t[:MAX_TEXT], ("office" if t.strip() else "none")
    if ext in ("txt", "csv", "tsv", "log", "json", "xml", "md", "eml") or mime.startswith("text/") \
            or mime == "message/rfc822":
        t = _decode(data)
        if ext in ("html", "htm") or mime == "text/html":
            t = htmlmod.unescape(re.sub(r"<(script|style)[^>]*>.*?</\1>|<[^>]+>", " ", t, flags=re.S | re.I))
        return t[:MAX_TEXT], "plain"
    if ext in ("html", "htm"):
        t = _decode(data)
        t = htmlmod.unescape(re.sub(r"<(script|style)[^>]*>.*?</\1>|<[^>]+>", " ", t, flags=re.S | re.I))
        return t[:MAX_TEXT], "plain"
    return "", "none"


# ----------------------------------------------------------------- tables
_SPLIT = re.compile(r"\t|\s*\|\s*|\s{2,}")
_NUMERIC = re.compile(r"^[-+(]?[₹$€£]?\s?\d[\d,]*(\.\d+)?%?\)?$")


def _cells(line: str) -> list[str]:
    return [c.strip() for c in _SPLIT.split(line.strip()) if c.strip()]


def _cells_loose(line: str) -> list[str]:
    """OCR text loses column gaps: split 'Rice bags 4 2,400.00' into text + trailing numbers."""
    toks = line.split()
    nums = []
    while toks and _NUMERIC.match(toks[-1]):
        nums.insert(0, toks.pop())
    if not nums or not toks:
        return []
    return [" ".join(toks)] + nums


def find_tables(text: str, min_rows: int = 2, min_cols: int = 2) -> list[list[list[str]]]:
    """Detect column-aligned blocks in (layout) text and return them as tables."""
    def run(splitter):
        tables, block = [], []

        def flush():
            if len(block) >= min_rows:
                width = max(len(r) for r in block)
                # a real table has several columns and numbers somewhere
                if width >= min_cols and any(_NUMERIC.match(c) for r in block for c in r):
                    # short rows ("Sub Total  10,500.00"): label left, numbers right-aligned
                    tables.append([r[:1] + [""] * (width - len(r)) + r[1:] for r in block])
            block.clear()

        for line in text.splitlines():
            if not line.strip():
                continue                      # OCR puts blank lines between rows
            cells = splitter(line)
            if len(cells) >= min_cols:
                if block and abs(len(cells) - len(block[-1])) > 2:
                    flush()
                block.append(cells)
            else:
                flush()
        flush()
        return tables

    return (run(_cells) or run(_cells_loose))[:20]


def tables_for(data: bytes, filename: str, mime: str, text: str = "") -> list[list[list[str]]]:
    ext = filename.rsplit(".", 1)[-1].lower() if "." in filename else ""
    if ext in ("xlsx", "xlsm"):
        return [rows for _, rows in _xlsx_rows(data) if rows][:20]
    if ext in ("csv", "tsv"):
        try:
            rows = list(csv.reader(io.StringIO(_decode(data)), delimiter="\t" if ext == "tsv" else ","))
            return [rows[:5000]] if rows else []
        except csv.Error:
            return []
    if ext == "pdf" or mime == "application/pdf":
        lay = pdf_text(data, layout=True)
        if len(re.sub(r"\s", "", lay)) >= 25:
            return find_tables(lay)
    return find_tables(text)
