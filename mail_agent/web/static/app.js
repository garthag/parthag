"use strict";
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const FACETS = ["sender", "subject", "body", "filename", "filetype", "date"];
const FACET_LABEL = { sender: "Sender", subject: "Subject", body: "Body/PDF", filename: "File name", filetype: "Type", date: "Date" };

let rows = [], view = [], selected = new Set(), lastClick = -1, activeId = null;
let sortKey = "date", sortDir = -1;

function store(k, v) { try { if (v === undefined) return JSON.parse(localStorage.getItem(k)); localStorage.setItem(k, JSON.stringify(v)); } catch { return null; } }

async function api(path, body) {
  const opt = body === undefined ? {} : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
  const r = await fetch(path, opt);
  if ((r.headers.get("content-type") || "").includes("application/json")) {
    const j = await r.json();
    if (!r.ok) throw new Error(j.error || r.statusText);
    return j;
  }
  if (!r.ok) throw new Error(r.statusText);
  return r;
}
function busy(on, msg) { $("#busy").classList.toggle("hidden", !on); if (msg) $("#busyMsg").textContent = msg; }
function size(n) { if (n < 1024) return n + " B"; if (n < 1048576) return (n / 1024).toFixed(1) + " KB"; return (n / 1048576).toFixed(1) + " MB"; }
function fmtDate(iso) { return iso ? iso.slice(0, 10) : "—"; }

/* ---------- hexagon radar (6 facets) ---------- */
function hexSVG(f, px = 30) {
  const c = px / 2, R = px / 2 - 2;
  const pt = (i, r) => { const a = -Math.PI / 2 + i * Math.PI / 3; return [c + r * Math.cos(a), c + r * Math.sin(a)]; };
  const poly = (r) => FACETS.map((_, i) => pt(i, r).map((v) => v.toFixed(1)).join(",")).join(" ");
  const vals = FACETS.map((k, i) => pt(i, R * (f[k] == null ? 0.15 : Math.max(0.08, f[k]))).map((v) => v.toFixed(1)).join(",")).join(" ");
  const tip = FACETS.map((k) => `${FACET_LABEL[k]}: ${f[k] == null ? "not used" : Math.round(f[k] * 100) + "%"}`).join("\n");
  return `<svg width="${px}" height="${px}" viewBox="0 0 ${px} ${px}" role="img" aria-label="match hexagon"><title>${esc(tip)}</title>
    <polygon points="${poly(R)}" fill="none" stroke="var(--grid)" stroke-width="1"/>
    <polygon points="${poly(R / 2)}" fill="none" stroke="var(--grid)" stroke-width=".6"/>
    <polygon points="${vals}" fill="var(--hexfill)" stroke="var(--hex)" stroke-width="1.4" stroke-linejoin="round"/></svg>`;
}

/* ---------- boot / login ---------- */
async function boot() {
  const s = await api("/api/status");
  $("#dest").value = store("dest") || s.download_dir;
  $("#ftpl").value = store("ftpl") || "sender/year-month";
  $("#ntpl").value = store("ntpl") || "original";
  if (!s.connected) {
    $("#login").classList.remove("hidden"); $("#app").classList.add("hidden");
    $("#useEnv").classList.toggle("hidden", !s.env_login);
    $("#savedBox").classList.toggle("hidden", !s.saved_account);
    $("#useSaved").textContent = `Continue as ${s.saved_account}`;
    $("#rememberRow").classList.toggle("hidden", !s.can_remember);
    $("#account").innerHTML = "";
    return;
  }
  $("#login").classList.add("hidden"); $("#app").classList.remove("hidden");
  $("#account").innerHTML = `<span>${esc(s.account)}${s.gmail ? " · Gmail fast search" : ""}</span>` +
    (s.demo ? "" : `<button class="btn ghost" id="logout">Disconnect</button>`);
  $("#logout")?.addEventListener("click", async () => { await api("/api/disconnect", {}); boot(); });
  try {
    const f = await api("/api/folders");
    $("#folders").innerHTML = f.folders.map((x) => `<option ${f.default.includes(x) ? "selected" : ""}>${esc(x)}</option>`).join("");
  } catch { /* folder list is optional */ }
  restoreForm(); renderHistory();
}
$("#provider").addEventListener("change", (e) => {
  $("#customHost").classList.toggle("hidden", e.target.value !== "custom");
  const hints = {
    gmail: "Gmail: turn on 2-step verification → <b>myaccount.google.com/apppasswords</b> → create one → paste it here.",
    outlook: "Outlook: use an app password from <b>account.microsoft.com/security</b> (IMAP must be enabled).",
    yahoo: "Yahoo: Account security → <b>Generate app password</b>.",
    zoho: "Zoho: enable IMAP in Mail settings, then create an app-specific password.",
    "zoho.in": "Zoho: enable IMAP in Mail settings, then create an app-specific password.",
    icloud: "iCloud: create an app-specific password at <b>appleid.apple.com</b>.",
  };
  $("#pwHint").innerHTML = hints[e.target.value] || "Use your IMAP password or an app password.";
});
$("#loginForm").addEventListener("submit", async (e) => {
  e.preventDefault(); $("#loginErr").textContent = "";
  const fd = Object.fromEntries(new FormData(e.target));
  busy(true, "Connecting…");
  try { await api("/api/connect", fd); e.target.password.value = ""; await boot(); }
  catch (er) { $("#loginErr").textContent = er.message; }
  finally { busy(false); }
});
async function quickConnect(body) {
  busy(true, "Connecting…"); $("#loginErr").textContent = "";
  try { await api("/api/connect", body); await boot(); }
  catch (er) { $("#loginErr").textContent = er.message; }
  finally { busy(false); }
}
$("#useEnv").addEventListener("click", () => quickConnect({ use_env: true }));
$("#useSaved").addEventListener("click", () => quickConnect({ use_saved: true }));
$("#forget").addEventListener("click", async () => { await api("/api/forget", {}); boot(); });

