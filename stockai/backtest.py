"""Walk-forward accuracy check: replay the suggestion-box pipeline on past dates.

At each test date the models see only data up to that day (exactly what the app would have
seen), then the calls are compared with what the market actually did afterwards.
"""

from __future__ import annotations

import multiprocessing
import os
from concurrent.futures import ProcessPoolExecutor
from typing import Callable

import numpy as np
import pandas as pd

from . import signals
from .forecast import HORIZONS
from .screener import _neutral_main

LOOKBACK = 1250  # ~5 years, same as the live app


def _replay(code: str, daily: pd.DataFrame, index: pd.DataFrame | None, dates: list) -> list[dict]:
    rows = []
    for d in dates:
        pos = daily.index.searchsorted(d, side="right")  # bars up to and including d
        if pos < 400:
            continue
        past = daily.iloc[max(0, pos - LOOKBACK):pos]
        idx = index.loc[:past.index[-1]] if index is not None else None
        try:
            q = signals.quick_analyze(past, idx)
        except Exception:
            continue
        entry = float(past["Close"].iloc[-1])
        for label, plan in q["plans"].items():
            h = HORIZONS[label]
            future = daily.iloc[pos:pos + h]
            if len(future) < h:
                continue  # outcome not known yet
            exit_px = float(future["Close"].iloc[-1])
            # Did the target or the stop trade first? (same-day touch counts as stop: conservative)
            hit = "none"
            for _, bar in future.iterrows():
                if plan.action in signals.BUY_LIKE and bar["Low"] <= plan.stop_loss:
                    hit = "stop"
                    break
                if plan.action in signals.BUY_LIKE and bar["High"] >= plan.target:
                    hit = "target"
                    break
            fc = q["forecasts"][label]
            rows.append({
                "Code": code, "Date": past.index[-1], "Horizon": label, "Action": plan.action,
                "Score": plan.score, "P(rise)": plan.probability_up, "Confidence": plan.confidence,
                "Entry": entry, "Predicted": fc.predicted_price, "Target": plan.target, "Stop": plan.stop_loss,
                "Exit": exit_px, "Return %": (exit_px / entry - 1) * 100,
                "Pred up": fc.predicted_price > entry, "Actual up": exit_px > entry, "Hit": hit,
                "Above SMA200": bool(entry > past["Close"].iloc[-200:].mean()),
            })
    return rows


def run(histories: dict[str, pd.DataFrame], index: pd.DataFrame | None, dates: list,
        progress: Callable[[float, str], None] | None = None, workers: int | None = None) -> pd.DataFrame:
    rows: list[dict] = []
    pool = ProcessPoolExecutor(max_workers=workers or min(8, os.cpu_count() or 2),
                               mp_context=multiprocessing.get_context("spawn"))
    with pool:
        with _neutral_main():
            futures = {pool.submit(_replay, c, df, index, dates): c for c, df in histories.items()}
        for done, (fut, code) in enumerate(futures.items(), start=1):
            try:
                rows.extend(fut.result())
            except Exception:
                pass
            if progress:
                progress(done / len(futures), f"Replayed {code} ({done}/{len(futures)})")
    return pd.DataFrame(rows)


def summary(res: pd.DataFrame) -> pd.DataFrame:
    """Per-horizon scorecard against simple baselines."""
    out = []
    for label in HORIZONS:
        d = res[res["Horizon"] == label]
        if d.empty:
            continue
        buys = d[d["Action"].isin(signals.BUY_LIKE)]
        strong = d[d["Action"] == "STRONG BUY"]
        sells = d[d["Action"].isin(("SELL", "STRONG SELL"))]
        decided = buys["Hit"].isin(("target", "stop"))
        out.append({
            "Horizon": label, "Predictions": len(d),
            "Direction accuracy %": 100 * (d["Pred up"] == d["Actual up"]).mean(),
            "Baseline: always 'up' %": 100 * d["Actual up"].mean(),
            "BUY calls": len(buys),
            "BUY win rate %": 100 * buys["Actual up"].mean() if len(buys) else np.nan,
            "STRONG BUY win rate %": 100 * strong["Actual up"].mean() if len(strong) else np.nan,
            "Target before stop %": 100 * (buys.loc[decided, "Hit"] == "target").mean() if decided.any() else np.nan,
            "Avg return BUY %": buys["Return %"].mean() if len(buys) else np.nan,
            "Avg return all stocks %": d["Return %"].mean(),
            "SELL calls that fell %": 100 * (~sells["Actual up"]).mean() if len(sells) else np.nan,
        })
    return pd.DataFrame(out).set_index("Horizon")


def calibration(res: pd.DataFrame, horizon: str) -> pd.DataFrame:
    """When the model said 'X% chance of a rise', how often did it actually rise?"""
    d = res[res["Horizon"] == horizon]
    bins = pd.cut(d["P(rise)"], [0, .4, .5, .6, .7, .8, 1.0])
    g = d.groupby(bins, observed=True)["Actual up"]
    return pd.DataFrame({"Predictions": g.size(), "Actually rose %": 100 * g.mean()})


def confidence_tradeoff(res: pd.DataFrame, horizon: str) -> pd.DataFrame:
    """Win rate if you only act on the highest-scoring BUY calls (fewer trades, higher bar)."""
    d = res[(res["Horizon"] == horizon) & res["Action"].isin(signals.BUY_LIKE)]
    rows = []
    for q in (0.0, 0.5, 0.75, 0.9, 0.95):
        cut = d["Score"].quantile(q) if len(d) else np.nan
        sel = d[d["Score"] >= cut]
        if len(sel):
            rows.append({"Only top % of BUYs": f"{100 - int(q * 100)}%", "Trades": len(sel),
                         "Win rate %": 100 * sel["Actual up"].mean(), "Avg return %": sel["Return %"].mean()})
    return pd.DataFrame(rows)
