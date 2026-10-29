"""Technical indicators, chart patterns and buyer/seller pressure, in pure pandas."""

from __future__ import annotations

import numpy as np
import pandas as pd


def sma(s: pd.Series, n: int) -> pd.Series:
    return s.rolling(n, min_periods=n).mean()


def ema(s: pd.Series, n: int) -> pd.Series:
    return s.ewm(span=n, adjust=False, min_periods=n).mean()


def rsi(close: pd.Series, n: int = 14) -> pd.Series:
    delta = close.diff()
    gain = delta.clip(lower=0).ewm(alpha=1 / n, adjust=False, min_periods=n).mean()
    loss = (-delta.clip(upper=0)).ewm(alpha=1 / n, adjust=False, min_periods=n).mean()
    rs = gain / loss.replace(0, np.nan)
    return (100 - 100 / (1 + rs)).fillna(100.0).where(gain.notna())


def macd(close: pd.Series, fast: int = 12, slow: int = 26, signal: int = 9) -> pd.DataFrame:
    line = ema(close, fast) - ema(close, slow)
    sig = line.ewm(span=signal, adjust=False).mean()
    return pd.DataFrame({"MACD": line, "MACD_signal": sig, "MACD_hist": line - sig})


def bollinger(close: pd.Series, n: int = 20, k: float = 2.0) -> pd.DataFrame:
    mid = sma(close, n)
    sd = close.rolling(n).std()
    return pd.DataFrame({"BB_mid": mid, "BB_up": mid + k * sd, "BB_low": mid - k * sd,
                         "BB_pctB": (close - (mid - k * sd)) / (2 * k * sd)})


def true_range(df: pd.DataFrame) -> pd.Series:
    prev = df["Close"].shift()
    return pd.concat([df["High"] - df["Low"], (df["High"] - prev).abs(), (df["Low"] - prev).abs()], axis=1).max(axis=1)


def atr(df: pd.DataFrame, n: int = 14) -> pd.Series:
    return true_range(df).ewm(alpha=1 / n, adjust=False, min_periods=n).mean()


def stochastic(df: pd.DataFrame, n: int = 14, d: int = 3) -> pd.DataFrame:
    lo, hi = df["Low"].rolling(n).min(), df["High"].rolling(n).max()
    k = 100 * (df["Close"] - lo) / (hi - lo)
    return pd.DataFrame({"STOCH_K": k, "STOCH_D": k.rolling(d).mean()})


def adx(df: pd.DataFrame, n: int = 14) -> pd.DataFrame:
    up = df["High"].diff()
    down = -df["Low"].diff()
    plus_dm = np.where((up > down) & (up > 0), up, 0.0)
    minus_dm = np.where((down > up) & (down > 0), down, 0.0)
    tr = atr(df, n)
    plus_di = 100 * pd.Series(plus_dm, df.index).ewm(alpha=1 / n, adjust=False).mean() / tr
    minus_di = 100 * pd.Series(minus_dm, df.index).ewm(alpha=1 / n, adjust=False).mean() / tr
    dx = 100 * (plus_di - minus_di).abs() / (plus_di + minus_di)
    return pd.DataFrame({"ADX": dx.ewm(alpha=1 / n, adjust=False).mean(), "+DI": plus_di, "-DI": minus_di})


def obv(df: pd.DataFrame) -> pd.Series:
    return (np.sign(df["Close"].diff()).fillna(0) * df["Volume"]).cumsum()


def mfi(df: pd.DataFrame, n: int = 14) -> pd.Series:
    tp = (df["High"] + df["Low"] + df["Close"]) / 3
    flow = tp * df["Volume"]
    pos = flow.where(tp > tp.shift(), 0).rolling(n).sum()
    neg = flow.where(tp < tp.shift(), 0).rolling(n).sum()
    return 100 - 100 / (1 + pos / neg.replace(0, np.nan))


def cci(df: pd.DataFrame, n: int = 20) -> pd.Series:
    tp = (df["High"] + df["Low"] + df["Close"]) / 3
    md = tp.rolling(n).apply(lambda x: np.abs(x - x.mean()).mean(), raw=True)
    return (tp - tp.rolling(n).mean()) / (0.015 * md)


