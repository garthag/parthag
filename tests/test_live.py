from datetime import datetime

from stockai import live


def test_market_status_sessions():
    ist = live.IST
    assert live.market_status(datetime(2026, 10, 29, 10, 0, tzinfo=ist))["open"]
    assert not live.market_status(datetime(2026, 10, 29, 16, 0, tzinfo=ist))["open"]
    assert "Pre-open" in live.market_status(datetime(2026, 10, 29, 9, 5, tzinfo=ist))["label"]
    sat = live.market_status(datetime(2026, 10, 31, 11, 0, tzinfo=ist))
    assert not sat["open"] and sat["detail"] == "opens in 1d 22h 15m"  # opens Monday 09:15


def test_demo_feed_ticks_and_momentum():
    feed = live.DemoFeed("TEST.NS", 1000.0, seed=1)
    n = len(feed.bars)
    q = feed.tick()
    assert len(q.bars) == n + 1
    assert q.day_low <= q.price <= q.day_high
    m = live.momentum(q.bars)
    assert -1 <= m["score"] <= 1 and m["vwap"] > 0


def test_alert_fires_once():
    a = live.Alert("X.NS", "above", 100)
    assert not a.check(99)
    assert a.check(101)
    assert not a.check(102)
    b = live.Alert("X.NS", "below", 100)
    assert b.check(95)
