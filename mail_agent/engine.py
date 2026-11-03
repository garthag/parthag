"""Search engine: runs the query matrix in parallel, de-duplicates and scores."""

from __future__ import annotations

import io
import threading
import time
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone

from . import hexagon
from .matrix import build_cells, normalise_types
from .models import Attachment, MessageMeta, SearchRequest, make_attachment_id

FETCH_BATCH = 150


class ByteCache:
    """Small LRU so a preview followed by a download doesn't fetch twice."""

    def __init__(self, max_bytes: int = 96 * 1024 * 1024):
        self.max = max_bytes
        self.size = 0
        self.data: OrderedDict[str, bytes] = OrderedDict()
        self.lock = threading.Lock()

    def get(self, k):
        with self.lock:
            v = self.data.get(k)
            if v is not None:
                self.data.move_to_end(k)
            return v

    def put(self, k, v: bytes):
        if len(v) > self.max // 4:
            return
        with self.lock:
            if k in self.data:
                return
            self.data[k] = v
            self.size += len(v)
            while self.size > self.max:
                _, old = self.data.popitem(last=False)
                self.size -= len(old)


def _sort_key(a: Attachment):
    d = a.date
    if d is None:
        return 0.0
    if d.tzinfo is None:
        d = d.replace(tzinfo=timezone.utc)
    return d.timestamp()


