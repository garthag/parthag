"""Combine every analysis into buy / hold / sell calls with targets and stop-losses."""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
import pandas as pd

from . import fundamentals, forecast, indicators, sentiment

# How much each pillar matters for each horizon (short term = technicals/flow,
# long term = fundamentals/trend).
WEIGHTS = {
    "Intraday (today)": {"technical": 0.30, "ml": 0.25, "flow": 0.25, "sentiment": 0.15, "fundamental": 0.0, "analyst": 0.05},
    "Next week": {"technical": 0.30, "ml": 0.30, "flow": 0.15, "sentiment": 0.15, "fundamental": 0.05, "analyst": 0.05},
    "1 month": {"technical": 0.25, "ml": 0.30, "flow": 0.10, "sentiment": 0.10, "fundamental": 0.15, "analyst": 0.10},
    "6 months": {"technical": 0.10, "ml": 0.25, "flow": 0.05, "sentiment": 0.05, "fundamental": 0.35, "analyst": 0.20},
    "1 year": {"technical": 0.05, "ml": 0.15, "flow": 0.05, "sentiment": 0.0, "fundamental": 0.45, "analyst": 0.30},
}

STOP_ATR = {"Intraday (today)": 0.75, "Next week": 1.5, "1 month": 2.5, "6 months": 4.0, "1 year": 6.0}


def technical_score(ind: pd.DataFrame) -> dict:
    """Each indicator votes -1..+1; returns votes and their mean."""
    last = ind.iloc[-1]
    c = last["Close"]
    votes = {}
    votes["Price vs 20 SMA"] = 1 if c > last["SMA20"] else -1
    votes["Price vs 50 SMA"] = 1 if c > last["SMA50"] else -1
    if not np.isnan(last["SMA200"]):
        votes["Price vs 200 SMA"] = 1 if c > last["SMA200"] else -1
        votes["50/200 SMA trend"] = 1 if last["SMA50"] > last["SMA200"] else -1
    votes["EMA 9/21"] = 1 if last["EMA9"] > last["EMA21"] else -1
    r = last["RSI"]
    votes["RSI"] = -0.5 if r > 75 else 0.5 if r < 30 else (r - 50) / 50
    votes["MACD"] = 1 if last["MACD"] > last["MACD_signal"] else -1
    votes["MACD histogram"] = float(np.sign(last["MACD_hist"] - ind["MACD_hist"].iloc[-2]))
    votes["Supertrend"] = float(last["ST_dir"])
    votes["ADX / DI"] = float(np.sign(last["+DI"] - last["-DI"]) * min(last["ADX"] / 25, 1))
    k = last["STOCH_K"]
    votes["Stochastic"] = 0.5 if k < 20 else -0.5 if k > 80 else (1 if k > last["STOCH_D"] else -1) * 0.5
    votes["Bollinger %B"] = float(np.clip(last["BB_pctB"] - 0.5, -0.5, 0.5))
    votes["MFI"] = -0.5 if last["MFI"] > 80 else 0.5 if last["MFI"] < 20 else (last["MFI"] - 50) / 60
    votes["CCI"] = float(np.clip(last["CCI"] / 200, -1, 1))
    votes["Williams %R"] = 0.5 if last["WILLR"] < -80 else -0.5 if last["WILLR"] > -20 else 0.0
    votes["Ichimoku cloud"] = 1 if c > max(last["ICH_spanA"], last["ICH_spanB"]) else -1 if c < min(last["ICH_spanA"], last["ICH_spanB"]) else 0
    votes["Momentum (ROC10)"] = float(np.clip(last["ROC10"] / 10, -1, 1))
    votes = {k: float(v) for k, v in votes.items() if not np.isnan(v)}
    score = float(np.mean(list(votes.values())))
    return {"score": score, "votes": votes,
            "label": "Bullish" if score > 0.2 else "Bearish" if score < -0.2 else "Neutral"}


def label_for(score: float) -> str:
    if score > 0.45:
        return "STRONG BUY"
    if score > 0.15:
        return "BUY"
    if score < -0.45:
        return "STRONG SELL"
    if score < -0.15:
        return "SELL"
    return "HOLD"


@dataclass
class TradePlan:
    horizon: str
    action: str
    score: float
    entry: float
    target: float
    stop_loss: float
    expected_profit_pct: float
    potential_loss_pct: float
    risk_reward: float
    probability_up: float
    confidence: float
    breakdown: dict = field(default_factory=dict)


def trade_plan(label: str, fc: forecast.HorizonForecast, pillars: dict, atr_value: float,
               entry: float | None = None, weights: dict | None = None) -> TradePlan:
    w = weights or WEIGHTS[label]
    ml_score = float(np.tanh(fc.expected_return_pct / (100 * fc.mc["daily_vol"] * np.sqrt(fc.horizon) + 1e-9)))
    ml_score = 0.6 * ml_score + 0.4 * (fc.prob_up - 0.5) * 2
    parts = {**pillars, "ml": ml_score}
    score = float(sum(w[k] * parts[k] for k in w))
    action = label_for(score)
    # Never recommend buying into a forecast decline (or selling into a rise).
    if score > 0.15 and fc.expected_return_pct < 0 and fc.prob_up < 0.5:
        action, score = "HOLD", min(score, 0.15)
    elif score < -0.15 and fc.expected_return_pct > 0 and fc.prob_up > 0.5:
        action, score = "HOLD", max(score, -0.15)
    entry = entry or fc.last_price
    stop_dist = STOP_ATR[label] * atr_value
    if score >= -0.15:  # long side
        target = (max(fc.predicted_price, fc.mc["percentiles"][60]) if score > 0.15
                  else max(fc.predicted_price, entry + 0.5 * stop_dist))
        stop = entry - stop_dist
        profit = (target / entry - 1) * 100
        loss = (stop / entry - 1) * 100
    else:  # short side / exit
        target = min(fc.predicted_price, fc.mc["percentiles"][25])
        stop = entry + stop_dist
        profit = (1 - target / entry) * 100
        loss = (1 - stop / entry) * 100
    rr = profit / abs(loss) if loss else np.nan
    return TradePlan(label, action, score, entry, float(target), float(stop), float(profit), float(loss),
                     float(rr), fc.prob_up, fc.confidence, parts)


