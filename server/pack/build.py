"""国交省の屋内地図から、サーバーとアプリが共通で使う経路パックを作る。

ネットワークと改札の位置は統合版から読む（東西自由通路のリンクは統合版にしかない）。
統合版の Opening には事業者の情報がないので、同じ階で同じ名前の開口部を分割版から探し、
そのフォルダ名から事業者を決める。

使い方:
    python -m pack.build --integrated <統合版のフォルダ> --split <分割版のフォルダ>
"""

from __future__ import annotations

import argparse
import csv
import glob
import hashlib
import json
import math
import os
import sys
import unicodedata
import warnings
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import jsonschema
import shapefile

from route.network import ORIGIN_LAT, ORIGIN_LON, to_local

# 統合版の nw は cp932、ShinjukuTerminal 以下は UTF-8 で読める
NW_ENCODING = "cp932"
LAYER_ENCODING = "utf-8"

# 端点が欠けたリンクを、この距離までの既存ノードにつなぎ直す
SNAP_TOLERANCE_METERS = 3.5

# 歩行空間ネットワークの「経路の種別」と「方向性」のコード（整備仕様 2024-07 版）
ROUTE_TYPE_KINDS = {
    "4": "elevator",
    "5": "escalator",
    "6": "stairs",
    "7": "slope",
}
DIRECTION_FORWARD_ONLY = "2"
DIRECTION_BACKWARD_ONLY = "3"

# 分割版のフォルダ番号から事業者を決める。名前からの判断で、公式の構内図とは未照合
OPERATOR_BY_FOLDER_NUMBER = {
    "1": "JR東日本",
    "2": "JR東日本",
    "3": "JR東日本",
    "4": "ルミネ",
    "5": "東京メトロ",
    "6": "小田急",
    "7": "京王",
    "8": "京王",
    "9": "都営",
    "10": "都営",
    "11": "サブナード",
    "12": "西武",
    "14": "バスタ新宿",
}

UNKNOWN_OPENING_NAMES = {"", "不明"}
ATTRIBUTION = (
    "「新宿駅周辺屋内地図データ」（国土交通省）"
    "（https://www.geospatial.jp/ckan/dataset/mlit-indoor-shinjuku-r2）を加工して作成"
)
COORD_DECIMALS = 3
# 改札と通路がこれより離れていたら、線が壁を通る恐れがあるので現地で確かめる
LARGE_GAP_METERS = 5.0


@dataclass(frozen=True)
class Node:
    """ネットワークのノード。"""

    uuid: str
    x: float
    z: float
    ordinal: float


class PackBuildError(Exception):
    """入力データが経路パックの前提を満たさないときの例外。"""


def _reader(path: str, encoding: str) -> shapefile.Reader:
    """Shapefile を開く。文字化けは置き換えて読み進める。"""
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        return shapefile.Reader(path, encoding=encoding, encodingErrors="replace")


def _round(value: float) -> float:
    return round(value, COORD_DECIMALS)


def floor_label(ordinal: float) -> str:
    """階の番号を画面用の表記にする。

    Args:
        ordinal: データの階番号。-1 は地下1階。

    Returns:
        "B1"、"2F"、グランドレベルは "GL"。半階は小数を残す。
    """
    if ordinal == 0:
        return "GL"
    magnitude = abs(ordinal)
    text = str(int(magnitude)) if magnitude == int(magnitude) else str(magnitude)
    return f"B{text}" if ordinal < 0 else f"{text}F"


def link_key(start_uuid: str, end_uuid: str) -> str:
    """現地の差分で指定するリンクの ID を作る。

    データの link_id は重複があるので、両端のノードから作る。

    Args:
        start_uuid: 起点のノード ID。
        end_uuid: 終点のノード ID。

    Returns:
        向きによらず同じになる短い ID。
    """
    first, second = sorted((start_uuid[:8], end_uuid[:8]))
    return f"{first}-{second}"


def load_nodes(root: str) -> dict[str, Node]:
    """統合版のノードを読む。"""
    nodes: dict[str, Node] = {}
    for rec in _reader(
        os.path.join(root, "nw", "Shinjuku_node"), NW_ENCODING
    ).records():
        x, z = to_local(rec["lon"], rec["lat"])
        nodes[rec["node_id"]] = Node(rec["node_id"], x, z, float(rec["ordinal"]))
    return nodes


def _nearest_node(
    point: tuple[float, float], nodes: dict[str, Node], ordinal: float | None
) -> tuple[Node, float] | None:
    """点に最も近いノードと距離を返す。ordinal を渡すと同じ階だけを見る。"""
    best: tuple[Node, float] | None = None
    for node in nodes.values():
        if ordinal is not None and node.ordinal != ordinal:
            continue
        distance = math.hypot(node.x - point[0], node.z - point[1])
        if best is None or distance < best[1]:
            best = (node, distance)
    return best