/* ---------- desktop-app extras (native dialogs via pywebview) ---------- */
const desk = () => (window.pywebview && window.pywebview.api) || null;
function onDesktopReady() { $$(".desk-only").forEach((el) => el.classList.remove("hidden")); }
window.addEventListener("pywebviewready", onDesktopReady);
if (desk()) onDesktopReady();
$("#browse").addEventListener("click", async () => {
  const p = await desk()?.pick_folder($("#dest").value);
  if (p) { $("#dest").value = p; store("dest", p); }
});
function openBtn(path) {
  return desk() ? `<button class="btn" data-open="${esc(path)}">Open folder</button>` : "";
}
$("#dlReport").addEventListener("click", (e) => { const p = e.target.dataset.open; if (p) desk()?.open_folder(p); });

/* ---------- search form ---------- */
function iso(d) { return d.toISOString().slice(0, 10); }
$("#quickDates").addEventListener("click", (e) => {
  const q = e.target.dataset.q; if (!q) return;
  const t = new Date(), y = t.getFullYear(), fyStart = t.getMonth() >= 3 ? y : y - 1;
  const set = (a, b) => { $("[name=date_from]").value = a; $("[name=date_to]").value = b; };
  if (q === "fy") set(`${fyStart}-04-01`, `${fyStart + 1}-03-31`);
  if (q === "lfy") set(`${fyStart - 1}-04-01`, `${fyStart}-03-31`);
  if (q === "y") set(`${y}-01-01`, `${y}-12-31`);
  if (q === "ly") set(`${y - 1}-01-01`, `${y - 1}-12-31`);
  if (q === "90") set(iso(new Date(Date.now() - 90 * 864e5)), iso(t));
  if (q === "clr") set("", "");
});
$("[name=min_score]").addEventListener("input", (e) => { $("#msOut").textContent = Math.round(e.target.value * 100) + "%"; });

function readForm() {
  const f = $("#searchForm");
  const types = $$("[name=t]:checked", f).map((x) => x.value).concat(f.custom_types.value.split(/[,\s]+/).filter(Boolean));
  return {
    senders: f.senders.value, keywords: f.keywords.value, exclude: f.exclude.value,
    date_from: f.date_from.value, date_to: f.date_to.value, file_types: types,
    folders: $$("#folders option:checked").map((o) => o.value),
    match_pdf_text: f.match_pdf_text.checked, hide_duplicates: f.hide_duplicates.checked,
    include_inline: f.include_inline.checked, min_score: +f.min_score.value, window_months: +f.window_months.value,
  };
}
function fillForm(q) {
  const f = $("#searchForm");
  for (const k of ["senders", "keywords", "exclude", "date_from", "date_to"]) f[k].value = q[k] || "";
  const groups = $$("[name=t]", f).map((x) => x.value);
  $$("[name=t]", f).forEach((x) => (x.checked = (q.file_types || []).includes(x.value)));
  f.custom_types.value = (q.file_types || []).filter((t) => !groups.includes(t)).join(", ");
  f.match_pdf_text.checked = !!q.match_pdf_text; f.hide_duplicates.checked = q.hide_duplicates !== false;
  f.include_inline.checked = !!q.include_inline;
}
function restoreForm() { const q = store("lastQuery"); if (q) fillForm(q); }
function renderHistory() {
  const h = store("history") || [];
  $("#history").innerHTML = h.length ? "<div class='muted'>Recent searches</div>" + h.map((q, i) =>
    `<a data-i="${i}">${esc([q.senders, q.keywords, q.date_from && `${q.date_from}→${q.date_to}`].filter(Boolean).join(" · ") || "search")}</a>`).join("") : "";
}
$("#history").addEventListener("click", (e) => { const i = e.target.dataset.i; if (i != null) { fillForm(store("history")[i]); $("#searchForm").requestSubmit(); } });

