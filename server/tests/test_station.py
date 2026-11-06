"""経路パックと経路探索を、実データで確かめる。"""

from __future__ import annotations

import math
from typing import Any

import pytest

from app.station import LINE_END_MARGIN_METERS, Station, StationError

WEST = "B1:JR東日本:西改札"
EAST = "B1:JR東日本:東改札"


def test_east_west_passage_has_a_network_link(station: Station) -> None:
    # 統合版にしかない東西自由通路のリンクが使えていること
    route = station.plan(EAST, from_gate_id=WEST)

    assert route.length_meters == pytest.approx(79.5, abs=1.0)
    assert [m.kind for m in route.moves] == []


def test_elevator_route_keeps_vertical_moves(station: Station) -> None:
    route = station.plan(
        "3F:バスタ新宿:バスタ新宿", from_gate_id="2F:JR東日本:新南口改札"
    )

    assert route.length_meters == pytest.approx(72.0, abs=1.0)
    assert {m.kind for m in route.moves} == {"elevator"}
    for move in route.moves:
        assert 0 <= move.from_index < move.to_index < len(route.points)


def test_every_gate_is_reachable_from_the_passage(station: Station) -> None:
    for gate_id in station.gates:
        assert station.route_length("S-B1-CENTER", gate_id) is not None, gate_id


def test_sign_snaps_to_the_passage_not_to_a_far_node(station: Station) -> None:
    # 東西自由通路のリンクは 56m あるので、ノードに吸着すると 20m 以上離れてしまう
    route = station.plan("B1:JR東日本:中央東改札", from_sign_id="S-B1-CENTER")

    assert route.start.offset_meters < 10.0
    first = route.points[0]
    assert 35.0 < first.x < 50.0


def test_line_stops_before_the_gate(station: Station) -> None:
    route = station.plan(EAST, from_gate_id=WEST)
    opening = route.goal_opening
    last = route.points[-1]

    distance = math.hypot(last.x - opening["x"], last.z - opening["z"])

    assert distance == pytest.approx(LINE_END_MARGIN_METERS, abs=0.05)


def test_curated_line_replaces_the_u_shaped_network_line(station: Station) -> None:
    route = station.plan(EAST, from_sign_id="S-B1-WEST")

    assert route.source == "curated"
    assert len(route.points) == 3


def test_same_gate_name_on_two_floors_stays_separate(station: Station) -> None:
    assert station.gates["B1:JR東日本:東改札"]["ordinal"] == -1
    assert station.gates["B2:東京メトロ:東改札"]["ordinal"] == -2


@pytest.mark.parametrize(
    ("kwargs", "code"),
    [
        ({"to_gate_id": "ない改札", "from_sign_id": "S-B1-WEST"}, "unknown_gate"),
        ({"to_gate_id": EAST, "from_sign_id": "ない案内板"}, "unknown_sign"),
        ({"to_gate_id": EAST, "from_gate_id": "ない改札"}, "unknown_gate"),
        ({"to_gate_id": EAST}, "invalid_request"),
        (
            {"to_gate_id": EAST, "from_sign_id": "S-B1-WEST", "from_gate_id": WEST},
            "invalid_request",
        ),
    ],
)
def test_plan_reports_a_code(
    station: Station, kwargs: dict[str, str], code: str
) -> None:
    with pytest.raises(StationError) as error:
        station.plan(**kwargs)

    assert error.value.code == code


def _small_pack(one_way: bool) -> dict[str, Any]:
    """A → B の1本だけの駅を作る。"""
    return {
        "version": "test",
        "origin": {"lat": 0.0, "lon": 0.0},
        "attribution": "",
        "nodes": [
            {"x": 0.0, "z": 0.0, "ordinal": 0.0},
            {"x": 0.0, "z": 10.0, "ordinal": 0.0},
        ],
        "links": [
            {
                "key": "a-b",
                "a": 0,
                "b": 1,
                "length": 10.0,
                "kind": "walk",
                "oneWay": one_way,
                "interior": [],
            }
        ],
        "gates": [
            {
                "id": gate_id,
                "name": gate_id,
                "operator": "",
                "ordinal": 0.0,
                "floor": "GL",
                "openings": [
                    {
                        "x": 0.0,
                        "z": z,
                        "node": node,
                        "gapMeters": 0.0,
                        "ax": 0.0,
                        "az": z,
                        "bx": 1.0,
                        "bz": z,
                    }
                ],
            }
            for gate_id, z, node in (("A", 0.0, 0), ("B", 10.0, 1))
        ],
        "signs": [],
        "lines": [],
        "curatedLines": [],
    }


