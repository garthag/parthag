"""Real-time layer: market hours, live quotes, intraday momentum, watchlists and alerts."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, time, timedelta
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd

from . import data

IST = ZoneInfo("Asia/Kolkata")
PRE_OPEN, OPEN, CLOSE = time(9, 0), time(9, 15), time(15, 30)


def market_status(now: datetime | None = None) -> dict:
    """NSE/BSE session state in IST (exchange holidays are not modelled)."""
    now = (now or datetime.now(IST)).astimezone(IST)
    t, weekday = now.time(), now.weekday()

    def next_open():
        d = now.date() + timedelta(days=0 if t < OPEN and weekday < 5 else 1)
        while d.weekday() >= 5:
            d += timedelta(days=1)
        return datetime.combine(d, OPEN, IST)

    if weekday < 5 and OPEN <= t < CLOSE:
        left = datetime.combine(now.date(), CLOSE, IST) - now
        return {"open": True, "label": "🟢 Market open", "detail": f"closes in {_fmt(left)}", "now": now}
    if weekday < 5 and PRE_OPEN <= t < OPEN:
        return {"open": False, "label": "🟡 Pre-open session", "detail": f"opens in {_fmt(next_open() - now)}", "now": now}
    return {"open": False, "label": "🔴 Market closed", "detail": f"opens in {_fmt(next_open() - now)}", "now": now}


def _fmt(td: timedelta) -> str:
    mins = int(td.total_seconds() // 60)
    d, rem = divmod(mins, 1440)
    h, m = divmod(rem, 60)
    return (f"{d}d " if d else "") + f"{h}h {m:02d}m"


@dataclass
class Quote:
    symbol: str
    price: float
    prev_close: float
    day_open: float
    day_high: float
    day_low: float
    volume: float
    updated: datetime
    bars: pd.DataFrame = field(default_factory=pd.DataFrame)
    source: str = "Yahoo Finance"
    extra: dict = field(default_factory=dict)  # broker extras: market depth, buy/sell qty, circuits

    @property
    def change(self) -> float:
        return self.price - self.prev_close

    @property
    def change_pct(self) -> float:
        return 100 * self.change / self.prev_close if self.prev_close else 0.0


def _quote_from_bars(symbol: str, bars: pd.DataFrame, prev_close: float) -> Quote:
    return Quote(symbol, float(bars["Close"].iloc[-1]), prev_close, float(bars["Open"].iloc[0]),
                 float(bars["High"].max()), float(bars["Low"].min()), float(bars["Volume"].sum()),
                 datetime.now(IST), bars)


def fetch_quote(symbol: str) -> Quote:
    """Latest 1-minute bars for the current (or last) session from Yahoo Finance."""
    if data.yf is None:
        raise RuntimeError("yfinance is not installed")
    t = data.yf.Ticker(symbol)
    bars = data._clean_ohlcv(t.history(period="5d", interval="1m"))
    if bars.empty:
        raise ValueError(f"No live data for {symbol}")
    last_day = bars.index[-1].date()
    today = bars[bars.index.date == last_day]
    earlier = bars[bars.index.date < last_day]
    prev_close = float(earlier["Close"].iloc[-1]) if not earlier.empty else float(today["Open"].iloc[0])
    try:
        prev_close = float(t.fast_info["previous_close"]) or prev_close
    except Exception:
        pass
    return _quote_from_bars(symbol, today, prev_close)


class DemoFeed:
    """Simulated tick stream so the real-time UI can be used offline."""

    def __init__(self, symbol: str, start_price: float, seed: int | None = None):
        self.symbol = symbol
        self.prev_close = start_price
        self.rng = np.random.default_rng(seed if seed is not None else sum(map(ord, symbol)))
        now = datetime.now(IST).replace(second=0, microsecond=0)
        idx = pd.date_range(end=now, periods=60, freq="1min")
        r = self.rng.normal(0, 0.0012, len(idx))
        close = start_price * np.exp(np.cumsum(r))
        self.bars = self._ohlc(idx, np.r_[start_price, close[:-1]], close)

    def _ohlc(self, idx, open_, close):
        noise = np.abs(self.rng.normal(0, 0.0006, len(idx)))
        return pd.DataFrame({
            "Open": open_, "High": np.maximum(open_, close) * (1 + noise),
            "Low": np.minimum(open_, close) * (1 - noise), "Close": close,
            "Volume": self.rng.lognormal(9, 0.5, len(idx)).astype(int),
        }, index=idx)

    def tick(self) -> Quote:
        last = float(self.bars["Close"].iloc[-1])
        nxt = last * float(np.exp(self.rng.normal(0, 0.0015)))
        ts = max(self.bars.index[-1] + timedelta(seconds=1), datetime.now(IST).replace(microsecond=0))
        self.bars = pd.concat([self.bars, self._ohlc(pd.DatetimeIndex([ts]), np.array([last]), np.array([nxt]))]).iloc[-375:]
        return _quote_from_bars(self.symbol, self.bars, self.prev_close)


def momentum(bars: pd.DataFrame) -> dict:
    """Short-term intraday read from 1-minute bars: VWAP, fast/slow EMA, RSI."""
    if bars is None or len(bars) < 5:
        return {"label": "Not enough data", "score": 0.0, "vwap": np.nan, "rsi": np.nan, "reasons": []}
    c = bars["Close"]
    tp = (bars["High"] + bars["Low"] + c) / 3
    vwap = float((tp * bars["Volume"]).sum() / max(bars["Volume"].sum(), 1))
    ema_f, ema_s = c.ewm(span=9).mean().iloc[-1], c.ewm(span=21).mean().iloc[-1]
    delta = c.diff()
    gain = delta.clip(lower=0).rolling(14, min_periods=5).mean().iloc[-1]
    loss = (-delta.clip(upper=0)).rolling(14, min_periods=5).mean().iloc[-1]
    rsi = float(100 - 100 / (1 + gain / loss)) if loss else 100.0
    last = float(c.iloc[-1])
    votes = {
        f"Price {'above' if last > vwap else 'below'} VWAP": 1 if last > vwap else -1,
        f"EMA9 {'above' if ema_f > ema_s else 'below'} EMA21": 1 if ema_f > ema_s else -1,
        f"RSI {rsi:.0f}": 0.5 if 50 < rsi < 70 else -0.5 if 30 < rsi <= 50 else -0.5 if rsi >= 70 else 0.5,
        "Last 5 bars " + ("rising" if c.iloc[-1] > c.iloc[-5] else "falling"): 1 if c.iloc[-1] > c.iloc[-5] else -1,
    }
    score = float(np.mean(list(votes.values())))
    label = "🟢 Bullish" if score > 0.3 else "🔴 Bearish" if score < -0.3 else "🟡 Sideways"
    return {"label": label, "score": score, "vwap": vwap, "rsi": rsi, "reasons": list(votes)}


def fetch_watchlist(symbols: list[str]) -> pd.DataFrame:
    rows = []
    for sym in symbols:
        try:
            q = fetch_quote(sym)
            rows.append(watch_row(q))
        except Exception as exc:
            rows.append({"Symbol": sym, "Signal": f"⚠️ {type(exc).__name__}"})
    return pd.DataFrame(rows)


def watch_row(q: Quote) -> dict:
    m = momentum(q.bars)
    return {"Symbol": q.symbol, "Price": q.price, "Change": q.change, "Change %": q.change_pct,
            "Day low": q.day_low, "Day high": q.day_high, "Volume": q.volume, "Signal": m["label"],
            "Updated": q.updated.strftime("%H:%M:%S")}


@dataclass
class Alert:
    symbol: str
    direction: str  # "above" or "below"
    level: float
    triggered: bool = False

    def check(self, price: float) -> bool:
        hit = price >= self.level if self.direction == "above" else price <= self.level
        if hit and not self.triggered:
            self.triggered = True
            return True
        return False