$("#searchForm").addEventListener("submit", async (e) => {
  e.preventDefault(); $("#searchErr").textContent = "";
  const q = readForm();
  busy(true, q.match_pdf_text ? "Searching mail + reading PDFs…" : "Searching…");
  try {
    const res = await api("/api/search", q);
    store("lastQuery", q);
    const key = JSON.stringify([q.senders, q.keywords, q.date_from, q.date_to]);
    const h = (store("history") || []).filter((x) => JSON.stringify([x.senders, x.keywords, x.date_from, x.date_to]) !== key);
    store("history", [q, ...h].slice(0, 8)); renderHistory();
    rows = res.results; selected.clear(); lastClick = -1;
    const s = res.stats;
    $("#stats").innerHTML = `<b>${s.results}</b> attachment${s.results === 1 ? "" : "s"} · <b>${size(s.total_size)}</b> · from <b>${s.messages}</b> emails in <b>${s.seconds}s</b>
      <span class="badge">${s.cells} matrix cells</span>
      <span class="badge" title="same email in several folders/labels">${s.duplicate_messages} duplicate emails removed</span>
      <span class="badge" title="same file sent again">${s.duplicate_attachments} duplicate files removed</span>
      ${s.pdfs_scanned ? `<span class="badge">${s.pdfs_scanned} PDFs read</span>` : ""}`;
    $("#dlReport").classList.add("hidden");
    render();
  } catch (er) { $("#searchErr").textContent = er.message; }
  finally { busy(false); }
});

/* ---------- table ---------- */
function render() {
  const f = $("#filter").value.trim().toLowerCase();
  view = rows.filter((r) => !f || `${r.filename} ${r.from_name} ${r.from_email} ${r.subject} ${r.date}`.toLowerCase().includes(f));
  view.sort((a, b) => {
    const x = a[sortKey] ?? "", y = b[sortKey] ?? "";
    return (x < y ? -1 : x > y ? 1 : 0) * sortDir;
  });
  $("#tbl tbody").innerHTML = view.map((r, i) => `
    <tr data-i="${i}" class="${selected.has(r.id) ? "sel" : ""} ${r.id === activeId ? "active" : ""}">
      <td class="c"><input type="checkbox" ${selected.has(r.id) ? "checked" : ""} aria-label="select"></td>
      <td><div class="hexcell">${hexSVG(r.facets)}<span>${Math.round(r.score * 100)}%</span></div></td>
      <td><div class="fn"><span class="ext x-${esc(r.ext)}">${esc(r.ext || "file")}</span>${esc(r.filename)}${r.duplicates ? `<span class="badge" title="identical copies hidden">+${r.duplicates} dup</span>` : ""}</div>
          <div class="sub">${esc(r.subject)}</div></td>
      <td class="cf"><div>${esc(r.from_name)}</div><div class="sub hide-sm">${esc(r.from_email)}</div></td>
      <td class="nw">${fmtDate(r.date)}</td>
      <td class="r nw">${size(r.size)}</td>
      <td><button class="btn ghost pv" title="Preview">👁</button></td>
    </tr>`).join("");
  $("#empty").classList.toggle("hidden", !!view.length || !rows.length);
  $$("#tbl th[data-sort]").forEach((th) => { th.textContent = th.textContent.replace(/ [▴▾]$/, "") + (th.dataset.sort === sortKey ? (sortDir < 0 ? " ▾" : " ▴") : ""); });
  updateSel();
}
function updateSel() {
  const bytes = rows.filter((r) => selected.has(r.id)).reduce((a, r) => a + r.size, 0);
  $("#selInfo").textContent = `${selected.size} selected${selected.size ? " · " + size(bytes) : ""}`;
  $("#saveBtn").disabled = $("#zipBtn").disabled = !selected.size;
  $("#chkAll").checked = view.length > 0 && view.every((r) => selected.has(r.id));
  $("#pvSel").textContent = activeId && selected.has(activeId) ? "Selected ✓" : "Select";
}
function toggle(id, on) { (on ?? !selected.has(id)) ? selected.add(id) : selected.delete(id); }

