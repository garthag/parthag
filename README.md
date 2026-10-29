# parthag — StockAI: NSE / BSE Stock Predictor & Deep Analyser

A Streamlit web app that analyses any Indian stock on **NSE or BSE** and predicts its movement
**intraday, next week, 1 month and 6 months**, with a **Buy / Hold / Sell** call, target,
stop-loss and expected profit margin for each horizon.

> ⚠️ Educational tool, **not investment advice**. Forecasts are statistical estimates; markets are
> uncertain. Do your own research or consult a SEBI-registered adviser.

## Install & run

### Windows 10 / 11 (desktop app)
1. Download the code: on GitHub click **Code → Download ZIP**, then right-click the ZIP → **Extract All**
   (or `git clone` this repo).
2. Double-click **`install_windows.bat`**. It installs Python 3.12 if missing (via `winget`; run the
   installer again after Python finishes), creates a private environment, installs the packages
   and puts a **StockAI** shortcut on your desktop.
3. Double-click the **StockAI** desktop shortcut. A console window starts the server, and StockAI opens in its
   own Microsoft Edge app window, with no browser tabs or address bar. Keep the console open while you use it,
   and close it to stop the app.

> If Windows SmartScreen warns about the `.bat` file, click **More info → Run anyway**.
> If Windows Firewall asks about Python, allow **Private networks** so your phone can connect.

### macOS / Linux
```bash
./run.sh            # first run installs everything into .venv, then starts the app
```

### Mobile (Android / iPhone)
StockAI is a web app, so the phone just needs a browser. Choose one:

**A. Same Wi-Fi as your PC (free, quickest)**
1. Start StockAI on your PC (`run_windows.bat` / `./run.sh`). The console prints a line like
   `On your phone (Wi-Fi): http://192.168.1.23:8501`.
2. Open that address in Chrome (Android) or Safari (iPhone).
3. Install it on your home screen:
   - **Android / Chrome:** menu ⋮ → **Add to Home screen** → *Install*.
   - **iPhone / Safari:** Share ⬆ → **Add to Home Screen** → *Add*.

   It now opens from its own icon like an app. The PC must be on for it to work.

**B. Anywhere, 24×7 (free cloud hosting)**
1. Go to <https://share.streamlit.io>, sign in with GitHub and click **Create app**.
2. Choose this repository, the branch, and main file `app.py`, then **Deploy**. You get a link like
   `https://your-app.streamlit.app`.
3. Open the link on your phone and **Add to Home Screen** as above. This works on mobile data too.
   (Yahoo Finance sometimes rate-limits cloud servers; if quotes fail, use option A or a paid host.)

**C. Any Docker host** (Render, Railway, Hugging Face Spaces, a VPS): `docker build -t stockai . && docker run -p 8501:8501 stockai`.

## 💡 Suggestion box (stock picks by time frame)
The default screen scans **83 liquid NSE stocks across 13 sectors** in about 20–60 seconds and suggests
the best buys for **intraday, next week, 1 month, 6 months and 1 year**:

- **Top picks per time frame** with **Buy below** (entry), **Target** (sell) and **Stop-loss**, plus profit %,
  risk:reward, probability of a rise and model confidence. Live prices refresh automatically, and each pick
  shows its status: ✅ in buy zone, 🟡 slightly above, ⏳ ran up (wait for a dip), 🎯 target reached or 🛑 below stop.
- **Best stock in each sector**, with the sector's overall mood.
- **Avoid / sell list** for each time frame.
- **⭐ Strong on every time frame**: stocks rated BUY on the most horizons.
- **Deep-dive** any pick with one click (news, fundamentals, holders, order book).

The scan uses price and volume only: 20+ indicators, buyer-vs-seller flow, walk-forward-tested ML, Monte Carlo
simulation and damped trend models. The deep-dive adds news, fundamentals and analyst views, so its call can
differ. Rely on the deep-dive for 6-month and 1-year decisions. Filters: sectors, number of picks, and minimum
daily turnover (screens out illiquid stocks). Scans are cached for 30 minutes; **Run fresh scan** re-runs one.

