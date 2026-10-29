import numpy as np
import pytest

from stockai import data, forecast, fundamentals, indicators, sentiment, signals


@pytest.fixture(scope="module")
def analysis():
    return signals.analyze(data.load_stock("RELIANCE", "NSE", demo=True))


def test_resolve_symbol():
    assert data.resolve_symbol("reliance", "NSE") == "RELIANCE.NS"
    assert data.resolve_symbol("RELIANCE", "BSE") == "RELIANCE.BO"
    assert data.resolve_symbol("500325", "BSE") == "500325.BO"
    assert data.resolve_symbol("infosys", "NSE") == "INFY.NS"
    assert data.resolve_symbol("TCS.NS", "BSE") == "TCS.BO"


def test_indicators_ranges():
    ind = indicators.add_all(data.synthetic_history(seed=1))
    last = ind.iloc[-1]
    assert 0 <= last["RSI"] <= 100
    assert 0 <= last["STOCH_K"] <= 100
    assert last["BB_low"] < last["BB_mid"] < last["BB_up"]
    assert last["ATR"] > 0
    assert set(ind["ST_dir"].unique()) <= {1.0, -1.0}


def test_rsi_extremes():
    import pandas as pd
    up = pd.Series(np.arange(1, 60, dtype=float))
    assert indicators.rsi(up).iloc[-1] == pytest.approx(100)


def test_forecasts_cover_all_horizons(analysis):
    assert set(analysis.forecasts) == set(forecast.HORIZONS)
    for fc in analysis.forecasts.values():
        assert fc.low < fc.high
        assert 0 < fc.prob_up < 1
        assert 0 <= fc.confidence <= 1
        for s in fc.ml.scores.values():
            assert 0 <= s["dir_acc"] <= 1


def test_trade_plans_are_consistent(analysis):
    for plan in analysis.plans.values():
        assert plan.action in {"STRONG BUY", "BUY", "HOLD", "SELL", "STRONG SELL"}
        if plan.action in {"BUY", "STRONG BUY", "HOLD"}:
            assert plan.stop_loss < plan.entry
        else:
            assert plan.stop_loss > plan.entry
        fc = analysis.forecasts[plan.horizon]
        if plan.action.endswith("BUY"):
            assert not (fc.expected_return_pct < 0 and fc.prob_up < 0.5)


def test_monte_carlo_percentiles_ordered():
    close = data.synthetic_history(seed=3)["Close"]
    mc = forecast.monte_carlo(close, 21, n_paths=2000)
    p = mc["percentiles"]
    assert p[5] < p[25] < p[50] < p[75] < p[95]


def test_sentiment_lexicon():
    scored = sentiment.score_news([
        {"title": "Stock surges after brokerage upgrade", "published": None},
        {"title": "Shares plunge on fraud probe", "published": None},
    ])
    assert scored["sentiment"].iloc[0] > 0 > scored["sentiment"].iloc[1]


def test_fundamental_score_bounds():
    good = fundamentals.fundamental_score({"trailingPE": 12, "returnOnEquity": 0.25, "debtToEquity": 20})
    bad = fundamentals.fundamental_score({"trailingPE": 80, "returnOnEquity": 0.02, "debtToEquity": 300})
    assert good["score"] > bad["score"]
    assert -1 <= bad["score"] <= good["score"] <= 1
