"use strict";
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const FACETS = ["sender", "subject", "body", "filename", "filetype", "date"];
const FACET_LABEL = { sender: "Sender", subject: "Subject", body: "Body/text", filename: "File name", filetype: "Type", date: "Date" };
const DOC = { invoice: "Invoice", receipt: "Receipt", statement: "Statement", payslip: "Payslip", quotation: "Quotation", order: "Order", credit_note: "Credit note", other: "Other" };

let rows = [], view = [], selected = new Set(), lastClick = -1, activeId = null;
let sortKey = "date", sortDir = -1, sheetSort = "date", sheetDir = -1;
let status = {}, pvMode = "doc", docCache = new Map();

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
let toastT;
function toast(msg, ms = 3500) { const t = $("#toast"); t.textContent = msg; t.classList.remove("hidden"); clearTimeout(toastT); toastT = setTimeout(() => t.classList.add("hidden"), ms); }
function size(n) { if (n < 1024) return n + " B"; if (n < 1048576) return (n / 1024).toFixed(1) + " KB"; if (n < 1073741824) return (n / 1048576).toFixed(1) + " MB"; return (n / 1073741824).toFixed(2) + " GB"; }
function fmtDate(iso) { return iso ? iso.slice(0, 10) : "—"; }
function money(v, cur) { if (v == null || v === "") return ""; const s = Number(v).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 }); return (cur && cur !== "INR" ? cur + " " : cur === "INR" ? "₹" : "") + s; }
function ago(ts) { if (!ts) return "never"; const s = Date.now() / 1000 - ts; if (s < 90) return "just now"; if (s < 3600) return Math.round(s / 60) + " min ago"; if (s < 86400) return Math.round(s / 3600) + " h ago"; return Math.round(s / 86400) + " days ago"; }
const byId = (id) => rows.find((x) => x.id === id);

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

/* ---------- desktop-app extras (native dialogs via pywebview) ---------- */
const desk = () => (window.pywebview && window.pywebview.api) || null;
function onDesktopReady() { $$(".desk-only").forEach((el) => el.classList.remove("hidden")); }
window.addEventListener("pywebviewready", onDesktopReady);
if (desk()) onDesktopReady();
function openBtns(path, isFile) {
  if (!desk()) return "";
  return (isFile ? `<button class="btn" data-openfile="${esc(path)}">Open</button>` : "") + `<button class="btn" data-open="${esc(path)}">Show in folder</button>`;
}
document.addEventListener("click", (e) => {
  const f = e.target.dataset?.openfile, d = e.target.dataset?.open;
  if (f) desk()?.open_path(f); else if (d) desk()?.open_folder(d);
});

/* Save a generated file: desktop -> native "Save as" dialog; browser -> normal download. */
async function saveFile(endpoint, body, name, kind) {
  if (desk()) {
    const path = await desk().save_path(name, kind, $("#dest").value);
    if (!path) return null;
    return await api(endpoint, { ...body, save_to: path });
  }
  const r = await api(endpoint, body);
  const blob = await r.blob();
  const fn = (r.headers.get("content-disposition") || "").match(/filename="([^"]+)"/)?.[1] || name;
  const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(blob), download: fn });
  document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  return { downloaded: fn, headers: r.headers };
}
function report(html) { const r = $("#dlReport"); r.innerHTML = html; r.classList.remove("hidden"); }

