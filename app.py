"""StockAI - NSE / BSE stock analysis and prediction dashboard.

Run with:  streamlit run app.py
"""

from __future__ import annotations

from datetime import datetime

import numpy as np
import pandas as pd
import plotly.graph_objects as go
import streamlit as st
from plotly.subplots import make_subplots

from stockai import data, indicators, kite, live, sentiment, signals
from stockai.forecast import HORIZONS

st.set_page_config(page_title="StockAI · NSE/BSE Predictor", page_icon="📈", layout="wide")

# Reference categorical palette (fixed order) + status colours.
BLUE, ORANGE, AQUA, YELLOW, MAGENTA, VIOLET = "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#6250d6"
UP, DOWN, NEUTRAL = "#0ca30c", "#d03b3b", "#8a8984"
ACTION_STYLE = {"STRONG BUY": ("🟢", UP), "BUY": ("🟢", UP), "HOLD": ("🟡", YELLOW),
                "SELL": ("🔴", DOWN), "STRONG SELL": ("🔴", DOWN)}


def rupee(x, d=2):
    return "—" if x is None or (isinstance(x, float) and np.isnan(x)) else f"₹{x:,.{d}f}"


def pct(x, d=2, sign=True):
    return "—" if x is None or (isinstance(x, float) and np.isnan(x)) else f"{x:+.{d}f}%" if sign else f"{x:.{d}f}%"


def crore(x):
    if not x:
        return "—"
    cr = x / 1e7
    return f"₹{cr / 1e5:,.2f}L Cr" if cr >= 1e5 else f"₹{cr:,.0f} Cr"


def span(lo, hi, d=0):
    return f"{lo:,.{d}f} – {hi:,.{d}f}"


@st.cache_data(ttl=300, show_spinner=False)
def run_analysis(query: str, exchange: str, demo: bool, source: str = "yahoo", _broker=None):
    bundle = data.load_stock(query, exchange, demo=demo, broker=_broker)
    return signals.analyze(bundle)


@st.cache_data(ttl=4, show_spinner=False)
def _live_quote(symbol: str):
    return live.fetch_quote(symbol)


@st.cache_resource(show_spinner=False)
def kite_source(api_key: str, access_token: str, stream: bool) -> kite.KiteSource:
    src = kite.KiteSource(api_key, access_token)
    if stream:
        src.start_stream()
    return src


def active_kite() -> kite.KiteSource | None:
    return st.session_state.get("kite_src")


def get_quote(symbol: str, demo: bool, start_price: float | None = None) -> live.Quote:
    """Live quote from Zerodha Kite or Yahoo, or the next tick of a simulated feed in demo mode."""
    if not demo:
        src = active_kite()
        if src is not None:
            return src.quote(symbol)
        return _live_quote(symbol)
    feeds = st.session_state.setdefault("feeds", {})
    if symbol not in feeds:
        base = start_price or float(data.demo_bundle(symbol[:-3], "NSE").daily["Close"].iloc[-1])
        feeds[symbol] = live.DemoFeed(symbol, base)
    return feeds[symbol].tick()


def check_alerts(symbol: str, price: float):
    for a in st.session_state.get("alerts", []):
        if a.symbol == symbol and a.check(price):
            msg = f"🔔 {symbol} is {a.direction} ₹{a.level:,.2f} (now ₹{price:,.2f})"
            st.toast(msg, icon="🔔")
            st.session_state.setdefault("alert_log", []).insert(0, f"{datetime.now(live.IST):%H:%M:%S} · {msg}")


def watchlist_panel(exchange: str, demo: bool):
    """Live-refreshing watchlist table plus alert manager."""
    default = [f"{s}{data.EXCHANGE_SUFFIX[exchange]}" for s in ("RELIANCE", "HDFCBANK", "ICICIBANK", "TCS", "LT", "HAL")]
    wl = st.session_state.setdefault("watchlist", default)
    with st.expander("✏️ Edit watchlist & price alerts"):
        add = st.text_input("Add stock (name or code)", key="wl_add")
        c1, c2 = st.columns(2)
        if c1.button("➕ Add to watchlist") and add:
            try:
                sym = data.resolve_symbol(add, exchange)
                if sym not in wl:
                    wl.append(sym)
            except ValueError as exc:
                st.error(str(exc))
        remove = c2.multiselect("Remove", wl, key="wl_remove")
        if remove:
            st.session_state["watchlist"] = wl = [s for s in wl if s not in remove]
        st.markdown("**New price alert**")
        a1, a2, a3, a4 = st.columns([2, 1, 1, 1])
        a_sym = a1.selectbox("Stock", wl, key="al_sym") if wl else None
        a_dir = a2.selectbox("When price is", ["above", "below"], key="al_dir")
        a_lvl = a3.number_input("₹ level", min_value=0.0, step=1.0, key="al_lvl")
        if a4.button("🔔 Set alert") and a_sym and a_lvl > 0:
            st.session_state.setdefault("alerts", []).append(live.Alert(a_sym, a_dir, a_lvl))
        alerts = st.session_state.get("alerts", [])
        if alerts:
            st.dataframe(pd.DataFrame([{"Stock": a.symbol, "Condition": f"{a.direction} ₹{a.level:,.2f}",
                                        "Status": "✅ triggered" if a.triggered else "⏳ watching"} for a in alerts]),
                         hide_index=True, width="stretch")
            if st.button("Clear alerts"):
                st.session_state["alerts"] = []

    @st.fragment(run_every=REFRESH)
    def _table():
        rows = []
        symbols = st.session_state.get("watchlist", [])
        src = None if demo else active_kite()
        batch = {}
        if src is not None and symbols:
            try:
                batch = src.quote_many(symbols)  # one API call for the whole list
            except Exception as exc:
                st.warning(f"Kite quotes failed: {exc}")
        for sym in symbols:
            try:
                q = batch.get(sym) or get_quote(sym, demo)
                check_alerts(sym, q.price)
                rows.append(live.watch_row(q))
            except Exception as exc:
                rows.append({"Symbol": sym, "Signal": f"⚠️ {exc}"[:60]})
        if rows:
            df = pd.DataFrame(rows)
            st.dataframe(df, hide_index=True, width="stretch", column_config={
                "Price": st.column_config.NumberColumn(format="₹%.2f"),
                "Change": st.column_config.NumberColumn(format="%+.2f"),
                "Change %": st.column_config.NumberColumn(format="%+.2f%%"),
                "Day low": st.column_config.NumberColumn(format="₹%.2f"),
                "Day high": st.column_config.NumberColumn(format="₹%.2f"),
                "Volume": st.column_config.NumberColumn(format="%d"),
            })
        for line in st.session_state.get("alert_log", [])[:5]:
            st.caption(line)
    _table()


