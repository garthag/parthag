# parthag — StockAI: NSE / BSE Stock Predictor & Deep Analyser

A Streamlit web app that analyses any Indian stock on **NSE or BSE** and predicts its movement
**intraday, next week, 1 month and 6 months**, with a **Buy / Hold / Sell** call, target,
stop-loss and expected profit margin for each horizon.

> ⚠️ Educational tool, **not investment advice**. Forecasts are statistical estimates; markets are
> uncertain. Do your own research or consult a SEBI-registered adviser.

## Quick start

```bash
pip install -r requirements.txt
streamlit run app.py
```

Open http://localhost:8501, choose **NSE** or **BSE**, pick a popular stock or type a name / symbol
/ BSE scrip code (e.g. `RELIANCE`, `Infosys`, `500325`) and click **Analyse**.
Switch on **Demo mode** to try the app offline with synthetic data.

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
