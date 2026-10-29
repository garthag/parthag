"""Market data access: symbol resolution, prices, company info, holders and news.

Live data comes from Yahoo Finance (via ``yfinance``) and Google News RSS.
When the network is unavailable the caller can fall back to
``synthetic_history`` so the whole pipeline can still be exercised.
"""

from __future__ import annotations

import re
from urllib.parse import quote_plus
from dataclasses import dataclass, field
from datetime import datetime, timezone

import numpy as np
import pandas as pd

try:
    import yfinance as yf
except ImportError:  # pragma: no cover - optional at import time
    yf = None

try:
    import feedparser
except ImportError:  # pragma: no cover
    feedparser = None

EXCHANGE_SUFFIX = {"NSE": ".NS", "BSE": ".BO"}

# Common names -> NSE trading symbol. BSE uses the same symbol on Yahoo
# (e.g. RELIANCE.BO) or the numeric scrip code (e.g. 500325.BO).
POPULAR_STOCKS = {
    "RELIANCE": "Reliance Industries",
    "TCS": "Tata Consultancy Services",
    "HDFCBANK": "HDFC Bank",
    "INFY": "Infosys",
    "ICICIBANK": "ICICI Bank",
    "HINDUNILVR": "Hindustan Unilever",
    "ITC": "ITC Ltd",
    "SBIN": "State Bank of India",
    "BHARTIARTL": "Bharti Airtel",
    "KOTAKBANK": "Kotak Mahindra Bank",
    "LT": "Larsen & Toubro",
    "AXISBANK": "Axis Bank",
    "BAJFINANCE": "Bajaj Finance",
    "ASIANPAINT": "Asian Paints",
    "MARUTI": "Maruti Suzuki",
    "HCLTECH": "HCL Technologies",
    "SUNPHARMA": "Sun Pharmaceutical",
    "TITAN": "Titan Company",
    "WIPRO": "Wipro",
    "ULTRACEMCO": "UltraTech Cement",
    "TATAMOTORS": "Tata Motors",
    "TATASTEEL": "Tata Steel",
    "ADANIENT": "Adani Enterprises",
    "ADANIPORTS": "Adani Ports",
    "NTPC": "NTPC",
    "POWERGRID": "Power Grid Corporation",
    "ONGC": "Oil & Natural Gas Corporation",
    "COALINDIA": "Coal India",
    "JSWSTEEL": "JSW Steel",
    "M&M": "Mahindra & Mahindra",
    "NESTLEIND": "Nestle India",
    "TECHM": "Tech Mahindra",
    "DRREDDY": "Dr. Reddy's Laboratories",
    "CIPLA": "Cipla",
    "BAJAJFINSV": "Bajaj Finserv",
    "HDFCLIFE": "HDFC Life Insurance",
    "SBILIFE": "SBI Life Insurance",
    "ZOMATO": "Zomato",
    "IRCTC": "IRCTC",
    "PAYTM": "One97 Communications (Paytm)",
    "DMART": "Avenue Supermarts (DMart)",
    "YESBANK": "Yes Bank",
    "IDEA": "Vodafone Idea",
    "SUZLON": "Suzlon Energy",
    "HAL": "Hindustan Aeronautics",
    "BEL": "Bharat Electronics",
    "IRFC": "Indian Railway Finance Corp",
}

INDEX_SYMBOL = {"NSE": "^NSEI", "BSE": "^BSESN"}


@dataclass
class StockBundle:
    """Everything the analysis layer needs about one stock."""

    symbol: str
    exchange: str
    name: str
    daily: pd.DataFrame
    intraday: pd.DataFrame
    info: dict = field(default_factory=dict)
    holders: dict = field(default_factory=dict)
    news: list = field(default_factory=list)
    index_daily: pd.DataFrame | None = None
    demo: bool = False


def resolve_symbol(query: str, exchange: str) -> str:
    """Turn user input (name, NSE code or BSE scrip code) into a Yahoo ticker."""
    q = query.strip().upper()
    if not q:
        raise ValueError("Please enter a stock name or code")
    suffix = EXCHANGE_SUFFIX[exchange]
    if q.endswith((".NS", ".BO")):
        return q[:-3] + suffix
    if q.isdigit():  # BSE scrip code such as 500325
        return f"{q}.BO"
    if q in POPULAR_STOCKS:
        return q + suffix
    for code, name in POPULAR_STOCKS.items():
        if q in name.upper():
            return code + suffix
    found = search_symbol(query, exchange)
    if found:
        return found
    return re.sub(r"\s+", "", q) + suffix


