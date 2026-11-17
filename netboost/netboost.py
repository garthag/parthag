#!/usr/bin/env python3
"""
NetBoost - Wi-Fi / internet connection analyzer and optimizer.

What it does (honestly):
  * Reads your Wi-Fi adapter: signal, band, channel, link rate, radio type.
  * Measures ping, jitter and packet loss to your router (Wi-Fi quality)
    and to the internet (ISP quality) so you can see WHERE the slowdown is.
  * Benchmarks DNS servers and tells you the fastest one for your location.
  * Runs a speed test with 1 connection vs. many parallel connections
    (one worker thread per CPU core) to show the real max of your line.
  * Downloads files with many parallel connections (HTTP range requests).
  * Optional --apply (Windows, run as Administrator): enables multi-core
    packet processing (RSS), TCP auto-tuning, sets the fastest DNS and turns
    off Wi-Fi power saving. --restore undoes the DNS and power changes.

What no software can do: make your line faster than the plan your ISP sells
you. NetBoost removes the bottlenecks between you and that limit.

No third-party packages needed. Python 3.8+.
"""

import argparse
import concurrent.futures as cf
import os
import platform
import random
import re
import socket
import statistics
import struct
import subprocess
import sys
import threading
import time
import urllib.request

OS = platform.system()  # "Windows", "Linux", "Darwin"
CPU_CORES = os.cpu_count() or 2

SPEED_DOWN_URL = "https://speed.cloudflare.com/__down?bytes={n}"
SPEED_UP_URL = "https://speed.cloudflare.com/__up"

DNS_SERVERS = {
    "Cloudflare": ("1.1.1.1", "1.0.0.1"),
    "Google": ("8.8.8.8", "8.8.4.4"),
    "Quad9": ("9.9.9.9", "149.112.112.112"),
    "OpenDNS": ("208.67.222.222", "208.67.220.220"),
    "AdGuard": ("94.140.14.14", "94.140.15.15"),
}
DNS_TEST_DOMAINS = [
    "google.com", "youtube.com", "facebook.com", "amazon.in", "wikipedia.org",
    "whatsapp.com", "instagram.com", "netflix.com", "microsoft.com", "github.com",
]


# --------------------------------------------------------------------------- #
# Helpers
# --------------------------------------------------------------------------- #
def run(cmd, timeout=30):
    """Run a command and return stdout as text ('' on failure)."""
    try:
        out = subprocess.run(
            cmd, capture_output=True, timeout=timeout,
            shell=isinstance(cmd, str),
        )
        return out.stdout.decode(errors="ignore")
    except Exception:
        return ""


def is_admin():
    if OS == "Windows":
        try:
            import ctypes
            return bool(ctypes.windll.shell32.IsUserAnAdmin())
        except Exception:
            return False
    return hasattr(os, "geteuid") and os.geteuid() == 0


def header(title):
    print("\n" + "=" * 64)
    print("  " + title)
    print("=" * 64)


def mbps(nbytes, seconds):
    return (nbytes * 8 / 1_000_000) / seconds if seconds > 0 else 0.0


