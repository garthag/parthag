"""Price forecasting: machine-learning ensemble, Monte Carlo simulation and trend models.

Every horizon is forecast by several independent methods and then blended.
Each ML model is scored with walk-forward (time-series) cross validation so
the app can show how reliable the predictions have been historically.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
import pandas as pd
from sklearn.ensemble import ExtraTreesRegressor, HistGradientBoostingRegressor, RandomForestRegressor
from sklearn.linear_model import Ridge
from sklearn.model_selection import TimeSeriesSplit
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

HORIZONS = {"Intraday (today)": 1, "Next week": 5, "1 month": 21, "6 months": 126, "1 year": 252}

FEATURES = [
    "ret1", "ret5", "ret10", "ret21", "ret63", "dist_sma20", "dist_sma50", "dist_sma200",
    "RSI", "macd_n", "BB_pctB", "STOCH_K", "ADX", "di_diff", "CMF", "MFI", "ATR_pct",
    "HV20", "vol_ratio_log", "ST_dir", "hl_range", "gap", "dow", "mkt_ret5", "rel_strength21",
]


def make_features(ind: pd.DataFrame, index_daily: pd.DataFrame | None = None) -> pd.DataFrame:
    c = ind["Close"]
    f = pd.DataFrame(index=ind.index)
    for n in (1, 5, 10, 21, 63):
        f[f"ret{n}"] = np.log(c / c.shift(n))
    for n in (20, 50, 200):
        f[f"dist_sma{n}"] = c / ind[f"SMA{n}"] - 1
    f["RSI"] = ind["RSI"]
    f["macd_n"] = ind["MACD_hist"] / c
    f["BB_pctB"] = ind["BB_pctB"]
    f["STOCH_K"] = ind["STOCH_K"]
    f["ADX"] = ind["ADX"]
    f["di_diff"] = ind["+DI"] - ind["-DI"]
    f["CMF"] = ind["CMF"]
    f["MFI"] = ind["MFI"]
    f["ATR_pct"] = ind["ATR_pct"]
    f["HV20"] = ind["HV20"]
    f["vol_ratio_log"] = np.log(ind["VOL_ratio"].clip(lower=1e-3))
    f["ST_dir"] = ind["ST_dir"]
    f["hl_range"] = (ind["High"] - ind["Low"]) / c
    f["gap"] = ind["Open"] / c.shift() - 1
    f["dow"] = ind.index.dayofweek
    if index_daily is not None and not index_daily.empty:
        mc = index_daily["Close"].reindex(ind.index).ffill()
        f["mkt_ret5"] = np.log(mc / mc.shift(5))
        f["rel_strength21"] = f["ret21"] - np.log(mc / mc.shift(21))
    else:
        f["mkt_ret5"] = 0.0
        f["rel_strength21"] = 0.0
    return f.replace([np.inf, -np.inf], np.nan)


def _models(fast: bool = False) -> dict:
    if fast:  # used by the multi-stock scanner
        return {
            "Ridge regression": make_pipeline(StandardScaler(), Ridge(alpha=10.0)),
            "Extra trees": ExtraTreesRegressor(n_estimators=40, max_depth=5, min_samples_leaf=30,
                                               n_jobs=1, random_state=42),
        }
    return {
        "Ridge regression": make_pipeline(StandardScaler(), Ridge(alpha=10.0)),
        "Random forest": RandomForestRegressor(n_estimators=150, max_depth=6, min_samples_leaf=20,
                                               n_jobs=-1, random_state=42),
        "Gradient boosting": HistGradientBoostingRegressor(max_depth=3, learning_rate=0.05, max_iter=200,
                                                           min_samples_leaf=30, random_state=42),
    }


@dataclass
class MLResult:
    horizon: int
    predictions: dict            # model -> predicted log return
    scores: dict                 # model -> {"dir_acc", "mae"}
    ensemble_return: float       # predicted simple return
    ensemble_dir_acc: float
    feature_importance: pd.Series = field(default_factory=pd.Series)


def _usable_splits(n: int, horizon: int, wanted: int) -> int:
    """Largest walk-forward split count whose first training fold still has >= 50 rows."""
    for k in range(wanted, 1, -1):
        if n - k * (n // (k + 1)) - horizon >= 50:
            return k
    return 0


def ml_forecast(features: pd.DataFrame, close: pd.Series, horizon: int, n_splits: int = 4,
                fast: bool = False) -> MLResult:
    """Walk-forward-validated ML ensemble; returns an empty result when history is too short."""
    target = np.log(close.shift(-horizon) / close)
    data = features.join(target.rename("y"))
    train = data.dropna()
    latest = features.iloc[[-1]].fillna(features.median())
    X, y = train[FEATURES], train["y"]
    preds, scores = {}, {}
    n_splits = _usable_splits(len(train), horizon, 3 if fast else n_splits)
    if len(train) < 150 or n_splits == 0:
        return MLResult(horizon, {}, {}, 0.0, 0.5)
    tscv = TimeSeriesSplit(n_splits=n_splits, gap=horizon)
    importance = None
    for name, model in _models(fast).items():
        dir_hits, errs = [], []
        for tr, te in tscv.split(X):
            model.fit(X.iloc[tr], y.iloc[tr])
            p = model.predict(X.iloc[te])
            dir_hits.append(np.mean(np.sign(p) == np.sign(y.iloc[te])))
            errs.append(np.mean(np.abs(p - y.iloc[te])))
        model.fit(X, y)
        preds[name] = float(model.predict(latest[FEATURES])[0])
        scores[name] = {"dir_acc": float(np.mean(dir_hits)), "mae": float(np.mean(errs))}
        if name == "Random forest":
            importance = pd.Series(model.feature_importances_, index=FEATURES).sort_values(ascending=False)
    # Weight models by how much better than a coin flip they were.
    w = {k: max(v["dir_acc"] - 0.45, 0.01) for k, v in scores.items()}
    tot = sum(w.values())
    ens = sum(preds[k] * w[k] / tot for k in preds)
    ens_acc = sum(scores[k]["dir_acc"] * w[k] / tot for k in scores)
    return MLResult(horizon, preds, scores, float(np.expm1(ens)), float(ens_acc), importance)


def monte_carlo(close: pd.Series, horizon: int, n_paths: int = 5000, seed: int = 0) -> dict:
    """Blend of GBM (EWMA volatility) and historical bootstrap simulations."""
    rng = np.random.default_rng(seed)
    r = np.log(close / close.shift()).dropna().iloc[-756:]
    sigma = float(np.sqrt((r ** 2).ewm(alpha=0.06).mean().iloc[-1]))
    # Shrink the stock's own past drift halfway toward a long-run equity return (~12%/yr),
    # so one hot (or awful) stretch is not projected forward at full strength.
    mu = 0.5 * float(r.mean()) + 0.5 * np.log(1.12) / 252
    gbm = rng.normal(mu - 0.5 * sigma ** 2, sigma, (n_paths // 2, horizon)).sum(axis=1)
    boot = rng.choice(r.to_numpy() - r.mean() + mu, (n_paths - n_paths // 2, horizon)).sum(axis=1)
    total = np.r_[gbm, boot]
    last = float(close.iloc[-1])
    prices = last * np.exp(total)
    pct = {p: float(np.percentile(prices, p)) for p in (5, 10, 25, 50, 60, 75, 90, 95)}
    # Fan chart: percentiles along the path for the GBM leg.
    cum = np.cumsum(rng.normal(mu - 0.5 * sigma ** 2, sigma, (2000, horizon)), axis=1)
    fan = pd.DataFrame({p: last * np.exp(np.percentile(cum, p, axis=0)) for p in (5, 25, 50, 75, 95)})
    return {
        "expected": float(prices.mean()),
        "median": pct[50],
        "percentiles": pct,
        "prob_up": float((prices > last).mean()),
        "prob_up_5pct": float((prices > last * 1.05).mean()),
        "prob_down_5pct": float((prices < last * 0.95).mean()),
        "daily_vol": sigma,
        "fan": fan,
    }


def trend_projection(close: pd.Series, horizon: int, lookback: int = 252) -> dict:
    """Log-linear regression trend plus Holt's double exponential smoothing."""
    y = np.log(close.iloc[-lookback:].to_numpy())
    x = np.arange(len(y))
    slope, intercept = np.polyfit(x, y, 1)
    resid = y - (slope * x + intercept)
    r2 = 1 - resid.var() / y.var() if y.var() > 0 else 0.0
    # Trends fade: damp the extrapolated slope so a hot year is not simply repeated.
    damped = horizon / (1 + horizon / 126)
    lin = float(np.exp(y[-1] + slope * damped))  # trend slope anchored at today's price
    # Holt's linear method on log prices.
    alpha, beta = 0.3, 0.05
    level, trend = y[0], y[1] - y[0]
    for v in y[1:]:
        prev = level
        level = alpha * v + (1 - alpha) * (level + trend)
        trend = beta * (level - prev) + (1 - beta) * trend
    holt = float(np.exp(level + trend * damped))
    return {"linear": lin, "holt": holt, "r2": float(r2), "annual_trend_pct": float(np.expm1(slope * 252) * 100)}


