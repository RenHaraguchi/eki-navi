"""経路パックを読み込み、改札と案内板の間の経路を計算する。"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import networkx as nx

from app.route_shape import (
    SLOPE_KIND,
    RoutePoint,
    Turn,
    VerticalMove,
    find_turns,
    polyline_length,
    simplify,
    trim_end,
)
from app.vocab import RouteSource

# 間引いてよい横方向のずれ。線幅 0.4m より十分大きく、通路の幅より十分小さい値
SIMPLIFY_TOLERANCE_METERS = 1.0
# RouteRenderer の turnAngleThreshold と同じ値にそろえている
TURN_THRESHOLD_DEG = 30.0
# 線は改札の開口線の手前で終える（修正版の5章）
LINE_END_MARGIN_METERS = 2.0
# 経路上の案内板として返す範囲
SIGN_NEAR_ROUTE_METERS = 30.0

VERTICAL_KINDS = {"elevator", "escalator", "stairs"}


class StationError(Exception):
    """経路を計算できないときの例外。code は API のエラーコードになる。"""

    def __init__(self, code: str, message: str) -> None:
        """エラーコードと、画面に出せる説明を持たせる。"""
        super().__init__(message)
        self.code = code
        self.message = message


@dataclass(frozen=True)
class Start:
    """経路の出発点の候補。

    lead はネットワークのノードに入るまでの点列で、gap_meters はその長さ。
    offset_meters は案内板から通路の線までの距離で、経路の長さには含めない。
    """

    node: int
    gap_meters: float
    lead: tuple[RoutePoint, ...]
    offset_meters: float
    endpoint_id: str
    endpoint_name: str
    ordinal: float
    operator: str
    floor: str


@dataclass(frozen=True)
class PlannedRoute:
    """計算した経路。"""

    start: Start
    goal: dict[str, Any]
    goal_opening: dict[str, Any]
    length_meters: float
    source: RouteSource
    points: list[RoutePoint]
    moves: list[VerticalMove]
    turns: list[Turn]


class Station:
    """経路パックの内容と、経路探索のグラフを持つ。"""

    def __init__(self, pack: dict[str, Any]) -> None:
        """経路パックからグラフを作る。

        Args:
            pack: pack.build が作った内容。
        """
        self.pack = pack
        self.version: str = pack["version"]
        self.nodes: list[dict[str, Any]] = pack["nodes"]
        self.gates: dict[str, dict[str, Any]] = {g["id"]: g for g in pack["gates"]}
        self.signs: dict[str, dict[str, Any]] = {s["id"]: s for s in pack["signs"]}
        self.curated: dict[tuple[str, str], dict[str, Any]] = {
            (c["fromSignId"], c["toGateId"]): c for c in pack["curatedLines"]
        }
        self.graph = self._build_graph(pack["links"])
        self._floor_graphs: dict[float, nx.DiGraph] = {}
        self._sign_starts: dict[str, list[Start]] = {}

    @classmethod
    def load(cls, path: Path) -> Station:
        """ファイルから読み込む。

        Args:
            path: pack.json のパス。

        Returns:
            読み込んだ駅。
        """
        return cls(json.loads(path.read_text(encoding="utf-8")))

    @staticmethod
    def _build_graph(links: list[dict[str, Any]]) -> nx.DiGraph:
        graph = nx.DiGraph()
        for link in links:
            forward = {
                "length": link["length"],
                "kind": link["kind"],
                "interior": link["interior"],
            }
            graph.add_edge(link["a"], link["b"], **forward)
            if not link["oneWay"]:
                graph.add_edge(
                    link["b"],
                    link["a"],
                    **{**forward, "interior": list(reversed(link["interior"]))},
                )
        return graph

    def _floor_graph(self, ordinal: float) -> nx.DiGraph:
        """その階のノードどうしを結ぶリンクだけのグラフを返す（階を変えない経路用）。"""
        graph = self._floor_graphs.get(ordinal)
        if graph is None:
            graph = nx.subgraph_view(
                self.graph,
                filter_node=lambda node: self.nodes[node]["ordinal"] == ordinal,
            )
            self._floor_graphs[ordinal] = graph
        return graph

    def floors(self) -> list[float]:
        """ノードがある階の一覧を返す。"""
        return sorted({node["ordinal"] for node in self.nodes})

    def map_lines(self, ordinal: float) -> list[list[list[float]]]:
        """その階の通路の線を返す（2D 表示の下地）。

        Args:
            ordinal: 階番号。

        Returns:
            線の一覧。線は [x, z] の並び。
        """
        lines = []
        for link in self.pack["links"]:
            a, b = self.nodes[link["a"]], self.nodes[link["b"]]
            if a["ordinal"] != ordinal or b["ordinal"] != ordinal:
                continue
            lines.append([[a["x"], a["z"]], *link["interior"], [b["x"], b["z"]]])
        return lines

    def map_gates(self, ordinal: float) -> list[dict[str, Any]]:
        """その階の改札と、地図に名前を出す位置を返す。

        Args:
            ordinal: 階番号。

        Returns:
            改札の ID、名前、事業者、位置（開口の中心の平均）の一覧。
        """
        found = []
        for gate in self.gates.values():
            openings = gate["openings"]
            if gate["ordinal"] != ordinal or not openings:
                continue
            found.append(
                {
                    "id": gate["id"],
                    "name": gate["name"],
                    "operator": gate["operator"],
                    "x": round(sum(o["x"] for o in openings) / len(openings), 1),
                    "z": round(sum(o["z"] for o in openings) / len(openings), 1),
                }
            )
        return found

    def _nearest_on_links(
        self, x: float, z: float, ordinal: float
    ) -> tuple[dict[str, Any], int, RoutePoint, float]:
        """同じ階の通路の線で、点に最も近い位置を探す。

        東西自由通路のリンクは 56m あるので、ノードではなく線に投影する。

        Returns:
            リンク、線の中の区間の番号、投影した点、点からの距離。

        Raises:
            StationError: その階に通路がないとき。
        """
        best: tuple[dict[str, Any], int, RoutePoint, float] | None = None
        for link in self.pack["links"]:
            if link["kind"] in VERTICAL_KINDS:
                continue
            shape = self._link_shape(link)
            if shape[0].ordinal != ordinal or shape[-1].ordinal != ordinal:
                continue
            for index, (a, b) in enumerate(zip(shape, shape[1:], strict=False)):
                point = _project_on_segment(x, z, a, b)
                distance = math.hypot(point.x - x, point.z - z)
                if best is None or distance < best[3]:
                    best = (link, index, point, distance)
        if best is None:
            raise StationError("no_route", f"{ordinal} 階に通路がありません")
        return best

    def _link_shape(self, link: dict[str, Any]) -> list[RoutePoint]:
        a, b = self.nodes[link["a"]], self.nodes[link["b"]]
        return [
            RoutePoint(a["x"], a["z"], a["ordinal"]),
            *(RoutePoint(x, z, a["ordinal"]) for x, z in link["interior"]),
            RoutePoint(b["x"], b["z"], b["ordinal"]),
        ]

    def _starts_from_sign(self, sign_id: str) -> list[Start]:
        cached = self._sign_starts.get(sign_id)
        if cached is not None:
            return cached
        sign = self.signs.get(sign_id)
        if sign is None:
            raise StationError("unknown_sign", "案内板が見つかりません")
        link, index, point, offset = self._nearest_on_links(
            sign["x"], sign["z"], sign["ordinal"]
        )
        shape = self._link_shape(link)
        # 投影した点からリンクの両端へ向かう2通りを候補にする
        toward_b = (point, *shape[index + 1 :])
        starts = [self._sign_start(sign, link["b"], toward_b, offset)]
        if not link["oneWay"]:
            toward_a = (point, *reversed(shape[: index + 1]))
            starts.append(self._sign_start(sign, link["a"], toward_a, offset))
        self._sign_starts[sign_id] = starts
        return starts

    @staticmethod
    def _sign_start(
        sign: dict[str, Any],
        node: int,
        lead: tuple[RoutePoint, ...],
        offset: float,
    ) -> Start:
        return Start(
            node=node,
            gap_meters=polyline_length(list(lead)),
            lead=lead,
            offset_meters=round(offset, 1),
            endpoint_id=sign["id"],
            endpoint_name=sign["name"],
            ordinal=sign["ordinal"],
            operator="",
            floor="",
        )

    def _gate(self, gate_id: str) -> dict[str, Any]:
        gate = self.gates.get(gate_id)
        if gate is None:
            raise StationError("unknown_gate", "改札が見つかりません")
        return gate

    def _starts_from_gate(self, gate_id: str) -> list[Start]:
        gate = self._gate(gate_id)
        return [
            Start(
                node=opening["node"],
                gap_meters=opening["gapMeters"],
                lead=(RoutePoint(opening["x"], opening["z"], gate["ordinal"]),),
                offset_meters=0.0,
                endpoint_id=gate_id,
                endpoint_name=gate["name"],
                ordinal=gate["ordinal"],
                operator=gate["operator"],
                floor=gate["floor"],
            )
            for opening in gate["openings"]
        ]

    def _search(
        self, starts: list[Start], gate: dict[str, Any], same_floor: bool = False
    ) -> tuple[float, Start, dict[str, Any], list[int]]:
        best: tuple[float, Start, dict[str, Any], list[int]] | None = None
        for start in starts:
            graph = self.graph
            if same_floor:
                if gate["ordinal"] != start.ordinal:
                    continue
                graph = self._floor_graph(start.ordinal)
                if start.node not in graph:
                    continue
            lengths, paths = nx.single_source_dijkstra(
                graph, start.node, weight="length"
            )
            for opening in gate["openings"]:
                node = opening["node"]
                if node not in lengths:
                    continue
                total = lengths[node] + start.gap_meters + opening["gapMeters"]
                if best is None or total < best[0]:
                    best = (total, start, opening, paths[node])
        if best is None:
            where = "同じ階の中で" if same_floor else ""
            raise StationError(
                "no_route",
                f"{starts[0].endpoint_name} から {gate['name']} への経路が"
                f"{where}ありません",
            )
        return best

    def _polyline(
        self, start: Start, opening: dict[str, Any], path: list[int], ordinal: float
    ) -> tuple[list[RoutePoint], list[VerticalMove]]:
        points: list[RoutePoint] = []
        moves: list[VerticalMove] = []

        def add(point: RoutePoint) -> None:
            if points and points[-1] == point:
                return
            points.append(point)

        for point in start.lead:
            add(point)
        first = self.nodes[path[0]]
        add(RoutePoint(first["x"], first["z"], first["ordinal"]))
        for a, b in zip(path, path[1:], strict=False):
            edge = self.graph[a][b]
            node_a, node_b = self.nodes[a], self.nodes[b]
            begin = len(points) - 1
            steps = len(edge["interior"]) + 1
            for i, (x, z) in enumerate(edge["interior"], start=1):
                # リンクの途中の点の階は、両端の階の間で補間する
                rate = i / steps
                level = (
                    node_a["ordinal"] + (node_b["ordinal"] - node_a["ordinal"]) * rate
                )
                add(RoutePoint(x, z, level))
            add(RoutePoint(node_b["x"], node_b["z"], node_b["ordinal"]))
            if edge["kind"] in VERTICAL_KINDS:
                moves.append(VerticalMove(edge["kind"], begin, len(points) - 1))
            elif node_a["ordinal"] != node_b["ordinal"]:
                # 坂で階が変わるリンクがある。階の変わり目をアプリに伝える
                moves.append(VerticalMove(SLOPE_KIND, begin, len(points) - 1))
        add(RoutePoint(opening["x"], opening["z"], ordinal))
        return points, moves

    def plan(
        self,
        to_gate_id: str,
        from_sign_id: str = "",
        from_gate_id: str = "",
        same_floor: bool = False,
    ) -> PlannedRoute:
        """経路を計算する。

        Args:
            to_gate_id: 行き先の改札。
            from_sign_id: 出発の案内板。
            from_gate_id: 出発の改札。案内板か改札のどちらか一方を渡す。
            same_floor: True なら、出発の階から出ない経路だけを探す。
                階段、エスカレーター、エレベーター、階が変わる坂を通らない。

        Returns:
            計算した経路。

        Raises:
            StationError: ID が不明なとき、または経路がないとき。
        """
        if bool(from_sign_id) == bool(from_gate_id):
            raise StationError(
                "invalid_request", "出発は案内板か改札のどちらか一方を指定してください"
            )
        gate = self._gate(to_gate_id)
        starts = (
            self._starts_from_sign(from_sign_id)
            if from_sign_id
            else self._starts_from_gate(from_gate_id)
        )
        total, start, opening, path = self._search(starts, gate, same_floor)

        curated = self.curated.get((from_sign_id, gate["id"]))
        if curated is not None and same_floor:
            if any(p["ordinal"] != start.ordinal for p in curated["points"]):
                curated = None
        if curated is not None:
            points = _curated_points(curated)
            moves: list[VerticalMove] = []
            source: RouteSource = "curated"
            total = polyline_length(points)
        else:
            raw, raw_moves = self._polyline(start, opening, path, gate["ordinal"])
            points, moves = simplify(raw, raw_moves, SIMPLIFY_TOLERANCE_METERS)
            source = "network"
        points, moves = trim_end(points, moves, LINE_END_MARGIN_METERS)
        return PlannedRoute(
            start=start,
            goal=gate,
            goal_opening=opening,
            length_meters=round(total, 1),
            source=source,
            points=points,
            moves=moves,
            turns=find_turns(points, moves, TURN_THRESHOLD_DEG),
        )

    def route_length(self, from_sign_id: str, to_gate_id: str) -> float | None:
        """案内板から改札までの距離を返す。行き先の候補を並べ替えるために使う。

        Args:
            from_sign_id: 出発の案内板。
            to_gate_id: 行き先の改札。

        Returns:
            距離。経路がなければ None。
        """
        curated = self.curated.get((from_sign_id, to_gate_id))
        if curated is not None:
            # 実際に案内する線の長さにそろえる
            return round(polyline_length(_curated_points(curated)), 1)
        try:
            total, _, _, _ = self._search(
                self._starts_from_sign(from_sign_id), self._gate(to_gate_id)
            )
        except StationError:
            return None
        return round(total, 1)

    def signs_near_route(self, points: list[RoutePoint]) -> list[dict[str, Any]]:
        """経路の近くにある案内板を、経路に沿った順に返す。

        Args:
            points: 経路の点列。

        Returns:
            案内板の ID、経路に沿った距離、経路からの距離の一覧。
        """
        found = []
        for sign in self.signs.values():
            near = _project_on_route(sign, points)
            if near is None or near[1] > SIGN_NEAR_ROUTE_METERS:
                continue
            found.append(
                {
                    "sign_id": sign["id"],
                    "along_meters": round(near[0], 1),
                    "offset_meters": round(near[1], 1),
                }
            )
        return sorted(found, key=lambda item: item["along_meters"])


def _curated_points(curated: dict[str, Any]) -> list[RoutePoint]:
    """手で直した線を点列にする。"""
    return [RoutePoint(p["x"], p["z"], p["ordinal"]) for p in curated["points"]]


def _project_on_segment(x: float, z: float, a: RoutePoint, b: RoutePoint) -> RoutePoint:
    """点を線分 ab に投影する。"""
    dx, dz = b.x - a.x, b.z - a.z
    length_sq = dx * dx + dz * dz
    if length_sq == 0:
        return a
    t = max(0.0, min(1.0, ((x - a.x) * dx + (z - a.z) * dz) / length_sq))
    return RoutePoint(a.x + t * dx, a.z + t * dz, a.ordinal)


def _project_on_route(
    sign: dict[str, Any], points: list[RoutePoint]
) -> tuple[float, float] | None:
    """案内板を同じ階の経路に投影し、経路に沿った距離と横の距離を返す。"""
    best: tuple[float, float] | None = None
    along = 0.0
    for a, b in zip(points, points[1:], strict=False):
        dx, dz = b.x - a.x, b.z - a.z
        length = math.hypot(dx, dz)
        if length > 0 and a.ordinal == sign["ordinal"] and b.ordinal == sign["ordinal"]:
            t = ((sign["x"] - a.x) * dx + (sign["z"] - a.z) * dz) / (length * length)
            t = max(0.0, min(1.0, t))
            offset = math.hypot(sign["x"] - (a.x + t * dx), sign["z"] - (a.z + t * dz))
            if best is None or offset < best[1]:
                best = (along + t * length, offset)
        along += length
    return best