$("#tbl tbody").addEventListener("click", (e) => {
  const tr = e.target.closest("tr"); if (!tr) return;
  const i = +tr.dataset.i, r = view[i];
  if (e.target.closest(".pv")) { openPreview(r.id); return; }
  if (e.shiftKey && lastClick >= 0) {            // shift-click = range select
    const [a, b] = [Math.min(lastClick, i), Math.max(lastClick, i)];
    const on = !selected.has(r.id);
    for (let k = a; k <= b; k++) toggle(view[k].id, on);
  } else if (e.target.type === "checkbox" || e.ctrlKey || e.metaKey) {
    toggle(r.id);
  } else {
    openPreview(r.id); return;
  }
  lastClick = i; render();
});
$("#tbl thead").addEventListener("click", (e) => {
  const th = e.target.closest("th[data-sort]"); if (!th) return;
  sortDir = th.dataset.sort === sortKey ? -sortDir : (th.dataset.sort === "date" || th.dataset.sort === "score" ? -1 : 1);
  sortKey = th.dataset.sort; render();
});
$("#chkAll").addEventListener("click", (e) => { e.stopPropagation(); const on = e.target.checked; view.forEach((r) => toggle(r.id, on)); render(); });
$("#selAll").addEventListener("click", () => { view.forEach((r) => selected.add(r.id)); render(); });
$("#selNone").addEventListener("click", () => { selected.clear(); render(); });
$("#selInv").addEventListener("click", () => { view.forEach((r) => toggle(r.id)); render(); });
$("#filter").addEventListener("input", render);

/* ---------- preview ---------- */
const PREVIEWABLE = ["pdf", "png", "jpg", "jpeg", "gif", "webp", "bmp", "txt", "csv", "log", "json", "xml", "eml", "html", "htm"];
async function openPreview(id) {
  activeId = id;
  const r = rows.find((x) => x.id === id); if (!r) return;
  $("#preview").classList.remove("hidden"); $("#app").classList.add("with-preview");
  $("#pvName").textContent = r.filename;
  $("#pvMeta").textContent = `${r.from_name} · ${fmtDate(r.date)} · ${size(r.size)} · ${r.folder}`;
  $("#pvHex").innerHTML = hexSVG(r.facets, 86) + "<table>" + FACETS.map((k) =>
    `<tr><td class="muted">${FACET_LABEL[k]}</td><td>${r.facets[k] == null ? "—" : Math.round(r.facets[k] * 100) + "%"}</td></tr>`).join("") + "</table>" +
    `<div class="sub pvsnip">${esc(r.subject)}<br>${esc(r.snippet.slice(0, 180))}</div>`;
  const isPdf = (r.ext || (r.mime === "application/pdf" ? "pdf" : "")) === "pdf";
  $("#pvTabs").classList.toggle("hidden", !isPdf);
  if (isPdf && pvMode === "text") return showPdfText(id);
  $$("#pvTabs button").forEach((b) => b.classList.toggle("on", b.dataset.m === "doc"));
  const url = `/api/preview/${encodeURIComponent(id)}`;
  const body = $("#pvBody");
  const ext = r.ext || (r.mime === "application/pdf" ? "pdf" : "");
  if (ext === "pdf") body.innerHTML = `<iframe src="${url}#view=FitH" title="preview"></iframe>`;
  else if (r.mime.startsWith("image/") && r.mime !== "image/svg+xml") body.innerHTML = `<img src="${url}" alt="">`;
  else if (PREVIEWABLE.includes(ext) || r.mime.startsWith("text/")) {
    body.innerHTML = "<div class='none'>Loading…</div>";
    try { const t = await (await fetch(url)).text(); if (activeId === id) body.innerHTML = `<pre>${esc(t)}</pre>`; }
    catch { body.innerHTML = "<div class='none'>Could not load preview.</div>"; }
  } else body.innerHTML = `<div class="none">No preview for .${esc(ext || "?")} files.<br>Select it and download to open.</div>`;
  render();
}
let pvMode = "doc";
async function showPdfText(id) {
  $$("#pvTabs button").forEach((b) => b.classList.toggle("on", b.dataset.m === "text"));
  const body = $("#pvBody");
  body.innerHTML = "<div class='none'>Reading PDF…</div>";
  try {
    const j = await api(`/api/pdftext/${encodeURIComponent(id)}`);
    if (activeId === id) body.innerHTML = j.text.trim() ? `<pre>${esc(j.text)}</pre>` : "<div class='none'>No text layer (scanned image PDF).</div>";
  } catch (er) { body.innerHTML = `<div class='none'>${esc(er.message)}</div>`; }
  render();
}
$("#pvTabs").addEventListener("click", (e) => {
  const m = e.target.dataset.m;
  if (!m || !activeId) return;
  pvMode = m; openPreview(activeId);
});
function closePreview() { activeId = null; $("#preview").classList.add("hidden"); $("#app").classList.remove("with-preview"); $("#pvBody").innerHTML = ""; render(); }
function step(d) { const i = view.findIndex((r) => r.id === activeId); const n = view[i + d]; if (n) openPreview(n.id); }
$("#pvClose").addEventListener("click", closePreview);
$("#pvPrev").addEventListener("click", () => step(-1));
$("#pvNext").addEventListener("click", () => step(1));
$("#pvSel").addEventListener("click", () => { if (activeId) { toggle(activeId); render(); } });
document.addEventListener("keydown", (e) => {
  if (!activeId || ["INPUT", "SELECT", "TEXTAREA"].includes(document.activeElement.tagName)) return;
  if (e.key === "Escape") closePreview();
  else if (e.key === "ArrowDown" || e.key === "ArrowRight" || e.key === "j") { e.preventDefault(); step(1); }
  else if (e.key === "ArrowUp" || e.key === "ArrowLeft" || e.key === "k") { e.preventDefault(); step(-1); }
  else if (e.key === " ") { e.preventDefault(); toggle(activeId); render(); }
});