def intraday_outlook(daily_ind: pd.DataFrame, intraday: pd.DataFrame) -> dict:
    """Expected day range and current intraday bias."""
    last_close = float(daily_ind["Close"].iloc[-1])
    a = float(daily_ind["ATR"].iloc[-1])
    out = {"expected_low": last_close - 0.6 * a, "expected_high": last_close + 0.6 * a, "atr": a}
    if intraday is None or intraday.empty:
        out["bias"] = "No intraday data"
        return out
    today = intraday[intraday.index.date == intraday.index[-1].date()]
    if today.empty:
        return out
    day_open = float(today["Open"].iloc[0])
    cur = float(today["Close"].iloc[-1])
    hi, lo = float(today["High"].max()), float(today["Low"].min())
    used = hi - lo
    remaining = max(a - used, 0)
    tp = (today["High"] + today["Low"] + today["Close"]) / 3
    vw = float((tp * today["Volume"]).sum() / max(today["Volume"].sum(), 1))
    ema_fast = today["Close"].ewm(span=9).mean().iloc[-1]
    ema_slow = today["Close"].ewm(span=21).mean().iloc[-1]
    score = 0
    score += 1 if cur > vw else -1
    score += 1 if ema_fast > ema_slow else -1
    score += 1 if cur > day_open else -1
    out.update({
        "day_open": day_open, "current": cur, "day_high": hi, "day_low": lo, "vwap": vw,
        "expected_high": max(hi, cur + remaining * 0.6),
        "expected_low": min(lo, cur - remaining * 0.6),
        "range_used_pct": 100 * used / a if a else np.nan,
        "bias_score": score / 3,
        "bias": "Bullish" if score >= 2 else "Bearish" if score <= -2 else "Sideways",
        "session_date": today.index[-1].date(),
    })
    return out


