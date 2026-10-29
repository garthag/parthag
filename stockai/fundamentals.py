"""Fundamental valuation scoring, analyst consensus and shareholder analysis."""

from __future__ import annotations

import numpy as np
import pandas as pd


def _get(info: dict, key: str):
    v = info.get(key)
    return None if v is None or (isinstance(v, float) and np.isnan(v)) else v


def fundamental_score(info: dict) -> dict:
    """Score each metric from -1 (poor) to +1 (strong) and average them."""
    checks = []

    def add(name, value, score, note):
        checks.append({"metric": name, "value": value, "score": score, "comment": note})

    pe = _get(info, "trailingPE")
    if pe is not None:
        s = 1 if 0 < pe < 18 else 0.5 if pe < 28 else -0.3 if pe < 50 else -1
        add("P/E (trailing)", f"{pe:.1f}", s if pe > 0 else -1,
            "Cheap" if s >= 1 else "Reasonable" if s > 0 else "Expensive" if pe > 0 else "Loss making")
    fpe = _get(info, "forwardPE")
    if fpe is not None and pe:
        add("Forward P/E", f"{fpe:.1f}", 0.5 if fpe < pe else -0.3,
            "Earnings expected to grow" if fpe < pe else "Earnings expected to shrink")
    pb = _get(info, "priceToBook")
    if pb is not None:
        add("Price / Book", f"{pb:.2f}", 1 if pb < 2 else 0.3 if pb < 5 else -0.5,
            "Low" if pb < 2 else "Moderate" if pb < 5 else "High")
    roe = _get(info, "returnOnEquity")
    if roe is not None:
        add("Return on equity", f"{roe * 100:.1f}%", 1 if roe > 0.18 else 0.4 if roe > 0.12 else -0.5,
            "Excellent" if roe > 0.18 else "Decent" if roe > 0.12 else "Weak")
    de = _get(info, "debtToEquity")
    if de is not None:
        add("Debt / Equity", f"{de / 100:.2f}", 1 if de < 50 else 0.2 if de < 100 else -0.8,
            "Low debt" if de < 50 else "Manageable" if de < 100 else "High leverage")
    pm = _get(info, "profitMargins")
    if pm is not None:
        add("Net profit margin", f"{pm * 100:.1f}%", 1 if pm > 0.15 else 0.3 if pm > 0.07 else -0.6,
            "High" if pm > 0.15 else "Average" if pm > 0.07 else "Thin")
    rg = _get(info, "revenueGrowth")
    if rg is not None:
        add("Revenue growth (YoY)", f"{rg * 100:.1f}%", 1 if rg > 0.15 else 0.4 if rg > 0.05 else -0.7,
            "Fast" if rg > 0.15 else "Steady" if rg > 0.05 else "Slow / declining")
    eg = _get(info, "earningsGrowth")
    if eg is not None:
        add("Earnings growth (YoY)", f"{eg * 100:.1f}%", 1 if eg > 0.15 else 0.3 if eg > 0 else -0.8,
            "Strong" if eg > 0.15 else "Positive" if eg > 0 else "Falling")
    dy = _get(info, "dividendYield")
    if dy is not None:
        dy = dy / 100 if dy > 1 else dy  # yfinance sometimes reports percent
        add("Dividend yield", f"{dy * 100:.2f}%", 0.6 if dy > 0.02 else 0.1, "Good payout" if dy > 0.02 else "Low payout")
    score = float(np.mean([c["score"] for c in checks])) if checks else 0.0
    return {"score": score, "checks": pd.DataFrame(checks),
            "label": "Strong" if score > 0.4 else "Average" if score > -0.1 else "Weak"}


def analyst_view(info: dict, price: float) -> dict:
    target = _get(info, "targetMeanPrice")
    rec = (_get(info, "recommendationKey") or "none").replace("_", " ")
    rec_score = {"strong buy": 1, "buy": 0.6, "hold": 0, "underperform": -0.6, "sell": -1, "strong sell": -1}.get(rec, 0)
    upside = (target / price - 1) * 100 if target and price else None
    score = rec_score * 0.5 + (np.clip(upside / 20, -1, 1) * 0.5 if upside is not None else 0)
    return {"recommendation": rec.title(), "target_mean": target, "target_high": _get(info, "targetHighPrice"),
            "target_low": _get(info, "targetLowPrice"), "upside_pct": upside,
            "analysts": _get(info, "numberOfAnalystOpinions"), "score": float(score)}


def ownership(info: dict, holders: dict) -> dict:
    """Who owns / is buying the stock: promoters, institutions, funds."""
    insiders = _get(info, "heldPercentInsiders")
    inst = _get(info, "heldPercentInstitutions")
    out = {
        "promoters_insiders_pct": insiders * 100 if insiders is not None else None,
        "institutions_pct": inst * 100 if inst is not None else None,
    }
    if insiders is not None and inst is not None:
        out["public_retail_pct"] = max(0.0, 100 - (insiders + inst) * 100)
    frames = []
    for key, label in (("institutional", "Institution"), ("mutualfund", "Mutual fund")):
        df = holders.get(key)
        if isinstance(df, pd.DataFrame) and not df.empty:
            d = df.copy()
            d["Type"] = label
            frames.append(d)
    top = pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()
    out["top_holders"] = top
    score = 0.0
    if not top.empty and "pctChange" in top:
        chg = top["pctChange"].dropna()
        if len(chg):
            buyers = int((chg > 0).sum())
            sellers = int((chg < 0).sum())
            out["institutions_adding"] = buyers
            out["institutions_reducing"] = sellers
            score = (buyers - sellers) / max(buyers + sellers, 1)
    if insiders is not None:
        score += 0.3 if insiders > 0.5 else 0.1 if insiders > 0.3 else -0.1
    out["score"] = float(np.clip(score, -1, 1))
    for key in ("insider_transactions", "insider_purchases", "upgrades_downgrades", "recommendations"):
        if key in holders:
            out[key] = holders[key]
    return out