/* ---------- boot / login ---------- */
async function boot() {
  status = await api("/api/status");
  $("#dest").value = store("dest") || status.download_dir;
  $("#ftpl").value = store("ftpl") || "sender/year-month";
  $("#ntpl").value = store("ntpl") || "original";
  if (!status.connected) {
    $("#login").classList.remove("hidden"); $("#app").classList.add("hidden"); $("#tabs").classList.add("hidden");
    $("#useEnv").classList.toggle("hidden", !status.env_login);
    $("#savedBox").classList.toggle("hidden", !status.saved_account);
    $("#useSaved").textContent = `Continue as ${status.saved_account}`;
    $("#rememberRow").classList.toggle("hidden", !status.can_remember);
    $("#account").innerHTML = "";
    return;
  }
  $("#login").classList.add("hidden"); $("#app").classList.remove("hidden"); $("#tabs").classList.remove("hidden");
  $("#account").innerHTML = `<span>${esc(status.account)}${status.gmail ? " · Gmail" : ""}</span>` +
    (status.demo ? "" : ` <button class="btn ghost" id="logout">Disconnect</button>`);
  $("#logout")?.addEventListener("click", async () => { await api("/api/disconnect", {}); boot(); });
  try {
    const f = await api("/api/folders");
    const opts = f.folders.map((x) => `<option ${f.default.includes(x) ? "selected" : ""}>${esc(x)}</option>`).join("");
    $("#folders").innerHTML = opts; $("#idxFolders").innerHTML = f.folders.map((x) => `<option>${esc(x)}</option>`).join("");
  } catch { /* folder list is optional */ }
  restoreForm(); renderHistory(); renderIndex(status.index);
  const n = status.index?.attachments || 0;
  setSource(store("source") || (n ? "index" : "live"));
  $("#autoSync").checked = store("autoSync") !== false;
  if (status.jobs?.length) status.jobs.forEach(watchJob);
  else if (n && !status.demo && $("#autoSync").checked) startSync(false, true);
  else if (!n && !status.demo) { showView("index"); toast("Tip: build the local index once for instant searches."); }
}
const HINTS = {
  gmail: ["App password (16 letters – spaces are fine)", "Gmail / Google Workspace: turn on 2-Step Verification, then create an App Password at <b>myaccount.google.com/apppasswords</b> and paste it here. Your normal password won't work."],
  outlook: ["App password", "Microsoft: create an App Password at <b>account.microsoft.com/security</b>. Company accounts: IT must allow IMAP."],
  yahoo: ["App password", "Yahoo: Account security → <b>Generate app password</b>."],
  zoho: ["Password (or app-specific password)", "Zoho: enable IMAP in Mail → Settings → Mail Accounts → IMAP. With 2-factor login, use an app-specific password."],
  icloud: ["App-specific password", "iCloud: create an app-specific password at <b>appleid.apple.com</b>."],
  own: ["Email password", "Use the same password you use for webmail."],
};
HINTS["zoho.in"] = HINTS.zoho; HINTS.rediff = HINTS.titan = HINTS.godaddy = HINTS.hostinger = HINTS.custom = HINTS.own;
const PROV_HINT = { google: "gmail", microsoft: "outlook", yahoo: "yahoo", zoho: "zoho", "zoho.in": "zoho", "zoho.eu": "zoho", icloud: "icloud" };
function setHint(key, extra) {
  const [label, text] = HINTS[key] || HINTS.own;
  $("#pwLabel").textContent = label; $("#pwHint").innerHTML = text + (extra ? "<br>" + extra : "");
}
let detectT, lastDetected = null;
async function runDetect() {
  const email = $("#loginForm").user.value.trim(), info = $("#detectInfo");
  if ($("#provider").value !== "auto" || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) { info.classList.add("hidden"); return; }
  info.className = "detect"; info.textContent = "Finding the mail server for " + email.split("@")[1] + "…";
  try {
    const d = await api("/api/detect?email=" + encodeURIComponent(email));
    if ($("#loginForm").user.value.trim() !== email) return;
    lastDetected = d;
    if (d.found) {
      info.innerHTML = `✔ <b>${esc(d.label)}</b> <span class="muted">· ${esc(d.host)}</span>`;
      setHint(PROV_HINT[d.provider] || "own");
    } else {
      info.className = "detect bad"; info.textContent = d.reason;
      $("#provider").value = "custom"; $("#customHost").classList.remove("hidden"); setHint("custom");
    }
  } catch (er) { info.className = "detect bad"; info.textContent = er.message; }
}
$("#loginForm").user.addEventListener("input", () => { clearTimeout(detectT); detectT = setTimeout(runDetect, 600); });
$("#loginForm").user.addEventListener("change", runDetect);
$("#provider").addEventListener("change", (e) => {
  const v = e.target.value;
  $("#customHost").classList.toggle("hidden", v !== "custom");
  if (v === "auto") { setHint("own"); $("#pwHint").innerHTML = "Type your email address – the mail server is found automatically."; runDetect(); }
  else { $("#detectInfo").classList.add("hidden"); setHint(v); }
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

/* ---------- views ---------- */
function showView(v) {
  $$("#tabs button").forEach((b) => b.classList.toggle("on", b.dataset.view === v));
  $("#viewSearch").classList.toggle("hidden", v !== "search");
  $("#viewTable").classList.toggle("hidden", v !== "table");
  $("#viewIndex").classList.toggle("hidden", v !== "index");
  if (v !== "search") closePreview(true);
  if (v === "table") renderSheet();
  if (v === "index") refreshIndex();
}
$("#tabs").addEventListener("click", (e) => { const v = e.target.closest("button")?.dataset.view; if (v) showView(v); });

/* ---------- search form ---------- */
function setSource(v) {
  const n = status.index?.attachments || 0;
  if (v === "index" && !n) v = "live";
  $$("[name=source]").forEach((r) => (r.checked = r.value === v));
  const i = status.index;
  $("#srcHint").innerHTML = v === "index"
    ? (n ? `${n.toLocaleString()} attachments indexed · synced ${ago(i.last_sync)}` : "")
    : (n ? "Searches the mail server directly (slower, always up to date)." : `Index empty – <a href="#" id="goIdx">build it</a> for instant search.`);
  $("#goIdx")?.addEventListener("click", (e) => { e.preventDefault(); showView("index"); });
}
$$("[name=source]").forEach((r) => r.addEventListener("change", () => { store("source", r.value); setSource(r.value); }));

function iso(d) { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; }
$("#quickDates").addEventListener("click", (e) => {
  const q = e.target.dataset.q; if (!q) return;
  const t = new Date(), y = t.getFullYear(), fyStart = t.getMonth() >= 3 ? y : y - 1;
  const set = (a, b) => { $("[name=date_from]").value = a; $("[name=date_to]").value = b; };
  if (q === "fy") set(`${fyStart}-04-01`, `${fyStart + 1}-03-31`);
  if (q === "lfy") set(`${fyStart - 1}-04-01`, `${fyStart}-03-31`);
  if (q === "m") set(iso(new Date(y, t.getMonth(), 1)), iso(t));
  if (q === "y") set(`${y}-01-01`, `${y}-12-31`);
  if (q === "ly") set(`${y - 1}-01-01`, `${y - 1}-12-31`);
  if (q === "90") set(iso(new Date(Date.now() - 90 * 864e5)), iso(t));
  if (q === "clr") set("", "");
});
$("[name=min_score]").addEventListener("input", (e) => { $("#msOut").textContent = Math.round(e.target.value * 100) + "%"; });

const TEXT_FIELDS = ["senders", "recipients", "keywords", "exclude", "date_from", "date_to", "filename_pattern", "size_min_mb", "size_max_mb", "amount_min", "amount_max"];
function readForm() {
  const f = $("#searchForm");
  const types = $$("[name=t]:checked", f).map((x) => x.value).concat(f.custom_types.value.split(/[,\s]+/).filter(Boolean));
  const q = { source: $("[name=source]:checked").value, file_types: types, doc_types: $$("[name=dt]:checked", f).map((x) => x.value),
    folders: $$("#folders option:checked").map((o) => o.value), match_mode: f.match_mode.value, text_state: f.text_state.value,
    fuzzy: f.fuzzy.checked, match_pdf_text: f.match_pdf_text.checked, hide_duplicates: f.hide_duplicates.checked,
    include_inline: f.include_inline.checked, min_score: +f.min_score.value, window_months: +f.window_months.value };
  for (const k of TEXT_FIELDS) q[k] = f[k].value.trim();
  return q;
}
function fillForm(q) {
  const f = $("#searchForm");
  for (const k of TEXT_FIELDS) f[k].value = q[k] || "";
  const groups = $$("[name=t]", f).map((x) => x.value);
  $$("[name=t]", f).forEach((x) => (x.checked = (q.file_types || []).includes(x.value)));
  $$("[name=dt]", f).forEach((x) => (x.checked = (q.doc_types || []).includes(x.value)));
  f.custom_types.value = (q.file_types || []).filter((t) => !groups.includes(t)).join(", ");
  f.match_mode.value = q.match_mode || "any"; f.text_state.value = q.text_state || "any";
  f.fuzzy.checked = q.fuzzy !== false; f.match_pdf_text.checked = !!q.match_pdf_text;
  f.hide_duplicates.checked = q.hide_duplicates !== false; f.include_inline.checked = !!q.include_inline;
  const advanced = q.recipients || q.exclude || q.filename_pattern || q.size_min_mb || q.size_max_mb || q.amount_min || q.amount_max || (q.text_state && q.text_state !== "any");
  if (advanced) $("#moreFilters").open = true;
}
function restoreForm() { const q = store("lastQuery"); if (q) fillForm(q); }
$("#resetBtn").addEventListener("click", () => { fillForm({ file_types: ["pdf"] }); $("[name=min_score]").value = 0; $("#msOut").textContent = "0%"; });
function qLabel(q) {
  return [q.senders, q.keywords, q.doc_types?.length && q.doc_types.join("/"), q.date_from && `${q.date_from}→${q.date_to || "…"}`].filter(Boolean).join(" · ") || "search";
}
function renderHistory() {
  const h = store("history") || [];
  $("#history").innerHTML = h.length ? "<div class='muted'>Recent searches</div>" + h.map((q, i) => `<a data-i="${i}">${esc(qLabel(q))}</a>`).join("") : "";
}
$("#history").addEventListener("click", (e) => { const i = e.target.dataset.i; if (i != null) { fillForm(store("history")[i]); $("#searchForm").requestSubmit(); } });

$("#searchForm").addEventListener("submit", async (e) => {
  e.preventDefault(); $("#searchErr").textContent = "";
  const q = readForm();
  busy(true, q.source === "index" ? "Searching index…" : q.match_pdf_text ? "Searching mail + reading files…" : "Searching mail server…");
  try {
    const res = await api("/api/search", q);
    store("lastQuery", q);
    const key = qLabel(q);
    store("history", [q, ...(store("history") || []).filter((x) => qLabel(x) !== key)].slice(0, 8)); renderHistory();
    rows = res.results; selected.clear(); lastClick = -1; docCache.clear();
    const s = res.stats;
    $("#stats").innerHTML = `<b>${s.results}</b> attachment${s.results === 1 ? "" : "s"} · <b>${size(s.total_size)}</b>` +
      (s.total_amount ? ` · total <b>${money(s.total_amount)}</b>` : "") +
      ` · <b>${s.seconds < 1 ? Math.round(s.seconds * 1000) + " ms" : s.seconds + " s"}</b>
      <span class="badge">${s.mode === "index" ? "⚡ index" : s.cells + " matrix cells"}</span>
      ${s.duplicate_messages ? `<span class="badge" title="same email in several folders/labels">${s.duplicate_messages} duplicate emails removed</span>` : ""}
      ${s.duplicate_attachments ? `<span class="badge" title="same file sent again">${s.duplicate_attachments} duplicate files removed</span>` : ""}
      ${s.pdfs_scanned ? `<span class="badge">${s.pdfs_scanned} files read</span>` : ""}
      ${s.truncated ? `<span class="badge">showing first 5000</span>` : ""}`;
    $("#dlReport").classList.add("hidden");
    if (s.mode === "live") refreshIndex(true);
    if ($("#viewIndex").classList.contains("hidden") === false) showView("search");
    render(); renderSheet();
  } catch (er) { $("#searchErr").textContent = er.message; }
  finally { busy(false); }
});

/* ---------- results table ---------- */
function sortVal(r, k) { return k === "amount" ? (r.fields.amount ?? -1) : (r[k] ?? ""); }
function render() {
  const f = $("#filter").value.trim().toLowerCase();
  view = rows.filter((r) => !f || `${r.filename} ${r.from_name} ${r.from_email} ${r.subject} ${r.date} ${r.fields.doc_no || ""} ${r.fields.amount || ""}`.toLowerCase().includes(f));
  view.sort((a, b) => { const x = sortVal(a, sortKey), y = sortVal(b, sortKey); return (x < y ? -1 : x > y ? 1 : 0) * sortDir; });
  $("#tbl tbody").innerHTML = view.map((r, i) => {
    const d = r.fields.doc_type && r.fields.doc_type !== "other" ? `<span class="doc d-${esc(r.fields.doc_type)}">${esc(DOC[r.fields.doc_type] || r.fields.doc_type)}${r.fields.doc_no ? " " + esc(r.fields.doc_no) : ""}</span>` : "";
    return `<tr data-i="${i}" class="${selected.has(r.id) ? "sel" : ""} ${r.id === activeId ? "active" : ""}">
      <td class="c"><input type="checkbox" ${selected.has(r.id) ? "checked" : ""} aria-label="select"></td>
      <td><div class="hexcell">${hexSVG(r.facets)}<span>${Math.round(r.score * 100)}%</span></div></td>
      <td><div class="fn"><span class="ext x-${esc(r.ext)}">${esc(r.ext || "file")}</span>${esc(r.filename)}${r.duplicates ? `<span class="badge" title="identical copies hidden">+${r.duplicates} dup</span>` : ""}${r.text_source === "ocr" ? ` <span class="src s-ocr">OCR</span>` : ""}</div>
          <div class="sub">${d}${esc(r.subject)}</div></td>
      <td class="cf"><div>${esc(r.from_name)}</div><div class="sub hide-sm">${esc(r.from_email)}</div></td>
      <td class="r cf amt">${money(r.fields.amount, r.fields.currency)}</td>
      <td class="nw">${fmtDate(r.date)}</td>
      <td class="r nw">${size(r.size)}</td>
      <td><button class="btn ghost pv" title="Preview">👁</button></td>
    </tr>`;
  }).join("");
  $("#empty").classList.toggle("hidden", !!view.length || !rows.length);
  $$("#tbl th[data-sort]").forEach((th) => { th.textContent = th.textContent.replace(/ [▴▾]$/, "") + (th.dataset.sort === sortKey ? (sortDir < 0 ? " ▾" : " ▴") : ""); });
  updateSel();
}
function updateSel() {
  const bytes = rows.filter((r) => selected.has(r.id)).reduce((a, r) => a + r.size, 0);
  const amt = rows.filter((r) => selected.has(r.id)).reduce((a, r) => a + (r.fields.amount || 0), 0);
  $("#selInfo").textContent = `${selected.size} selected${selected.size ? " · " + size(bytes) : ""}${amt ? " · " + money(amt) : ""}`;
  $$(".need-sel").forEach((b) => (b.disabled = !selected.size));
  $$(".need-sel-s").forEach((b) => b.setAttribute("aria-disabled", String(!selected.size)));
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
  } else { openPreview(r.id); return; }
  lastClick = i; render();
});
$("#tbl thead").addEventListener("click", (e) => {
  const th = e.target.closest("th[data-sort]"); if (!th) return;
  sortDir = th.dataset.sort === sortKey ? -sortDir : (["date", "score", "amount", "size"].includes(th.dataset.sort) ? -1 : 1);
  sortKey = th.dataset.sort; render();
});
$("#chkAll").addEventListener("click", (e) => { e.stopPropagation(); const on = e.target.checked; view.forEach((r) => toggle(r.id, on)); render(); });
$("#selAll").addEventListener("click", () => { view.forEach((r) => selected.add(r.id)); render(); });
$("#selNone").addEventListener("click", () => { selected.clear(); render(); });
$("#selInv").addEventListener("click", () => { view.forEach((r) => toggle(r.id)); render(); });
$("#filter").addEventListener("input", render);