def search_symbol(query: str, exchange: str) -> str | None:
    """Look the name up on Yahoo search and keep the result for the exchange."""
    if yf is None or not hasattr(yf, "Search"):
        return None
    suffix = EXCHANGE_SUFFIX[exchange]
    try:
        quotes = yf.Search(query, max_results=10).quotes
    except Exception:
        return None
    for quote in quotes:
        sym = quote.get("symbol", "")
        if sym.endswith(suffix):
            return sym
    for quote in quotes:  # listed on the other Indian exchange only
        sym = quote.get("symbol", "")
        if sym.endswith((".NS", ".BO")):
            return sym[:-3] + suffix
    return None


def _clean_ohlcv(df: pd.DataFrame) -> pd.DataFrame:
    if df is None or df.empty:
        return pd.DataFrame(columns=["Open", "High", "Low", "Close", "Volume"])
    if isinstance(df.columns, pd.MultiIndex):
        df = df.droplevel(1, axis=1)
    df = df[["Open", "High", "Low", "Close", "Volume"]].copy()
    df = df.dropna(subset=["Close"])
    df["Volume"] = df["Volume"].fillna(0)
    return df


def fetch_history(symbol: str, period: str = "5y", interval: str = "1d") -> pd.DataFrame:
    if yf is None:
        raise RuntimeError("yfinance is not installed")
    df = yf.Ticker(symbol).history(period=period, interval=interval, auto_adjust=True)
    return _clean_ohlcv(df)


def fetch_info(symbol: str) -> dict:
    try:
        return yf.Ticker(symbol).info or {}
    except Exception:
        return {}


def fetch_holders(symbol: str) -> dict:
    """Major, institutional and mutual-fund holders plus insider activity."""
    out: dict = {}
    t = yf.Ticker(symbol)
    for key, attr in [
        ("major", "major_holders"),
        ("institutional", "institutional_holders"),
        ("mutualfund", "mutualfund_holders"),
        ("insider_transactions", "insider_transactions"),
        ("insider_purchases", "insider_purchases"),
        ("recommendations", "recommendations"),
        ("upgrades_downgrades", "upgrades_downgrades"),
    ]:
        try:
            val = getattr(t, attr)
            if isinstance(val, pd.DataFrame) and not val.empty:
                out[key] = val
        except Exception:
            continue
    return out


def fetch_news(symbol: str, company: str, limit: int = 40) -> list[dict]:
    """News from Google News RSS (India edition) and Yahoo Finance."""
    items: list[dict] = []
    if feedparser is not None:
        q = f'"{company}" stock OR share NSE OR BSE'
        url = (
            "https://news.google.com/rss/search?q="
            + quote_plus(q)
            + "&hl=en-IN&gl=IN&ceid=IN:en"
        )
        try:
            feed = feedparser.parse(url)
            for e in feed.entries[:limit]:
                published = None
                if getattr(e, "published_parsed", None):
                    published = datetime(*e.published_parsed[:6], tzinfo=timezone.utc)
                items.append({
                    "title": e.get("title", ""),
                    "summary": re.sub(r"<[^>]+>", " ", e.get("summary", "")),
                    "link": e.get("link", ""),
                    "publisher": (e.get("source") or {}).get("title", "Google News"),
                    "published": published,
                })
        except Exception:
            pass
    if yf is not None:
        try:
            for n in yf.Ticker(symbol).news or []:
                c = n.get("content", n)
                pub = c.get("pubDate") or c.get("providerPublishTime")
                if isinstance(pub, (int, float)):
                    pub = datetime.fromtimestamp(pub, tz=timezone.utc)
                elif isinstance(pub, str):
                    pub = pd.to_datetime(pub, utc=True).to_pydatetime()
                link = c.get("link") or (c.get("canonicalUrl") or {}).get("url", "")
                items.append({
                    "title": c.get("title", ""),
                    "summary": c.get("summary", "") or "",
                    "link": link,
                    "publisher": c.get("publisher") or (c.get("provider") or {}).get("displayName", "Yahoo"),
                    "published": pub,
                })
        except Exception:
            pass
    seen, unique = set(), []
    for it in items:
        key = it["title"].lower()[:80]
        if key and key not in seen:
            seen.add(key)
            unique.append(it)
    unique.sort(key=lambda x: x["published"] or datetime(1970, 1, 1, tzinfo=timezone.utc), reverse=True)
    return unique[:limit]


