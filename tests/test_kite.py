from datetime import datetime, timedelta

import pytest

from stockai import kite
from stockai.live import IST


class FakeKite:
    """Stands in for kiteconnect.KiteConnect, recording API calls."""

    def __init__(self):
        self.quote_calls = 0

    def instruments(self, exchange=None):
        return [{"exchange_token": 500325, "tradingsymbol": "RELIANCE", "segment": "BSE"}]

    def quote(self, *instruments):
        self.quote_calls += 1
        out = {}
        for i, key in enumerate(instruments):
            out[key] = {
                "instrument_token": 1000 + i, "last_price": 1450.5, "volume": 123456,
                "buy_quantity": 60000, "sell_quantity": 40000, "average_price": 1444.2,
                "upper_circuit_limit": 1595.0, "lower_circuit_limit": 1305.0,
                "ohlc": {"open": 1430.0, "high": 1455.0, "low": 1425.0, "close": 1420.0},
                "depth": {"buy": [{"price": 1450.4, "quantity": 120, "orders": 3}],
                          "sell": [{"price": 1450.6, "quantity": 80, "orders": 2}]},
            }
        return out

    def historical_data(self, token, from_date, to_date, interval):
        start = datetime(2026, 10, 29, 9, 15)
        return [{"date": start + timedelta(minutes=m), "open": 1430 + m, "high": 1431 + m,
                 "low": 1429 + m, "close": 1430.5 + m, "volume": 1000} for m in range(30)]


@pytest.fixture
def src():
    return kite.KiteSource("key", "token", client=FakeKite())


def test_symbol_mapping(src):
    assert src.to_kite("RELIANCE.NS") == "NSE:RELIANCE"
    assert src.to_kite("RELIANCE.BO") == "BSE:RELIANCE"
    assert src.to_kite("500325.BO") == "BSE:RELIANCE"
    with pytest.raises(ValueError):
        src.to_kite("AAPL")


def test_quote_conversion(src):
    q = src.quote("RELIANCE.NS")
    assert q.source == "Zerodha Kite"
    assert q.price == 1450.5 and q.prev_close == 1420.0
    assert q.change_pct == pytest.approx(100 * 30.5 / 1420)
    assert q.day_high == 1455.0 and q.day_low == 1425.0
    assert len(q.bars) == 30 and q.bars["Close"].iloc[-1] == 1450.5
    assert q.bars["High"].iloc[-1] >= 1450.5  # live tick widens the last candle
    assert q.extra["buy_qty"] == 60000 and q.extra["depth_sell"][0]["price"] == 1450.6


def test_watchlist_uses_one_quote_call(src):
    quotes = src.quote_many(["RELIANCE.NS", "TCS.NS", "INFY.NS"])
    assert set(quotes) == {"RELIANCE.NS", "TCS.NS", "INFY.NS"}
    assert src.kite.quote_calls == 1
    src.quote_many(["RELIANCE.NS", "TCS.NS"])  # served from the fresh snapshot
    assert src.kite.quote_calls == 1


def test_stream_tick_overrides_snapshot(src):
    class FakeStream:
        def latest(self, token):
            return {"last_price": 1460.0, "total_buy_quantity": 10, "total_sell_quantity": 90,
                    "_received": datetime.now(IST)}

        def subscribe(self, tokens):
            pass

    src.stream = FakeStream()
    q = src.quote("RELIANCE.NS")
    assert q.price == 1460.0 and q.extra["sell_qty"] == 90


def test_saved_token_expires_at_6am(tmp_path, monkeypatch):
    monkeypatch.setattr(kite, "TOKEN_FILE", tmp_path / "tok.json")
    kite.save_token("key", "abc")
    assert kite.load_saved_token("key") == "abc"
    assert kite.load_saved_token("other-key") is None
    old = datetime.now(IST) - timedelta(days=2)
    (tmp_path / "tok.json").write_text(
        '{"api_key": "key", "access_token": "abc", "issued": "%s"}' % old.isoformat())
    assert kite.load_saved_token("key") is None