## Real-time features
- **Live price header**: price, change, day range and intraday momentum refresh automatically.
  Pick the interval (5 s to 60 s, or Off) under **⚡ Live refresh** in the sidebar.
- **⚡ Live tab**: today's 1-minute candles with VWAP, today's target/stop-loss lines, and momentum
  signals (VWAP, EMA 9/21, RSI, last-5-bar direction). It also tells you when the target or stop is hit.
- **Live watchlist** (also on the home screen): add or remove stocks; prices and signals refresh together.
- **Price alerts**: "notify me when X goes above/below ₹Y". A pop-up appears while the app is open.
- **Market clock**: NSE/BSE open, pre-open or closed in IST, with a countdown. Exchange holidays are not modelled.
- **Demo mode** streams simulated ticks, so every live feature can be tried offline or after hours.

### Live data sources (pick one in the sidebar)
| Source | Account needed | Speed | Extras |
|---|---|---|---|
| **NSE India** (default) | none | near real-time (seconds) | order book (5 levels), pending buy/sell quantity, **delivery %** |
| Yahoo Finance | none | delayed ~1–15 min | works for BSE too |
| Zerodha Kite | Zerodha + Kite Connect subscription | real-time ticks | full depth, WebSocket streaming |

**No broker account?** Use **NSE India**. It reads the same public data the nseindia.com quote page shows.
It is unofficial: NSE may rate-limit it or block cloud servers, and when that happens StockAI quietly falls
back to Yahoo. It works best when StockAI runs on your own PC. BSE-only symbols use Yahoo.

**Delivery %** is the share of today's traded shares actually taken into demat accounts. Above ~50% means real
investor buying; below ~30% means mostly intraday speculation. It's a good check on whether a price move is
"real".

## Zerodha Kite real-time data
With Kite connected, StockAI uses your Zerodha API for:
- **Live prices**, tick by tick over WebSocket, with REST polling as fallback.
- **Live order book**: top 5 bids/asks, total pending buy vs sell quantity ("who is stronger right now"),
  average traded price and circuit limits.
- **1-minute intraday candles** and **5 years of daily history** for every prediction, so Yahoo
  is no longer needed for prices. Yahoo and Google News are still used, best-effort, for company info,
  holders and news.

StockAI only reads data. It never places orders.

### One-time setup
1. You need a Zerodha trading account. Go to <https://developers.kite.trade>, sign up and click
   **Create new app** (type *Connect*). Kite Connect is a paid API subscription with market data;
   check the current price on that site.
2. In the app form set **Redirect URL** to the address you open StockAI on:
   - on your PC: `http://localhost:8501`
   - on Streamlit Cloud: `https://your-app.streamlit.app`
3. Copy the **API key** and **API secret** into StockAI in one of these ways:
   - copy `.streamlit/secrets.toml.example` to `.streamlit/secrets.toml` and fill it in, or
   - on Streamlit Cloud, paste the same lines into **App settings → Secrets**, or
   - type them into **🔑 Kite API settings** in the sidebar (used for this session only).

### Every trading day
Zerodha access tokens expire each morning (around 6 AM). In the sidebar choose
**Live data source → Zerodha Kite (real-time)** and click **🔐 Log in with Zerodha**. Log in on Zerodha's page and you
are sent back to StockAI, which shows *Zerodha Kite connected*. The day's token is saved in
`.kite_token.json` next to the app. Phones on the same Wi-Fi then use the PC's connection without
logging in again.

Notes:
- Kite allows about 1 quote request per second. StockAI batches the watchlist into one call and uses
  WebSocket ticks where possible. Keep the refresh at 5 s or slower.