/* ---------- preview / reader ---------- */
const PREVIEWABLE = ["pdf", "png", "jpg", "jpeg", "gif", "webp", "bmp", "txt", "csv", "log", "json", "xml", "eml", "html", "htm"];
async function getDoc(id, force) {
  if (!force && docCache.has(id)) return docCache.get(id);
  const d = force ? await api(`/api/doc/${encodeURIComponent(id)}/reread`, {}) : await api(`/api/doc/${encodeURIComponent(id)}`);
  docCache.set(id, d);
  const r = byId(id); if (r) { r.fields = d.fields; r.text_source = d.source; }
  return d;
}
async function openPreview(id) {
  activeId = id;
  const r = byId(id); if (!r) return;
  $("#preview").classList.remove("hidden"); $("#app").classList.add("with-preview");
  $("#pvName").textContent = r.filename;
  $("#pvMeta").textContent = `${r.from_name} · ${fmtDate(r.date)} · ${size(r.size)} · ${r.folder}`;
  $("#pvHex").innerHTML = hexSVG(r.facets, 86) + "<table>" + FACETS.map((k) =>
    `<tr><td class="muted">${FACET_LABEL[k]}</td><td>${r.facets[k] == null ? "—" : Math.round(r.facets[k] * 100) + "%"}</td></tr>`).join("") + "</table>" +
    `<div class="sub pvsnip">${esc(r.subject)}<br>${esc(r.snippet.slice(0, 180))}</div>`;
  $$("#pvTabs button").forEach((b) => b.classList.toggle("on", b.dataset.m === pvMode));
  render();
  const body = $("#pvBody");
  if (pvMode === "doc") return showDocument(r);
  body.innerHTML = `<div class='none'>${r.text_source ? "Loading…" : "Reading the file" + (["pdf", "png", "jpg", "jpeg", "tif", "tiff"].includes(r.ext) ? " (scanning if needed)…" : "…")}</div>`;
  try {
    const d = await getDoc(id);
    if (activeId !== id) return;
    if (pvMode === "text") showText(r, d); else if (pvMode === "fields") showFields(r, d); else showTables(r, d);
    render();
  } catch (er) { body.innerHTML = `<div class='none'>${esc(er.message)}</div>`; }
}
function showDocument(r) {
  const url = `/api/preview/${encodeURIComponent(r.id)}`, body = $("#pvBody");
  const ext = r.ext || (r.mime === "application/pdf" ? "pdf" : "");
  if (ext === "pdf") body.innerHTML = `<iframe src="${url}#view=FitH" title="preview"></iframe>`;
  else if (r.mime.startsWith("image/") && r.mime !== "image/svg+xml") body.innerHTML = `<img src="${url}" alt="">`;
  else if (PREVIEWABLE.includes(ext) || r.mime.startsWith("text/")) {
    body.innerHTML = "<div class='none'>Loading…</div>";
    fetch(url).then((x) => x.text()).then((t) => { if (activeId === r.id) body.innerHTML = `<pre>${esc(t)}</pre>`; })
      .catch(() => { body.innerHTML = "<div class='none'>Could not load preview.</div>"; });
  } else body.innerHTML = `<div class="none">No picture preview for .${esc(ext || "?")} files.<br>Open the <b>Text</b> or <b>Tables</b> tab, or download it.</div>`;
}
function highlight(text, q) {
  const safe = esc(text);
  const words = (q || "").split(/[,\s]+/).filter((w) => w.length > 1).map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  if (!words.length) return safe;
  return safe.replace(new RegExp(`(${words.join("|")})`, "gi"), "<mark>$1</mark>");
}
function showText(r, d) {
  const src = { pdf: "PDF text", ocr: "scanned (OCR)", office: "Office document", plain: "text file", none: "no text found" }[d.source] || d.source;
  const q = $("[name=keywords]").value;
  $("#pvBody").innerHTML = `<div class="pvtext"><div class="pvbar">
      <span class="src ${d.source === "ocr" ? "s-ocr" : ""}">${esc(src)}</span>
      <input id="pvFind" type="search" placeholder="Find in text…" value="${esc(q)}">
      <span class="spacer"></span>
      <button class="btn" id="pvCopy">Copy</button><button class="btn" id="pvTxt">Save .txt</button>
      <button class="btn" id="pvOcr" title="Read again; scans the pages with OCR if there is no text layer">${d.ocr_engine ? "Re-scan" : "Re-read"}</button></div>
    <pre id="pvPre">${d.text ? highlight(d.text, q) : (d.source === "none" && !d.ocr_engine ? "No text layer and no OCR engine available.\nOn Windows the built-in OCR is used automatically; elsewhere install Tesseract." : "(no text)")}</pre></div>`;
  $("#pvFind").addEventListener("input", (e) => { $("#pvPre").innerHTML = highlight(d.text || "", e.target.value); $("#pvPre mark")?.scrollIntoView({ block: "center" }); });
  $("#pvCopy").addEventListener("click", async () => { try { await navigator.clipboard.writeText(d.text || ""); toast("Text copied"); } catch { toast("Copy not allowed here – select the text and press Ctrl+C"); } });
  $("#pvTxt").addEventListener("click", () => downloadBlob(new Blob([d.text || ""], { type: "text/plain" }), r.filename.replace(/\.[^.]+$/, "") + ".txt"));
  $("#pvOcr").addEventListener("click", async () => { $("#pvPre").textContent = "Reading again…"; try { const nd = await getDoc(r.id, true); if (activeId === r.id) showText(r, nd); render(); } catch (er) { $("#pvPre").textContent = er.message; } });
}
function downloadBlob(blob, name) {
  const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(blob), download: name });
  document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
}
function showFields(r, d) {
  const f = d.fields || {};
  $("#pvBody").innerHTML = `<form class="fieldsform" id="fieldsForm">
    <label>Document</label><select name="doc_type">${Object.entries(DOC).map(([k, v]) => `<option value="${k}" ${f.doc_type === k ? "selected" : ""}>${v}</option>`).join("")}</select>
    <label>Number</label><input name="doc_no" value="${esc(f.doc_no || "")}">
    <label>Document date</label><input name="doc_date" type="date" value="${esc(f.doc_date || "")}">
    <label>Amount</label><input name="amount" inputmode="decimal" value="${f.amount ?? ""}">
    <label>Currency</label><input name="currency" value="${esc(f.currency || "")}" placeholder="INR">
    <label>GSTIN</label><input name="gstin" value="${esc(f.gstin || "")}">
    <div class="full muted small">Detected automatically from the ${d.source === "ocr" ? "scanned" : ""} text. Corrections are saved instantly and kept.</div>
    <div class="full"><hr></div>
    <label>From</label><div>${esc(r.from_name)} &lt;${esc(r.from_email)}&gt;</div>
    <label>To</label><div class="small">${esc(r.to || "—")}</div>
    <label>Subject</label><div>${esc(r.subject)}</div>
    <label>Email date</label><div>${esc((r.date || "").replace("T", " ").slice(0, 16))}</div>
    <label>Folder</label><div>${esc(r.folder)}</div>
    <label>Email text</label><div class="small">${esc(r.snippet)}</div></form>`;
  $("#fieldsForm").addEventListener("change", async (e) => {
    const el = e.target; if (!el.name) return;
    try { const j = await api("/api/fields", { id: r.id, field: el.name, value: el.value }); r.fields = j.fields; d.fields = j.fields; toast("Saved"); render(); }
    catch (er) { toast(er.message); }
  });
}
function showTables(r, d) {
  const t = d.tables || [];
  if (!t.length) { $("#pvBody").innerHTML = `<div class="none">No tables found in this file.<br><span class="small">Tables are detected from column-aligned text (line items, statements, Excel/CSV sheets).</span></div>`; return; }
  $("#pvBody").innerHTML = `<div class="tblhead"><b>${t.length} table${t.length > 1 ? "s" : ""}</b><span class="spacer"></span>
      <button class="btn" data-tx="xlsx">All → Excel</button><button class="btn" data-tx="csv">All → CSV</button></div>` +
    t.map((tb, i) => `<div class="tblhead"><span class="muted">Table ${i + 1} · ${tb.length} rows</span><span class="spacer"></span>
      <button class="btn" data-tx="xlsx" data-n="${i}">Excel</button><button class="btn" data-tx="csv" data-n="${i}">CSV</button><button class="btn" data-tcopy="${i}">Copy</button></div>
      <table class="dt">${tb.slice(0, 300).map((row) => `<tr>${row.map((c) => `<td>${esc(c)}</td>`).join("")}</tr>`).join("")}</table>`).join("");
  $("#pvBody").onclick = async (e) => {
    const b = e.target.closest("button"); if (!b) return;
    if (b.dataset.tcopy != null) {
      const tsv = t[+b.dataset.tcopy].map((row) => row.join("\t")).join("\n");
      try { await navigator.clipboard.writeText(tsv); toast("Table copied – paste into Excel"); } catch { toast("Copy not allowed here"); }
      return;
    }
    if (!b.dataset.tx) return;
    const fmt = b.dataset.tx, n = b.dataset.n;
    const name = r.filename.replace(/\.[^.]+$/, "") + (n != null ? `-table${+n + 1}` : "-tables") + "." + fmt;
    try { const res = await saveFile(`/api/tables/${encodeURIComponent(r.id)}`, { format: fmt, n: n != null ? +n : null }, name, fmt);
      if (res?.path) report(`<div class="ok">✔ Saved <b>${esc(res.path)}</b>${openBtns(res.path, true)}</div>`); }
    catch (er) { toast(er.message); }
  };
}
function closePreview(silent) { activeId = null; $("#preview").classList.add("hidden"); $("#app").classList.remove("with-preview"); $("#pvBody").innerHTML = ""; if (!silent) render(); }
function step(d) { const i = view.findIndex((r) => r.id === activeId); const n = view[i + d]; if (n) openPreview(n.id); }
$("#pvTabs").addEventListener("click", (e) => { const m = e.target.dataset.m; if (!m || !activeId) return; pvMode = m; store("pvMode", m); openPreview(activeId); });
$("#pvClose").addEventListener("click", () => closePreview());
$("#pvPrev").addEventListener("click", () => step(-1));
$("#pvNext").addEventListener("click", () => step(1));
$("#pvSel").addEventListener("click", () => { if (activeId) { toggle(activeId); render(); } });
document.addEventListener("keydown", (e) => {
  if (!activeId || ["INPUT", "SELECT", "TEXTAREA"].includes(document.activeElement.tagName) || document.activeElement.isContentEditable) return;
  if (e.key === "Escape") closePreview();
  else if (e.key === "ArrowDown" || e.key === "ArrowRight" || e.key === "j") { e.preventDefault(); step(1); }
  else if (e.key === "ArrowUp" || e.key === "ArrowLeft" || e.key === "k") { e.preventDefault(); step(-1); }
  else if (e.key === " ") { e.preventDefault(); toggle(activeId); render(); }
});