/* ---------- download ---------- */
function dlOpts() {
  store("dest", $("#dest").value); store("ftpl", $("#ftpl").value); store("ntpl", $("#ntpl").value);
  return { ids: [...selected], dest: $("#dest").value, folder_template: $("#ftpl").value, name_template: $("#ntpl").value };
}
$("#saveBtn").addEventListener("click", async () => {
  busy(true, `Saving ${selected.size} files…`);
  const rep = $("#dlReport");
  try {
    const r = await api("/api/download", dlOpts());
    rep.innerHTML = `<div class="ok">✔ Saved ${r.saved.length} file(s) to <b>${esc(r.folder)}</b>${openBtn(r.folder)}</div>` +
      (r.skipped.length ? `<div class="warn">↷ ${r.skipped.length} skipped – identical file already in the folder</div>` : "") +
      r.failed.map((f) => `<div class="bad">✖ ${esc(f.filename)}: ${esc(f.error)}</div>`).join("") +
      `<div class="muted small">A manifest.csv with sender, subject, date and SHA-256 of every saved file is kept in that folder.</div>`;
  } catch (er) { rep.innerHTML = `<div class="bad">✖ ${esc(er.message)}</div>`; }
  finally { rep.classList.remove("hidden"); busy(false); }
});
$("#zipBtn").addEventListener("click", async () => {
  if (desk()) {                                  // desktop app: native "Save as" dialog
    const stamp = new Date().toISOString().slice(0, 16).replace(/[-:]/g, "").replace("T", "-");
    const path = await desk().save_zip_path(`mail-attachments-${stamp}.zip`, $("#dest").value);
    if (!path) return;
    busy(true, `Packing ${selected.size} files…`);
    try {
      const r = await api("/api/zip", { ...dlOpts(), save_to: path });
      $("#dlReport").innerHTML = `<div class="ok">✔ ZIP saved: <b>${esc(r.path)}</b> – ${r.added} files` +
        `${r.skipped ? `, ${r.skipped} duplicate skipped` : ""}${r.failed ? `, ${r.failed} failed` : ""}${openBtn(r.path)}</div>`;
    } catch (er) { $("#dlReport").innerHTML = `<div class="bad">✖ ${esc(er.message)}</div>`; }
    finally { $("#dlReport").classList.remove("hidden"); busy(false); }
    return;
  }
  busy(true, `Packing ${selected.size} files…`);
  try {
    const r = await api("/api/zip", dlOpts());
    const blob = await r.blob();
    const name = (r.headers.get("content-disposition") || "").match(/filename="([^"]+)"/)?.[1] || "attachments.zip";
    const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(blob), download: name });
    document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    $("#dlReport").innerHTML = `<div class="ok">✔ ZIP ready: ${esc(r.headers.get("x-zip-report") || "")}</div>`;
  } catch (er) { $("#dlReport").innerHTML = `<div class="bad">✖ ${esc(er.message)}</div>`; }
  finally { $("#dlReport").classList.remove("hidden"); busy(false); }
});

boot().catch((e) => { document.body.insertAdjacentHTML("beforeend", `<p class="err" style="padding:20px">${esc(e.message)}</p>`); });