# --------------------------------------------------------------------------- #
# 1. Hardware / Wi-Fi adapter analysis
# --------------------------------------------------------------------------- #
def wifi_info():
    """Return a dict describing the active Wi-Fi link (best effort)."""
    info = {}
    if OS == "Windows":
        text = run(["netsh", "wlan", "show", "interfaces"])
        for line in text.splitlines():
            if ":" not in line:
                continue
            key, _, val = line.partition(":")
            key, val = key.strip().lower(), val.strip()
            if key == "name":
                info["adapter_name"] = val
            elif key == "description":
                info["adapter"] = val
            elif key == "ssid":
                info["ssid"] = val
            elif key == "bssid" or key == "ap bssid":
                info["bssid"] = val
            elif key == "radio type":
                info["radio"] = val
            elif key == "band":
                info["band"] = val
            elif key == "channel":
                info["channel"] = val
            elif key.startswith("receive rate"):
                info["rx_mbps"] = float(re.sub(r"[^\d.]", "", val) or 0)
            elif key.startswith("transmit rate"):
                info["tx_mbps"] = float(re.sub(r"[^\d.]", "", val) or 0)
            elif key == "signal":
                info["signal_pct"] = int(re.sub(r"\D", "", val) or 0)
            elif key == "rssi":
                info["rssi_dbm"] = int(re.sub(r"[^\d-]", "", val) or 0)
            elif key == "state":
                info["state"] = val
    elif OS == "Linux":
        dev_text = run(["iw", "dev"])
        m = re.search(r"Interface\s+(\S+)", dev_text)
        if m:
            iface = m.group(1)
            info["adapter_name"] = iface
            link = run(["iw", "dev", iface, "link"])
            if (m := re.search(r"SSID:\s*(.+)", link)):
                info["ssid"] = m.group(1).strip()
            if (m := re.search(r"freq:\s*([\d.]+)", link)):
                freq = float(m.group(1))
                info["band"] = "5 GHz" if freq > 4900 else "2.4 GHz"
                if freq > 5900:
                    info["band"] = "6 GHz"
                info["channel"] = str(freq_to_channel(freq))
            if (m := re.search(r"signal:\s*(-?\d+)", link)):
                info["rssi_dbm"] = int(m.group(1))
                info["signal_pct"] = dbm_to_pct(info["rssi_dbm"])
            if (m := re.search(r"rx bitrate:\s*([\d.]+)", link)):
                info["rx_mbps"] = float(m.group(1))
            if (m := re.search(r"tx bitrate:\s*([\d.]+)", link)):
                info["tx_mbps"] = float(m.group(1))
            for tag, name in (("EHT", "802.11be (Wi-Fi 7)"), ("HE", "802.11ax (Wi-Fi 6)"),
                              ("VHT", "802.11ac (Wi-Fi 5)"), ("MCS", "802.11n (Wi-Fi 4)")):
                if tag in link:
                    info["radio"] = name
                    break
    elif OS == "Darwin":
        text = run(["system_profiler", "SPAirPortDataType"])
        cur = text.split("Current Network Information:")
        if len(cur) > 1:
            block = cur[1]
            if (m := re.search(r"PHY Mode:\s*(.+)", block)):
                info["radio"] = m.group(1).strip()
            if (m := re.search(r"Channel:\s*(\d+)\s*\(([^,)]+)", block)):
                info["channel"], info["band"] = m.group(1), m.group(2)
            if (m := re.search(r"Signal / Noise:\s*(-?\d+)", block)):
                info["rssi_dbm"] = int(m.group(1))
                info["signal_pct"] = dbm_to_pct(info["rssi_dbm"])
            if (m := re.search(r"Transmit Rate:\s*(\d+)", block)):
                info["tx_mbps"] = float(m.group(1))
    if "band" not in info and info.get("channel", "").isdigit():
        info["band"] = "2.4 GHz" if int(info["channel"]) <= 14 else "5 GHz"
    return info


def freq_to_channel(freq):
    if 2412 <= freq <= 2484:
        return 14 if freq == 2484 else int((freq - 2407) / 5)
    if 5000 <= freq <= 5900:
        return int((freq - 5000) / 5)
    if freq >= 5955:
        return int((freq - 5950) / 5)
    return 0


def dbm_to_pct(dbm):
    return max(0, min(100, 2 * (dbm + 100)))


def nearby_networks():
    """Count networks per channel to spot congestion (Windows / Linux)."""
    channels = {}
    if OS == "Windows":
        text = run(["netsh", "wlan", "show", "networks", "mode=bssid"])
        for m in re.finditer(r"Channel\s*:\s*(\d+)", text):
            channels[int(m.group(1))] = channels.get(int(m.group(1)), 0) + 1
    elif OS == "Linux":
        text = run(["nmcli", "-t", "-f", "CHAN", "dev", "wifi", "list"])
        for line in text.split():
            if line.strip().isdigit():
                c = int(line)
                channels[c] = channels.get(c, 0) + 1
    return channels


