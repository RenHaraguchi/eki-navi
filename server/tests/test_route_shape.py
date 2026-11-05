"""経路の点列の整形を確かめる。"""

from __future__ import annotations

import math

from app.route_shape import (
    RoutePoint,
    VerticalMove,
    find_turns,
    polyline_length,
    simplify,
    trim_end,
)


def _line(*coords: tuple[float, float]) -> list[RoutePoint]:
    return [RoutePoint(x, z, 0.0) for x, z in coords]


def test_simplify_removes_points_on_a_straight_line() -> None:
    points = _line((0, 0), (0, 5), (0, 10))

    simplified, _ = simplify(points, [], 1.0)

    assert simplified == [points[0], points[2]]


def test_simplify_keeps_a_real_corner() -> None:
    points = _line((0, 0), (0, 10), (10, 10))

    simplified, _ = simplify(points, [], 1.0)

    assert simplified == points


def test_simplify_keeps_vertical_move_points_and_renumbers_them() -> None:
    points = _line((0, 0), (0, 1), (0, 2), (0, 3), (0, 4))
    moves = [VerticalMove("elevator", 2, 3)]

    simplified, new_moves = simplify(points, moves, 1.0)

    assert simplified == [points[0], points[2], points[3], points[4]]
    assert new_moves == [VerticalMove("elevator", 1, 2)]


def test_trim_end_stops_before_the_goal() -> None:
    points = _line((0, 0), (0, 10))

    trimmed, _ = trim_end(points, [], 2.0)

    assert math.isclose(trimmed[-1].z, 8.0)


def test_trim_end_walks_back_over_short_segments() -> None:
    points = _line((0, 0), (0, 10), (0, 11))

    trimmed, _ = trim_end(points, [], 2.0)

    assert len(trimmed) == 2
    assert math.isclose(trimmed[-1].z, 9.0)


def test_trim_end_does_not_cut_into_a_vertical_move() -> None:
    points = _line((0, 0), (0, 1), (0, 2))
    moves = [VerticalMove("elevator", 1, 2)]

    trimmed, _ = trim_end(points, moves, 5.0)

    assert trimmed == points


def test_find_turns_uses_compass_directions() -> None:
    right = _line((0, 0), (0, 10), (10, 10))
    left = _line((0, 0), (0, 10), (-10, 10))

    assert find_turns(right, [], 30.0)[0].direction == "right"
    assert find_turns(left, [], 30.0)[0].direction == "left"
    assert find_turns(right, [], 30.0)[0].distance_from_start == 10.0


def test_find_turns_ignores_small_bends() -> None:
    points = _line((0, 0), (0, 10), (1, 20))

    assert find_turns(points, [], 30.0) == []


def test_find_turns_ignores_an_elevator() -> None:
    # エレベーターは水平に動かないので、前後で偽の曲がり角が出やすい
    points = [
        RoutePoint(0, 0, 2.0),
        RoutePoint(0, 10, 2.0),
        RoutePoint(0, 10, 3.0),
        RoutePoint(10, 10, 3.0),
    ]
    moves = [VerticalMove("elevator", 1, 2)]

    assert find_turns(points, moves, 30.0) == []


def test_find_turns_keeps_real_corners_around_a_move() -> None:
    points = _line((0, 0), (0, 10), (10, 10), (10, 20), (20, 20))
    moves = [VerticalMove("stairs", 2, 3)]

    turns = find_turns(points, moves, 30.0)

    assert [turn.point_index for turn in turns] == [1]


def test_find_turns_continues_on_a_slope() -> None:
    # 坂は水平にも進むので、曲がり角は判定する
    points = _line((0, 0), (0, 10), (10, 10))
    moves = [VerticalMove("slope", 0, 2)]

    assert len(find_turns(points, moves, 30.0)) == 1


def test_polyline_length() -> None:
    assert polyline_length(_line((0, 0), (3, 4), (3, 10))) == 11.0