def _resolve_endpoint(
    uuid: str,
    shape_point: tuple[float, float],
    other_uuid: str,
    nodes: dict[str, Node],
    changes_floor: bool,
) -> tuple[Node | None, bool]:
    """リンクの端点をノードに解決する。欠けていれば形の端から探す。

    Args:
        uuid: 端点のノード ID。
        shape_point: リンクの形の端の座標。
        other_uuid: 反対側の端点のノード ID。
        nodes: load_nodes の結果。
        changes_floor: 階段など、階が変わりうるリンクかどうか。

    Returns:
        ノード（見つからなければ None）と、つなぎ直したかどうか。
    """
    if uuid in nodes:
        return nodes[uuid], False
    other = nodes.get(other_uuid)
    same_floor = other.ordinal if other and not changes_floor else None
    found = _nearest_node(shape_point, nodes, same_floor)
    if found is None or found[1] > SNAP_TOLERANCE_METERS:
        return None, False
    return found[0], True


def _link_kind(route_type: str) -> str:
    return ROUTE_TYPE_KINDS.get(str(route_type), "walk")


def _is_one_way(direction: str) -> bool:
    return str(direction) in (DIRECTION_FORWARD_ONLY, DIRECTION_BACKWARD_ONLY)


def load_links(
    root: str, nodes: dict[str, Node]
) -> tuple[list[dict[str, Any]], dict[str, int]]:
    """統合版のリンクを読み、端点の欠けと重複を整理する。

    Args:
        root: 統合版のフォルダ。
        nodes: load_nodes の結果。

    Returns:
        リンクの一覧と、処理した件数の内訳。
    """
    reader = _reader(os.path.join(root, "nw", "Shinjuku_link"), NW_ENCODING)
    report = {"read": 0, "snapped": 0, "dropped": 0, "duplicate": 0}
    best: dict[tuple[str, str], dict[str, Any]] = {}

    for rec, shape in zip(reader.records(), reader.shapes(), strict=True):
        report["read"] += 1
        pts = [to_local(lon, lat) for lon, lat in shape.points]
        if len(pts) < 2:
            report["dropped"] += 1
            continue
        changes_floor = _link_kind(rec["route_type"]) != "walk"
        start, start_snapped = _resolve_endpoint(
            rec["start_id"], pts[0], rec["end_id"], nodes, changes_floor
        )
        end, end_snapped = _resolve_endpoint(
            rec["end_id"], pts[-1], rec["start_id"], nodes, changes_floor
        )
        if start is None or end is None or start.uuid == end.uuid:
            report["dropped"] += 1
            continue
        report["snapped"] += int(start_snapped or end_snapped)

        # 形の端はノードの座標に合わせる。ずれたまま描くと線が飛ぶ
        interior = [[_round(x), _round(z)] for x, z in pts[1:-1]]
        direction = str(rec["direction"])
        length = float(rec["distance"] or 0) or math.dist(pts[0], pts[-1])
        if direction == DIRECTION_BACKWARD_ONLY:
            start, end = end, start
            interior = list(reversed(interior))
        link = {
            "key": link_key(start.uuid, end.uuid),
            "a": start.uuid,
            "b": end.uuid,
            "length": _round(length),
            "kind": _link_kind(rec["route_type"]),
            "oneWay": _is_one_way(direction),
            "interior": interior,
        }
        pair = (start.uuid, end.uuid)
        if pair in best:
            report["duplicate"] += 1
            if best[pair]["length"] <= link["length"]:
                continue
        best[pair] = link
    return list(best.values()), report


def _floor_ordinals(root: str) -> dict[str, float]:
    """floor_id から階番号を引く表を作る。"""
    ordinals: dict[str, float] = {}
    for path in glob.glob(os.path.join(root, "**", "*_Floor.shp"), recursive=True):
        for rec in _reader(path, LAYER_ENCODING).records():
            ordinals[rec["id"]] = float(rec["ordinal"])
    return ordinals


def _read_openings(root: str) -> list[dict[str, Any]]:
    """開口部を読む。名前のないものと「不明」は改札として扱わない。"""
    ordinals = _floor_ordinals(root)
    openings: list[dict[str, Any]] = []
    for path in glob.glob(os.path.join(root, "**", "*_Opening.shp"), recursive=True):
        folder = os.path.relpath(path, root).split(os.sep)[0]
        reader = _reader(path, LAYER_ENCODING)
        for rec, shape in zip(reader.records(), reader.shapes(), strict=True):
            name = unicodedata.normalize("NFKC", (rec["name"] or "").strip())
            if name in UNKNOWN_OPENING_NAMES or not shape.points:
                continue
            openings.append(
                {
                    "folder": folder,
                    "name": name,
                    "ordinal": ordinals.get(rec["floor_id"]),
                    "points": [to_local(lon, lat) for lon, lat in shape.points],
                }
            )
    return openings