class SearchEngine:
    def __init__(self, source, workers: int = 6):
        self.source = source
        self.workers = max(1, workers)
        self.registry: dict[str, Attachment] = {}
        self.cache = ByteCache()
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ search
    def search(self, req: SearchRequest, progress=None) -> dict:
        t0 = time.perf_counter()
        say = progress or (lambda *_: None)
        folders = req.folders or self.source.default_folders()
        cells = build_cells(req)
        types = normalise_types(req.file_types)
        stats = {"folders": folders, "cells": len(cells) * len(folders), "server_hits": 0}

        # 1. Matrix search - every (folder x cell) in parallel. Result is a set,
        #    so overlapping windows / senders never produce duplicates.
        hits: set[tuple[str, int]] = set()
        say(f"Searching {stats['cells']} matrix cells")
        with ThreadPoolExecutor(self.workers) as ex:
            futs = {ex.submit(self.source.search, f, c): f for f in folders for c in cells}
            for fut in as_completed(futs):
                uids = fut.result()
                stats["server_hits"] += len(uids)
                hits.update((futs[fut], u) for u in uids)
        stats["unique_hits"] = len(hits)

        # 2. Fetch metadata (structure + headers + snippet) in parallel batches.
        by_folder: dict[str, list[int]] = {}
        for f, u in hits:
            by_folder.setdefault(f, []).append(u)
        batches = [(f, sorted(us)[i:i + FETCH_BATCH]) for f, us in by_folder.items()
                   for i in range(0, len(us), FETCH_BATCH)]
        say(f"Reading {len(hits)} messages")
        metas: list[MessageMeta] = []
        with ThreadPoolExecutor(self.workers) as ex:
            for res in ex.map(lambda b: self.source.fetch_meta(*b), batches):
                metas.extend(res)

        # 3. Message-level de-dup (same email in INBOX + label/folder copies).
        seen_mid: set[str] = set()
        unique: list[MessageMeta] = []
        for m in sorted(metas, key=lambda m: (m.folder, m.uid)):
            key = m.message_id.strip("<> ").lower() or f"{m.folder}|{m.uid}"
            if key in seen_mid:
                continue
            seen_mid.add(key)
            unique.append(m)
        stats["messages"] = len(unique)
        stats["duplicate_messages"] = len(metas) - len(unique)

        # 4. Explode to attachments and score each on the hexagon.
        candidates: list[Attachment] = []
        considered = 0
        for m in unique:
            for p in m.attachments:
                if p.inline and not req.include_inline:
                    continue
                considered += 1
                candidates.append(Attachment(
                    id=make_attachment_id(m.folder, m.uid, p.part), folder=m.folder, uid=m.uid,
                    part=p.part, filename=p.filename, mime=p.mime, size=p.size, encoding=p.encoding,
                    inline=p.inline, message_id=m.message_id, from_name=m.from_name,
                    from_email=m.from_email, subject=m.subject, date=m.date, snippet=m.snippet))
        stats["attachments_seen"] = considered

        matched: list[Attachment] = []
        pdf_retry: list[Attachment] = []
        for a in candidates:
            a.score, a.facets, ok = hexagon.score(a, req, types)
            if ok:
                matched.append(a)
            elif req.match_pdf_text and req.keywords and hexagon.ext_of(a) == "pdf" and self._only_keyword_failed(a):
                pdf_retry.append(a)

        # 5. Optional deep scan: look for the keyword inside PDF text.
        if pdf_retry:
            say(f"Scanning text of {len(pdf_retry)} PDFs")
            with ThreadPoolExecutor(self.workers) as ex:
                for a, text in zip(pdf_retry, ex.map(self._pdf_text, pdf_retry)):
                    a.score, a.facets, ok = hexagon.score(a, req, types, pdf_text=text)
                    if ok:
                        matched.append(a)
            stats["pdfs_scanned"] = len(pdf_retry)

        matched = [a for a in matched if a.score >= req.min_score]

        # 6. Attachment-level de-dup: same file (name + size) sent/forwarded again.
        matched.sort(key=_sort_key)                   # keep the earliest copy
        dupes = 0
        if req.hide_duplicates:
            keep: dict[tuple, Attachment] = {}
            for a in matched:
                k = (a.filename.strip().lower(), a.size // 64)   # tolerate tiny encoding diffs
                if k in keep:
                    keep[k].duplicates += 1
                    dupes += 1
                else:
                    keep[k] = a
            matched = list(keep.values())
        matched.sort(key=_sort_key, reverse=True)
        stats["duplicate_attachments"] = dupes
        stats["results"] = len(matched)
        stats["total_size"] = sum(a.size for a in matched)
        stats["seconds"] = round(time.perf_counter() - t0, 2)

        with self._lock:
            for a in matched:
                self.registry[a.id] = a
        return {"results": [a.to_json() for a in matched], "stats": stats}

    @staticmethod
    def _only_keyword_failed(a: Attachment) -> bool:
        f = a.facets
        return all(f[k] is None or f[k] >= (1 if k in ("date", "filetype") else hexagon.MATCH)
                   for k in ("sender", "filetype", "date"))

    def _pdf_text(self, a: Attachment) -> str:
        try:
            return pdf_text(self._bytes_for(a))
        except Exception:
            return ""

    # ------------------------------------------------------------------- bytes
    def get(self, att_id: str) -> Attachment:
        with self._lock:
            a = self.registry.get(att_id)
        if a is None:
            raise KeyError("Unknown attachment - run the search again")
        return a

    def fetch_bytes(self, att_id: str) -> bytes:
        cached = self.cache.get(att_id)
        if cached is not None:
            return cached
        return self._bytes_for(self.get(att_id))

    def _bytes_for(self, a: Attachment) -> bytes:
        cached = self.cache.get(a.id)
        if cached is not None:
            return cached
        data = self.source.fetch_part(a.folder, a.uid, a.part, a.encoding)
        self.cache.put(a.id, data)
        return data

    def fetch_many(self, ids: list[str]):
        """Yield (attachment, bytes | Exception) - parallel, order preserved."""
        def job(i):
            try:
                return self.fetch_bytes(i)
            except Exception as e:      # report per-file, keep going
                return e
        with ThreadPoolExecutor(self.workers) as ex:
            for i, data in zip(ids, ex.map(job, ids)):
                yield self.get(i), data

    def close(self):
        try:
            self.source.close()
        except Exception:
            pass


def pdf_text(data: bytes, max_pages: int = 5) -> str:
    try:
        from pypdf import PdfReader
    except ImportError:
        return ""
    try:
        r = PdfReader(io.BytesIO(data))
        return " ".join((p.extract_text() or "") for p in r.pages[:max_pages])
    except Exception:
        return ""


def now_utc() -> datetime:
    return datetime.now(timezone.utc)