@dataclass
class HorizonForecast:
    label: str
    horizon: int
    last_price: float
    ml: MLResult
    mc: dict
    trend: dict
    predicted_price: float
    low: float
    high: float
    expected_return_pct: float
    prob_up: float
    confidence: float


def forecast_all(ind: pd.DataFrame, index_daily: pd.DataFrame | None = None, fast: bool = False,
                 horizons: dict | None = None) -> dict[str, HorizonForecast]:
    feats = make_features(ind, index_daily)
    close = ind["Close"]
    last = float(close.iloc[-1])
    out = {}
    for label, h in (horizons or HORIZONS).items():
        ml = ml_forecast(feats, close, h, fast=fast)
        mc = monte_carlo(close, h, n_paths=2000 if fast else 5000)
        tr = trend_projection(close, h)
        ml_price = last * (1 + ml.ensemble_return)
        has_ml = bool(ml.predictions)
        # ML weight grows with its out-of-sample directional skill (zero when it could not be trained).
        skill = float(np.clip((ml.ensemble_dir_acc - 0.5) * 5, 0, 1)) if has_ml else 0.0
        w_ml, w_mc, w_tr = (0.35 + 0.25 * skill if has_ml else 0.0), 0.35, 0.30 - 0.10 * skill
        trend_price = 0.5 * tr["linear"] + 0.5 * tr["holt"]
        tot = w_ml + w_mc + w_tr
        pred = (w_ml * ml_price + w_mc * mc["median"] + w_tr * trend_price) / tot
        # Probability of a rise: MC baseline nudged by ML signal strength.
        ml_tilt = np.tanh(ml.ensemble_return / max(mc["daily_vol"] * np.sqrt(h), 1e-6))
        prob_up = float(np.clip(mc["prob_up"] + 0.2 * skill * ml_tilt + 0.05 * np.sign(trend_price - last), 0.10, 0.90))  # never claim near-certainty
        views = [mc["median"], trend_price] + ([ml_price] if has_ml else [])
        agree = np.mean([np.sign(v - last) for v in views])
        confidence = float(np.clip(0.35 + 0.25 * abs(agree) + 0.4 * skill, 0, 1))
        out[label] = HorizonForecast(
            label, h, last, ml, mc, tr, float(pred),
            mc["percentiles"][10], mc["percentiles"][90],
            100 * (pred / last - 1), prob_up, confidence,
        )
    return out
