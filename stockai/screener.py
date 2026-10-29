"""Suggestion box: scan the sector universe and rank the best buy candidates per horizon."""

from __future__ import annotations

import multiprocessing
import os
import sys
import types
from contextlib import contextmanager
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor
from concurrent.futures.process import BrokenProcessPool
from typing import Callable

import numpy as np
import pandas as pd

from . import data, signals
from .forecast import HORIZONS
from .universe import NAMES, SECTORS, sector_of

BUY_ACTIONS = ("STRONG BUY", "BUY")


def load_histories(codes: list[str], exchange: str, demo: bool = False, broker=None) -> tuple[dict, pd.DataFrame | None]:
    """Five years of daily candles for every code, plus the exchange index."""
    suffix = data.EXCHANGE_SUFFIX[exchange]
    if demo:
        hist = {c: data.demo_bundle(c, exchange).daily for c in codes}
        return hist, data.synthetic_history(seed=99, start_price=20000)
    if broker is not None:
        hist = {}
        for c in codes:
            try:
                hist[c] = broker.history(c + suffix, "day", 1825)
            except Exception:
                continue
        try:
            index = broker.history(data.KITE_INDEX[exchange], "day", 1825)
        except Exception:
            index = None
        return hist, index
    tickers = [c + suffix for c in codes]
    raw = data.yf.download(tickers, period="5y", interval="1d", group_by="ticker", auto_adjust=True,
                           threads=True, progress=False)
    hist = {}
    for c, t in zip(codes, tickers):
        try:
            df = data._clean_ohlcv(raw[t] if len(tickers) > 1 else raw)
        except KeyError:
            continue
        if len(df) > 300:
            hist[c] = df
    try:
        index = data.fetch_history(data.INDEX_SYMBOL[exchange], period="5y", interval="1d")
    except Exception:
        index = None
    return hist, index


def _scan_one(code: str, daily: pd.DataFrame, index: pd.DataFrame | None, exchange: str) -> list[dict]:
    q = signals.quick_analyze(daily, index)
    rows = []
    for label, plan in q["plans"].items():
        fc = q["forecasts"][label]
        rows.append({
            "Symbol": code + data.EXCHANGE_SUFFIX[exchange], "Code": code,
            "Name": data.POPULAR_STOCKS.get(code) or NAMES.get(code, code), "Sector": sector_of(code), "Horizon": label,
            "Action": plan.action, "Score": plan.score, "Price": plan.entry,
            "Buy below": plan.entry * 1.003, "Target": plan.target, "Stop-loss": plan.stop_loss,
            "Profit %": plan.expected_profit_pct, "Risk %": plan.potential_loss_pct,
            "Risk:Reward": plan.risk_reward, "P(rise) %": plan.probability_up * 100,
            "Confidence %": plan.confidence * 100, "Predicted": fc.predicted_price,
            "Low": fc.low, "High": fc.high,
            "Technicals": q["technical"]["label"], "Buyers vs sellers": q["pressure"]["verdict"],
            "RSI": q["rsi"], "1M %": q["ret_1m"], "1Y %": q["ret_1y"], "From 52W high %": q["off_52w_high"],
            "Turnover ₹cr": q["turnover_cr"], "Volatility %": q["volatility"],
        })
    return rows


def scan(exchange: str = "NSE", demo: bool = False, broker=None, sectors: list[str] | None = None,
         progress: Callable[[float, str], None] | None = None, workers: int | None = None) -> pd.DataFrame:
    """Analyse every stock in the chosen sectors; one row per (stock, horizon)."""
    codes = [c for s in (sectors or SECTORS) for c in SECTORS[s]]
    if progress:
        progress(0.02, f"Downloading 5-year history for {len(codes)} stocks…")
    hist, index = load_histories(codes, exchange, demo, broker)
    try:
        # "spawn" (the Windows default) is safe inside multi-threaded hosts like Streamlit; fork is not.
        pool = ProcessPoolExecutor(max_workers=workers or min(8, os.cpu_count() or 2),
                                   mp_context=multiprocessing.get_context("spawn"))
        rows = _run(pool, hist, index, exchange, progress)
    except (OSError, NotImplementedError, BrokenProcessPool):
        # Processes sidestep the GIL for the CPU-bound models; threads are the fallback.
        rows = _run(ThreadPoolExecutor(max_workers=4), hist, index, exchange, progress)
    return pd.DataFrame(rows)