def order_book(q: live.Quote):
    """Live market depth and pending buy vs sell quantity (broker data only)."""
    st.subheader("📊 Live order book")
    ex = q.extra
    buy, sell = float(ex.get("buy_qty") or 0), float(ex.get("sell_qty") or 0)
    if buy + sell:
        share = buy / (buy + sell)
        c = st.columns(4)
        c[0].metric("Pending buy qty", f"{buy:,.0f}")
        c[1].metric("Pending sell qty", f"{sell:,.0f}")
        verdict = "🟢 buyers stronger" if share > 0.55 else "🔴 sellers stronger" if share < 0.45 else "🟡 balanced"
        c[2].metric(f"Buyers' share · {verdict}", f"{share * 100:.0f}%")
        c[3].metric("Avg traded price", rupee(ex.get("avg_price")))
        st.progress(share, text=f"Buy {share * 100:.0f}%  ·  Sell {(1 - share) * 100:.0f}%")
    rows = []
    for i in range(max(len(ex.get("depth_buy", [])), len(ex.get("depth_sell", [])))):
        b = ex["depth_buy"][i] if i < len(ex["depth_buy"]) else {}
        a = ex["depth_sell"][i] if i < len(ex["depth_sell"]) else {}
        rows.append({"Bid orders": b.get("orders"), "Bid qty": b.get("quantity"), "Bid ₹": b.get("price"),
                     "Ask ₹": a.get("price"), "Ask qty": a.get("quantity"), "Ask orders": a.get("orders")})
    if rows:
        st.dataframe(pd.DataFrame(rows), hide_index=True, width="stretch")
    if ex.get("upper_circuit"):
        st.caption(f"Circuit limits: {rupee(ex.get('lower_circuit'))} – {rupee(ex.get('upper_circuit'))}")


def style_fig(fig, height=420):
    fig.update_layout(height=height, margin=dict(l=10, r=10, t=50, b=10), hovermode="x unified",
                      legend=dict(orientation="h", yanchor="top", y=-0.08, x=0))
    fig.update_xaxes(showgrid=False)
    fig.update_yaxes(gridcolor="rgba(128,128,128,0.15)")
    return fig


def kite_sidebar():
    """Credentials, daily login redirect and connection status for Zerodha Kite."""
    if not kite.available():
        st.error("Install the Kite package first: `pip install kiteconnect`")
        return
    try:
        secrets = dict(st.secrets)
    except Exception:  # no secrets.toml
        secrets = {}
    cred = kite.credentials(secrets)
    with st.expander("🔑 Kite API settings", expanded=not (cred["api_key"] and cred["api_secret"])):
        cred["api_key"] = st.text_input("API key", value=cred["api_key"]).strip()
        cred["api_secret"] = st.text_input("API secret", value=cred["api_secret"], type="password").strip()
        stream = st.checkbox("Stream ticks over WebSocket", value=True,
                             help="Tick-by-tick prices and market depth. Uses one WebSocket connection.")
        st.caption("Set the redirect URL of your Kite Connect app to this app's address, "
                   "e.g. http://localhost:8501. Keys can also go in .streamlit/secrets.toml.")
    if not (cred["api_key"] and cred["api_secret"]):
        st.info("Enter your Kite Connect API key and secret.")
        return
    params = st.query_params
    if params.get("request_token") and params.get("status") == "success":
        try:
            st.session_state["kite_token"] = kite.exchange_request_token(
                cred["api_key"], cred["api_secret"], params["request_token"])
            st.toast("Logged in to Zerodha Kite", icon="✅")
        except Exception as exc:
            st.error(f"Kite login failed: {exc}")
        st.query_params.clear()
    token = (st.session_state.get("kite_token") or cred["access_token"]
             or kite.load_saved_token(cred["api_key"]))
    if not token:
        st.link_button("🔐 Log in with Zerodha", kite.login_url(cred["api_key"]), type="primary",
                       width="stretch")
        st.caption("Kite tokens expire daily around 6 AM, so log in once each trading day.")
        return
    try:
        src = kite_source(cred["api_key"], token, stream)
        name = st.session_state.get("kite_user") or src.profile_name()
        st.session_state["kite_user"] = name
    except Exception as exc:
        st.error(f"Kite connection failed: {exc}")
        st.session_state.pop("kite_token", None)
        st.link_button("🔐 Log in again", kite.login_url(cred["api_key"]), width="stretch")
        return
    st.session_state["kite_src"] = src
    status = src.stream.status if src.stream else "REST polling"
    st.success(f"Zerodha Kite connected · {name} · {status}")
    if st.button("Log out of Kite", width="stretch"):
        st.session_state.pop("kite_token", None)
        st.session_state["kite_src"] = None
        kite.TOKEN_FILE.unlink(missing_ok=True)
        st.rerun()