@dataclass
class Analysis:
    bundle: object
    ind: pd.DataFrame
    technical: dict
    forecasts: dict
    news: pd.DataFrame
    sentiment: dict
    interest: dict
    pressure: dict
    fundamental: dict
    analyst: dict
    owners: dict
    plans: dict
    levels: dict
    events: list
    patterns: list
    intraday: dict
    beta: float | None


def _beta(daily: pd.DataFrame, index_daily: pd.DataFrame | None) -> float | None:
    if index_daily is None or index_daily.empty:
        return None
    r = daily["Close"].pct_change()
    m = index_daily["Close"].reindex(daily.index).ffill().pct_change()
    j = pd.concat([r, m], axis=1).dropna().iloc[-252:]
    if len(j) < 60 or j.iloc[:, 1].var() == 0:
        return None
    return float(j.cov().iloc[0, 1] / j.iloc[:, 1].var())


def analyze(bundle) -> Analysis:
    ind = indicators.add_all(bundle.daily)
    tech = technical_score(ind)
    fcs = forecast.forecast_all(ind, bundle.index_daily)
    scored = sentiment.score_news(bundle.news)
    sent = sentiment.aggregate_sentiment(scored)
    interest = sentiment.public_interest(bundle.daily, scored)
    pressure = indicators.buyer_seller_pressure(bundle.daily, bundle.intraday)
    fund = fundamentals.fundamental_score(bundle.info)
    price = float(ind["Close"].iloc[-1])
    analyst = fundamentals.analyst_view(bundle.info, price)
    owners = fundamentals.ownership(bundle.info, bundle.holders)
    intraday = forecast.intraday_outlook(ind, bundle.intraday)
    flow = 0.7 * pressure["score"] + 0.3 * owners["score"]
    pillars = {"technical": tech["score"], "flow": flow, "sentiment": float(np.clip(sent["score"] * 2, -1, 1)),
               "fundamental": fund["score"], "analyst": analyst["score"]}
    atr_value = float(ind["ATR"].iloc[-1])
    plans = {}
    for label, fc in fcs.items():
        p, entry = dict(pillars), None
        if label == "Intraday (today)" and "bias_score" in intraday:
            p["technical"] = 0.5 * tech["score"] + 0.5 * intraday["bias_score"]
            entry = intraday["current"]
        plans[label] = trade_plan(label, fc, p, atr_value, entry)
    levels = {
        "pivots": indicators.pivot_points(bundle.daily),
        "fibonacci": indicators.fibonacci_levels(bundle.daily),
        "sr": indicators.support_resistance(bundle.daily),
    }
    return Analysis(bundle, ind, tech, fcs, scored, sent, interest, pressure, fund, analyst, owners, plans,
                    levels, indicators.chart_events(ind), indicators.candlestick_patterns(bundle.daily),
                    intraday, _beta(bundle.daily, bundle.index_daily))


def quick_analyze(daily: pd.DataFrame, index_daily: pd.DataFrame | None = None) -> dict:
    """Price-only analysis for scanning many stocks: technicals, order flow, fast ML and Monte Carlo.

    News, fundamentals and analyst pillars are skipped (they need slow per-stock lookups),
    so each horizon's weights are re-normalised over the pillars that are available.
    """
    ind = indicators.add_all(daily)
    tech = technical_score(ind)
    pressure = indicators.buyer_seller_pressure(daily)
    fcs = forecast.forecast_all(ind, index_daily, fast=True)
    pillars = {"technical": tech["score"], "flow": pressure["score"]}
    atr_value = float(ind["ATR"].iloc[-1])
    plans = {}
    for label, fc in fcs.items():
        w = {k: v for k, v in WEIGHTS[label].items() if k in ("technical", "flow", "ml")}
        tot = sum(w.values())
        plans[label] = trade_plan(label, fc, pillars, atr_value, weights={k: v / tot for k, v in w.items()})
    last = ind.iloc[-1]
    close = ind["Close"]
    return {
        "plans": plans, "forecasts": fcs, "technical": tech, "pressure": pressure,
        "price": float(last["Close"]), "rsi": float(last["RSI"]),
        "ret_1m": float(close.iloc[-1] / close.iloc[-22] - 1) * 100 if len(close) > 22 else np.nan,
        "ret_1y": float(close.iloc[-1] / close.iloc[-253] - 1) * 100 if len(close) > 253 else np.nan,
        "off_52w_high": float(close.iloc[-1] / ind["High"].iloc[-252:].max() - 1) * 100,
        "turnover_cr": float((ind["Close"] * ind["Volume"]).iloc[-20:].mean() / 1e7),
        "volatility": float(last["HV20"]) * 100,
    }