def williams_r(df: pd.DataFrame, n: int = 14) -> pd.Series:
    hi, lo = df["High"].rolling(n).max(), df["Low"].rolling(n).min()
    return -100 * (hi - df["Close"]) / (hi - lo)


def chaikin_money_flow(df: pd.DataFrame, n: int = 20) -> pd.Series:
    rng = (df["High"] - df["Low"]).replace(0, np.nan)
    mfm = ((df["Close"] - df["Low"]) - (df["High"] - df["Close"])) / rng
    return (mfm * df["Volume"]).rolling(n).sum() / df["Volume"].rolling(n).sum()


def vwap(df: pd.DataFrame) -> pd.Series:
    """Session VWAP for intraday data (resets each day)."""
    tp = (df["High"] + df["Low"] + df["Close"]) / 3
    day = df.index.date
    pv = (tp * df["Volume"]).groupby(day).cumsum()
    vol = df["Volume"].groupby(day).cumsum().replace(0, np.nan)
    return pv / vol


def supertrend(df: pd.DataFrame, n: int = 10, mult: float = 3.0) -> pd.DataFrame:
    a = atr(df, n)
    hl2 = (df["High"] + df["Low"]) / 2
    upper, lower = (hl2 + mult * a).to_numpy(copy=True), (hl2 - mult * a).to_numpy(copy=True)
    close = df["Close"].to_numpy()
    st = np.full(len(df), np.nan)
    direction = np.ones(len(df))
    for i in range(1, len(df)):
        if np.isnan(upper[i - 1]):
            continue
        if close[i - 1] <= upper[i - 1]:
            upper[i] = min(upper[i], upper[i - 1])
        if close[i - 1] >= lower[i - 1]:
            lower[i] = max(lower[i], lower[i - 1])
        if close[i] > upper[i - 1]:
            direction[i] = 1
        elif close[i] < lower[i - 1]:
            direction[i] = -1
        else:
            direction[i] = direction[i - 1]
        st[i] = lower[i] if direction[i] == 1 else upper[i]
    return pd.DataFrame({"SUPERTREND": st, "ST_dir": direction}, index=df.index)


def ichimoku(df: pd.DataFrame) -> pd.DataFrame:
    conv = (df["High"].rolling(9).max() + df["Low"].rolling(9).min()) / 2
    base = (df["High"].rolling(26).max() + df["Low"].rolling(26).min()) / 2
    span_a = ((conv + base) / 2)
    span_b = (df["High"].rolling(52).max() + df["Low"].rolling(52).min()) / 2
    return pd.DataFrame({"ICH_conv": conv, "ICH_base": base, "ICH_spanA": span_a, "ICH_spanB": span_b})


def add_all(df: pd.DataFrame) -> pd.DataFrame:
    """Return a copy of ``df`` with every indicator column appended."""
    out = df.copy()
    c = out["Close"]
    for n in (5, 10, 20, 50, 100, 200):
        out[f"SMA{n}"] = sma(c, n)
    for n in (9, 21, 50):
        out[f"EMA{n}"] = ema(c, n)
    out["RSI"] = rsi(c)
    out = out.join(macd(c)).join(bollinger(c)).join(stochastic(out)).join(adx(out))
    out = out.join(supertrend(out)).join(ichimoku(out))
    out["ATR"] = atr(out)
    out["ATR_pct"] = out["ATR"] / c
    out["OBV"] = obv(out)
    out["MFI"] = mfi(out)
    out["CCI"] = cci(out)
    out["WILLR"] = williams_r(out)
    out["CMF"] = chaikin_money_flow(out)
    out["ROC10"] = c.pct_change(10) * 100
    out["VOL_SMA20"] = sma(out["Volume"], 20)
    out["VOL_ratio"] = out["Volume"] / out["VOL_SMA20"]
    out["HV20"] = np.log(c).diff().rolling(20).std() * np.sqrt(252)
    return out


# --------------------------------------------------------------------------
# Levels and patterns
# --------------------------------------------------------------------------