# --------------------------------------------------------------------------
# Sidebar
# --------------------------------------------------------------------------
with st.sidebar:
    st.title("📈 StockAI")
    exchange = st.radio("Exchange", ["NSE", "BSE"], horizontal=True)
    pick = st.selectbox("Popular stocks", ["— type below —"] + [f"{k} · {v}" for k, v in data.POPULAR_STOCKS.items()])
    typed = st.text_input("Stock name or code", placeholder="e.g. RELIANCE, Infosys, 500325")
    query = typed.strip() or ("" if pick.startswith("—") else pick.split(" · ")[0])
    demo = st.toggle("Demo mode (synthetic data, works offline)", value=False)
    refresh_label = st.selectbox("⚡ Live refresh", ["5 s", "10 s", "15 s", "30 s", "60 s", "Off"], index=2)
    kite_default = bool(st.query_params.get("request_token") or kite.TOKEN_FILE.exists())
    source = st.radio("Live data source", ["Yahoo Finance (free, delayed)", "Zerodha Kite (real-time)"],
                      index=1 if kite_default else 0, disabled=demo, key="source")
    st.session_state["kite_src"] = None
    if source.startswith("Zerodha") and not demo:
        kite_sidebar()
    go_btn = st.button("🔍 Analyse", type="primary", width="stretch")
    if st.button("🔄 Refresh live data", width="stretch"):
        run_analysis.clear()
    st.caption("Tip: BSE scrip codes (e.g. 500325) work with BSE selected. Prices: Zerodha Kite when "
               "connected, otherwise Yahoo Finance (delayed ~1-15 min). News: Google News & Yahoo.")

REFRESH = None if refresh_label == "Off" else int(refresh_label.split()[0])
status = live.market_status()
st.caption(f"{status['label']} · {status['detail']} · {status['now']:%a %d %b, %H:%M:%S} IST"
           + ("" if REFRESH is None else f" · auto-refresh every {REFRESH}s"))

st.warning("⚠️ Educational tool, not investment advice. Predictions are statistical estimates with real "
           "uncertainty — always do your own research or consult a SEBI-registered adviser.", icon="⚠️")

if go_btn and query:
    st.session_state["active"] = (query, exchange, demo)
active = st.session_state.get("active")
if not active:
    st.header("NSE / BSE Stock Predictor & Deep Analyser")
    st.markdown(
        "Choose **NSE or BSE**, pick or type a **stock name / code** and press **Analyse**. You get:\n"
        "- **Real-time position** and intraday movement / expected day range\n"
        "- **Predictions** for today, next week, 1 month and 6 months from an ML ensemble "
        "(Ridge, Random Forest, Gradient Boosting), Monte Carlo simulation and trend models\n"
        "- **Buy / Hold / Sell** for each horizon with target, stop-loss and profit margin\n"
        "- **20+ technical indicators**, support/resistance, pivots, Fibonacci, candlestick patterns\n"
        "- **News & sentiment**, **public interest**, **buyers vs sellers**, **who owns it** "
        "(promoters, FIIs/DIIs, mutual funds) and **fundamentals**"
    )
    st.subheader("⚡ Live watchlist")
    watchlist_panel(exchange, demo)
    st.stop()

query, exchange, demo = active
with st.spinner(f"Fetching data and running models for {query} on {exchange}…"):
    try:
        broker = None if demo else active_kite()
        A = run_analysis(query, exchange, demo, "kite" if broker else "yahoo", broker)
    except Exception as exc:  # network / symbol errors
        st.error(f"Could not analyse **{query}** on {exchange}: {exc}")
        st.info("Check the symbol, try the other exchange, or switch on **Demo mode** if you're offline.")
        st.stop()

B = A.bundle
ind = A.ind
last = ind.iloc[-1]
prev = ind.iloc[-2]
price = float(last["Close"])
if A.intraday.get("current"):
    price = A.intraday["current"]
chg = price - float(prev["Close"])

st.header(f"{B.name}  ·  {B.symbol}")
st.caption(f"{B.info.get('sector', '')} · {B.info.get('industry', '')} · {B.exchange} · "
           f"analysed {datetime.now():%d %b %Y %H:%M}" + ("  ·  **DEMO DATA**" if B.demo else ""))



