"""contracts/ のサンプルと仕様を、動いているサーバーから作る。

手で書かないことで、サーバーの実装とサンプルがずれないようにする。

使い方:
    python -m app.export_contracts           # 書き出す
    python -m app.export_contracts --check   # ずれていれば失敗する
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any

from fastapi.testclient import TestClient

from app.alignment import align_to_sign
from app.config import Settings
from app.gemini import Classification
from app.main import create_app
from app.vocab import Engine

CONTRACTS_DIR = Path(__file__).resolve().parent.parent.parent / "contracts"
FROM_SIGN = "S-B1-CENTER"
TO_GATE = "B1:JR東日本:中央東改札"


class _ScriptedClassifier:
    """サンプルを作るために、決まった答えを返す判定処理。"""

    engine: Engine = "gemini"

    def classify(self, text: str) -> Classification:
        """文章の代わりに、決めておいた答えを返す。"""
        answers = {
            "ambiguous": Classification("ambiguous", ("line:keio", "line:keio-new")),
            "out_of_scope": Classification("out_of_scope", ()),
        }
        return answers.get(text, Classification("ok", ("line:keio",)))


def _stable(response_json: dict[str, Any]) -> dict[str, Any]:
    """実行のたびに変わる値を固定する。"""
    return (
        {**response_json, "latencyMs": 0}
        if "latencyMs" in response_json
        else response_json
    )


def _api_samples() -> dict[str, dict[str, Any]]:
    settings = Settings()
    client = TestClient(create_app(settings, classifier=_ScriptedClassifier()))
    stub = TestClient(create_app(settings))
    route_body = {"fromSignId": FROM_SIGN, "toGateId": TO_GATE}
    return {
        "station": client.get("/v1/station").json(),
        "route_ok_from_sign": client.post("/v1/route", json=route_body).json(),
        "route_ok_curated": client.post(
            "/v1/route",
            json={"fromSignId": "S-B1-WEST", "toGateId": "B1:JR東日本:東改札"},
        ).json(),
        "route_ok_between_gates": client.post(
            "/v1/route",
            json={
                "fromGateId": "2F:JR東日本:新南口改札",
                "toGateId": "3F:バスタ新宿:バスタ新宿",
            },
        ).json(),
        "error_unknown_gate": client.post(
            "/v1/route", json={"fromSignId": FROM_SIGN, "toGateId": "存在しない改札"}
        ).json(),
        "destination_ok": _stable(
            client.post(
                "/v1/destination",
                json={"text": "京王線に乗りたい", "fromSignId": FROM_SIGN},
            ).json()
        ),
        "destination_ambiguous": _stable(
            client.post(
                "/v1/destination", json={"text": "ambiguous", "fromSignId": FROM_SIGN}
            ).json()
        ),
        "destination_out_of_scope": _stable(
            client.post("/v1/destination", json={"text": "out_of_scope"}).json()
        ),
        "destination_unavailable": _stable(
            stub.post("/v1/destination", json={"text": "よくわからない文章"}).json()
        ),
    }


def _alignment_vectors() -> dict[str, Any]:
    """Unity 側の実装が同じ答えを出すかを確かめるための入力と答え。"""
    cases = [
        (
            "ARの北が地図の北と同じ",
            (20.0, 136.5),
            353.0,
            (0.0, 0.0),
            353.0,
            0.0,
            (0.0, 0.0),
        ),
        (
            "ARが37度回っている",
            (20.0, 136.5),
            353.0,
            (1.5, -2.0),
            30.0,
            0.0,
            (0.0, 0.0),
        ),
        (
            "板が東を向き、ARが200度回っている",
            (42.0, 139.2),
            90.0,
            (-3.0, 4.0),
            290.0,
            0.0,
            (0.0, 0.0),
        ),
        ("方位が0度をまたぐ", (64.0, 141.8), 5.0, (0.5, 0.5), 355.0, 0.0, (0.0, 0.0)),
        ("現地の補正がある", (20.0, 136.5), 353.0, (1.5, -2.0), 30.0, 3.0, (0.5, -1.0)),
    ]
    points = [(21.0, 141.0), (64.0, 146.5), (20.0, 136.5)]
    vectors = []
    for name, sign_map, azimuth, position, detected_az, yaw_fix, shift in cases:
        rad = math.radians(detected_az)
        normal = (_round6(math.sin(rad)), _round6(math.cos(rad)))
        alignment = align_to_sign(sign_map, azimuth, position, normal, yaw_fix, shift)
        vectors.append(
            {
                "name": name,
                "signMap": {"x": sign_map[0], "z": sign_map[1]},
                "signAzimuthDeg": azimuth,
                "correctionYawDeg": yaw_fix,
                "correction": {"x": shift[0], "z": shift[1]},
                "detectedPosition": {"x": position[0], "z": position[1]},
                "detectedNormal": {"x": normal[0], "z": normal[1]},
                "expectedYawDeg": _round6(alignment.yaw_deg),
                "mapToAr": [
                    {
                        "map": {"x": x, "z": z},
                        "ar": dict(
                            zip(
                                ("x", "z"),
                                map(_round6, alignment.map_to_ar(x, z)),
                                strict=True,
                            )
                        ),
                    }
                    for x, z in points
                ],
            }
        )
    return {"vectors": vectors}


def _round6(value: float) -> float:
    return round(value, 6)


def _dump(data: dict[str, Any]) -> str:
    return json.dumps(data, ensure_ascii=False, indent=2) + "\n"


def render_all() -> dict[Path, str]:
    """contracts/ に置く内容を作る。

    Returns:
        パスと内容の組。
    """
    openapi = create_app(Settings()).openapi()
    files = {CONTRACTS_DIR / "openapi.json": _dump(openapi)}
    files[CONTRACTS_DIR / "alignment_vectors.json"] = _dump(_alignment_vectors())
    for name, sample in _api_samples().items():
        files[CONTRACTS_DIR / "samples" / f"{name}.json"] = _dump(sample)
    return files


def main(argv: list[str] | None = None) -> int:
    """コマンドラインの入口。"""
    parser = argparse.ArgumentParser(description="contracts/ を作る")
    parser.add_argument("--check", action="store_true", help="ずれを確かめるだけにする")
    args = parser.parse_args(argv)

    stale = []
    for path, content in render_all().items():
        current = path.read_text(encoding="utf-8") if path.exists() else None
        if current == content:
            continue
        stale.append(path)
        if not args.check:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
    label = "ずれています" if args.check else "書き出しました"
    print(f"{label}: {len(stale)} 件")
    return 1 if args.check and stale else 0


if __name__ == "__main__":
    sys.exit(main())