/* ---------- download / zip / export ---------- */
function dlOpts() {
  store("dest", $("#dest").value); store("ftpl", $("#ftpl").value); store("ntpl", $("#ntpl").value);
  return { ids: [...selected], dest: $("#dest").value, folder_template: $("#ftpl").value, name_template: $("#ntpl").value };
}
$("#browse").addEventListener("click", async () => { const p = await desk()?.pick_folder($("#dest").value); if (p) { $("#dest").value = p; store("dest", p); } });
$("#saveBtn").addEventListener("click", async () => {
  busy(true, `Saving ${selected.size} files…`);
  try {
    const r = await api("/api/download", dlOpts());
    report(`<div class="ok">✔ Saved ${r.saved.length} file(s) to <b>${esc(r.folder)}</b>${openBtns(r.folder)}</div>` +
      (r.skipped.length ? `<div class="warn">↷ ${r.skipped.length} skipped – identical file already in the folder</div>` : "") +
      r.failed.map((f) => `<div class="bad">✖ ${esc(f.filename)}: ${esc(f.error)}</div>`).join("") +
      `<div class="muted small">manifest.csv in that folder lists sender, subject, date and SHA-256 of every saved file.</div>`);
  } catch (er) { report(`<div class="bad">✖ ${esc(er.message)}</div>`); }
  finally { busy(false); }
});
$("#zipBtn").addEventListener("click", async () => {
  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:]/g, "").replace("T", "-");
  busy(true, `Packing ${selected.size} files…`);
  try {
    const r = await saveFile("/api/zip", dlOpts(), `mail-attachments-${stamp}.zip`, "zip");
    if (r?.path) report(`<div class="ok">✔ ZIP saved: <b>${esc(r.path)}</b> – ${r.added} files${r.skipped ? `, ${r.skipped} duplicate skipped` : ""}${openBtns(r.path, true)}</div>`);
    else if (r) report(`<div class="ok">✔ ZIP downloaded: ${esc(r.headers.get("x-zip-report") || "")}</div>`);
  } catch (er) { report(`<div class="bad">✖ ${esc(er.message)}</div>`); }
  finally { busy(false); }
});
async function doExport(fmt, ids) {
  const stamp = new Date().toISOString().slice(0, 16).replace(/[-:]/g, "").replace("T", "-");
  busy(true, fmt === "txt" ? "Reading files…" : "Exporting…");
  try {
    const r = await saveFile("/api/export", { ids, format: fmt, ocr: true }, `attachments-${stamp}.${fmt}`, fmt);
    if (r?.path) report(`<div class="ok">✔ Exported <b>${esc(r.path)}</b>${openBtns(r.path, true)}</div>`);
    else if (r) report(`<div class="ok">✔ Downloaded ${esc(r.downloaded)}</div>`);
    if (fmt === "txt") refreshRows(ids);
  } catch (er) { report(`<div class="bad">✖ ${esc(er.message)}</div>`); }
  finally { busy(false); $$(".menu").forEach((m) => (m.open = false)); }
}
$$("[data-export]").forEach((b) => b.addEventListener("click", () => doExport(b.dataset.export, [...selected])));
document.addEventListener("click", (e) => { $$(".menu[open]").forEach((m) => { if (!m.contains(e.target)) m.open = false; }); });

