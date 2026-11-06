"""新宿駅周辺屋内地図オープンデータ（Shapefile）から経路を探索する。

歩行空間ネットワーク（nw/Shinjuku_node, nw/Shinjuku_link）をグラフにし、
改札（各駅の *_Opening.shp のうち名前に「改札」を含むもの）同士の最短経路を返す。

座標の共通基準（チームで決めたもの）
- 単位はメートル
- 原点は ORIGIN_LAT / ORIGIN_LON に固定（新宿駅の中央付近。仮の値）
- 軸は Unity に合わせて x = 東、z = 北、y = 上
- 階層は ordinal（0 = 地上階、-1 = 地下1階、2 = 2階 …。データの値をそのまま使う）
"""

from __future__ import annotations

import glob
import itertools
import math
import os
import warnings
from dataclasses import dataclass, field

import networkx as nx
import shapefile

# 固定原点（仮）。チームで決め直したら、ここだけ変える。
ORIGIN_LAT = 35.6900
ORIGIN_LON = 139.7000

# 歩行空間ネットワークデータの「方向性」: 1 = 両方向, 2 = 起点→終点, 3 = 終点→起点
DIRECTION_FORWARD_ONLY = "2"
DIRECTION_BACKWARD_ONLY = "3"

# 「経路の構造」: 4 = エレベーター, 5 = エスカレーター, 6 = 階段
ROUTE_TYPE_NAMES = {"4": "elevator", "5": "escalator", "6": "stairs"}


def _meters_per_degree(lat_deg: float) -> tuple[float, float]:
    """緯度 lat_deg における、経度1度・緯度1度あたりのメートル数を返す。"""
    phi = math.radians(lat_deg)
    per_lat = 111132.92 - 559.82 * math.cos(2 * phi) + 1.175 * math.cos(4 * phi)
    per_lon = 111412.84 * math.cos(phi) - 93.5 * math.cos(3 * phi)
    return per_lon, per_lat


_PER_LON, _PER_LAT = _meters_per_degree(ORIGIN_LAT)


def to_local(lon: float, lat: float) -> tuple[float, float]:
    """経度・緯度を、固定原点からの (x = 東, z = 北) メートルに変換する。"""
    return (lon - ORIGIN_LON) * _PER_LON, (lat - ORIGIN_LAT) * _PER_LAT


@dataclass
class Gate:
    id: str  # 例: "JR新宿駅:南改札"
    station: str
    name: str
    ordinal: float
    # 同じ改札に開口部が複数あることがあるので、位置は複数持つ
    positions: list[tuple[float, float]] = field(default_factory=list)  # (x, z)


@dataclass
class RoutePoint:
    x: float
    z: float
    ordinal: float


@dataclass
class Route:
    start: Gate
    goal: Gate
    length_m: float
    points: list[RoutePoint]
    # 経路の途中にある階段・エスカレーター・エレベーター（points のインデックス範囲）
    vertical_moves: list[dict]


def _reader(path: str, encoding: str) -> shapefile.Reader:
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        return shapefile.Reader(path, encoding=encoding, encodingErrors="replace")


def _station_name(folder: str) -> str:
    # "3.JR新宿駅" → "JR新宿駅"
    return folder.split(".", 1)[1] if "." in folder else folder