KITE_INDEX = {"NSE": "NSE:NIFTY 50", "BSE": "BSE:SENSEX"}


def load_stock(query: str, exchange: str, demo: bool = False, broker=None) -> StockBundle:
    """Fetch every piece of data for a stock (live, or synthetic when ``demo``).

    With ``broker`` (a ``kite.KiteSource``) prices come from Zerodha; Yahoo is then only
    used, best-effort, for company info, holders and news.
    """
    if demo:
        return demo_bundle(query, exchange)
    symbol = resolve_symbol(query, exchange)
    if broker is not None:
        daily = broker.history(symbol, "day", 1825)
    else:
        daily = fetch_history(symbol, period="5y", interval="1d")
    if daily.empty:
        raise ValueError(f"No price data found for {symbol}. Check the name/code and exchange.")
    try:
        intraday = (broker.history(symbol, "5minute", 7) if broker is not None
                    else fetch_history(symbol, period="5d", interval="5m"))
    except Exception:
        intraday = _clean_ohlcv(None)
    info = fetch_info(symbol) if yf is not None else {}
    name = info.get("longName") or info.get("shortName") or POPULAR_STOCKS.get(symbol[:-3], symbol[:-3])
    try:
        index_daily = (broker.history(KITE_INDEX[exchange], "day", 1825) if broker is not None
                       else fetch_history(INDEX_SYMBOL[exchange], period="5y", interval="1d"))
    except Exception:
        index_daily = None
    try:
        holders = fetch_holders(symbol)
    except Exception:
        holders = {}
    return StockBundle(
        symbol=symbol,
        exchange=exchange,
        name=name,
        daily=daily,
        intraday=intraday,
        info=info,
        holders=holders,
        news=fetch_news(symbol, name),
        index_daily=index_daily,
    )


# --------------------------------------------------------------------------
# Synthetic data for offline demo / tests
# --------------------------------------------------------------------------