def _operator_for(name: str, ordinal: float, split: list[dict[str, Any]]) -> str:
    """分割版のフォルダ名から事業者を決める。"""
    for opening in split:
        if opening["name"] == name and opening["ordinal"] == ordinal:
            number, _, rest = opening["folder"].partition(".")
            return OPERATOR_BY_FOLDER_NUMBER.get(number, rest or opening["folder"])
    raise PackBuildError(f"分割版に対応する開口部がありません: {ordinal} {name}")


def gate_id(ordinal: float, operator: str, name: str) -> str:
    """改札の ID を作る。同じ名前が別の階にあるので、階と事業者を含める。"""
    return f"{floor_label(ordinal)}:{operator}:{name}"


def build_gates(
    integrated: str, split: str, nodes: dict[str, Node]
) -> list[dict[str, Any]]:
    """改札の一覧を作る。同じ階で同じ名前の開口部は1つの改札にまとめる。

    Args:
        integrated: 統合版のフォルダ。
        split: 分割版のフォルダ。
        nodes: load_nodes の結果。

    Returns:
        改札の一覧。

    Raises:
        PackBuildError: 階が不明な開口部があるとき。
    """
    split_openings = _read_openings(split)
    grouped: dict[str, dict[str, Any]] = {}
    for opening in _read_openings(integrated):
        ordinal = opening["ordinal"]
        if ordinal is None:
            raise PackBuildError(f"階が不明な開口部です: {opening['name']}")
        operator = _operator_for(opening["name"], ordinal, split_openings)
        identifier = gate_id(ordinal, operator, opening["name"])
        gate = grouped.setdefault(
            identifier,
            {
                "id": identifier,
                "name": opening["name"],
                "operator": operator,
                "ordinal": ordinal,
                "floor": floor_label(ordinal),
                "openings": [],
            },
        )
        pts = opening["points"]
        center = (
            sum(p[0] for p in pts) / len(pts),
            sum(p[1] for p in pts) / len(pts),
        )
        found = _nearest_node(center, nodes, ordinal)
        if found is None:
            raise PackBuildError(f"同じ階にノードがありません: {identifier}")
        gate["openings"].append(
            {
                "x": _round(center[0]),
                "z": _round(center[1]),
                "node": found[0].uuid,
                "gapMeters": _round(found[1]),
                "ax": _round(pts[0][0]),
                "az": _round(pts[0][1]),
                "bx": _round(pts[-1][0]),
                "bz": _round(pts[-1][1]),
            }
        )
    return sorted(grouped.values(), key=lambda g: (g["ordinal"], g["id"]))


def read_overrides(path: Path) -> list[dict[str, str]]:
    """現地の差分を読む。

    Args:
        path: overrides.csv のパス。

    Returns:
        差分の行。

    Raises:
        PackBuildError: 知らない種類の行があるとき。
    """
    with path.open(encoding="utf-8", newline="") as file:
        rows = list(csv.DictReader(file))
    for row in rows:
        if row["type"] not in ("close_link", "close_gate"):
            raise PackBuildError(f"overrides.csv の種類が不明です: {row['type']}")
    return rows


