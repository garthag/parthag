"""Cross-sectional ranking model: which stocks are likely to beat the others over each horizon.

Instead of guessing each stock's absolute direction (close to a coin flip, and dominated by
the overall market), one gradient-boosting model per horizon is trained on *all* stocks
together to predict each stock's return relative to the universe average. Features are used
both raw and as same-day percentile ranks, so market-wide moves cancel out.

Walk-forward backtests on real Nifty 50 data (no look-ahead; see ``walk_forward``) found the
top-5 ranked stocks beat the average stock in both 2018-2023 and 2023-2026; the edge is
clearest for the next week (+0.6 to +0.7% per week in both periods).
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import pandas as pd
from sklearn.ensemble import HistGradientBoostingRegressor

from . import forecast, indicators
from .forecast import BASE_RATE_UP, HORIZONS  # noqa: F401 (re-exported)

EXTRA = ["mom_12_1", "vol_63", "dist_52w_high"]
FEATS = forecast.FEATURES + EXTRA
X_COLS = FEATS + [c + "_r" for c in FEATS]

# Walk-forward results on real Nifty 50 data (``walk_forward``), monthly rebalancing, top 5 of 48 stocks.
# "excess": average return of the top 5 minus the average stock per holding period; "beat": share of
# top-5 picks that beat the average stock. Periods: 2018-10..2023-09 and 2023-10..2026-09.
BACKTEST_EDGE = {
    "Intraday (today)": {"excess": (0.26, 0.21), "beat": (50.3, 47.8), "edge": "small"},
    "Next week": {"excess": (0.70, 0.61), "beat": (58.0, 60.0), "edge": "moderate"},
    "1 month": {"excess": (0.95, 0.13), "beat": (54.3, 48.6), "edge": "small"},
    "6 months": {"excess": (2.66, 3.57), "beat": (50.3, 53.3), "edge": "weak / noisy"},
    "1 year": {"excess": (7.70, 4.30), "beat": (44.7, 51.7), "edge": "weak / noisy"},
}


def stock_features(daily: pd.DataFrame, index: pd.DataFrame | None, with_targets: bool = True) -> pd.DataFrame:
    ind = indicators.add_all(daily)
    f = forecast.make_features(ind, index)
    c = daily["Close"]
    f["mom_12_1"] = np.log(c.shift(21) / c.shift(252))  # classic 12-1 month momentum
    f["vol_63"] = np.log(c).diff().rolling(63).std()
    f["dist_52w_high"] = c / daily["High"].rolling(252).max() - 1
    if with_targets:
        for h in HORIZONS.values():
            f[f"fwd_{h}"] = np.log(c.shift(-h) / c)
    return f


def build_panel(histories: dict[str, pd.DataFrame], index: pd.DataFrame | None) -> pd.DataFrame:
    frames = []
    for code, df in histories.items():
        if len(df) < 300:
            continue
        f = stock_features(df, index)
        f["code"] = code
        frames.append(f)
    panel = pd.concat(frames)
    panel.index.name = "Date"
    panel = panel.reset_index().dropna(subset=["dist_sma200", "mom_12_1"])
    for col in FEATS:  # same-day cross-sectional percentile ranks
        panel[col + "_r"] = panel.groupby("Date")[col].rank(pct=True)
    for h in HORIZONS.values():
        panel[f"xs_{h}"] = panel[f"fwd_{h}"] - panel.groupby("Date")[f"fwd_{h}"].transform("mean")
    return panel


def _model() -> HistGradientBoostingRegressor:
    return HistGradientBoostingRegressor(max_depth=3, learning_rate=0.05, max_iter=150, min_samples_leaf=200,
                                         l2_regularization=1.0, random_state=0)


def _train_rows(panel: pd.DataFrame, h: int, day, years: int = 6) -> pd.DataFrame:
    """Weekly samples whose h-day outcome was already known on ``day`` (no look-ahead)."""
    days = np.sort(panel["Date"].unique())
    pos = np.searchsorted(days, np.datetime64(day))
    cutoff = days[max(0, pos - h - 1)]
    tr = panel[(panel["Date"] <= cutoff) & panel["Date"].dt.dayofweek.eq(2)].dropna(subset=[f"xs_{h}"])
    return tr[tr["Date"] >= cutoff - np.timedelta64(365 * years, "D")]


@dataclass
class Ranking:
    day: pd.Timestamp
    table: pd.DataFrame  # one row per stock: predicted excess return and percentile per horizon


def rank_latest(panel: pd.DataFrame) -> Ranking:
    """Train on everything known today and rank every stock on its latest bar."""
    day = panel["Date"].max()
    latest = panel[panel["Date"] == day].set_index("code")
    out = pd.DataFrame(index=latest.index)
    for label, h in HORIZONS.items():
        tr = _train_rows(panel, h, day)
        if len(tr) < 500:
            out[f"xs {label}"] = np.nan
            continue
        model = _model().fit(tr[X_COLS], tr[f"xs_{h}"])
        out[f"xs {label}"] = model.predict(latest[X_COLS])
        out[f"pct {label}"] = out[f"xs {label}"].rank(pct=True)
    return Ranking(pd.Timestamp(day), out)


def walk_forward(panel: pd.DataFrame, dates: list, top_n: int = 5, progress=None,
                 horizons: list[str] | None = None) -> pd.DataFrame:
    """Re-run ``rank_latest`` as of each past date and record what the top picks really did."""
    days = np.sort(panel["Date"].unique())
    rows = []
    for i, d in enumerate(dates, start=1):
        day = days[days <= np.datetime64(d)][-1]
        test = panel[panel["Date"] == day]
        for label, h in HORIZONS.items():
            if horizons and label not in horizons:
                continue
            t = test.dropna(subset=[f"fwd_{h}"])
            tr = _train_rows(panel, h, day)
            if t.empty or len(tr) < 500:
                continue
            pred = _model().fit(tr[X_COLS], tr[f"xs_{h}"]).predict(t[X_COLS])
            t = t.assign(pred=pred).sort_values("pred", ascending=False)
            ret = 100 * np.expm1(t[f"fwd_{h}"])
            top = t.head(top_n)
            rows.append({"Date": pd.Timestamp(day), "Horizon": label,
                         "Top picks": ", ".join(top["code"]),
                         "Top-N return %": float(100 * np.expm1(top[f"fwd_{h}"]).mean()),
                         "Average stock %": float(ret.mean()),
                         "Top-N rose %": float(100 * (top[f"fwd_{h}"] > 0).mean()),
                         "Top-N beat avg %": float(100 * (top[f"xs_{h}"] > 0).mean())})
        if progress:
            progress(i / len(dates), f"Tested {pd.Timestamp(day).date()} ({i}/{len(dates)})")
    res = pd.DataFrame(rows)
    if not res.empty:
        res["Excess %"] = res["Top-N return %"] - res["Average stock %"]
    return res


def walk_forward_summary(res: pd.DataFrame) -> pd.DataFrame:
    g = res.groupby("Horizon", sort=False)
    out = pd.DataFrame({
        "Test dates": g.size(),
        "Top picks avg return %": g["Top-N return %"].mean(),
        "Average stock %": g["Average stock %"].mean(),
        "Edge per period %": g["Excess %"].mean(),
        "Periods top picks won %": g["Excess %"].apply(lambda s: 100 * (s > 0).mean()),
        "Picks that rose %": g["Top-N rose %"].mean(),
        "Picks that beat avg %": g["Top-N beat avg %"].mean(),
    })
    return out.reindex([h for h in HORIZONS if h in out.index])
