"""Search engine: live matrix search or instant index search, de-dup, scoring, analysis."""

from __future__ import annotations

import hashlib
import os
import threading
import time
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone

from . import hexagon, smartfields, textract
from .index import SYNC_BATCH, Index
from .matrix import build_cells, normalise_types
from .models import Attachment, MessageMeta, SearchRequest, make_attachment_id

FETCH_BATCH = 150
MAX_RESULTS = 5000
ANALYZE_EXTS = ["pdf", "png", "jpg", "jpeg", "tif", "tiff", "bmp", "webp", "docx", "xlsx", "csv", "txt"]


class Cancelled(Exception):
    pass


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

    def clear(self):
        with self.lock:
            self.data.clear()
            self.size = 0

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
    def __init__(self, source, workers: int = 6, index: Index | None = None):
        self.source = source
        self.workers = max(1, workers)
        self.index = index or Index(":memory:")
        self.registry: dict[str, Attachment] = {}
        self.cache = ByteCache()
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ search
    def search(self, req: SearchRequest, progress=None) -> dict:
        t0 = time.perf_counter()
        say = progress or (lambda *_: None)
        types = normalise_types(req.file_types)
        if req.source == "index":
            stats = {"mode": "index", "folders": req.folders or ["all indexed"], "cells": 0}
            raw = self.index.candidates(req, types, req.folders or None)
            # same email filed in several folders / labels -> keep one copy
            seen, candidates, msgs = set(), [], set()
            for a in sorted(raw, key=lambda a: (a.folder, a.uid)):
                mid = a.message_id.strip("<> ").lower() or f"{a.folder}|{a.uid}"
                if (mid, a.part) in seen:
                    continue
                seen.add((mid, a.part))
                msgs.add(mid)
                candidates.append(a)
            stats["unique_hits"] = stats["messages"] = len(msgs)
            stats["duplicate_messages"] = len({(a.message_id, a.folder) for a in raw}) - len(msgs)
        else:
            candidates, stats = self._live_candidates(req, say)
        stats["attachments_seen"] = len(candidates)
        results = self._finish(candidates, req, types, stats, say)
        stats["seconds"] = round(time.perf_counter() - t0, 3)
        return {"results": [a.to_json() for a in results], "stats": stats}

    def _live_candidates(self, req: SearchRequest, say) -> tuple[list[Attachment], dict]:
        folders = req.folders or self.source.default_folders()
        cells = build_cells(req)
        stats = {"mode": "live", "folders": folders, "cells": len(cells) * len(folders), "server_hits": 0}

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
        metas = self._fetch_metas(hits, say)

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

        # write-through: everything seen live goes into the index (searches get faster over time)
        self.index.upsert(unique)
        known = self.index.get_many([make_attachment_id(m.folder, m.uid, p.part) for m in unique for p in m.attachments])
        cands = []
        for m in unique:
            for p in m.attachments:
                aid = make_attachment_id(m.folder, m.uid, p.part)
                a = known.get(aid)
                if a is None:
                    a = Attachment(id=aid, folder=m.folder, uid=m.uid, part=p.part, filename=p.filename,
                                   mime=p.mime, size=p.size, encoding=p.encoding, inline=p.inline,
                                   message_id=m.message_id, from_name=m.from_name, from_email=m.from_email,
                                   subject=m.subject, date=m.date, snippet=m.snippet, to=m.to,
                                   fields=smartfields.quick(m.subject, m.snippet, p.filename))
                if a.inline and not req.include_inline:
                    continue
                cands.append(a)
        return cands, stats

    def _fetch_metas(self, hits, say, cancel=None, on_batch=None) -> list[MessageMeta]:
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
                if on_batch:
                    on_batch(res)
                if cancel is not None and cancel.is_set():
                    raise Cancelled()
        return metas

    def _finish(self, candidates: list[Attachment], req: SearchRequest, types, stats, say) -> list[Attachment]:
        matched: list[Attachment] = []
        retry: list[Attachment] = []
        for a in candidates:
            if not hexagon.passes_filters(a, req):
                continue
            a.score, a.facets, ok = hexagon.score(a, req, types)
            if ok:
                matched.append(a)
            elif (req.match_pdf_text and req.keywords and not a.text_source
                  and hexagon.ext_of(a) in ANALYZE_EXTS and self._only_keyword_failed(a)):
                retry.append(a)

        # Deep scan: read the files whose text we don't know yet (PDF text / OCR), then re-check.
        if retry:
            say(f"Reading text of {len(retry)} files")
            self.analyze([a for a in retry], ocr=True, register=False)
            for a in retry:
                a.score, a.facets, ok = hexagon.score(a, req, types)
                if ok and hexagon.passes_filters(a, req):
                    matched.append(a)
            stats["pdfs_scanned"] = len(retry)

        matched = [a for a in matched if a.score >= req.min_score]

        # Attachment-level de-dup: same file (content hash if known, else name + size).
        matched.sort(key=_sort_key)                   # keep the earliest copy
        dupes = 0
        if req.hide_duplicates:
            keep: dict[tuple, Attachment] = {}
            for a in matched:
                k = (a.filename.strip().lower(), a.size // 64)
                if k in keep:
                    keep[k].duplicates += 1
                    dupes += 1
                else:
                    keep[k] = a
            matched = list(keep.values())
        matched.sort(key=_sort_key, reverse=True)
        stats["duplicate_attachments"] = dupes
        stats["truncated"] = len(matched) > MAX_RESULTS
        matched = matched[:MAX_RESULTS]
        stats["results"] = len(matched)
        stats["total_size"] = sum(a.size for a in matched)
        stats["total_amount"] = round(sum(a.fields.get("amount") or 0 for a in matched), 2)
        with self._lock:
            for a in matched:
                self.registry[a.id] = a
        return matched

    @staticmethod
    def _only_keyword_failed(a: Attachment) -> bool:
        f = a.facets
        return all(f[k] is None or f[k] >= (1 if k in ("date", "filetype") else hexagon.MATCH)
                   for k in ("sender", "filetype", "date"))

    # --------------------------------------------------------------- indexing
    def sync(self, folders: list[str] | None = None, since=None, progress=None, cancel=None,
             full: bool = False) -> dict:
        """Incremental index sync: only mail newer than the last sync is read."""
        t0 = time.perf_counter()
        folders = folders or self.source.default_folders()
        report = {"folders": [], "added": 0, "removed": 0}
        say = progress or (lambda **_: None)
        for fi, folder in enumerate(folders):
            info = self.source.folder_info(folder)
            st = self.index.folder_state(folder)
            if full or (st and st["uidvalidity"] != info["uidvalidity"]):
                self.index.drop_folder(folder)            # server renumbered the folder
                st = None
            start = (st["last_uid"] + 1) if st else 1
            say(phase=f"Checking {folder}", folder=folder, done=0, total=0, step=fi + 1, steps=len(folders))
            uids = sorted(self.source.uids_since(folder, start, since))
            total = len(uids)
            cnt = {"atts": 0, "read": 0}

            def on_batch(metas, folder=folder, step=fi + 1):
                cnt["atts"] += self.index.upsert(metas)          # saved as we go - safe to cancel
                cnt["read"] = min(total, cnt["read"] + FETCH_BATCH)
                say(phase=f"Indexing {folder}", folder=folder, done=cnt["read"], total=total,
                    step=step, steps=len(folders))

            chunk = SYNC_BATCH * 8
            for i in range(0, total, chunk):
                part = uids[i:i + chunk]
                self._fetch_metas({(folder, u) for u in part}, lambda *_: None, cancel, on_batch)
                # checkpoint: a cancelled/interrupted sync resumes from here next time
                self.index.set_folder_state(folder, info["uidvalidity"], part[-1])
            removed = self.index.prune(folder, self.source.all_uids(folder)) if st else 0
            last = uids[-1] if uids else (st["last_uid"] if st else max(0, info["uidnext"] - 1))
            self.index.set_folder_state(folder, info["uidvalidity"], last)
            report["folders"].append({"folder": folder, "new_messages": total, "attachments": cnt["atts"],
                                      "removed": removed})
            report["added"] += cnt["atts"]
            report["removed"] += removed
        report["seconds"] = round(time.perf_counter() - t0, 2)
        report["stats"] = self.index.stats()
        return report

    # --------------------------------------------------------------- analysis
    def analyze(self, items: list, ocr: bool = True, progress=None, cancel=None, register: bool = True,
                force: bool = False) -> dict:
        """Read text (PDF text / OCR / Office), detect fields, store in the index."""
        atts: list[Attachment] = []
        for it in items:
            a = it if isinstance(it, Attachment) else self._lookup(it)
            if a is not None and (force or not a.text_source):
                atts.append(a)
        rep = {"analyzed": 0, "ocr": 0, "failed": 0, "total": len(atts)}
        if not atts:
            return rep
        lock = threading.Lock()

        def job(a: Attachment):
            if cancel is not None and cancel.is_set():
                return
            try:
                data = self._bytes_for(a)
                text, src = textract.extract_text(data, a.filename, a.mime, ocr=ocr)
                fields = smartfields.merge(a.fields, smartfields.extract(text, a.subject, a.filename))
                sha = hashlib.sha256(data).hexdigest()
                self.index.set_text(a.id, text, src, sha, fields)
                fresh = self.index.get(a.id)
                a.text, a.text_source, a.fields = text, src, (fresh.fields if fresh else fields)
                with lock:
                    rep["analyzed"] += 1
                    rep["ocr"] += src == "ocr"
            except Exception:
                with lock:
                    rep["failed"] += 1
            if progress:
                with lock:
                    progress(done=rep["analyzed"] + rep["failed"], total=rep["total"], file=a.filename)

        n = min(self.workers, max(1, (os.cpu_count() or 2)))
        with ThreadPoolExecutor(n) as ex:
            list(ex.map(job, atts))
        if register:
            with self._lock:
                for a in atts:
                    if a.id in self.registry:
                        self.registry[a.id].text = a.text
                        self.registry[a.id].text_source = a.text_source
                        self.registry[a.id].fields = a.fields
        if cancel is not None and cancel.is_set():
            rep["cancelled"] = True
        return rep

    def analyze_all(self, ocr: bool = True, progress=None, cancel=None) -> dict:
        ids = self.index.ids_to_analyze(ANALYZE_EXTS)
        return self.analyze(ids, ocr=ocr, progress=progress, cancel=cancel)

    def document(self, att_id: str, ocr: bool = True) -> dict:
        """Text + fields + tables for the reader pane."""
        a = self.get(att_id)
        if not a.text_source:
            self.analyze([a], ocr=ocr)
        data = self._bytes_for(a)
        tables = textract.tables_for(data, a.filename, a.mime, a.text)
        return {"text": a.text, "source": a.text_source, "fields": a.fields, "tables": tables,
                "ocr_engine": textract.ocr_engine()}

    def set_field(self, att_id: str, field: str, value) -> dict:
        self.index.set_user_field(att_id, field, value)
        fresh = self.index.get(att_id)
        a = self.get(att_id)
        if fresh:
            a.fields = fresh.fields
        return a.fields

    # ------------------------------------------------------------------- bytes
    def _lookup(self, att_id: str) -> Attachment | None:
        with self._lock:
            a = self.registry.get(att_id)
        if a is None:
            a = self.index.get(att_id)
            if a is not None:
                with self._lock:
                    self.registry[att_id] = a
        return a

    def get(self, att_id: str) -> Attachment:
        a = self._lookup(att_id)
        if a is None:
            raise KeyError("Unknown attachment - run the search again")
        return a

    def fetch_bytes(self, att_id: str) -> bytes:
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
        try:
            self.index.close()
        except Exception:
            pass


def pdf_text(data: bytes, max_pages: int = 5) -> str:
    return textract.pdf_text(data, max_pages=max_pages)


def now_utc() -> datetime:
    return datetime.now(timezone.utc)
