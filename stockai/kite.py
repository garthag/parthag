"""Zerodha Kite Connect data source: login, quotes, 1-minute bars, market depth and WebSocket ticks.

Needs a Kite Connect app (https://developers.kite.trade) with market-data access.
Credentials are read from Streamlit secrets or environment variables:

    KITE_API_KEY, KITE_API_SECRET   (required)
    KITE_ACCESS_TOKEN               (optional; otherwise log in from the app)

Access tokens expire every day (around 6 AM IST), so the app offers a one-click login
and caches the day's token in ``.kite_token.json`` next to the app.
"""

from __future__ import annotations

import json
import os
import threading
from datetime import datetime, timedelta
from pathlib import Path

import pandas as pd

from .live import IST, Quote, _quote_from_bars

try:
    from kiteconnect import KiteConnect, KiteTicker
except ImportError:  # pragma: no cover - optional dependency
    KiteConnect = KiteTicker = None

TOKEN_FILE = Path(__file__).resolve().parent.parent / ".kite_token.json"
YF_TO_KITE_EXCHANGE = {".NS": "NSE", ".BO": "BSE"}


def available() -> bool:
    return KiteConnect is not None


def credentials(secrets: dict | None = None) -> dict:
    """API key/secret/token from Streamlit secrets first, then environment variables."""
    secrets = secrets or {}
    get = lambda k: str(secrets.get(k) or os.environ.get(k) or "").strip()  # noqa: E731
    return {"api_key": get("KITE_API_KEY"), "api_secret": get("KITE_API_SECRET"),
            "access_token": get("KITE_ACCESS_TOKEN")}


def load_saved_token(api_key: str) -> str | None:
    """Today's cached access token, if it was issued after the last 6 AM IST reset."""
    try:
        saved = json.loads(TOKEN_FILE.read_text())
    except (OSError, ValueError):
        return None
    if saved.get("api_key") != api_key:
        return None
    issued = datetime.fromisoformat(saved["issued"])
    now = datetime.now(IST)
    reset = now.replace(hour=6, minute=0, second=0, microsecond=0)
    if now < reset:
        reset -= timedelta(days=1)
    return saved["access_token"] if issued >= reset else None


def save_token(api_key: str, access_token: str) -> None:
    try:
        TOKEN_FILE.write_text(json.dumps({"api_key": api_key, "access_token": access_token,
                                          "issued": datetime.now(IST).isoformat()}))
    except OSError:
        pass


def login_url(api_key: str) -> str:
    return KiteConnect(api_key=api_key).login_url()


def exchange_request_token(api_key: str, api_secret: str, request_token: str) -> str:
    """Complete the login redirect: request_token -> access_token (and cache it)."""
    session = KiteConnect(api_key=api_key).generate_session(request_token, api_secret=api_secret)
    token = session["access_token"]
    save_token(api_key, token)
    return token


