"""経路の点列を、アプリで描きやすい形に整える。"""

from __future__ import annotations

import math
from dataclasses import dataclass, replace

from app.vocab import TurnDirection

# 階が変わる坂。水平にも進むので、曲がり角の判定は続ける
SLOPE_KIND = "slope"
# これより短い線分は向きが安定しないので、曲がり角の判定に使わない
MIN_SEGMENT_METERS = 0.5


@dataclass(frozen=True)
class RoutePoint:
    """経路の点。座標は固定原点からのメートルで、x = 東、z = 北。"""

    x: float
    z: float
    ordinal: float


@dataclass(frozen=True)
class VerticalMove:
    """階段、エスカレーター、エレベーターの区間。添字は点列の位置。"""

    kind: str
    from_index: int
    to_index: int


@dataclass(frozen=True)
class Turn:
    """曲がり角。angle_deg は曲がる角度で、direction は "left" か "right"。"""

    point_index: int
    direction: TurnDirection
    angle_deg: float
    distance_from_start: float


def _distance(a: RoutePoint, b: RoutePoint) -> float:
    return math.hypot(a.x - b.x, a.z - b.z)


def _offset_from_segment(p: RoutePoint, a: RoutePoint, b: RoutePoint) -> float:
    """線分 ab から点 p までの距離を返す。"""
    dx, dz = b.x - a.x, b.z - a.z
    length_sq = dx * dx + dz * dz
    if length_sq == 0:
        return _distance(p, a)
    t = max(0.0, min(1.0, ((p.x - a.x) * dx + (p.z - a.z) * dz) / length_sq))
    return math.hypot(p.x - (a.x + t * dx), p.z - (a.z + t * dz))


def _keep_between(
    points: list[RoutePoint], first: int, last: int, tolerance: float
) -> set[int]:
    """Douglas-Peucker 法で、first と last の間に残す点の添字を返す。"""
    if last - first < 2:
        return set()
    offsets = [
        (_offset_from_segment(points[i], points[first], points[last]), i)
        for i in range(first + 1, last)
    ]
    worst, index = max(offsets)
    if worst <= tolerance:
        return set()
    return (
        {index}
        | _keep_between(points, first, index, tolerance)
        | _keep_between(points, index, last, tolerance)
    )


def simplify(
    points: list[RoutePoint], moves: list[VerticalMove], tolerance: float
) -> tuple[list[RoutePoint], list[VerticalMove]]:
    """細かい折れを間引く。階が変わる区間の両端は必ず残す。

    Args:
        points: 元の点列。
        moves: 元の階の移動。
        tolerance: 間引いてよい横方向のずれ（メートル）。

    Returns:
        間引いた点列と、添字を振り直した階の移動。
    """
    anchors = sorted(
        {0, len(points) - 1}
        | {i for move in moves for i in (move.from_index, move.to_index)}
    )
    kept = set(anchors)
    for first, last in zip(anchors, anchors[1:], strict=False):
        kept |= _keep_between(points, first, last, tolerance)
    order = sorted(kept)
    position = {old: new for new, old in enumerate(order)}
    return (
        [points[i] for i in order],
        [
            replace(
                move,
                from_index=position[move.from_index],
                to_index=position[move.to_index],
            )
            for move in moves
        ],
    )


def trim_end(
    points: list[RoutePoint], moves: list[VerticalMove], margin: float
) -> tuple[list[RoutePoint], list[VerticalMove]]:
    """線の終わりを margin メートル手前で止める（改札機に線が重ならないように）。

    Args:
        points: 点列。
        moves: 階の移動。
        margin: 手前で止める距離。

    Returns:
        短くした点列と、範囲を収めた階の移動。階の移動の途中までは短くしない。
    """
    last_move_end = max((move.to_index for move in moves), default=0)
    result = list(points)
    remaining = margin
    while remaining > 0 and len(result) - 1 > max(last_move_end, 0) + 1:
        length = _distance(result[-2], result[-1])
        if length > remaining:
            break
        remaining -= length
        result.pop()
    if len(result) - 1 > last_move_end:
        a, b = result[-2], result[-1]
        length = _distance(a, b)
        if length > remaining > 0:
            t = (length - remaining) / length
            result[-1] = replace(b, x=a.x + (b.x - a.x) * t, z=a.z + (b.z - a.z) * t)
    return result, moves


def _inside_moves(moves: list[VerticalMove]) -> set[int]:
    """階段などの区間に含まれる線分の番号を返す。番号 i は点 i から点 i+1 への線分。"""
    return {
        segment
        for move in moves
        if move.kind != SLOPE_KIND
        for segment in range(move.from_index, move.to_index)
    }


def find_turns(
    points: list[RoutePoint], moves: list[VerticalMove], threshold_deg: float
) -> list[Turn]:
    """曲がり角を探す。向きは Unity と同じ x = 東、z = 北で判定する。

    エレベーターは水平に動かないので、階段などの前後では向きが決まらない。
    そこで曲がり角と言えない場所を除くために、階の移動に接する点は判定しない。

    Args:
        points: 点列。
        moves: 階の移動。
        threshold_deg: 曲がり角とみなす最小の角度。

    Returns:
        曲がり角の一覧。
    """
    skipped = _inside_moves(moves)
    turns: list[Turn] = []
    along = 0.0
    for i in range(1, len(points) - 1):
        a, b, c = points[i - 1], points[i], points[i + 1]
        along += _distance(a, b)
        if i - 1 in skipped or i in skipped:
            continue
        if _distance(a, b) < MIN_SEGMENT_METERS or _distance(b, c) < MIN_SEGMENT_METERS:
            continue
        bearing_in = math.degrees(math.atan2(b.x - a.x, b.z - a.z))
        bearing_out = math.degrees(math.atan2(c.x - b.x, c.z - b.z))
        delta = (bearing_out - bearing_in + 180.0) % 360.0 - 180.0
        if abs(delta) < threshold_deg:
            continue
        direction: TurnDirection = "right" if delta > 0 else "left"
        turns.append(Turn(i, direction, round(abs(delta), 1), round(along, 1)))
    return turns


def polyline_length(points: list[RoutePoint]) -> float:
    """点列の水平の長さを返す。"""
    return sum(_distance(a, b) for a, b in zip(points, points[1:], strict=False))
