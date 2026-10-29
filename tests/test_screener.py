from stockai import screener
from stockai.forecast import HORIZONS


def test_scan_ranks_and_filters():
    res = screener.scan("NSE", demo=True, sectors=["Banks", "Defence"], workers=2)
    assert set(res["Horizon"]) == set(HORIZONS)
    assert res["Code"].nunique() == 12
    for h in HORIZONS:
        top = screener.top_buys(res, h, n=3)
        assert len(top) <= 3
        assert top["Action"].isin(screener.BUY_ACTIONS).all()
        assert (top["Target"] > top["Price"]).all() and (top["Stop-loss"] < top["Price"]).all()
        assert top["Rank"].is_monotonic_decreasing
        lead = screener.sector_leaders(res, h)
        assert set(lead["Sector"]) == {"Banks", "Defence"} and len(lead) == 2
    cw = screener.consistent_winners(res, 5)
    assert len(cw) == 5 and cw["BUY horizons"].between(0, len(HORIZONS)).all()
