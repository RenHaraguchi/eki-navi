"""改札から改札までの経路を計算して、Unity が読める JSON に書き出す。

使い方:
    python -m route.export_route <データのフォルダ> <出発の改札ID> <目的の改札ID> \
        <出力先.json>
    python -m route.export_route <データのフォルダ> --list   # 改札IDの一覧

例（データのフォルダは分割版）:
    python -m route.export_route \
        "~/data/新宿駅周辺屋内地図オープンデータ（Shapefile）" \
        "JR新宿駅:南改札" "JR新宿駅新南改札:新南口改札" \
        ../Assets/EkiNavi/Routes/jr_south_to_new_south.json
"""

from __future__ import annotations

import json
import os
import sys

from .network import ORIGIN_LAT, ORIGIN_LON, StationNetwork


def route_to_dict(route) -> dict:
    return {
        "origin": {"lat": ORIGIN_LAT, "lon": ORIGIN_LON},
        "start": {
            "id": route.start.id,
            "name": route.start.name,
            "ordinal": route.start.ordinal,
        },
        "goal": {
            "id": route.goal.id,
            "name": route.goal.name,
            "ordinal": route.goal.ordinal,
        },
        "lengthMeters": round(route.length_m, 1),
        "points": [
            {"x": round(p.x, 3), "z": round(p.z, 3), "ordinal": p.ordinal}
            for p in route.points
        ],
        "verticalMoves": [
            {"kind": v["kind"], "fromIndex": v["from_index"], "toIndex": v["to_index"]}
            for v in route.vertical_moves
        ],
    }


def main(argv: list[str]) -> int:
    if len(argv) >= 2 and argv[1] == "--list":
        net = StationNetwork(os.path.expanduser(argv[0]))
        for gate in sorted(
            net.gates.values(), key=lambda g: (g.station, g.ordinal, g.name)
        ):
            print(f"{gate.id}\t{gate.ordinal:g}階")
        return 0
    if len(argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2

    data_root, start_id, goal_id, out_path = argv
    net = StationNetwork(os.path.expanduser(data_root))
    route = net.route_between_gates(start_id, goal_id)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(route_to_dict(route), f, ensure_ascii=False, indent=2)
    summary = f"{route.start.name} → {route.goal.name}: {route.length_m:.0f}m"
    print(f"{summary}, {len(route.points)}点 → {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