@st.fragment(run_every=REFRESH)
def live_header():
    try:
        q = get_quote(B.symbol, B.demo, price)
    except Exception as exc:
        st.caption(f"Live quote unavailable ({exc}); showing last analysed price.")
        q = None
    c = st.columns(3)
    if q is not None:
        check_alerts(B.symbol, q.price)
        c[0].metric("Live price", rupee(q.price), f"{q.change:+.2f} ({q.change_pct:+.2f}%)")
        c[1].metric("Day range ₹", span(q.day_low, q.day_high))
        m = live.momentum(q.bars)
        c[2].metric("Live momentum", m["label"], f"VWAP ₹{m['vwap']:,.2f}" if not np.isnan(m["vwap"]) else None,
                    delta_color="off")
        st.caption(f"Updated {q.updated:%H:%M:%S} IST · " + ("simulated ticks" if B.demo else
                   "Zerodha Kite (real-time)" if q.source == "Zerodha Kite" else
                   "Yahoo Finance (may lag the exchange by a few minutes)"))
    else:
        c[0].metric("Price", rupee(price), f"{chg:+.2f} ({chg / float(prev['Close']) * 100:+.2f}%)")
        c[1].metric("Day range ₹", span(A.intraday.get("day_low", last["Low"]), A.intraday.get("day_high", last["High"])))


live_header()
c = st.columns(4)
c[0].metric("52W range ₹", span(ind["Low"].iloc[-252:].min(), ind["High"].iloc[-252:].max()))
c[1].metric("Market cap", crore(B.info.get("marketCap")))
c[2].metric("Beta vs index", f"{A.beta:.2f}" if A.beta is not None else "—")
c[3].metric("Volatility (ann.)", pct(float(last["HV20"]) * 100, 1, sign=False))

tabs = st.tabs(["⚡ Live", "🧭 Verdict", "📈 Charts", "🔮 Predictions", "🧮 Technicals", "📰 News & sentiment",
                "👥 Buyers & interest", "🏦 Fundamentals & holders"])

# --------------------------------------------------------------------------
# Live
# --------------------------------------------------------------------------
with tabs[0]:
    plan = A.plans["Intraday (today)"]

    @st.fragment(run_every=REFRESH)
    def live_chart():
        try:
            q = get_quote(B.symbol, B.demo, price)
        except Exception as exc:
            st.info(f"Live 1-minute data unavailable: {exc}")
            return
        bars = q.bars.copy()
        tp = (bars["High"] + bars["Low"] + bars["Close"]) / 3
        bars["VWAP"] = (tp * bars["Volume"]).cumsum() / bars["Volume"].cumsum().replace(0, np.nan)
        m = live.momentum(q.bars)
        left, right = st.columns([3, 1])
        with left:
            fig = go.Figure()
            fig.add_trace(go.Candlestick(x=bars.index, open=bars.Open, high=bars.High, low=bars.Low, close=bars.Close,
                                         name="1 min", increasing_line_color=UP, decreasing_line_color=DOWN))
            fig.add_trace(go.Scatter(x=bars.index, y=bars.VWAP, name="VWAP", line=dict(color=ORANGE, width=2)))
            lo, hi = float(bars.Low.min()), float(bars.High.max())
            pad = (hi - lo) * 0.15 or hi * 0.005
            for lvl, name, color in ((plan.target, "Target", UP), (plan.stop_loss, "Stop-loss", DOWN)):
                if lo - 3 * pad <= lvl <= hi + 3 * pad:  # only draw levels near today's action
                    fig.add_hline(y=lvl, line_dash="dot", line_color=color, annotation_text=f"{name} {lvl:,.1f}")
                    lo, hi = min(lo, lvl), max(hi, lvl)
            fig.update_yaxes(range=[lo - pad, hi + pad])
            fig.update_layout(xaxis_rangeslider_visible=False, title=f"{B.symbol} · today, 1-minute bars")
            st.plotly_chart(style_fig(fig, 460), width="stretch")
        with right:
            st.metric("Live price", rupee(q.price), f"{q.change_pct:+.2f}%")
            st.markdown(f"**{m['label']}**")
            for r in m["reasons"]:
                st.markdown(f"- {r}")
            st.markdown(f"**Today's plan:** {ACTION_STYLE[plan.action][0]} {plan.action}  \n"
                        f"🎯 Target {rupee(plan.target)}  \n🛑 Stop {rupee(plan.stop_loss)}")
            dist_t = (plan.target / q.price - 1) * 100
            dist_s = (plan.stop_loss / q.price - 1) * 100
            st.caption(f"Target is {dist_t:+.2f}% away, stop is {dist_s:+.2f}% away.")
            if q.price >= plan.target:
                st.success("Target reached — consider booking profit.")
            elif q.price <= plan.stop_loss:
                st.error("Stop-loss hit — the intraday view is invalidated.")
        if q.extra.get("depth_buy") or q.extra.get("buy_qty"):
            order_book(q)

    live_chart()
    st.subheader("⚡ Live watchlist")
    watchlist_panel(exchange, B.demo)