class KiteSource:
    """Quotes and intraday candles from Kite Connect, plus an optional live tick stream."""

    def __init__(self, api_key: str, access_token: str, client=None):
        if client is None:
            if KiteConnect is None:
                raise RuntimeError("Install the 'kiteconnect' package to use Zerodha Kite")
            client = KiteConnect(api_key=api_key)
            client.set_access_token(access_token)
        self.kite = client
        self.api_key, self.access_token = api_key, access_token
        self._bse_codes: dict[str, str] | None = None
        self._bars: dict[int, tuple[datetime, pd.DataFrame]] = {}
        self._tokens: dict[str, int] = {}
        self._snapshots: dict[str, tuple[datetime, dict]] = {}
        self.stream: KiteStream | None = None

    # -- symbols ------------------------------------------------------------
    def to_kite(self, symbol: str) -> str:
        """'RELIANCE.NS' -> 'NSE:RELIANCE'; BSE scrip code '500325.BO' -> 'BSE:RELIANCE'."""
        base, suffix = symbol[:-3], symbol[-3:]
        exch = YF_TO_KITE_EXCHANGE.get(suffix)
        if exch is None:
            raise ValueError(f"Not an NSE/BSE symbol: {symbol}")
        if exch == "BSE" and base.isdigit():
            base = self._bse_code_map().get(base, base)
        return f"{exch}:{base}"

    def _bse_code_map(self) -> dict[str, str]:
        if self._bse_codes is None:
            self._bse_codes = {str(i["exchange_token"]): i["tradingsymbol"]
                               for i in self.kite.instruments("BSE") if i.get("segment") == "BSE"}
        return self._bse_codes

    def profile_name(self) -> str:
        p = self.kite.profile()
        return p.get("user_name") or p.get("user_id", "")

    # -- data ---------------------------------------------------------------
    def quotes(self, symbols: list[str]) -> dict[str, dict]:
        keys = {self.to_kite(s): s for s in symbols}
        raw = self.kite.quote(*keys)
        return {keys[k]: v for k, v in raw.items() if k in keys}

    def token_for(self, symbol: str) -> int:
        """Instrument token for a Yahoo-style symbol or a raw Kite key like 'NSE:NIFTY 50'."""
        if symbol not in self._tokens:
            key = symbol if ":" in symbol else self.to_kite(symbol)
            self._tokens[symbol] = self.kite.quote(key)[key]["instrument_token"]
        return self._tokens[symbol]

    def history(self, symbol: str, interval: str = "day", days: int = 1825) -> pd.DataFrame:
        """OHLCV candles (Kite caps 'day' requests at 2000 days, '5minute' at 100 days)."""
        now = datetime.now(IST).replace(tzinfo=None, microsecond=0)
        candles = self.kite.historical_data(self.token_for(symbol), now - timedelta(days=days), now, interval)
        if not candles:
            return pd.DataFrame(columns=["Open", "High", "Low", "Close", "Volume"])
        df = pd.DataFrame(candles).rename(columns=str.title).set_index("Date")
        idx = pd.to_datetime(df.index)
        idx = idx.tz_localize(IST) if idx.tz is None else idx.tz_convert(IST)
        df.index = idx.tz_localize(None).normalize() if interval == "day" else idx
        return df[["Open", "High", "Low", "Close", "Volume"]].astype(float)

    def minute_bars(self, instrument_token: int) -> pd.DataFrame:
        """1-minute candles of the latest session (today, or the last trading day when closed)."""
        now = datetime.now(IST).replace(tzinfo=None, microsecond=0)
        candles = self.kite.historical_data(instrument_token, now - timedelta(days=6), now, "minute")
        if not candles:
            return pd.DataFrame(columns=["Open", "High", "Low", "Close", "Volume"])
        df = pd.DataFrame(candles).rename(columns=str.title).set_index("Date")
        df.index = pd.to_datetime(df.index)
        df.index = df.index.tz_localize(IST) if df.index.tz is None else df.index.tz_convert(IST)
        df = df[df.index.date == df.index[-1].date()]
        return df[["Open", "High", "Low", "Close", "Volume"]].astype(float)

    def quote(self, symbol: str, with_bars: bool = True) -> Quote:
        return self.quote_many([symbol], with_bars)[symbol]

    def quote_many(self, symbols: list[str], with_bars: bool = True, max_age: float = 2.0) -> dict[str, Quote]:
        """Quotes for many symbols using at most one REST call (Kite allows ~1 quote request/sec).

        Symbols with a fresh WebSocket tick, or a REST snapshot younger than ``max_age``
        seconds, are served without hitting the API.
        """
        now = datetime.now(IST)
        need = []
        for sym in symbols:
            token = self._tokens.get(sym)
            tick = self.stream.latest(token) if (self.stream and token) else None
            snap = self._snapshots.get(sym)
            fresh_tick = tick and (now - tick["_received"]).total_seconds() < 10
            fresh_snap = snap and (now - snap[0]).total_seconds() < max_age
            if not (fresh_tick or fresh_snap):
                need.append(sym)
        if need:
            for sym, q in self.quotes(need).items():
                self._snapshots[sym] = (now, q)
                self._tokens[sym] = q["instrument_token"]
            if self.stream:
                self.stream.subscribe([self._tokens[s] for s in need if s in self._tokens])
        out = {}
        for sym in symbols:
            if sym not in self._snapshots:
                raise ValueError(f"Kite returned no quote for {sym} ({self.to_kite(sym)})")
            q = dict(self._snapshots[sym][1])
            token = q["instrument_token"]
            tick = self.stream.latest(token) if self.stream else None
            if tick:  # WebSocket data is fresher than the REST snapshot
                q.update({"last_price": tick["last_price"], "ohlc": tick.get("ohlc", q.get("ohlc")),
                          "depth": tick.get("depth", q.get("depth")),
                          "volume": tick.get("volume_traded", q.get("volume")),
                          "buy_quantity": tick.get("total_buy_quantity", q.get("buy_quantity")),
                          "sell_quantity": tick.get("total_sell_quantity", q.get("sell_quantity")),
                          "average_price": tick.get("average_traded_price", q.get("average_price"))})
            bars = self.cached_bars(token) if with_bars else pd.DataFrame()
            out[sym] = to_quote(sym, q, bars)
        return out

    def cached_bars(self, token: int, max_age: int = 30) -> pd.DataFrame:
        """1-minute candles, re-fetched at most every ``max_age`` seconds (historical API is rate-limited)."""
        now = datetime.now(IST)
        hit = self._bars.get(token)
        if hit is None or (now - hit[0]).total_seconds() > max_age:
            try:
                hit = (now, self.minute_bars(token))
            except Exception:
                hit = (now, hit[1] if hit else pd.DataFrame())
            self._bars[token] = hit
        return hit[1]

    # -- streaming ----------------------------------------------------------
    def start_stream(self) -> "KiteStream":
        if self.stream is None:
            self.stream = KiteStream(self.api_key, self.access_token)
        return self.stream