/* ---------- read text / analyze (background job) ---------- */
async function analyze(ids, opts = {}) {
  try {
    const job = await api("/api/analyze", { ids, ocr: true, ...opts });
    watchJob(job, () => refreshRows(ids));
    toast(`Reading ${ids ? ids.length : "all"} file(s) in the background…`);
  } catch (er) { toast(er.message); }
}
$("#readBtn").addEventListener("click", () => analyze([...selected]));
async function refreshRows(ids) {
  // re-run the last search silently so new amounts/fields/text show up
  const q = store("lastQuery"); if (!q || !rows.length) return;
  try {
    const res = await api("/api/search", { ...q, match_pdf_text: false });
    const fresh = new Map(res.results.map((r) => [r.id, r]));
    rows.forEach((r) => { const f = fresh.get(r.id); if (f) { r.fields = f.fields; r.text_source = f.text_source; } });
    docCache.clear(); render(); renderSheet();
  } catch { /* ignore */ }
}

/* ---------- jobs (sync / analyze) ---------- */
const watching = new Set();
function watchJob(job, onDone) {
  if (watching.has(job.id)) return; watching.add(job.id);
  const label = job.kind === "sync" ? "Syncing" : "Reading files";
  $("#jobBox").classList.remove("hidden"); $("#jobCancel").onclick = () => api(`/api/jobs/${job.id}/cancel`, {});
  const tick = async () => {
    let j; try { j = await api(`/api/jobs/${job.id}`); } catch { watching.delete(job.id); return; }
    const p = j.progress || {}, pct = p.total ? Math.round(100 * p.done / p.total) : null;
    $("#jobBar").parentElement.classList.toggle("indet", pct == null);
    $("#jobBar").style.width = (pct ?? 30) + "%";
    const txt = j.kind === "sync" ? `${p.phase || "Starting"}${p.total ? ` · ${p.done}/${p.total} new emails` : ""}${p.steps > 1 ? ` · folder ${p.step}/${p.steps}` : ""}`
      : `Reading ${p.done || 0}/${p.total || "?"}${p.file ? " · " + p.file : ""}`;
    $("#jobText").textContent = txt;
    $("#jobChip").textContent = `${label}${pct != null ? " " + pct + "%" : "…"}`; $("#jobChip").classList.remove("hidden");
    if (j.state === "running") { setTimeout(tick, 600); return; }
    watching.delete(job.id);
    if (!watching.size) { $("#jobChip").classList.add("hidden"); $("#jobBox").classList.add("hidden"); }
    if (j.state === "error") toast(`${label} failed: ${j.error}`, 6000);
    else if (j.state === "cancelled") toast(`${label} cancelled (progress so far is kept)`);
    else if (j.kind === "sync") toast(`Index up to date · ${j.result.added} new attachment(s)${j.result.removed ? ` · ${j.result.removed} removed` : ""} · ${j.result.seconds}s`);
    else toast(`Read ${j.result.analyzed} file(s)${j.result.ocr ? `, ${j.result.ocr} scanned with OCR` : ""}${j.result.failed ? `, ${j.result.failed} failed` : ""}`);
    refreshIndex(); onDone?.(j);
  };
  tick();
}