def gateway_ip():
    if OS == "Windows":
        # IPv4 routing table: "0.0.0.0  0.0.0.0  <gateway>  <interface>  <metric>"
        for m in re.finditer(r"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(\d+\.\d+\.\d+\.\d+)",
                             run(["route", "print", "-4", "0.0.0.0"]), re.M):
            return m.group(1)
        # Fallback: ipconfig, where an IPv6 gateway may come first and IPv4 on the next line
        text = run(["ipconfig"])
        for m in re.finditer(r"Default Gateway[ .]*:(.*(?:\n\s{20,}\S.*)*)", text):
            v4 = re.search(r"\b(\d+\.\d+\.\d+\.\d+)\b", m.group(1))
            if v4:
                return v4.group(1)
        return None
    if OS == "Linux":
        m = re.search(r"default via ([\d.]+)", run(["ip", "route"]))
        return m.group(1) if m else None
    if OS == "Darwin":
        m = re.search(r"gateway:\s*([\d.]+)", run(["route", "-n", "get", "default"]))
        return m.group(1) if m else None
    return None


def analyze_hardware():
    header("1. Wi-Fi hardware & link analysis (receiver <-> router)")
    print(f"  OS: {OS} {platform.release()}   CPU cores: {CPU_CORES}")
    w = wifi_info()
    if not w:
        print("  No Wi-Fi link found (wired connection, or adapter info unavailable).")
    else:
        rows = [
            ("Adapter", w.get("adapter", w.get("adapter_name"))),
            ("Network (SSID)", w.get("ssid")),
            ("Router (BSSID)", w.get("bssid")),
            ("Wi-Fi standard", w.get("radio")),
            ("Band", w.get("band")),
            ("Channel", w.get("channel")),
            ("Signal", f"{w['signal_pct']}%" if "signal_pct" in w else None),
            ("RSSI", f"{w['rssi_dbm']} dBm" if "rssi_dbm" in w else None),
            ("Link rate down", f"{w['rx_mbps']:.0f} Mbps" if "rx_mbps" in w else None),
            ("Link rate up", f"{w['tx_mbps']:.0f} Mbps" if "tx_mbps" in w else None),
        ]
        for k, v in rows:
            if v:
                print(f"  {k:<16}: {v}")
    gw = gateway_ip()
    print(f"  {'Router IP':<16}: {gw or 'not found'}")
    chans = nearby_networks()
    if chans:
        busy = ", ".join(f"ch{c}:{n}" for c, n in sorted(chans.items()))
        print(f"  {'Nearby networks':<16}: {busy}")
    return w, gw, chans


# --------------------------------------------------------------------------- #
# 2. Ping / jitter / packet loss
# --------------------------------------------------------------------------- #
def ping(host, count=20):
    """Return list of RTTs in ms (None for lost packets)."""
    if OS == "Windows":
        cmd = ["ping", "-n", str(count), "-w", "1000", host]
    else:
        cmd = ["ping", "-c", str(count), "-i", "0.2", "-W", "1", host]
    text = run(cmd, timeout=count * 2 + 10)
    times = [float(t) for t in re.findall(r"time[=<]\s*([\d.]+)", text)]
    if not text:
        times = tcp_ping(host, count)
    lost = max(0, count - len(times))
    return times + [None] * lost


def tcp_ping(host, count=10, port=443):
    """Fallback when ICMP ping is blocked: time a TCP handshake."""
    out = []
    for _ in range(count):
        t0 = time.perf_counter()
        try:
            with socket.create_connection((host, port), timeout=2):
                out.append((time.perf_counter() - t0) * 1000)
        except OSError:
            pass
        time.sleep(0.1)
    return out


def ping_stats(samples):
    ok = [s for s in samples if s is not None]
    if not ok:
        return {"avg": None, "min": None, "max": None, "jitter": None, "loss": 100.0}
    jitter = statistics.mean(abs(a - b) for a, b in zip(ok, ok[1:])) if len(ok) > 1 else 0.0
    return {
        "avg": statistics.mean(ok), "min": min(ok), "max": max(ok),
        "jitter": jitter, "loss": 100.0 * (len(samples) - len(ok)) / len(samples),
    }


def fmt_ping(s):
    if s["avg"] is None:
        return "no reply (100% loss)"
    return (f"avg {s['avg']:6.1f} ms  min {s['min']:5.1f}  max {s['max']:6.1f}  "
            f"jitter {s['jitter']:5.1f} ms  loss {s['loss']:4.1f}%")