def to_quote(symbol: str, q: dict, bars: pd.DataFrame) -> Quote:
    """Convert a Kite quote (REST or full-mode tick) into the app's Quote."""
    ohlc = q.get("ohlc", {})
    price = float(q["last_price"])
    prev_close = float(ohlc.get("close") or price)
    if bars is None or bars.empty:
        now = datetime.now(IST)
        bars = pd.DataFrame({"Open": [ohlc.get("open", price)], "High": [ohlc.get("high", price)],
                             "Low": [ohlc.get("low", price)], "Close": [price],
                             "Volume": [q.get("volume", 0)]}, index=[now])
    else:
        bars = bars.astype(float)  # copy; Kite may send integer prices
        last = bars.index[-1]
        bars.loc[last, "Close"] = price
        bars.loc[last, "High"] = max(bars.loc[last, "High"], price)
        bars.loc[last, "Low"] = min(bars.loc[last, "Low"], price)
    out = _quote_from_bars(symbol, bars, prev_close)
    out.price = price
    out.updated = datetime.now(IST)
    out.day_open = float(ohlc.get("open") or out.day_open)
    out.day_high = max(float(ohlc.get("high") or price), price)
    out.day_low = min(float(ohlc.get("low") or price), price)
    out.volume = float(q.get("volume") or q.get("volume_traded") or out.volume)
    out.source = "Zerodha Kite"
    depth = q.get("depth") or {}
    out.extra = {
        "buy_qty": q.get("buy_quantity", q.get("total_buy_quantity")),
        "sell_qty": q.get("sell_quantity", q.get("total_sell_quantity")),
        "avg_price": q.get("average_price", q.get("average_traded_price")),
        "upper_circuit": q.get("upper_circuit_limit"), "lower_circuit": q.get("lower_circuit_limit"),
        "depth_buy": depth.get("buy", []), "depth_sell": depth.get("sell", []),
    }
    return out


class KiteStream:
    """Background KiteTicker WebSocket keeping the latest full-mode tick per instrument.

    Twisted's reactor can only start once per process, so create one stream per app run.
    """

    def __init__(self, api_key: str, access_token: str):
        self.ticks: dict[int, dict] = {}
        self.tokens: set[int] = set()
        self.status = "connecting"
        self._lock = threading.Lock()
        self.kws = KiteTicker(api_key, access_token)
        self.kws.on_ticks = self._on_ticks
        self.kws.on_connect = self._on_connect
        self.kws.on_close = lambda ws, code, reason: setattr(self, "status", f"closed: {reason}")
        self.kws.on_error = lambda ws, code, reason: setattr(self, "status", f"error: {reason}")
        self.kws.on_reconnect = lambda ws, attempts: setattr(self, "status", f"reconnecting ({attempts})")
        self.kws.connect(threaded=True)

    def _on_connect(self, ws, response):
        self.status = "live"
        if self.tokens:
            ws.subscribe(list(self.tokens))
            ws.set_mode(ws.MODE_FULL, list(self.tokens))

    def _on_ticks(self, ws, ticks):
        with self._lock:
            for t in ticks:
                t["_received"] = datetime.now(IST)
                self.ticks[t["instrument_token"]] = t

    def subscribe(self, tokens: list[int]) -> None:
        new = [t for t in tokens if t not in self.tokens]
        if not new:
            return
        self.tokens.update(new)
        if self.kws.is_connected():
            self.kws.subscribe(new)
            self.kws.set_mode(self.kws.MODE_FULL, new)

    def latest(self, token: int) -> dict | None:
        with self._lock:
            return self.ticks.get(token)