def test_turns_never_fall_inside_a_vertical_move(station: Station) -> None:
    for sign_id in station.signs:
        for gate_id in station.gates:
            route = station.plan(gate_id, from_sign_id=sign_id)
            inside = {
                index
                for move in route.moves
                if move.kind != "slope"
                for index in range(move.from_index, move.to_index + 1)
            }
            turns = {turn.point_index for turn in route.turns}
            assert not turns & inside, (sign_id, gate_id)


def test_floor_change_on_a_walk_link_is_reported(station: Station) -> None:
    # 坂で階が変わるリンクがあり、階の移動として返すこと
    found = []
    for sign_id in station.signs:
        for gate_id in station.gates:
            route = station.plan(gate_id, from_sign_id=sign_id)
            found += [move for move in route.moves if move.kind == "slope"]
    assert found


def test_every_floor_change_is_inside_a_move(station: Station) -> None:
    for sign_id in station.signs:
        for gate_id in station.gates:
            route = station.plan(gate_id, from_sign_id=sign_id)
            covered = {
                index
                for move in route.moves
                for index in range(move.from_index, move.to_index)
            }
            for index, (a, b) in enumerate(
                zip(route.points, route.points[1:], strict=False)
            ):
                if a.ordinal != b.ordinal:
                    assert index in covered, (sign_id, gate_id, index)


def test_route_length_matches_the_curated_line(station: Station) -> None:
    gate_id = "B1:JR東日本:東改札"

    length = station.route_length("S-B1-WEST", gate_id)

    assert length == station.plan(gate_id, from_sign_id="S-B1-WEST").length_meters


def test_one_way_link_cannot_be_walked_backwards() -> None:
    station = Station(_small_pack(one_way=True))

    assert station.plan("B", from_gate_id="A").length_meters == 10.0
    with pytest.raises(StationError) as error:
        station.plan("A", from_gate_id="B")
    assert error.value.code == "no_route"


def test_two_way_link_can_be_walked_both_ways() -> None:
    station = Station(_small_pack(one_way=False))

    assert station.plan("A", from_gate_id="B").length_meters == 10.0


def test_same_floor_route_avoids_another_floor(station: Station) -> None:
    # 普通に探すと一度地上（0階）を通る組
    start, goal = "2F:JR東日本:新南口改札", "2F:JR東日本:東南改札"
    normal = station.plan(goal, from_gate_id=start)
    flat = station.plan(goal, from_gate_id=start, same_floor=True)

    assert {p.ordinal for p in normal.points} != {2.0}
    assert {p.ordinal for p in flat.points} == {2.0}
    assert not [m for m in flat.moves if m.kind != "stairs"]
    assert flat.length_meters >= normal.length_meters


def test_same_floor_route_from_the_demo_sign_stays_on_2f(station: Station) -> None:
    plan = station.plan(
        "2F:JR東日本:新南口改札", from_sign_id="S-2F-SOUTH", same_floor=True
    )

    assert {p.ordinal for p in plan.points} == {2.0}


def test_same_floor_route_to_another_floor_is_no_route(station: Station) -> None:
    with pytest.raises(StationError) as error:
        station.plan("B1:JR東日本:東改札", from_sign_id="S-2F-SOUTH", same_floor=True)

    assert error.value.code == "no_route"


def test_map_gates_are_on_the_floor(station: Station) -> None:
    names = {gate["name"] for gate in station.map_gates(2.0)}

    assert {"南改札", "新南口改札"} <= names
    assert "東改札" not in names