# --------------------------------------------------------------------------
# Verdict
# --------------------------------------------------------------------------
with tabs[1]:
    st.subheader("Should you buy? — by time horizon")
    cols = st.columns(4)
    for col, (label, plan) in zip(cols, A.plans.items()):
        icon, color = ACTION_STYLE[plan.action]
        with col.container(border=True):
            st.markdown(f"**{label}**")
            st.markdown(f"<h3 style='color:{color};margin:0'>{icon} {plan.action}</h3>", unsafe_allow_html=True)
            fc = A.forecasts[label]
            st.markdown(
                f"Predicted price: **{rupee(fc.predicted_price)}** ({pct(fc.expected_return_pct)})  \n"
                f"Likely range: {rupee(fc.low, 1)} – {rupee(fc.high, 1)}  \n"
                f"Entry: **{rupee(plan.entry)}**  \n"
                f"🎯 Target: **{rupee(plan.target)}**  \n"
                f"🛑 Stop-loss: **{rupee(plan.stop_loss)}**  \n"
                f"Profit margin: **{pct(plan.expected_profit_pct)}** · Risk: {pct(plan.potential_loss_pct)}  \n"
                f"Risk : reward = **1 : {plan.risk_reward:.2f}**  \n"
                f"Chance of rise: **{plan.probability_up * 100:.0f}%**"
            )
            st.progress(plan.confidence, text=f"Model confidence {plan.confidence * 100:.0f}%")

    st.subheader("What drives the call")
    rows = []
    for label, plan in A.plans.items():
        w = signals.WEIGHTS[label]
        rows.append({"Horizon": label, **{k.title(): plan.breakdown[k] for k in w}, "Composite": plan.score})
    bd = pd.DataFrame(rows).set_index("Horizon")
    st.dataframe(bd.style.format("{:+.2f}").background_gradient(cmap="RdYlGn", vmin=-1, vmax=1),
                 width="stretch")
    st.caption("Each pillar is scored from -1 (very bearish) to +1 (very bullish). Short horizons lean on "
               "technicals, ML and order-flow; long horizons lean on fundamentals, analysts and trend.")

    st.subheader("Summary")
    bullets = [
        f"**Trend / technicals:** {A.technical['label']} (score {A.technical['score']:+.2f}, "
        f"{sum(v > 0 for v in A.technical['votes'].values())} of {len(A.technical['votes'])} indicators bullish).",
        f"**Intraday:** {A.intraday.get('bias', 'n/a')} — expected day range {rupee(A.intraday['expected_low'])} – "
        f"{rupee(A.intraday['expected_high'])}" + (f", VWAP {rupee(A.intraday['vwap'])}" if 'vwap' in A.intraday else "") + ".",
        f"**Buyers vs sellers:** {A.pressure['verdict']} — {A.pressure['buy_volume_pct']:.0f}% of last-20-day volume on "
        f"up-days, OBV {A.pressure['obv_trend']}.",
        f"**News sentiment:** {A.sentiment['label']} ({A.sentiment['positive']} positive / "
        f"{A.sentiment['negative']} negative of {A.sentiment['count']} articles).",
        f"**Public interest:** {A.interest['level']} — volume {A.interest['volume_ratio_5d_vs_3m']:.2f}x the 3-month norm.",
        f"**Fundamentals:** {A.fundamental['label']} (score {A.fundamental['score']:+.2f}).",
        f"**Analysts:** {A.analyst['recommendation']}"
        + (f", mean target {rupee(A.analyst['target_mean'])} ({pct(A.analyst['upside_pct'])} upside)" if A.analyst['target_mean'] else "") + ".",
    ]
    bullets += [f"**Signal:** {e}" for e in A.events]
    st.markdown("\n".join(f"- {b}" for b in bullets))

# --------------------------------------------------------------------------
# Charts
# --------------------------------------------------------------------------
with tabs[2]:
    period = st.radio("Range", ["1M", "3M", "6M", "1Y", "2Y", "5Y"], index=3, horizontal=True)
    n = {"1M": 21, "3M": 63, "6M": 126, "1Y": 252, "2Y": 504, "5Y": len(ind)}[period]
    overlays = st.multiselect("Overlays", ["SMA20", "SMA50", "SMA200", "EMA21", "Bollinger", "Supertrend"],
                              default=["SMA50", "SMA200"])
    d = ind.iloc[-n:]
    fig = make_subplots(rows=4, cols=1, shared_xaxes=True, row_heights=[0.55, 0.15, 0.15, 0.15], vertical_spacing=0.02)
    fig.add_trace(go.Candlestick(x=d.index, open=d["Open"], high=d["High"], low=d["Low"], close=d["Close"],
                                 name="Price", increasing_line_color=UP, decreasing_line_color=DOWN), 1, 1)
    palette = iter([BLUE, ORANGE, VIOLET, MAGENTA])
    for o in overlays:
        if o == "Bollinger":
            for col_name in ("BB_up", "BB_low"):
                fig.add_trace(go.Scatter(x=d.index, y=d[col_name], name=col_name, line=dict(color=NEUTRAL, width=1, dash="dot")), 1, 1)
        elif o == "Supertrend":
            fig.add_trace(go.Scatter(x=d.index, y=d["SUPERTREND"].where(d["ST_dir"] > 0), name="Supertrend ↑", line=dict(color=UP, width=1.5)), 1, 1)
            fig.add_trace(go.Scatter(x=d.index, y=d["SUPERTREND"].where(d["ST_dir"] < 0), name="Supertrend ↓", line=dict(color=DOWN, width=1.5)), 1, 1)
        else:
            fig.add_trace(go.Scatter(x=d.index, y=d[o], name=o, line=dict(color=next(palette), width=2)), 1, 1)
    vol_colors = np.where(d["Close"] >= d["Open"], UP, DOWN)
    fig.add_trace(go.Bar(x=d.index, y=d["Volume"], name="Volume", marker_color=vol_colors, opacity=0.6), 2, 1)
    fig.add_trace(go.Scatter(x=d.index, y=d["RSI"], name="RSI", line=dict(color=VIOLET, width=2)), 3, 1)
    fig.add_hline(y=70, line_dash="dot", line_color=NEUTRAL, row=3, col=1)
    fig.add_hline(y=30, line_dash="dot", line_color=NEUTRAL, row=3, col=1)
    fig.add_trace(go.Bar(x=d.index, y=d["MACD_hist"], name="MACD hist",
                         marker_color=np.where(d["MACD_hist"] >= 0, UP, DOWN)), 4, 1)
    fig.add_trace(go.Scatter(x=d.index, y=d["MACD"], name="MACD", line=dict(color=BLUE, width=2)), 4, 1)
    fig.add_trace(go.Scatter(x=d.index, y=d["MACD_signal"], name="Signal", line=dict(color=ORANGE, width=2)), 4, 1)
    fig.update_layout(xaxis_rangeslider_visible=False)
    for i, t in enumerate(["Volume", "RSI", "MACD"], start=2):
        fig.update_yaxes(title_text=t, row=i, col=1)
    st.plotly_chart(style_fig(fig, 820), width="stretch")

    st.subheader("Intraday (5-minute)")
    if B.intraday.empty:
        st.info("Intraday data not available (market closed or data source limit).")
    else:
        idf = B.intraday.copy()
        idf["VWAP"] = indicators.vwap(idf)
        idf = idf.iloc[-75 * 2:]
        f2 = go.Figure()
        f2.add_trace(go.Candlestick(x=idf.index, open=idf.Open, high=idf.High, low=idf.Low, close=idf.Close,
                                    name="5m", increasing_line_color=UP, decreasing_line_color=DOWN))
        f2.add_trace(go.Scatter(x=idf.index, y=idf.VWAP, name="VWAP", line=dict(color=ORANGE, width=2)))
        f2.update_layout(xaxis_rangeslider_visible=False)
        f2.update_xaxes(rangebreaks=[dict(bounds=["sat", "mon"]), dict(bounds=[15.5, 9.25], pattern="hour")])
        st.plotly_chart(style_fig(f2, 420), width="stretch")