def analyze_latency(gw, count=20):
    header("2. Ping, jitter & packet loss")
    targets = []
    if gw:
        targets.append(("Router (Wi-Fi hop)", gw))
    targets += [("Cloudflare 1.1.1.1", "1.1.1.1"), ("Google 8.8.8.8", "8.8.8.8")]
    results = {}
    # Ping all targets at the same time (one thread each) to save time.
    with cf.ThreadPoolExecutor(max_workers=len(targets)) as ex:
        futs = {ex.submit(ping, host, count): name for name, host in targets}
        for f in cf.as_completed(futs):
            results[futs[f]] = ping_stats(f.result())
    for name, _ in targets:
        print(f"  {name:<20} {fmt_ping(results[name])}")
    return results


# --------------------------------------------------------------------------- #
# 3. DNS benchmark (raw UDP queries, no extra libraries)
# --------------------------------------------------------------------------- #
def dns_query_time(server, domain, timeout=2.0):
    tid = random.randint(0, 0xFFFF)
    q = struct.pack(">HHHHHH", tid, 0x0100, 1, 0, 0, 0)
    for part in domain.split("."):
        q += bytes([len(part)]) + part.encode()
    q += b"\x00" + struct.pack(">HH", 1, 1)
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(timeout)
    try:
        t0 = time.perf_counter()
        s.sendto(q, (server, 53))
        data, _ = s.recvfrom(512)
        if struct.unpack(">H", data[:2])[0] != tid:
            return None
        return (time.perf_counter() - t0) * 1000
    except OSError:
        return None
    finally:
        s.close()


def system_dns():
    if OS == "Windows":
        text = run(["ipconfig", "/all"])
        m = re.search(r"DNS Servers[ .]*:\s*([\d.]+)", text)
        return m.group(1) if m else None
    try:
        with open("/etc/resolv.conf") as f:
            m = re.search(r"^nameserver\s+([\d.]+)", f.read(), re.M)
            return m.group(1) if m else None
    except OSError:
        return None


def benchmark_dns():
    header("3. DNS speed benchmark (how fast websites start loading)")
    servers = dict(DNS_SERVERS)
    cur = system_dns()
    if cur and cur not in {ip for pair in servers.values() for ip in pair}:
        servers["Current (router/ISP)"] = (cur,)

    def test(item):
        name, ips = item
        times = [dns_query_time(ips[0], d) for d in DNS_TEST_DOMAINS]
        ok = [t for t in times if t is not None]
        return name, ips, (statistics.median(ok) if ok else None), len(ok)

    with cf.ThreadPoolExecutor(max_workers=len(servers)) as ex:
        results = list(ex.map(test, servers.items()))
    results.sort(key=lambda r: r[2] if r[2] is not None else 1e9)
    for name, ips, med, ok in results:
        val = f"{med:6.1f} ms" if med is not None else "   blocked/timeout"
        print(f"  {name:<22} {ips[0]:<16} {val}   ({ok}/{len(DNS_TEST_DOMAINS)} answered)")
    best = results[0] if results and results[0][2] is not None else None
    if best:
        print(f"\n  Fastest DNS: {best[0]} ({', '.join(best[1])})")
    return best, results


# --------------------------------------------------------------------------- #
# 4. Multi-connection speed test (one worker per CPU core)
# --------------------------------------------------------------------------- #
class Counter:
    def __init__(self):
        self.n = 0
        self.error = None
        self.lock = threading.Lock()

    def add(self, k):
        with self.lock:
            self.n += k


def _download_worker(counter, stop_at, chunk_bytes):
    buf = 64 * 1024
    while time.perf_counter() < stop_at:
        try:
            req = urllib.request.Request(SPEED_DOWN_URL.format(n=chunk_bytes),
                                         headers={"User-Agent": "NetBoost/1.0"})
            with urllib.request.urlopen(req, timeout=10) as r:
                while time.perf_counter() < stop_at:
                    data = r.read(buf)
                    if not data:
                        break
                    counter.add(len(data))
        except Exception as e:
            counter.error = e
            time.sleep(0.2)