def pivot_points(df: pd.DataFrame) -> dict:
    """Classic floor pivots from the last completed session."""
    last = df.iloc[-1]
    p = (last["High"] + last["Low"] + last["Close"]) / 3
    r = last["High"] - last["Low"]
    return {"S3": p - 2 * r, "S2": p - r, "S1": 2 * p - last["High"], "Pivot": p,
            "R1": 2 * p - last["Low"], "R2": p + r, "R3": p + 2 * r}


def fibonacci_levels(df: pd.DataFrame, lookback: int = 126) -> dict:
    window = df.iloc[-lookback:]
    hi, lo = window["High"].max(), window["Low"].min()
    diff = hi - lo
    return {f"{int(r * 1000) / 10}%": hi - r * diff for r in (0, 0.236, 0.382, 0.5, 0.618, 0.786, 1)}


def support_resistance(df: pd.DataFrame, lookback: int = 250, window: int = 5, max_levels: int = 4) -> dict:
    """Cluster swing highs / lows into the nearest support and resistance zones."""
    d = df.iloc[-lookback:]
    highs = d["High"][(d["High"] == d["High"].rolling(2 * window + 1, center=True).max())]
    lows = d["Low"][(d["Low"] == d["Low"].rolling(2 * window + 1, center=True).min())]
    price = float(d["Close"].iloc[-1])
    tol = 0.5 * float(atr(d).iloc[-1]) if len(d) > 20 else price * 0.01

    def cluster(levels):
        levels = sorted(levels)
        groups: list[list[float]] = []
        for lv in levels:
            if groups and lv - groups[-1][0] <= tol:
                groups[-1].append(lv)
            else:
                groups.append([lv])
        return [(float(np.mean(g)), len(g)) for g in groups]

    allv = cluster(list(highs) + list(lows))
    sup = sorted([lv for lv in allv if lv[0] < price], key=lambda x: -x[0])[:max_levels]
    res = sorted([lv for lv in allv if lv[0] > price], key=lambda x: x[0])[:max_levels]
    return {"support": sup, "resistance": res}


def candlestick_patterns(df: pd.DataFrame, lookback: int = 5) -> list[dict]:
    """Detect common single and double candle patterns in the last few sessions."""
    found = []
    d = df.iloc[-(lookback + 1):]
    for i in range(1, len(d)):
        o, h, l, c = d.iloc[i][["Open", "High", "Low", "Close"]]
        po, pc = d.iloc[i - 1][["Open", "Close"]]
        body = abs(c - o)
        rng = max(h - l, 1e-9)
        upper, lower = h - max(o, c), min(o, c) - l
        date = d.index[i]
        if body <= 0.1 * rng:
            found.append({"date": date, "pattern": "Doji", "bias": "neutral / indecision"})
        if lower >= 2 * body and upper <= 0.3 * body + 1e-9 and body > 0:
            found.append({"date": date, "pattern": "Hammer", "bias": "bullish reversal"})
        if upper >= 2 * body and lower <= 0.3 * body + 1e-9 and body > 0:
            found.append({"date": date, "pattern": "Shooting star", "bias": "bearish reversal"})
        if pc < po and c > o and c >= po and o <= pc:
            found.append({"date": date, "pattern": "Bullish engulfing", "bias": "bullish"})
        if pc > po and c < o and o >= pc and c <= po:
            found.append({"date": date, "pattern": "Bearish engulfing", "bias": "bearish"})
        if body >= 0.9 * rng and c > o:
            found.append({"date": date, "pattern": "Bullish marubozu", "bias": "strong buying"})
        if body >= 0.9 * rng and c < o:
            found.append({"date": date, "pattern": "Bearish marubozu", "bias": "strong selling"})
    return found