# --------------------------------------------------------------------------
# Predictions
# --------------------------------------------------------------------------
with tabs[3]:
    st.subheader("Price forecasts by horizon")
    rows = []
    for label, fc in A.forecasts.items():
        rows.append({
            "Horizon": label, "Trading days": fc.horizon, "Predicted price": fc.predicted_price,
            "Expected return %": fc.expected_return_pct, "Low (10th pct)": fc.low, "High (90th pct)": fc.high,
            "P(rise)": fc.prob_up * 100, "P(>+5%)": fc.mc["prob_up_5pct"] * 100, "P(<-5%)": fc.mc["prob_down_5pct"] * 100,
            "ML accuracy %": fc.ml.ensemble_dir_acc * 100, "Confidence %": fc.confidence * 100,
        })
    st.dataframe(pd.DataFrame(rows).set_index("Horizon").style.format("{:,.2f}"), width="stretch")

    label = st.selectbox("Inspect horizon", list(HORIZONS))
    fc = A.forecasts[label]
    left, right = st.columns([3, 2])
    with left:
        hist = ind["Close"].iloc[-max(fc.horizon * 3, 60):]
        future = pd.bdate_range(ind.index[-1] + pd.Timedelta(days=1), periods=fc.horizon)
        fan = fc.mc["fan"].set_index(future)
        f3 = go.Figure()
        f3.add_trace(go.Scatter(x=hist.index, y=hist, name="History", line=dict(color=BLUE, width=2)))
        f3.add_trace(go.Scatter(x=fan.index, y=fan[95], line=dict(width=0), showlegend=False, hoverinfo="skip"))
        f3.add_trace(go.Scatter(x=fan.index, y=fan[5], fill="tonexty", fillcolor="rgba(42,120,214,0.12)",
                                line=dict(width=0), name="90% band"))
        f3.add_trace(go.Scatter(x=fan.index, y=fan[75], line=dict(width=0), showlegend=False, hoverinfo="skip"))
        f3.add_trace(go.Scatter(x=fan.index, y=fan[25], fill="tonexty", fillcolor="rgba(42,120,214,0.25)",
                                line=dict(width=0), name="50% band"))
        f3.add_trace(go.Scatter(x=[hist.index[-1], future[-1]], y=[hist.iloc[-1], fc.predicted_price],
                                name="Blended forecast", line=dict(color=ORANGE, width=2, dash="dash"),
                                mode="lines+markers", marker=dict(size=8)))
        f3.update_layout(title=f"{label}: Monte Carlo fan & blended forecast")
        st.plotly_chart(style_fig(f3, 440), width="stretch")
    with right:
        st.markdown("**Machine-learning models** (walk-forward validated)")
        ml = pd.DataFrame({
            "Predicted return %": {k: np.expm1(v) * 100 for k, v in fc.ml.predictions.items()},
            "Direction accuracy %": {k: s["dir_acc"] * 100 for k, s in fc.ml.scores.items()},
            "Avg error %": {k: s["mae"] * 100 for k, s in fc.ml.scores.items()},
        })
        st.dataframe(ml.style.format("{:.2f}"), width="stretch")
        st.markdown("**Statistical models**")
        st.dataframe(pd.DataFrame({
            "Price": {"Monte Carlo median": fc.mc["median"], "Monte Carlo mean": fc.mc["expected"],
                      "Linear trend": fc.trend["linear"], "Holt smoothing": fc.trend["holt"],
                      "ML ensemble": fc.last_price * (1 + fc.ml.ensemble_return), "➡ Blended": fc.predicted_price},
        }).style.format("₹{:,.2f}"), width="stretch")
        st.caption(f"1-year trend: {fc.trend['annual_trend_pct']:+.1f}%/yr (R² {fc.trend['r2']:.2f}). "
                   f"Daily volatility {fc.mc['daily_vol'] * 100:.2f}%.")
    if fc.ml.feature_importance is not None and len(fc.ml.feature_importance):
        fi = fc.ml.feature_importance.iloc[:10][::-1]
        f4 = go.Figure(go.Bar(x=fi.values, y=fi.index, orientation="h", marker_color=BLUE))
        f4.update_layout(title="What the Random Forest relies on most")
        st.plotly_chart(style_fig(f4, 340), width="stretch")
    st.caption("Accuracy above 50% means the model beat a coin flip on past data it had not seen. "
               "Markets are noisy — treat predictions as probabilities, not certainties.")