- The WebSocket uses one of the 3 connections Kite allows per API key.
- BSE scrip codes (e.g. `500325`) are mapped to Kite symbols automatically.
- If the token is rejected (expired or revoked), the sidebar shows **Log in again**.

## What it analyses

| Tab | Contents |
|---|---|
| 🧭 Verdict | Buy/Hold/Sell per horizon, predicted price & range, entry, target, stop-loss, profit margin, risk:reward, probability of a rise, model confidence, and the pillar breakdown behind each call |
| 📈 Charts | Candlesticks with SMA/EMA/Bollinger/Supertrend, volume, RSI, MACD; 5-minute intraday chart with VWAP |
| 🔮 Predictions | Forecast table for all horizons, Monte Carlo fan chart, each ML model's prediction and walk-forward accuracy, trend models, feature importance |
| 🧮 Technicals | 18 indicator votes, live readings, crossovers/breakouts, candlestick patterns, pivot points, Fibonacci levels, support/resistance zones |
| 📰 News & sentiment | Latest news (Google News India + Yahoo Finance) scored with VADER NLP + a finance lexicon |
| 👥 Buyers & interest | Buyers vs sellers (up/down volume, Chaikin money flow, OBV, intraday VWAP), large-volume (block/institutional) days, public interest (volume surge, turnover, news flow, optional Google Trends) |
| 🏦 Fundamentals & holders | Valuation/quality scorecard (P/E, P/B, ROE, D/E, margins, growth, dividend), analyst consensus & targets, promoter / institutional / public split, top institutional & mutual-fund holders, insider transactions, broker upgrades/downgrades |

## How predictions are made

For each horizon (1, 5, 21, 126 and 252 trading days):

1. **Machine learning ensemble** — Ridge regression, Random Forest and Gradient Boosting trained on
   25 features (returns, moving-average distances, RSI, MACD, Bollinger, ADX, money flow, volatility,
   volume, market/index relative strength). Each is validated with walk-forward time-series CV and
   weighted by how much it beat a coin flip out of sample.
2. **Monte Carlo** — 5,000 paths mixing GBM with EWMA volatility and historical bootstrap → price
   percentiles and probabilities of rising / moving ±5%.
3. **Trend models** — log-linear regression slope and Holt's double exponential smoothing.

These are blended into a predicted price. The final call combines six pillars (technical, ML,
order-flow, news sentiment, fundamentals, analysts) with horizon-specific weights — short horizons
lean on technicals & flow, the 6-month view on fundamentals & trend. Stop-losses are ATR-based.

## Project layout

```
app.py                   Streamlit UI
stockai/live.py          market clock, live quotes, intraday momentum, watchlist, alerts
stockai/kite.py          Zerodha Kite Connect: login, quotes, candles, market depth, WebSocket ticks
stockai/nse.py           free NSE India quotes, order book and delivery % (no account)
stockai/screener.py      suggestion box: sector scan, ranking, top picks per time frame
stockai/universe.py      sector-wise stock universe
stockai/data.py          symbol resolution, Yahoo Finance prices/info/holders, news, demo data
stockai/indicators.py    technical indicators, levels, patterns, buyer/seller pressure
stockai/forecast.py      ML ensemble, Monte Carlo, trend models, intraday outlook
stockai/sentiment.py     news sentiment, public interest, Google Trends
stockai/fundamentals.py  fundamental score, analyst view, ownership
stockai/signals.py       technical scoring and final trade plans
tests/                   pytest suite (runs offline on demo data)
```

Run tests with `pytest -q`.

## Data notes

- Without Kite, prices come from Yahoo Finance (`SYMBOL.NS` for NSE, `SYMBOL.BO` / scrip code for BSE) and can be
  delayed by a few minutes. Press **Refresh live data** to re-fetch (results are cached for 5 minutes).
- Shareholding data from Yahoo is coarser than the exchange's quarterly shareholding-pattern filings
  (promoter / FII / DII). Check NSE/BSE filings for the official split.