/* ---------- index view ---------- */
async function refreshIndex(quiet) {
  try { const s = await api("/api/index"); status.index = s; renderIndex(s); setSource($("[name=source]:checked")?.value || "live"); }
  catch (er) { if (!quiet) toast(er.message); }
}
function renderIndex(s) {
  if (!s) return;
  $("#idxBadge").textContent = s.attachments ? (s.attachments > 999 ? Math.round(s.attachments / 1000) + "k" : s.attachments) : "";
  const k = (v, l) => `<div class="kpi"><b>${v}</b><span>${l}</span></div>`;
  $("#kpis").innerHTML = k(s.attachments.toLocaleString(), "attachments") + k(s.messages.toLocaleString(), "emails") +
    k(size(s.total_size), "of files on the server") + k(`${s.analyzed.toLocaleString()}`, `documents read (${s.scanned} scanned)`) +
    k(s.oldest ? `${s.oldest.slice(0, 7)} → ${s.newest.slice(0, 7)}` : "—", "date range") + k(ago(s.last_sync), "last sync") +
    (s.db_bytes ? k(size(s.db_bytes), "index size on disk") : "");
  $("#idxDetails").innerHTML =
    `<div><b>Folders</b><table>${s.folders.map((f) => `<tr><td>${esc(f.name)}</td><td class="muted">${ago(f.synced_at)}</td></tr>`).join("") || "<tr><td class='muted'>not synced yet</td></tr>"}</table></div>` +
    `<div><b>Document types</b><table>${s.doc_types.map((d) => `<tr><td>${esc(DOC[d.doc_type] || d.doc_type)}</td><td class="r">${d.n}</td></tr>`).join("")}</table></div>` +
    `<div><b>File types</b><table>${s.exts.map((d) => `<tr><td>${esc(d.ext || "?")}</td><td class="r">${d.n}</td></tr>`).join("")}</table></div>`;
}
async function startSync(full, quiet) {
  try {
    const job = await api("/api/index/sync", { folders: $$("#idxFolders option:checked").map((o) => o.value), since: $("#idxSince").value, full });
    watchJob(job, () => { if (!quiet) toast("Index ready – searches now run instantly"); setSource("index"); store("source", "index"); });
  } catch (er) { toast(er.message); }
}
$("#syncBtn").addEventListener("click", () => startSync(false));
$("#rebuildBtn").addEventListener("click", () => { if (confirm("Re-read every email from the server? (documents already read will need reading again)")) startSync(true); });
$("#clearBtn").addEventListener("click", async () => { if (!confirm("Delete the local index? Your mail is not touched.")) return; renderIndex(await api("/api/index/clear", {})); refreshIndex(); });
$("#readAllBtn").addEventListener("click", () => analyze(null, { all: true, ocr: $("#readOcr").checked }));
$("#autoSync").addEventListener("change", (e) => store("autoSync", e.target.checked));