# --------------------------------------------------------------------------
# Technicals
# --------------------------------------------------------------------------
with tabs[4]:
    left, right = st.columns(2)
    with left:
        st.subheader(f"Indicator votes — {A.technical['label']}")
        votes = pd.DataFrame({"Vote": A.technical["votes"]})
        votes["Signal"] = np.where(votes["Vote"] > 0.1, "🟢 Bullish", np.where(votes["Vote"] < -0.1, "🔴 Bearish", "🟡 Neutral"))
        st.dataframe(votes.style.format({"Vote": "{:+.2f}"}), width="stretch", height=520)
    with right:
        st.subheader("Current readings")
        readings = {
            "RSI (14)": f"{last['RSI']:.1f}", "MACD / signal": f"{last['MACD']:.2f} / {last['MACD_signal']:.2f}",
            "ADX (trend strength)": f"{last['ADX']:.1f}", "Stochastic %K": f"{last['STOCH_K']:.1f}",
            "MFI": f"{last['MFI']:.1f}", "CCI": f"{last['CCI']:.1f}", "Williams %R": f"{last['WILLR']:.1f}",
            "Chaikin money flow": f"{last['CMF']:+.3f}", "ATR (14)": rupee(last["ATR"]),
            "SMA 20 / 50 / 200": f"{last['SMA20']:,.1f} / {last['SMA50']:,.1f} / {last['SMA200']:,.1f}",
            "Supertrend": f"{rupee(last['SUPERTREND'])} ({'up' if last['ST_dir'] > 0 else 'down'})",
        }
        st.table(pd.Series(readings, name="Value"))
        st.subheader("Chart signals")
        for e in A.events or ["No fresh crossover / breakout signals"]:
            st.markdown(f"- {e}")
        st.subheader("Candlestick patterns (last 5 sessions)")
        if A.patterns:
            st.dataframe(pd.DataFrame(A.patterns), width="stretch", hide_index=True)
        else:
            st.write("None detected.")
    st.subheader("Key price levels")
    l1, l2, l3 = st.columns(3)
    l1.markdown("**Pivot points (next session)**")
    l1.table(pd.Series({k: rupee(v) for k, v in A.levels["pivots"].items()}, name="Level"))
    l2.markdown("**Fibonacci retracement (6M swing)**")
    l2.table(pd.Series({k: rupee(v) for k, v in A.levels["fibonacci"].items()}, name="Level"))
    l3.markdown("**Support / resistance zones**")
    sr = A.levels["sr"]
    sr_rows = [{"Type": "Resistance", "Price": rupee(p), "Touches": n} for p, n in sr["resistance"]][::-1]
    sr_rows += [{"Type": "Support", "Price": rupee(p), "Touches": n} for p, n in sr["support"]]
    if not sr["resistance"]:
        sr_rows.insert(0, {"Type": "Resistance", "Price": "No overhead swing high (blue sky)", "Touches": 0})
    l3.dataframe(pd.DataFrame(sr_rows), hide_index=True, width="stretch")

# --------------------------------------------------------------------------
# News & sentiment
# --------------------------------------------------------------------------
with tabs[5]:
    s = A.sentiment
    c = st.columns(4)
    c[0].metric("Overall news sentiment", s["label"], f"{s['score']:+.2f}")
    c[1].metric("Positive articles", s["positive"])
    c[2].metric("Negative articles", s["negative"])
    c[3].metric("Neutral articles", s["neutral"])
    if A.news.empty:
        st.info("No recent news found.")
    else:
        nd = A.news.copy()
        nd["When"] = nd["published"].apply(lambda t: t.strftime("%d %b %H:%M") if pd.notna(t) and t else "")
        nd["Mood"] = nd["label"].map({"Positive": "🟢 Positive", "Negative": "🔴 Negative", "Neutral": "🟡 Neutral"})
        st.dataframe(
            nd[["When", "Mood", "sentiment", "title", "publisher", "link"]].rename(
                columns={"sentiment": "Score", "title": "Headline", "publisher": "Source", "link": "Link"}),
            column_config={"Link": st.column_config.LinkColumn("Link", display_text="open"),
                           "Score": st.column_config.NumberColumn(format="%+.2f")},
            width="stretch", hide_index=True, height=480,
        )
        st.caption("Sentiment uses VADER NLP with a finance-specific lexicon (e.g. 'upgrade', 'probe', 'pledge'); "
                   "recent news is weighted more heavily.")