def _upload_worker(counter, stop_at, chunk_bytes):
    payload = os.urandom(chunk_bytes)
    while time.perf_counter() < stop_at:
        try:
            req = urllib.request.Request(SPEED_UP_URL, data=payload, method="POST",
                                         headers={"User-Agent": "NetBoost/1.0",
                                                  "Content-Type": "application/octet-stream"})
            with urllib.request.urlopen(req, timeout=30) as r:
                r.read()
            counter.add(len(payload))
        except Exception as e:
            counter.error = e
            time.sleep(0.2)


def throughput(worker, connections, seconds, chunk_bytes):
    counter = Counter()
    stop_at = time.perf_counter() + seconds
    threads = [threading.Thread(target=worker, args=(counter, stop_at, chunk_bytes), daemon=True)
               for _ in range(connections)]
    t0 = time.perf_counter()
    for t in threads:
        t.start()
    while any(t.is_alive() for t in threads):
        elapsed = time.perf_counter() - t0
        sys.stdout.write(f"\r    {connections:>2} connection(s): {mbps(counter.n, elapsed):8.1f} Mbps "
                         f"[{min(elapsed, seconds):4.1f}s] ")
        sys.stdout.flush()
        time.sleep(0.25)
    result = mbps(counter.n, time.perf_counter() - t0)
    if counter.n == 0 and counter.error:
        sys.stdout.write(f"\r    {connections:>2} connection(s): FAILED - {counter.error}\n")
        return 0.0
    sys.stdout.write(f"\r    {connections:>2} connection(s): {result:8.1f} Mbps            \n")
    return result


def speed_test(seconds=8, connections=None, upload=True):
    connections = connections or max(4, min(CPU_CORES * 2, 16))
    header(f"4. Speed test: 1 connection vs {connections} parallel connections ({CPU_CORES} CPU cores)")
    print("  Download:")
    d1 = throughput(_download_worker, 1, seconds, 25_000_000)
    dn = throughput(_download_worker, connections, seconds, 25_000_000)
    u1 = un = None
    if upload:
        print("  Upload:")
        # Upload lines are slow: a few connections with small chunks avoids timeouts.
        u1 = throughput(_upload_worker, 1, seconds, 1_000_000)
        un = throughput(_upload_worker, min(connections, 4), seconds, 500_000)
    if d1 > 0 and dn > d1 * 1.2:
        print(f"\n  Parallel connections are {dn / d1:.1f}x faster than one. Use a multi-connection")
        print("  downloader (e.g. `netboost.py download URL`) for big files.")
    return {"down_1": d1, "down_n": dn, "up_1": u1, "up_n": un, "connections": connections}