/* ---------- table (sheet) view ---------- */
const SHEET_EDIT = ["doc_no", "doc_date", "amount", "currency", "gstin"];
function sheetVal(r, k) { return ["date", "from_name", "filename"].includes(k) ? (r[k] ?? "") : (r.fields[k] ?? (k === "amount" ? -Infinity : "")); }
function monthOf(r) { return (r.fields.doc_date || r.date || "").slice(0, 7) || "unknown"; }
function renderSheet() {
  const list = [...view.length || !rows.length ? view : rows];
  $("#tblInfo").textContent = list.length ? `${list.length} rows from the current results` : "Run a search first.";
  list.sort((a, b) => { const x = sheetVal(a, sheetSort), y = sheetVal(b, sheetSort); return (x < y ? -1 : x > y ? 1 : 0) * sheetDir; });
  $("#sheet tbody").innerHTML = list.map((r) => `<tr data-id="${esc(r.id)}">
    <td class="nw">${fmtDate(r.date)}</td><td><div class="sub">${esc(r.from_name)}</div></td><td><div class="sub" title="${esc(r.filename)}">${esc(r.filename)}</div></td>
    <td><select data-f="doc_type">${Object.entries(DOC).map(([k, v]) => `<option value="${k}" ${r.fields.doc_type === k ? "selected" : ""}>${v}</option>`).join("")}</select></td>
    ${SHEET_EDIT.map((k) => `<td contenteditable="plaintext-only" data-f="${k}" class="${k === "amount" ? "r amt" : ""}">${esc(k === "amount" ? (r.fields.amount != null ? Number(r.fields.amount).toFixed(2) : "") : r.fields[k] || "")}</td>`).join("")}
    <td>${r.text_source ? `<span class="src ${r.text_source === "ocr" ? "s-ocr" : ""}">${esc(r.text_source)}</span>` : `<span class="muted small">not read</span>`}</td></tr>`).join("");
  const total = list.reduce((a, r) => a + (r.fields.amount || 0), 0), withAmt = list.filter((r) => r.fields.amount != null).length;
  $("#sheetCount").textContent = `${list.length} documents · ${withAmt} with amount`;
  $("#sheetTotal").textContent = money(total);
  const g = $("#groupBy").value, box = $("#groupSummary");
  if (!g || !list.length) { box.classList.add("hidden"); return; }
  const groups = new Map();
  for (const r of list) {
    const key = g === "month" ? monthOf(r) : g === "sender" ? (r.from_name || r.from_email) : (DOC[r.fields.doc_type] || "Other");
    const v = groups.get(key) || [0, 0]; v[0]++; v[1] += r.fields.amount || 0; groups.set(key, v);
  }
  const ent = [...groups.entries()].sort((a, b) => g === "month" ? (a[0] < b[0] ? 1 : -1) : b[1][1] - a[1][1]);
  box.innerHTML = `<table><tr><th>${esc($("#groupBy").selectedOptions[0].text)}</th><th class="r">Files</th><th class="r">Total</th></tr>` +
    ent.map(([k, [n, s]]) => `<tr><td>${esc(k)}</td><td class="r">${n}</td><td class="r amt">${money(s)}</td></tr>`).join("") + "</table>";
  box.classList.remove("hidden");
}
$("#groupBy").addEventListener("change", renderSheet);
$("#sheet thead").addEventListener("click", (e) => {
  const th = e.target.closest("th[data-ssort]"); if (!th) return;
  sheetDir = th.dataset.ssort === sheetSort ? -sheetDir : (["date", "amount", "doc_date"].includes(th.dataset.ssort) ? -1 : 1);
  sheetSort = th.dataset.ssort; renderSheet();
});
async function saveCell(el) {
  const tr = el.closest("tr"), r = byId(tr.dataset.id), field = el.dataset.f;
  const value = el.tagName === "SELECT" ? el.value : el.textContent.trim();
  const old = field === "amount" ? (r.fields.amount != null ? Number(r.fields.amount).toFixed(2) : "") : (r.fields[field] || "");
  if (value === old) return;
  try { const j = await api("/api/fields", { id: r.id, field, value }); r.fields = j.fields; el.classList.add("edited"); render(); updateSheetTotals(); }
  catch (er) { toast(er.message); el.textContent = old; }
}
function updateSheetTotals() { const list = view.length ? view : rows; $("#sheetTotal").textContent = money(list.reduce((a, r) => a + (r.fields.amount || 0), 0)); }
$("#sheet tbody").addEventListener("focusout", (e) => { if (e.target.dataset?.f) saveCell(e.target); });
$("#sheet tbody").addEventListener("change", (e) => { if (e.target.tagName === "SELECT") saveCell(e.target); });
$("#sheet tbody").addEventListener("keydown", (e) => { if (e.key === "Enter" && e.target.isContentEditable) { e.preventDefault(); e.target.blur(); } });
$("#sheet tbody").addEventListener("dblclick", (e) => { const tr = e.target.closest("tr"); if (tr && !e.target.isContentEditable) { showView("search"); openPreview(tr.dataset.id); } });
$("#tblRead").addEventListener("click", () => { const ids = (view.length ? view : rows).filter((r) => !r.text_source).map((r) => r.id); if (!ids.length) { toast("All rows are already read"); return; } analyze(ids); });
$$("[data-texport]").forEach((b) => b.addEventListener("click", () => { const ids = (view.length ? view : rows).map((r) => r.id); if (ids.length) doExport(b.dataset.texport, ids); }));

