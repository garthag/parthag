"""Free near-real-time NSE quotes from the public nseindia.com website (no broker account needed).

These are the JSON endpoints the NSE website itself uses. They are unofficial: NSE may change them,
rate-limit heavy use, or block cloud servers. Use them for personal, light polling only. Callers
should fall back to another source on any error.
"""

from __future__ import annotations

import threading
import time
from datetime import datetime

import pandas as pd
import requests

from .live import IST, Quote, _quote_from_bars

BASE = "https://www.nseindia.com"
HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                  "Chrome/124.0 Safari/537.36",
    "Accept": "application/json, text/plain, */*",
    "Accept-Language": "en-IN,en;q=0.9",
    "Referer": f"{BASE}/get-quotes/equity",
}


class NSESource:
    """Session-cookie aware client for NSE quote, order-book and intraday-chart data."""

    min_interval = 0.35  # seconds between requests, to stay polite

    def __init__(self, session: requests.Session | None = None):
        self.s = session or requests.Session()
        self.s.headers.update(HEADERS)
        self._primed = False
        self._last = 0.0
        self._lock = threading.Lock()

    def _get(self, path: str, params: dict | None = None) -> dict:
        with self._lock:
            for attempt in range(2):
                if not self._primed:
                    self.s.get(BASE, timeout=10)  # sets the cookies the API requires
                    self._primed = True
                wait = self.min_interval - (time.monotonic() - self._last)
                if wait > 0:
                    time.sleep(wait)
                r = self.s.get(BASE + path, params=params, timeout=10)
                self._last = time.monotonic()
                if r.status_code in (401, 403) and attempt == 0:
                    self._primed = False  # cookies expired: refresh once
                    continue
                r.raise_for_status()
                return r.json()
        raise RuntimeError("NSE request failed")

    @staticmethod
    def code(symbol: str) -> str:
        if not symbol.endswith(".NS"):
            raise ValueError("The NSE source supports NSE symbols only")
        return symbol[:-3]

    def quote_json(self, symbol: str) -> dict:
        return self._get("/api/quote-equity", {"symbol": self.code(symbol)})

    def trade_info(self, symbol: str) -> dict:
        return self._get("/api/quote-equity", {"symbol": self.code(symbol), "section": "trade_info"})

    def intraday_bars(self, symbol: str) -> pd.DataFrame:
        """Today's price line resampled to 1-minute candles (NSE gives prices but no volume)."""
        raw = self._get("/api/chart-databyindex", {"index": f"{self.code(symbol)}EQN"})
        points = raw.get("grapthData") or raw.get("graphData") or []
        if not points:
            return pd.DataFrame(columns=["Open", "High", "Low", "Close", "Volume"])
        # NSE encodes IST wall-clock time as if it were UTC epoch milliseconds.
        ts = pd.to_datetime([p[0] for p in points], unit="ms").tz_localize(IST)
        px = pd.Series([float(p[1]) for p in points], index=ts)
        bars = px.resample("1min").ohlc().dropna()
        bars.columns = ["Open", "High", "Low", "Close"]
        bars["Volume"] = 1.0  # equal weights, so VWAP becomes a time-weighted average here
        return bars

    def quote(self, symbol: str, with_bars: bool = True, with_depth: bool = False) -> Quote:
        q = self.quote_json(symbol)
        p = q["priceInfo"]
        price = float(p["lastPrice"])
        hl = p.get("intraDayHighLow") or {}
        bars = pd.DataFrame()
        if with_bars:
            try:
                bars = self.intraday_bars(symbol)
            except Exception:
                bars = pd.DataFrame()
        if bars.empty:
            bars = pd.DataFrame({"Open": [p.get("open") or price], "High": [hl.get("max") or price],
                                 "Low": [hl.get("min") or price], "Close": [price], "Volume": [1.0]},
                                index=[datetime.now(IST)])
        out = _quote_from_bars(symbol, bars, float(p.get("previousClose") or price))
        out.price = price
        out.day_open = float(p.get("open") or out.day_open)
        out.day_high = float(hl.get("max") or out.day_high)
        out.day_low = float(hl.get("min") or out.day_low)
        out.source = "NSE India"
        out.extra = {"avg_price": p.get("vwap"), "upper_circuit": _num(p.get("upperCP")),
                     "lower_circuit": _num(p.get("lowerCP"))}
        if with_depth:
            try:
                out.extra.update(parse_trade_info(self.trade_info(symbol)))
            except Exception:
                pass
        return out


def _num(v):
    try:
        return float(str(v).replace(",", ""))
    except (TypeError, ValueError):
        return None


def parse_trade_info(t: dict) -> dict:
    """Order book and delivery % from the trade_info section."""
    book = t.get("marketDeptOrderBook") or {}
    dp = t.get("securityWiseDP") or {}
    side = lambda rows: [{"price": r.get("price"), "quantity": r.get("quantity"), "orders": None}  # noqa: E731
                         for r in rows or [] if r.get("price")]
    out = {
        "buy_qty": book.get("totalBuyQuantity"), "sell_qty": book.get("totalSellQuantity"),
        "depth_buy": side(book.get("bid")), "depth_sell": side(book.get("ask")),
        "delivery_pct": _num(dp.get("deliveryToTradedQuantity")),
        "delivery_qty": dp.get("deliveryQuantity"),
    }
    return out
