import numpy as np
import pandas as pd
import pytest

from stockai import data, ranker
from stockai.forecast import HORIZONS


@pytest.fixture(scope="module")
def panel():
    hist = {c: data.demo_bundle(c, "NSE").daily for c in ("AAA", "BBB", "CCC", "DDD", "EEE", "FFF")}
    return ranker.build_panel(hist, data.synthetic_history(seed=99, start_price=20000))


def test_training_never_sees_the_future(panel):
    days = np.sort(panel["Date"].unique())
    day = days[-300]
    for h in HORIZONS.values():
        tr = ranker._train_rows(panel, h, day)
        assert not tr.empty
        # every training sample's outcome (h trading days later) must already be known on `day`
        last_outcome = days[np.searchsorted(days, tr["Date"].max()) + h]
        assert last_outcome <= day


def test_rank_latest_and_walk_forward(panel):
    r = ranker.rank_latest(panel)
    assert len(r.table) == 6
    pct = r.table["pct Next week"]
    assert pct.between(0, 1).all() and pct.max() == 1
    dates = list(pd.date_range(panel["Date"].max() - pd.DateOffset(months=4), periods=3, freq="BME"))
    res = ranker.walk_forward(panel, dates, top_n=2, horizons=["Next week"])
    assert set(res["Horizon"]) == {"Next week"} and len(res) == 3
    summ = ranker.walk_forward_summary(res)
    assert list(summ.index) == ["Next week"]