def chart_events(ind: pd.DataFrame) -> list[str]:
    """Plain-language list of notable recent crossovers / breakouts."""
    ev = []
    last, prev = ind.iloc[-1], ind.iloc[-2]
    if prev["SMA50"] < prev["SMA200"] <= last["SMA200"] < last["SMA50"]:
        ev.append("Golden cross: 50-day SMA just crossed above 200-day SMA (long-term bullish)")
    if prev["SMA50"] > prev["SMA200"] >= last["SMA200"] > last["SMA50"]:
        ev.append("Death cross: 50-day SMA just crossed below 200-day SMA (long-term bearish)")
    if prev["MACD"] < prev["MACD_signal"] and last["MACD"] > last["MACD_signal"]:
        ev.append("MACD bullish crossover")
    if prev["MACD"] > prev["MACD_signal"] and last["MACD"] < last["MACD_signal"]:
        ev.append("MACD bearish crossover")
    if prev["ST_dir"] != last["ST_dir"]:
        ev.append("Supertrend flipped " + ("bullish" if last["ST_dir"] > 0 else "bearish"))
    if last["Close"] > last["BB_up"]:
        ev.append("Price closed above upper Bollinger Band (strong momentum / overbought)")
    if last["Close"] < last["BB_low"]:
        ev.append("Price closed below lower Bollinger Band (oversold / breakdown)")
    hi52 = ind["High"].iloc[-252:].max()
    lo52 = ind["Low"].iloc[-252:].min()
    if last["Close"] >= 0.98 * hi52:
        ev.append("Trading near 52-week high")
    if last["Close"] <= 1.02 * lo52:
        ev.append("Trading near 52-week low")
    if last["VOL_ratio"] > 2:
        ev.append(f"Volume spike: {last['VOL_ratio']:.1f}x the 20-day average")
    return ev


def buyer_seller_pressure(daily: pd.DataFrame, intraday: pd.DataFrame | None = None) -> dict:
    """Estimate who is in control (buyers vs sellers) from price-volume behaviour."""
    d = daily.iloc[-20:]
    up_vol = d.loc[d["Close"] > d["Open"], "Volume"].sum()
    down_vol = d.loc[d["Close"] <= d["Open"], "Volume"].sum()
    total = max(up_vol + down_vol, 1)
    cmf = float(chaikin_money_flow(daily).iloc[-1])
    obv_series = obv(daily)
    obv_slope = float(np.polyfit(np.arange(20), obv_series.iloc[-20:].to_numpy(), 1)[0])
    avg_vol = float(daily["Volume"].iloc[-20:].mean())
    res = {
        "buy_volume_pct": 100 * up_vol / total,
        "sell_volume_pct": 100 * down_vol / total,
        "cmf": cmf,
        "obv_trend": "rising (accumulation)" if obv_slope > 0 else "falling (distribution)",
        "obv_slope_vs_avg_vol": obv_slope / max(avg_vol, 1),
    }
    # Large-volume days = likely institutional (block / bulk) activity.
    vol_mean, vol_std = daily["Volume"].iloc[-120:].mean(), daily["Volume"].iloc[-120:].std()
    big = daily.iloc[-60:][daily["Volume"].iloc[-60:] > vol_mean + 2 * vol_std]
    res["big_volume_days"] = [
        {"date": i.date(), "volume": int(r["Volume"]), "change_pct": 100 * (r["Close"] / r["Open"] - 1),
         "type": "Big buying" if r["Close"] > r["Open"] else "Big selling"}
        for i, r in big.iterrows()
    ]
    if intraday is not None and not intraday.empty:
        today = intraday[intraday.index.date == intraday.index[-1].date()]
        if len(today) > 1:
            iv = vwap(intraday).iloc[-1]
            res["intraday_vwap"] = float(iv)
            res["above_vwap"] = bool(today["Close"].iloc[-1] > iv)
            t_up = today.loc[today["Close"] > today["Open"], "Volume"].sum()
            res["intraday_buy_pct"] = 100 * t_up / max(today["Volume"].sum(), 1)
    score = (res["buy_volume_pct"] - 50) / 50 * 0.4 + np.clip(cmf * 3, -1, 1) * 0.4 + np.sign(obv_slope) * 0.2
    res["score"] = float(np.clip(score, -1, 1))
    res["verdict"] = "Buyers in control" if score > 0.15 else "Sellers in control" if score < -0.15 else "Balanced"
    return res