/* ---------- share ---------- */
$("#shareBtn").addEventListener("click", () => {
  const n = selected.size, f = $("#shareForm");
  $("#shareCount").textContent = `${n} file${n > 1 ? "s" : ""}`;
  const sel = rows.filter((r) => selected.has(r.id)), bytes = sel.reduce((a, r) => a + r.size, 0);
  $("#shareNote").textContent = `${size(bytes)} total` + (bytes > 18 * 1048576 ? " – too large for one email, use ZIP instead." : status.demo ? " · demo mailbox: nothing is really sent." : ` · sent from ${status.account}.`);
  if (!f.subject.value) f.subject.value = sel.length === 1 ? sel[0].filename : `${n} attachments`;
  $("#shareMsg").textContent = ""; $("#shareDlg").showModal();
});
$("#shareForm").addEventListener("submit", async (e) => {
  const action = e.submitter?.value; if (!action || action === "cancel") return;
  e.preventDefault();
  const f = $("#shareForm"), ids = [...selected], body = { ids, to: f.to.value, subject: f.subject.value, message: f.message.value };
  const msg = $("#shareMsg");
  try {
    if (action === "copy") {
      const j = await api("/api/share/text", { ids });
      try { await navigator.clipboard.writeText(j.text); msg.textContent = "✔ List copied – paste it in WhatsApp, email or a document."; }
      catch { msg.textContent = "Copy not allowed here."; }
    } else if (action === "eml") {
      msg.textContent = "Preparing draft…";
      const r = await saveFile("/api/share/eml", body, "draft.eml", "eml");
      if (r?.path) { desk()?.open_path(r.path); msg.textContent = "✔ Draft opened in your mail app."; }
      else if (r) msg.textContent = "✔ Draft downloaded – open the .eml file to send it from Outlook / Mail.";
    } else {
      msg.textContent = "Sending…";
      const r = await api("/api/share/email", body);
      msg.innerHTML = r.demo ? esc(r.note) : `✔ Sent ${r.files} file(s) to ${esc(r.sent_to.join(", "))}`;
    }
  } catch (er) { msg.innerHTML = `<span class="err">${esc(er.message)}</span>`; }
});

pvMode = store("pvMode") || "doc";
boot().catch((e) => { document.body.insertAdjacentHTML("beforeend", `<p class="err" style="padding:20px">${esc(e.message)}</p>`); });
