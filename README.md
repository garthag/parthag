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

## Real-time features
- **Live price header**: price, change, day range and intraday momentum refresh automatically.
  Pick the interval (5 s to 60 s, or Off) under **⚡ Live refresh** in the sidebar.
- **⚡ Live tab**: today's 1-minute candles with VWAP, today's target/stop-loss lines, and momentum
  signals (VWAP, EMA 9/21, RSI, last-5-bar direction). It also tells you when the target or stop is hit.
- **Live watchlist** (also on the home screen): add or remove stocks; prices and signals refresh together.
- **Price alerts**: "notify me when X goes above/below ₹Y". A pop-up appears while the app is open.
- **Market clock**: NSE/BSE open, pre-open or closed in IST, with a countdown. Exchange holidays are not modelled.
- **Demo mode** streams simulated ticks, so every live feature can be tried offline or after hours.

Quotes come from Yahoo Finance and are typically delayed a few minutes versus the exchange. Exchange-grade
tick data needs a broker API (Zerodha Kite Connect, Upstox, Angel One SmartAPI, Fyers); `stockai/live.py` is
the place to plug one in.

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

For each horizon (1, 5, 21, 126 trading days):

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

- Prices come from Yahoo Finance (`SYMBOL.NS` for NSE, `SYMBOL.BO` / scrip code for BSE) and can be
  delayed by a few minutes. Press **Refresh live data** to re-fetch (results are cached for 5 minutes).
- Shareholding data from Yahoo is coarser than the exchange's quarterly shareholding-pattern filings
  (promoter / FII / DII). Check NSE/BSE filings for the official split.