# --------------------------------------------------------------------------
# Buyers & public interest
# --------------------------------------------------------------------------
with tabs[6]:
    p = A.pressure
    c = st.columns(2) + st.columns(2)
    c[0].metric("Who's in control", p["verdict"])
    c[1].metric("Buy-side volume (20D)", pct(p["buy_volume_pct"], 0, sign=False))
    c[2].metric("Chaikin money flow", f"{p['cmf']:+.3f}")
    c[3].metric("OBV trend", p["obv_trend"].split(" ")[0].title(), p["obv_trend"].split(" ", 1)[1])
    if "intraday_buy_pct" in p:
        st.markdown(f"**Today:** {p['intraday_buy_pct']:.0f}% of volume traded on rising 5-min bars; price is "
                    f"{'above' if p['above_vwap'] else 'below'} VWAP {rupee(p['intraday_vwap'])} "
                    f"→ {'buyers' if p['above_vwap'] else 'sellers'} have the upper hand intraday.")
    st.subheader("Large-volume sessions (possible institutional / block activity, last 60 days)")
    if p["big_volume_days"]:
        st.dataframe(pd.DataFrame(p["big_volume_days"]).style.format({"volume": "{:,}", "change_pct": "{:+.2f}%"}),
                     hide_index=True, width="stretch")
    else:
        st.write("No unusual volume days.")

    st.subheader(f"Public interest: {A.interest['level']}")
    c = st.columns(4)
    c[0].metric("Volume vs 3-month avg", f"{A.interest['volume_ratio_5d_vs_3m']:.2f}x")
    c[1].metric("Today's volume z-score", f"{A.interest['volume_zscore_today']:+.1f}")
    c[2].metric("Avg turnover (5D)", f"₹{A.interest['avg_turnover_cr_5d']:,.1f} Cr")
    c[3].metric("News articles (7D)", A.interest["news_last_7d"])
    wv = A.interest["weekly_volume"]
    f5 = go.Figure(go.Bar(x=wv.index, y=wv.values, marker_color=BLUE, name="Weekly volume"))
    f5.update_layout(title="Weekly traded volume (26 weeks)")
    st.plotly_chart(style_fig(f5, 320), width="stretch")
    if not B.demo and st.checkbox("Load Google search trends (needs `pytrends`, can be slow)"):
        trend = sentiment.google_trends(B.name.split(" Ltd")[0].split(" Limited")[0])
        if trend is None:
            st.info("Google Trends unavailable (install `pytrends` or rate-limited).")
        else:
            f6 = go.Figure(go.Scatter(x=trend.index, y=trend.values, line=dict(color=ORANGE, width=2), name="Search interest"))
            f6.update_layout(title="Google search interest in India (0-100)")
            st.plotly_chart(style_fig(f6, 300), width="stretch")

# --------------------------------------------------------------------------
# Fundamentals & holders
# --------------------------------------------------------------------------
with tabs[7]:
    left, right = st.columns([3, 2])
    with left:
        st.subheader(f"Fundamental health — {A.fundamental['label']}")
        checks = A.fundamental["checks"]
        if checks.empty:
            st.info("Fundamental data not available for this symbol.")
        else:
            checks = checks.copy()
            checks["Rating"] = np.where(checks["score"] > 0.3, "🟢", np.where(checks["score"] < -0.1, "🔴", "🟡"))
            st.dataframe(checks[["Rating", "metric", "value", "comment"]].rename(
                columns={"metric": "Metric", "value": "Value", "comment": "Comment"}), hide_index=True, width="stretch")
        an = A.analyst
        st.subheader("Analyst consensus")
        c = st.columns(2) + st.columns(2)
        c[0].metric("Recommendation", an["recommendation"])
        c[1].metric("Mean target", rupee(an["target_mean"], 0), pct(an["upside_pct"]) if an["upside_pct"] is not None else None)
        c[2].metric("Target range", f"{rupee(an['target_low'], 0)} – {rupee(an['target_high'], 0)}")
        c[3].metric("Analysts", an["analysts"] or "—")
    with right:
        st.subheader("Who owns it")
        o = A.owners
        parts = {"Promoters / insiders": o.get("promoters_insiders_pct"), "Institutions (FII/DII/MF)": o.get("institutions_pct"),
                 "Public / retail & others": o.get("public_retail_pct")}
        parts = {k: v for k, v in parts.items() if v is not None}
        if parts:
            f7 = go.Figure(go.Pie(labels=list(parts), values=list(parts.values()), hole=0.55,
                                  marker=dict(colors=[BLUE, ORANGE, AQUA], line=dict(color="white", width=2)),
                                  textinfo="percent"))
            f7.update_layout(showlegend=True)
            st.plotly_chart(style_fig(f7, 320), width="stretch")
        else:
            st.info("Shareholding split not available.")
        if "institutions_adding" in o:
            st.markdown(f"**{o['institutions_adding']}** top institutions increased their stake, "
                        f"**{o['institutions_reducing']}** reduced it.")
    st.subheader("Top institutional & mutual-fund holders")
    top = A.owners.get("top_holders")
    if top is not None and not top.empty:
        st.dataframe(top, hide_index=True, width="stretch")
    else:
        st.write("Not available for this stock.")
    for key, title in (("insider_transactions", "Insider / promoter transactions"),
                       ("upgrades_downgrades", "Broker upgrades & downgrades")):
        if key in A.owners:
            st.subheader(title)
            st.dataframe(A.owners[key].head(20), width="stretch")

st.caption("Built with yfinance, scikit-learn, VADER and Streamlit. Not financial advice.")