# --------------------------------------------------------------------------- #
# 5. Multi-connection file downloader (HTTP Range)
# --------------------------------------------------------------------------- #
def parallel_download(url, out=None, connections=None):
    connections = connections or max(4, min(CPU_CORES * 2, 16))
    out = out or os.path.basename(url.split("?")[0]) or "download.bin"
    head = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "NetBoost/1.0"})
    with urllib.request.urlopen(head, timeout=15) as r:
        size = int(r.headers.get("Content-Length") or 0)
        ranges_ok = r.headers.get("Accept-Ranges", "").lower() == "bytes"
    if not size or not ranges_ok:
        print("  Server does not support split downloads; using a single connection.")
        connections = 1
    print(f"  Downloading {out} ({size / 1e6:.1f} MB) with {connections} connection(s)...")
    if size and size < connections * 1_000_000:
        connections = max(1, size // 1_000_000)  # tiny file: fewer parts
    with open(out, "wb") as f:
        if size:
            f.truncate(size)
    counter = Counter()
    lock = threading.Lock()

    def part(start, end):
        pos = start
        for attempt in range(5):
            hdrs = {"User-Agent": "NetBoost/1.0"}
            if ranges_ok:
                hdrs["Range"] = f"bytes={pos}-{end}"  # resume where we stopped
            elif pos:
                counter.add(-pos)  # no resume possible: start over
                pos = 0
            try:
                req = urllib.request.Request(url, headers=hdrs)
                with urllib.request.urlopen(req, timeout=30) as r, open(out, "r+b") as f:
                    while True:
                        data = r.read(256 * 1024)
                        if not data:
                            break
                        with lock:
                            f.seek(pos)
                            f.write(data)
                        pos += len(data)
                        counter.add(len(data))
                return
            except Exception:
                time.sleep(1 + attempt)
        raise RuntimeError(f"part {start}-{end} failed after retries")

    step = (size // connections) if size else 0
    parts = [(i * step, (size - 1) if i == connections - 1 else (i + 1) * step - 1)
             for i in range(connections)] if size else [(0, 0)]
    t0 = time.perf_counter()
    with cf.ThreadPoolExecutor(max_workers=connections) as ex:
        futs = [ex.submit(part, s, e) for s, e in parts]
        while not all(f.done() for f in futs):
            el = time.perf_counter() - t0
            pct = 100 * counter.n / size if size else 0
            sys.stdout.write(f"\r  {pct:5.1f}%  {mbps(counter.n, el):7.1f} Mbps ")
            sys.stdout.flush()
            time.sleep(0.3)
        for f in futs:
            f.result()
    el = time.perf_counter() - t0
    print(f"\r  Done: {counter.n / 1e6:.1f} MB in {el:.1f}s  ({mbps(counter.n, el):.1f} Mbps)   ")


# --------------------------------------------------------------------------- #
# 6. Diagnosis & recommendations
# --------------------------------------------------------------------------- #
def recommend(w, lat, dns_best, chans, speed):
    header("5. Diagnosis & what to do")
    tips = []
    sig = w.get("signal_pct")
    if sig is not None:
        if sig < 50:
            tips.append(f"WEAK SIGNAL ({sig}%). Move closer to the router, remove walls/metal in between, "
                        "raise the router higher, or add a mesh node / Wi-Fi extender.")
        elif sig < 75:
            tips.append(f"Signal is fair ({sig}%). Getting closer or in line of sight will raise link speed.")
    band = (w.get("band") or "").lower()
    if band.startswith("2.4"):
        tips.append("You are on 2.4 GHz (slower, crowded). If your router has 5 GHz, connect to the "
                    "5 GHz network (often named '<name>_5G') for 2-5x more speed when near the router.")
        ch = w.get("channel")
        if ch and ch.isdigit() and chans:
            mine = int(ch)
            crowd = sum(n for c, n in chans.items() if abs(c - mine) < 5)
            if crowd > 3:
                best = min((1, 6, 11), key=lambda c: sum(n for k, n in chans.items() if abs(k - c) < 5))
                tips.append(f"Channel {mine} is crowded ({crowd} networks overlap). In the router admin page "
                            f"set the 2.4 GHz channel to {best} (use only 1, 6 or 11).")
        if (w.get("rx_mbps") or 0) <= 72.2 and sum(chans.values() or [0]) <= 3:
            tips.append("Your link is 72 Mbps, which means a 20 MHz channel. Few networks are nearby, so in "
                        "the router's Wi-Fi settings set 2.4 GHz channel width to 40 MHz - this can double "
                        "the link (up to 150 Mbps) for free.")
    rx = w.get("rx_mbps")
    if rx and speed and speed.get("down_n") and speed["down_n"] > 0.4 * rx:
        tips.append(f"Wi-Fi link rate ({rx:.0f} Mbps) is likely the bottleneck: real-world Wi-Fi gives only "
                    f"about half the link rate (~{rx * 0.55:.0f} Mbps here). A faster link (5 GHz, 40 MHz "
                    "channel width, better adapter) or a LAN cable will raise it.")
    radio = (w.get("radio") or "").lower()
    if "802.11n" in radio or "802.11g" in radio:
        tips.append("Your adapter/router link is Wi-Fi 4 (802.11n) or older. A Wi-Fi 5/6 USB adapter "
                    "(and router) can give a big jump.")
    gw = next((v for k, v in lat.items() if k.startswith("Router")), None)
    net = lat.get("Cloudflare 1.1.1.1")
    if gw and gw["avg"] is not None:
        if gw["avg"] > 10 or gw["jitter"] > 5 or gw["loss"] > 0:
            tips.append(f"The Wi-Fi hop itself is unstable (router ping {gw['avg']:.0f} ms, jitter "
                        f"{gw['jitter']:.0f} ms, loss {gw['loss']:.0f}%). This should be under 5 ms. "
                        "Fix signal/channel above, turn off Wi-Fi power saving (--apply), and restart the router.")
    if net and net["avg"] is not None and gw and gw["avg"] is not None:
        if net["avg"] - gw["avg"] > 80 or net["loss"] > 1:
            tips.append("Wi-Fi is fine but the internet side is slow/lossy - that is your ISP line. "
                        "Restart the modem, check cables, and if it continues, complain to your ISP.")
    if dns_best and dns_best[0].startswith("Current"):
        tips.append(f"Your current DNS ({dns_best[1][0]}) is already the fastest. Keep it - "
                    "--apply will not change it.")
    elif dns_best:
        tips.append(f"Use the fastest DNS: {dns_best[0]} {', '.join(dns_best[1])}  "
                    "(run with --apply as Administrator to set it automatically).")
    if speed and speed.get("down_1") and speed["down_n"] > 1.3 * speed["down_1"]:
        tips.append("Single connections are being limited. Multi-connection downloads help: "
                    "`python netboost.py download <url>`.")
    tips.append("For gaming/video calls: pause big downloads/updates on other devices, and if your router "
                "has QoS / 'SQM' / 'gaming mode', turn it on - it is the best fix for ping spikes.")
    tips.append("The single best upgrade for a PC near the router: a LAN (Ethernet) cable - "
                "lowest ping, zero Wi-Fi interference.")
    for i, t in enumerate(tips, 1):
        print(f"  {i}. {t}")


# --------------------------------------------------------------------------- #
# 7. Apply / restore Windows optimizations
# --------------------------------------------------------------------------- #
def apply_tweaks(dns_best, adapter_name):
    header("6. Applying optimizations")
    if OS != "Windows":
        print("  Automatic tweaks are for Windows. On Linux/macOS set DNS in network settings.")
        return
    if not is_admin():
        print("  Need Administrator rights. Right-click 'NetBoost.bat' -> 'Run as administrator'.")
        return
    name = adapter_name or "Wi-Fi"
    steps = [
        ("Multi-core packet processing (RSS)", ["netsh", "int", "tcp", "set", "global", "rss=enabled"]),
        ("TCP receive window auto-tuning", ["netsh", "int", "tcp", "set", "global", "autotuninglevel=normal"]),
        ("TCP fast-open", ["netsh", "int", "tcp", "set", "global", "fastopen=enabled"]),
        ("Flush DNS cache", ["ipconfig", "/flushdns"]),
        ("Wi-Fi adapter: max performance (AC power)",
         ["powercfg", "/setacvalueindex", "SCHEME_CURRENT",
          "19cab1ff-7ad8-4ec2-a48a-77be8dc1c6c4", "12bbebe6-58d6-4636-95bb-3217ef867c1a", "0"]),
        ("Wi-Fi adapter: max performance (battery)",
         ["powercfg", "/setdcvalueindex", "SCHEME_CURRENT",
          "19cab1ff-7ad8-4ec2-a48a-77be8dc1c6c4", "12bbebe6-58d6-4636-95bb-3217ef867c1a", "0"]),
        ("Activate power scheme", ["powercfg", "/setactive", "SCHEME_CURRENT"]),
    ]
    if dns_best and len(dns_best[1]) >= 1 and not dns_best[0].startswith("Current"):
        p = dns_best[1]
        steps.append((f"Set DNS to {dns_best[0]} {p[0]}",
                      ["netsh", "interface", "ipv4", "set", "dnsservers", f"name={name}",
                       "source=static", f"address={p[0]}", "register=primary", "validate=no"]))
        if len(p) > 1:
            steps.append((f"Add backup DNS {p[1]}",
                          ["netsh", "interface", "ipv4", "add", "dnsservers", f"name={name}",
                           f"address={p[1]}", "index=2", "validate=no"]))
    for label, cmd in steps:
        r = subprocess.run(cmd, capture_output=True)
        print(f"  [{'OK ' if r.returncode == 0 else 'ERR'}] {label}")
    print("\n  Done. Undo DNS/power changes any time with:  python netboost.py --restore")


def restore_tweaks(adapter_name):
    header("Restoring defaults")
    if OS != "Windows" or not is_admin():
        print("  Run on Windows as Administrator.")
        return
    name = adapter_name or "Wi-Fi"
    for label, cmd in [
        ("DNS back to automatic (router/ISP)",
         ["netsh", "interface", "ipv4", "set", "dnsservers", f"name={name}", "source=dhcp"]),
        ("Wi-Fi power saving back to default (battery)",
         ["powercfg", "/setdcvalueindex", "SCHEME_CURRENT",
          "19cab1ff-7ad8-4ec2-a48a-77be8dc1c6c4", "12bbebe6-58d6-4636-95bb-3217ef867c1a", "2"]),
        ("Activate power scheme", ["powercfg", "/setactive", "SCHEME_CURRENT"]),
        ("Flush DNS cache", ["ipconfig", "/flushdns"]),
    ]:
        r = subprocess.run(cmd, capture_output=True)
        print(f"  [{'OK ' if r.returncode == 0 else 'ERR'}] {label}")


# --------------------------------------------------------------------------- #
# 8. Live stability monitor
# --------------------------------------------------------------------------- #
def monitor(gw, interval=1.0):
    header("Live connection monitor (Ctrl+C to stop)")
    targets = [("Router", gw)] if gw else []
    targets.append(("Internet", "1.1.1.1"))
    hist = {n: [] for n, _ in targets}
    try:
        while True:
            line = []
            for name, host in targets:
                s = ping(host, 1)[0]
                hist[name] = (hist[name] + [s])[-60:]
                st = ping_stats(hist[name])
                now = f"{s:5.0f}ms" if s is not None else " LOST "
                spike = " SPIKE" if s and st["avg"] and s > st["avg"] * 3 and s > 30 else ""
                line.append(f"{name}: {now} (avg {st['avg'] or 0:4.0f} jit {st['jitter'] or 0:4.0f} "
                            f"loss {st['loss']:3.0f}%){spike}")
            w = wifi_info()
            sig = f"  signal {w['signal_pct']}%" if "signal_pct" in w else ""
            print(time.strftime("%H:%M:%S ") + " | ".join(line) + sig)
            time.sleep(interval)
    except KeyboardInterrupt:
        print("\n  Stopped.")


# --------------------------------------------------------------------------- #
def main():
    ap = argparse.ArgumentParser(description="NetBoost - analyze and optimize your Wi-Fi/internet.")
    ap.add_argument("command", nargs="?", default="analyze",
                    choices=["analyze", "monitor", "download"],
                    help="analyze (default), monitor (live ping), download <url>")
    ap.add_argument("url", nargs="?", help="URL for the download command")
    ap.add_argument("-o", "--output", help="output file for download")
    ap.add_argument("-c", "--connections", type=int, help="parallel connections (default: 2 x CPU cores, max 16)")
    ap.add_argument("--seconds", type=int, default=8, help="seconds per speed-test phase")
    ap.add_argument("--no-speed", action="store_true", help="skip the speed test")
    ap.add_argument("--no-upload", action="store_true", help="skip the upload test")
    ap.add_argument("--apply", action="store_true", help="apply Windows optimizations (Administrator)")
    ap.add_argument("--restore", action="store_true", help="undo DNS / power changes (Administrator)")
    a = ap.parse_args()

    print("NetBoost - Wi-Fi & internet analyzer / optimizer")
    if a.command == "download":
        if not a.url:
            ap.error("download needs a URL")
        parallel_download(a.url, a.output, a.connections)
        return
    if a.restore:
        restore_tweaks(wifi_info().get("adapter_name"))
        return
    w, gw, chans = analyze_hardware()
    if a.command == "monitor":
        monitor(gw)
        return
    lat = analyze_latency(gw)
    dns_best, _ = benchmark_dns()
    speed = None if a.no_speed else speed_test(a.seconds, a.connections, not a.no_upload)
    recommend(w, lat, dns_best, chans, speed)
    if a.apply:
        apply_tweaks(dns_best, w.get("adapter_name"))
    else:
        print("\n  Tip: run NetBoost.bat as Administrator and choose 'Apply' to set these automatically.")


if __name__ == "__main__":
    main()