def synthetic_history(seed: int = 7, days: int = 1250, start_price: float = 1500.0) -> pd.DataFrame:
    """Regime-switching GBM with realistic OHLCV structure."""
    rng = np.random.default_rng(seed)
    idx = pd.bdate_range(end=pd.Timestamp.today().normalize(), periods=days)
    drift = np.repeat(rng.normal(0.0004, 0.0012, days // 60 + 1), 60)[:days]
    vol = np.repeat(rng.uniform(0.010, 0.022, days // 40 + 1), 40)[:days]
    rets = rng.normal(drift, vol)
    close = start_price * np.exp(np.cumsum(rets))
    open_ = np.r_[start_price, close[:-1]] * np.exp(rng.normal(0, vol / 3))
    high = np.maximum(open_, close) * np.exp(np.abs(rng.normal(0, vol / 2)))
    low = np.minimum(open_, close) * np.exp(-np.abs(rng.normal(0, vol / 2)))
    volume = (rng.lognormal(14, 0.4, days) * (1 + 20 * np.abs(rets))).astype(int)
    return pd.DataFrame({"Open": open_, "High": high, "Low": low, "Close": close, "Volume": volume}, index=idx)


def synthetic_intraday(last_close: float, seed: int = 7, days: int = 5) -> pd.DataFrame:
    rng = np.random.default_rng(seed + 1)
    frames = []
    dates = pd.bdate_range(end=pd.Timestamp.today().normalize(), periods=days)
    price = last_close
    for d in dates:
        idx = pd.date_range(d + pd.Timedelta(hours=9, minutes=15), d + pd.Timedelta(hours=15, minutes=25), freq="5min")
        r = rng.normal(0, 0.0015, len(idx))
        c = price * np.exp(np.cumsum(r))
        o = np.r_[price, c[:-1]]
        h = np.maximum(o, c) * (1 + np.abs(rng.normal(0, 0.0007, len(idx))))
        l = np.minimum(o, c) * (1 - np.abs(rng.normal(0, 0.0007, len(idx))))
        v = rng.lognormal(10, 0.6, len(idx)).astype(int)
        frames.append(pd.DataFrame({"Open": o, "High": h, "Low": l, "Close": c, "Volume": v}, index=idx))
        price = c[-1]
    out = pd.concat(frames)
    scale = last_close / out["Close"].iloc[-1]  # end the session at the daily close
    out[["Open", "High", "Low", "Close"]] *= scale
    return out


def demo_bundle(query: str, exchange: str) -> StockBundle:
    code = (query or "DEMO").strip().upper() or "DEMO"
    seed = sum(map(ord, code)) % 1000
    daily = synthetic_history(seed=seed)
    index_daily = synthetic_history(seed=seed + 99, start_price=20000)
    intraday = synthetic_intraday(float(daily["Close"].iloc[-1]), seed=seed)
    name = POPULAR_STOCKS.get(code, code.title()) + " (demo data)"
    rng = np.random.default_rng(seed)
    last = float(daily["Close"].iloc[-1])
    info = {
        "longName": name,
        "sector": "Demo Sector",
        "industry": "Demo Industry",
        "marketCap": int(last * 6.7e9),
        "trailingPE": float(rng.uniform(12, 45)),
        "forwardPE": float(rng.uniform(10, 40)),
        "priceToBook": float(rng.uniform(1, 8)),
        "returnOnEquity": float(rng.uniform(0.05, 0.25)),
        "debtToEquity": float(rng.uniform(10, 120)),
        "profitMargins": float(rng.uniform(0.04, 0.22)),
        "revenueGrowth": float(rng.uniform(-0.05, 0.25)),
        "earningsGrowth": float(rng.uniform(-0.1, 0.3)),
        "dividendYield": float(rng.uniform(0, 0.03)),
        "heldPercentInsiders": float(rng.uniform(0.3, 0.6)),
        "heldPercentInstitutions": float(rng.uniform(0.15, 0.4)),
        "targetMeanPrice": last * float(rng.uniform(0.9, 1.25)),
        "targetHighPrice": last * 1.35,
        "targetLowPrice": last * 0.8,
        "recommendationKey": str(rng.choice(["buy", "hold", "strong_buy", "underperform"])),
        "numberOfAnalystOpinions": int(rng.integers(5, 35)),
        "fiftyTwoWeekHigh": float(daily["High"].iloc[-252:].max()),
        "fiftyTwoWeekLow": float(daily["Low"].iloc[-252:].min()),
        "beta": float(rng.uniform(0.6, 1.4)),
    }
    holders = {
        "institutional": pd.DataFrame({
            "Holder": ["Life Insurance Corporation of India", "SBI Mutual Fund", "Vanguard Group",
                       "BlackRock Inc.", "HDFC Mutual Fund", "ICICI Prudential MF"],
            "pctHeld": rng.uniform(0.005, 0.07, 6).round(4),
            "pctChange": rng.normal(0, 0.05, 6).round(4),
        }),
    }
    now = datetime.now(timezone.utc)
    headlines = [
        f"{code} shares surge after strong quarterly profit growth beats estimates",
        f"Brokerage upgrades {code} to buy, raises target price",
        f"{code} faces regulatory probe; stock slips in early trade",
        f"FIIs increase stake in {code} during the September quarter",
        f"{code} announces expansion plan and new order wins",
        f"Analysts cautious on {code} amid margin pressure and weak demand",
        f"{code} stock hits 52-week high as volumes jump",
        f"Promoter pledges part of holding in {code}",
    ]
    news = [{
        "title": h, "summary": "", "link": "", "publisher": "Demo News",
        "published": now - pd.Timedelta(hours=int(i * 17 + 3)),
    } for i, h in enumerate(headlines)]
    return StockBundle(code + EXCHANGE_SUFFIX[exchange], exchange, name, daily, intraday,
                       info, holders, news, index_daily, demo=True)
