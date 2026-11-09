// Small browser helpers for the web app (the Android app uses native code instead).
window.cdpr = {
  download(fileName, contentType, base64) {
    const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const a = Object.assign(document.createElement('a'), { href: url, download: fileName });
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
  },
  scrollX(el, x) { if (el) el.scrollLeft = Math.max(0, x); },
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { value == null ? localStorage.removeItem(key) : localStorage.setItem(key, value); } catch { } },
};
