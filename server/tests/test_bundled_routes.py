"""アプリに同梱した経路（Assets/EkiNavi/Routes/*.json）の形を確かめる。

RouteData.cs（JsonUtility）が読むキーがそろっていること、点が2つ以上あること、
lengthMeters が点をつないだ長さと合うことを見る。
"""

from __future__ import annotations

import json
import math
from pathlib import Path

import pytest

from tests.conftest import REPO_DIR

ROUTES_DIR = REPO_DIR / "Assets" / "EkiNavi" / "Routes"
ROUTE_FILES = sorted(ROUTES_DIR.glob("*.json"))
# 階段の区間は、ネットワークの線の長さが点をつないだ長さより少し長い（2階のデモで 0.7 m）ので 1% まで許す
LENGTH_TOLERANCE_RATIO = 0.01


def test_routes_are_bundled() -> None:
    assert ROUTE_FILES, f"{ROUTES_DIR} に経路がありません"


@pytest.mark.parametrize("path", ROUTE_FILES, ids=lambda p: p.stem)
def test_route_has_the_keys_route_data_reads(path: Path) -> None:
    route = json.loads(path.read_text(encoding="utf-8"))

    assert {"origin", "start", "goal", "lengthMeters", "points"} <= route.keys()
    assert route["origin"] == {"lat": 35.69, "lon": 139.7}
    for end in ("start", "goal"):
        assert {"id", "name", "ordinal"} <= route[end].keys()
    assert len(route["points"]) >= 2
    for point in route["points"]:
        assert {"x", "z", "ordinal"} <= point.keys()
    for move in route.get("verticalMoves", []):
        assert move["kind"] in ("stairs", "escalator", "elevator")
        assert 0 <= move["fromIndex"] < move["toIndex"] < len(route["points"])


@pytest.mark.parametrize("path", ROUTE_FILES, ids=lambda p: p.stem)
def test_route_length_matches_the_points(path: Path) -> None:
    route = json.loads(path.read_text(encoding="utf-8"))
    points = route["points"]
    # 同じ階の区間だけを足す（階をまたぐ区間の長さは平面の距離では測れない）
    length = sum(
        math.dist((a["x"], a["z"]), (b["x"], b["z"]))
        for a, b in zip(points, points[1:])
        if a["ordinal"] == b["ordinal"]
    )
    if all(p["ordinal"] == points[0]["ordinal"] for p in points):
        assert abs(length - route["lengthMeters"]) <= route["lengthMeters"] * LENGTH_TOLERANCE_RATIO


def test_b1_odakyu_route_stays_on_the_basement() -> None:
    route = json.loads((ROUTES_DIR / "b1_9_10_to_odakyu.json").read_text(encoding="utf-8"))

    assert {p["ordinal"] for p in route["points"]} == {-1.0}
    assert route["start"]["ordinal"] == route["goal"]["ordinal"] == -1.0
    # 手でなぞった道筋の長さ（9・10番線のエスカレーター → 小田急線のりば）
    assert route["lengthMeters"] == pytest.approx(162.8, abs=0.1)