def apply_overrides(
    links: list[dict[str, Any]],
    gates: list[dict[str, Any]],
    overrides: list[dict[str, str]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """閉鎖の差分を反映した新しい一覧を返す。

    Raises:
        PackBuildError: 差分の対象がデータにないとき。
    """
    closed_links = {r["target"] for r in overrides if r["type"] == "close_link"}
    closed_gates = {r["target"] for r in overrides if r["type"] == "close_gate"}
    missing = (closed_links - {link["key"] for link in links}) | (
        closed_gates - {gate["id"] for gate in gates}
    )
    if missing:
        raise PackBuildError(f"差分の対象が見つかりません: {sorted(missing)}")
    return (
        [link for link in links if link["key"] not in closed_links],
        [gate for gate in gates if gate["id"] not in closed_gates],
    )


def validate_references(
    gates: list[dict[str, Any]],
    signs: list[dict[str, Any]],
    lines: list[dict[str, Any]],
    curated: list[dict[str, Any]],
) -> None:
    """手で書いたデータが、実在する改札と案内板を指しているか確かめる。

    Raises:
        PackBuildError: 存在しない ID を指しているとき。
    """
    gate_ids = {gate["id"] for gate in gates}
    sign_ids = {sign["id"] for sign in signs}
    errors = [
        f"行き先 {line['id']} の改札がありません: {gate}"
        for line in lines
        for gate in line["gateIds"]
        if gate not in gate_ids
    ]
    errors += [
        f"手直しの線の参照先がありません: {item['fromSignId']} → {item['toGateId']}"
        for item in curated
        if item["fromSignId"] not in sign_ids or item["toGateId"] not in gate_ids
    ]
    if len(sign_ids) != len(signs):
        errors.append("案内板の ID が重複しています")
    if errors:
        raise PackBuildError("\n".join(errors))


def _compact_nodes(
    nodes: dict[str, Node],
    links: list[dict[str, Any]],
    gates: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    """UUID を番号に置き換えて、パックを小さくする。"""
    order = {uuid: index for index, uuid in enumerate(sorted(nodes))}
    compact_nodes = [
        {
            "x": _round(nodes[uuid].x),
            "z": _round(nodes[uuid].z),
            "ordinal": nodes[uuid].ordinal,
        }
        for uuid in sorted(nodes)
    ]
    compact_links = [
        {**link, "a": order[link["a"]], "b": order[link["b"]]} for link in links
    ]
    compact_gates = [
        {
            **gate,
            "openings": [
                {**opening, "node": order[opening["node"]]}
                for opening in gate["openings"]
            ],
        }
        for gate in gates
    ]
    return compact_nodes, compact_links, compact_gates


def build_pack(integrated: str, split: str, station_data: Path) -> dict[str, Any]:
    """経路パックを作る。

    Args:
        integrated: 統合版のフォルダ。
        split: 分割版のフォルダ。
        station_data: signs.json などを置いたフォルダ。

    Returns:
        経路パックの内容。
    """
    nodes = load_nodes(integrated)
    links, report = load_links(integrated, nodes)
    gates = build_gates(integrated, split, nodes)
    overrides = read_overrides(station_data / "overrides.csv")
    links, gates = apply_overrides(links, gates, overrides)

    signs_doc = json.loads((station_data / "signs.json").read_text(encoding="utf-8"))
    schema_path = station_data.parent.parent / "contracts" / "signs.schema.json"
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    jsonschema.validate(signs_doc, schema)
    lines = json.loads((station_data / "lines.json").read_text(encoding="utf-8"))
    curated = json.loads(
        (station_data / "curated_lines.json").read_text(encoding="utf-8")
    )
    validate_references(gates, signs_doc["signs"], lines["lines"], curated["lines"])

    compact_nodes, compact_links, compact_gates = _compact_nodes(nodes, links, gates)
    body = {
        "origin": {"lat": ORIGIN_LAT, "lon": ORIGIN_LON},
        "attribution": ATTRIBUTION,
        "report": {**report, "overrides": len(overrides)},
        "nodes": compact_nodes,
        "links": compact_links,
        "gates": compact_gates,
        "signs": signs_doc["signs"],
        "lines": lines["lines"],
        "curatedLines": curated["lines"],
    }
    digest = hashlib.sha256(
        json.dumps(body, ensure_ascii=False, sort_keys=True).encode("utf-8")
    ).hexdigest()
    return {"version": digest[:8], **body}


def main(argv: list[str] | None = None) -> int:
    """コマンドラインの入口。"""
    parser = argparse.ArgumentParser(description="経路パックを作る")
    parser.add_argument("--integrated", required=True, help="統合版のフォルダ")
    parser.add_argument("--split", required=True, help="分割版のフォルダ")
    parser.add_argument(
        "--station-data",
        default=str(Path(__file__).resolve().parent.parent / "station_data"),
        help="signs.json などを置いたフォルダ",
    )
    args = parser.parse_args(argv)

    station_data = Path(args.station_data)
    pack = build_pack(args.integrated, args.split, station_data)
    out = station_data / "pack.json"
    out.write_text(
        json.dumps(pack, ensure_ascii=False, separators=(",", ":")), encoding="utf-8"
    )
    print(
        f"版 {pack['version']}: ノード {len(pack['nodes'])}、"
        f"リンク {len(pack['links'])}、改札 {len(pack['gates'])}、"
        f"案内板 {len(pack['signs'])}、{out.stat().st_size // 1024} KB"
    )
    print(f"リンクの処理: {pack['report']}")
    for gate in pack["gates"]:
        for opening in gate["openings"]:
            if opening["gapMeters"] > LARGE_GAP_METERS:
                print(
                    f"要確認: {gate['id']} は通路まで {opening['gapMeters']} m あります"
                )
    return 0


if __name__ == "__main__":
    sys.exit(main())