class StationNetwork:
    def __init__(self, data_root: str):
        """data_root は「新宿駅周辺屋内地図オープンデータ（Shapefile）」フォルダ。"""
        self.data_root = data_root
        self.graph = nx.DiGraph()
        self.nodes: dict[str, tuple[float, float, float]] = (
            {}
        )  # node_id → (x, z, ordinal)
        self.gates: dict[str, Gate] = {}
        self._load_network()
        self._load_gates()

    # ---- 読み込み ----

    def _load_network(self) -> None:
        nw = os.path.join(self.data_root, "nw")
        for rec in _reader(os.path.join(nw, "Shinjuku_node"), "cp932").records():
            x, z = to_local(rec["lon"], rec["lat"])
            self.nodes[rec["node_id"]] = (x, z, float(rec["ordinal"]))

        links = _reader(os.path.join(nw, "Shinjuku_link"), "cp932")
        for rec, shape in zip(links.records(), links.shapes(), strict=True):
            a, b = rec["start_id"], rec["end_id"]
            pts = [to_local(lon, lat) for lon, lat in shape.points]
            attrs = {
                "length": float(rec["distance"]),
                "route_type": rec["route_type"],
            }
            if rec["direction"] != DIRECTION_BACKWARD_ONLY:
                self.graph.add_edge(a, b, pts=pts, **attrs)
            if rec["direction"] != DIRECTION_FORWARD_ONLY:
                self.graph.add_edge(b, a, pts=list(reversed(pts)), **attrs)

    def _load_gates(self) -> None:
        floor_ordinal: dict[str, float] = {}
        for path in glob.glob(
            os.path.join(self.data_root, "**", "*_Floor.shp"), recursive=True
        ):
            for rec in _reader(path, "utf-8").records():
                floor_ordinal[rec["id"]] = float(rec["ordinal"])

        for path in glob.glob(
            os.path.join(self.data_root, "**", "*_Opening.shp"), recursive=True
        ):
            station = _station_name(
                os.path.relpath(path, self.data_root).split(os.sep)[0]
            )
            r = _reader(path, "utf-8")
            for rec, shape in zip(r.records(), r.shapes(), strict=True):
                name = rec["name"] or ""
                if "改札" not in name or not shape.points:
                    continue
                lon = sum(p[0] for p in shape.points) / len(shape.points)
                lat = sum(p[1] for p in shape.points) / len(shape.points)
                gate_id = f"{station}:{name}"
                gate = self.gates.get(gate_id)
                if gate is None:
                    gate = Gate(
                        gate_id, station, name, floor_ordinal.get(rec["floor_id"], 0.0)
                    )
                    self.gates[gate_id] = gate
                gate.positions.append(to_local(lon, lat))

    # ---- 探索 ----

    def nearest_node(self, x: float, z: float, ordinal: float) -> tuple[str, float]:
        """同じ階のノードのうち、(x, z) にいちばん近いものと、その距離を返す。"""
        best = min(
            (
                (math.hypot(nx_ - x, nz - z), nid)
                for nid, (nx_, nz, o) in self.nodes.items()
                if o == ordinal
            ),
            default=None,
        )
        if best is None:
            raise ValueError(f"階 {ordinal} に通路のノードがありません")
        return best[1], best[0]

    def route_between_gates(self, start_id: str, goal_id: str) -> Route:
        start, goal = self.gates[start_id], self.gates[goal_id]
        best: tuple[float, list[str], tuple, tuple] | None = None
        # 開口部が複数ある改札は、いちばん短くなる組み合わせを選ぶ
        for sp in start.positions:
            s_node, s_gap = self.nearest_node(*sp, start.ordinal)
            for gp in goal.positions:
                g_node, g_gap = self.nearest_node(*gp, goal.ordinal)
                try:
                    path = nx.shortest_path(self.graph, s_node, g_node, weight="length")
                except nx.NetworkXNoPath:
                    continue
                length = nx.path_weight(self.graph, path, "length") + s_gap + g_gap
                if best is None or length < best[0]:
                    best = (length, path, sp, gp)
        if best is None:
            raise ValueError(f"{start_id} から {goal_id} への経路が見つかりません")

        length, path, sp, gp = best
        points: list[RoutePoint] = []

        def add(p: RoutePoint) -> None:
            # 重なっている点は足さない
            if points:
                q = points[-1]
                if math.hypot(p.x - q.x, p.z - q.z) <= 0.05 and p.ordinal == q.ordinal:
                    return
            points.append(p)

        add(RoutePoint(sp[0], sp[1], start.ordinal))
        vertical_moves = []
        for a, b in itertools.pairwise(path):
            edge = self.graph[a][b]
            first = len(points) - 1
            ordinal_a, ordinal_b = self.nodes[a][2], self.nodes[b][2]
            n = len(edge["pts"]) - 1
            for i, (x, z) in enumerate(edge["pts"][1:], start=1):
                # リンクの途中の点は、両端の階のあいだで補間する
                add(RoutePoint(x, z, ordinal_a + (ordinal_b - ordinal_a) * i / n))
            kind = ROUTE_TYPE_NAMES.get(edge["route_type"])
            if kind:
                vertical_moves.append(
                    {"kind": kind, "from_index": first, "to_index": len(points) - 1}
                )
        add(RoutePoint(gp[0], gp[1], goal.ordinal))
        return Route(start, goal, length, points, vertical_moves)
