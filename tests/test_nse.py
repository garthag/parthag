import pandas as pd

from stockai import nse

QUOTE = {"priceInfo": {"lastPrice": 1452.3, "previousClose": 1420.0, "open": 1430.0, "vwap": 1444.1,
                       "intraDayHighLow": {"min": 1425.5, "max": 1458.0}, "upperCP": "1,562.00", "lowerCP": "1278.00"}}
TRADE = {"marketDeptOrderBook": {"totalBuyQuantity": 61000, "totalSellQuantity": 39000,
                                 "bid": [{"price": 1452.2, "quantity": 50}, {"price": 0, "quantity": 0}],
                                 "ask": [{"price": 1452.5, "quantity": 20}]},
         "securityWiseDP": {"deliveryToTradedQuantity": 47.31, "deliveryQuantity": 2100000}}
# 09:15 and 09:15:30 then 09:16 IST, encoded NSE-style as UTC epoch ms of the IST wall clock
T0 = int(pd.Timestamp("2026-10-29 09:15:00").value / 1e6)
CHART = {"grapthData": [[T0, 1430.0], [T0 + 30_000, 1433.0], [T0 + 60_000, 1431.0]]}


class FakeResp:
    def __init__(self, payload, status=200):
        self.payload, self.status_code = payload, status

    def json(self):
        return self.payload

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(self.status_code)


class FakeSession:
    def __init__(self):
        self.headers, self.calls, self.denied_once = {}, [], False

    def get(self, url, params=None, timeout=None):
        self.calls.append((url, params))
        if url == nse.BASE:
            return FakeResp({})
        if not self.denied_once:  # first API call: cookies "expired"
            self.denied_once = True
            return FakeResp({}, 401)
        if "chart" in url:
            return FakeResp(CHART)
        return FakeResp(TRADE if params.get("section") == "trade_info" else QUOTE)


def make():
    src = nse.NSESource(session=FakeSession())
    src.min_interval = 0
    return src


def test_quote_with_bars_and_depth():
    q = make().quote("RELIANCE.NS", with_depth=True)
    assert q.source == "NSE India" and q.price == 1452.3 and q.prev_close == 1420.0
    assert q.day_high == 1458.0 and q.day_low == 1425.5
    assert list(q.bars["Open"]) == [1430.0, 1431.0] and q.bars["High"].iloc[0] == 1433.0
    assert str(q.bars.index[0]) == "2026-10-29 09:15:00+05:30"
    assert q.extra["buy_qty"] == 61000 and q.extra["delivery_pct"] == 47.31
    assert q.extra["upper_circuit"] == 1562.0
    assert len(q.extra["depth_buy"]) == 1  # empty price levels dropped


def test_cookie_refresh_after_401():
    src = make()
    src.quote("TCS.NS", with_bars=False)
    homepage_hits = [c for c in src.s.calls if c[0] == nse.BASE]
    assert len(homepage_hits) == 2


def test_bse_symbols_rejected():
    import pytest
    with pytest.raises(ValueError):
        make().quote("500325.BO")
