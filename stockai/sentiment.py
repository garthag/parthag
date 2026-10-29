"""News sentiment and public-interest analysis."""

from __future__ import annotations

from datetime import datetime, timezone

import numpy as np
import pandas as pd

try:
    from vaderSentiment.vaderSentiment import SentimentIntensityAnalyzer
except ImportError:  # pragma: no cover
    SentimentIntensityAnalyzer = None

# Finance-specific words VADER does not know (or mis-scores).
FINANCE_LEXICON = {
    "surge": 2.5, "surges": 2.5, "soar": 2.5, "soars": 2.5, "rally": 2.0, "rallies": 2.0, "jump": 1.8,
    "jumps": 1.8, "gain": 1.5, "gains": 1.5, "upgrade": 2.2, "upgrades": 2.2, "outperform": 2.0,
    "beat": 1.8, "beats": 1.8, "record": 1.2, "high": 0.6, "bullish": 2.5, "buy": 1.5, "accumulate": 1.4,
    "profit": 1.4, "growth": 1.4, "expansion": 1.3, "order": 0.8, "orders": 0.8, "wins": 1.5,
    "dividend": 1.2, "bonus": 1.4, "buyback": 1.6, "stake": 0.4, "approval": 1.3, "breakout": 1.6,
    "plunge": -2.8, "plunges": -2.8, "crash": -3.0, "slump": -2.4, "slumps": -2.4, "fall": -1.5,
    "falls": -1.5, "slip": -1.3, "slips": -1.3, "drop": -1.5, "drops": -1.5, "decline": -1.6,
    "downgrade": -2.3, "downgrades": -2.3, "underperform": -2.0, "miss": -1.8, "misses": -1.8,
    "loss": -2.0, "losses": -2.0, "bearish": -2.5, "sell": -1.4, "weak": -1.6, "probe": -2.0,
    "fraud": -3.2, "penalty": -2.0, "pledge": -1.6, "pledges": -1.6, "default": -2.8, "raid": -2.5,
    "cautious": -1.0, "pressure": -1.0, "low": -0.6, "resigns": -1.5, "lawsuit": -2.0, "sebi": -0.5,
}

POSITIVE_EVENTS = ["results", "order", "acquisition", "dividend", "bonus", "split", "buyback", "upgrade", "fii", "dii"]


def _analyzer():
    if SentimentIntensityAnalyzer is None:
        return None
    a = SentimentIntensityAnalyzer()
    a.lexicon.update(FINANCE_LEXICON)
    return a


def _fallback_score(text: str) -> float:
    words = [w.strip(".,:;!?'\"()").lower() for w in text.split()]
    s = sum(FINANCE_LEXICON.get(w, 0) for w in words)
    return float(np.tanh(s / 4))


def score_news(news: list[dict]) -> pd.DataFrame:
    analyzer = _analyzer()
    rows = []
    now = datetime.now(timezone.utc)
    for n in news:
        text = f"{n.get('title', '')}. {n.get('summary', '')[:300]}"
        s = analyzer.polarity_scores(text)["compound"] if analyzer else _fallback_score(text)
        pub = n.get("published")
        age_h = (now - pub).total_seconds() / 3600 if pub else 24 * 7
        rows.append({
            "published": pub, "title": n.get("title", ""), "publisher": n.get("publisher", ""),
            "link": n.get("link", ""), "sentiment": s,
            "label": "Positive" if s > 0.15 else "Negative" if s < -0.15 else "Neutral",
            "weight": float(np.exp(-max(age_h, 0) / 96)),  # ~4 day half-life-ish decay
            "topics": ", ".join(t for t in POSITIVE_EVENTS if t in text.lower()),
        })
    return pd.DataFrame(rows)


def aggregate_sentiment(scored: pd.DataFrame) -> dict:
    if scored.empty:
        return {"score": 0.0, "label": "No news", "count": 0, "positive": 0, "negative": 0, "neutral": 0}
    w = scored["weight"].clip(lower=0.05)
    score = float((scored["sentiment"] * w).sum() / w.sum())
    counts = scored["label"].value_counts()
    return {
        "score": score,
        "label": "Bullish" if score > 0.15 else "Bearish" if score < -0.15 else "Neutral",
        "count": int(len(scored)),
        "positive": int(counts.get("Positive", 0)),
        "negative": int(counts.get("Negative", 0)),
        "neutral": int(counts.get("Neutral", 0)),
    }


def public_interest(daily: pd.DataFrame, scored_news: pd.DataFrame) -> dict:
    """Gauge crowd attention: trading activity vs normal and news flow."""
    v = daily["Volume"]
    turnover = (daily["Close"] * v)
    recent = float(v.iloc[-5:].mean())
    base = float(v.iloc[-65:-5].mean()) or 1.0
    vol_z = float((v.iloc[-1] - v.iloc[-60:].mean()) / (v.iloc[-60:].std() or 1))
    news_7d = 0
    if not scored_news.empty and scored_news["published"].notna().any():
        cutoff = datetime.now(timezone.utc) - pd.Timedelta(days=7)
        news_7d = int((scored_news["published"].dropna() >= cutoff).sum())
    ratio = recent / base
    score = float(np.clip(np.log(ratio) * 1.5 + min(news_7d, 20) / 20 * 0.5, -1, 1))
    level = "Very high" if score > 0.6 else "High" if score > 0.25 else "Normal" if score > -0.25 else "Low"
    return {
        "volume_ratio_5d_vs_3m": ratio,
        "volume_zscore_today": vol_z,
        "avg_turnover_cr_5d": float(turnover.iloc[-5:].mean() / 1e7),
        "news_last_7d": news_7d,
        "score": score,
        "level": level,
        "weekly_volume": v.resample("W").sum().iloc[-26:],
    }


def google_trends(keyword: str) -> pd.Series | None:
    """Search interest in India over the last 3 months (optional ``pytrends``)."""
    try:
        from pytrends.request import TrendReq
    except ImportError:
        return None
    try:
        tr = TrendReq(hl="en-IN", tz=330)
        tr.build_payload([keyword], timeframe="today 3-m", geo="IN")
        df = tr.interest_over_time()
        return df[keyword] if not df.empty else None
    except Exception:
        return None