@contextmanager
def _neutral_main():
    """Hide the host script while workers spawn.

    Spawned workers re-import ``__main__``; under Streamlit that is app.py, which would
    re-run the whole UI inside every worker. An empty stand-in avoids that.
    """
    real = sys.modules.get("__main__")
    sys.modules["__main__"] = types.ModuleType("__main__")
    try:
        yield
    finally:
        sys.modules["__main__"] = real


def _run(pool, hist: dict, index, exchange: str, progress) -> list[dict]:
    rows: list[dict] = []
    with pool:
        with _neutral_main():  # workers start during the first submits
            futures = {pool.submit(_scan_one, c, df, index, exchange): c for c, df in hist.items()}
        for done, (fut, code) in enumerate(futures.items(), start=1):
            try:
                rows.extend(fut.result())
            except BrokenProcessPool:
                raise
            except Exception:
                pass
            if progress:
                progress(0.05 + 0.95 * done / max(len(futures), 1), f"Analysed {code} ({done}/{len(futures)})")
    return rows


def rank_key(df: pd.DataFrame) -> pd.Series:
    """Composite score first, then reward per unit of risk and probability of a rise."""
    rr = df["Risk:Reward"].clip(0, 4).fillna(0) / 4
    return df["Score"] + 0.10 * rr + 0.10 * (df["P(rise) %"] / 100 - 0.5) * 2


def top_buys(results: pd.DataFrame, horizon: str, n: int = 5, min_turnover_cr: float = 5.0) -> pd.DataFrame:
    """Best buy candidates for a horizon: buy-rated, liquid, positive expected profit."""
    d = results[(results["Horizon"] == horizon) & results["Action"].isin(BUY_ACTIONS)
                & (results["Profit %"] > 0) & (results["Turnover ₹cr"] >= min_turnover_cr)]
    return d.assign(Rank=rank_key(d)).sort_values("Rank", ascending=False).head(n)


def avoid_list(results: pd.DataFrame, horizon: str, n: int = 5) -> pd.DataFrame:
    d = results[(results["Horizon"] == horizon) & results["Action"].isin(("SELL", "STRONG SELL"))]
    return d.sort_values("Score").head(n)


def sector_leaders(results: pd.DataFrame, horizon: str) -> pd.DataFrame:
    """Best-ranked stock in each sector for a horizon, with that sector's average score."""
    d = results[results["Horizon"] == horizon].copy()
    d["Rank"] = rank_key(d)
    best = d.sort_values("Rank", ascending=False).groupby("Sector", sort=False).head(1)
    mood = d.groupby("Sector")["Score"].mean().rename("Sector score")
    best = best.join(mood, on="Sector")
    best["Sector mood"] = np.where(best["Sector score"] > 0.15, "🟢 Bullish",
                                   np.where(best["Sector score"] < -0.15, "🔴 Bearish", "🟡 Neutral"))
    return best.sort_values("Sector score", ascending=False)


def consistent_winners(results: pd.DataFrame, n: int = 10) -> pd.DataFrame:
    """Stocks rated BUY on the most horizons: the 'good on every time frame' list."""
    piv = results.pivot_table(index=["Symbol", "Name", "Sector"], columns="Horizon", values="Score")
    piv = piv[[h for h in HORIZONS if h in piv.columns]]
    buys = (piv > 0.15).sum(axis=1).rename("BUY horizons")
    out = piv.assign(**{"BUY horizons": buys, "Avg score": piv.mean(axis=1)})
    return out.sort_values(["BUY horizons", "Avg score"], ascending=False).head(n).reset_index()
