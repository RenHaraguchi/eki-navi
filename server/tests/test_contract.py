"""役割の間の取り決めがずれていないかを確かめる。"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any

import jsonschema
import pytest

from app.alignment import align_to_sign, azimuth_of, rotate_y
from app.export_contracts import CONTRACTS_DIR, render_all
from tests.conftest import REPO_DIR, SERVER_DIR

ROUTE_DATA_CS = REPO_DIR / "Assets/EkiNavi/Scripts/Route/RouteData.cs"
SAMPLES = sorted((CONTRACTS_DIR / "samples").glob("*.json"))
ROUTE_SAMPLES = [path for path in SAMPLES if path.name.startswith("route_ok")]


def test_contract_files_match_the_server() -> None:
    # ずれていたら python -m app.export_contracts で作り直す
    for path, content in render_all().items():
        assert path.read_text(encoding="utf-8") == content, path.name


def _walk_values(value: Any) -> list[Any]:
    if isinstance(value, dict):
        return [item for child in value.values() for item in _walk_values(child)]
    if isinstance(value, list):
        return [item for child in value for item in _walk_values(child)]
    return [value]


@pytest.mark.parametrize("path", SAMPLES, ids=lambda p: p.stem)
def test_samples_can_be_read_by_json_utility(path: Path) -> None:
    # JsonUtility は一番外側の配列と null を扱えない
    sample = json.loads(path.read_text(encoding="utf-8"))

    assert isinstance(sample, dict)
    assert None not in _walk_values(sample)


def _csharp_classes() -> dict[str, dict[str, str]]:
    """RouteData.cs から、クラスごとの公開フィールドと型を読み取る。"""
    source = ROUTE_DATA_CS.read_text(encoding="utf-8")
    classes: dict[str, dict[str, str]] = {}
    for match in re.finditer(r"public class (\w+)\s*\{(.*?)\n    \}", source, re.S):
        fields = re.findall(r"public ([\w\[\]]+) (\w+);", match.group(2))
        classes[match.group(1)] = {name: type_ for type_, name in fields}
    return classes


def _assert_matches(
    value: Any, type_: str, classes: dict[str, Any], where: str
) -> None:
    if type_.endswith("[]"):
        assert isinstance(value, list), where
        for index, item in enumerate(value):
            _assert_matches(item, type_[:-2], classes, f"{where}[{index}]")
    elif type_ in classes:
        assert isinstance(value, dict), where
        for name, field_type in classes[type_].items():
            assert name in value, f"{where}.{name} がありません"
            _assert_matches(value[name], field_type, classes, f"{where}.{name}")
    elif type_ in ("float", "double"):
        assert isinstance(value, int | float), where
    elif type_ == "int":
        assert isinstance(value, int), where
    elif type_ == "string":
        assert isinstance(value, str), where
    else:
        pytest.fail(f"想定していない型です: {type_}（{where}）")


@pytest.mark.parametrize("path", ROUTE_SAMPLES, ids=lambda p: p.stem)
def test_route_response_can_be_read_by_route_data(path: Path) -> None:
    # Unity の RouteData.cs を変えたら、ここで API とのずれが分かる
    classes = _csharp_classes()
    sample = json.loads(path.read_text(encoding="utf-8"))

    _assert_matches(sample, "RouteData", classes, "RouteData")


def test_route_sample_has_at_least_two_points() -> None:
    # RouteData.Parse は点が2つ未満だと例外を出す
    for path in ROUTE_SAMPLES:
        sample = json.loads(path.read_text(encoding="utf-8"))
        assert len(sample["points"]) >= 2, path.name


def test_signs_follow_the_schema() -> None:
    signs = json.loads(
        (SERVER_DIR / "station_data/signs.json").read_text(encoding="utf-8")
    )
    schema = json.loads(
        (CONTRACTS_DIR / "signs.schema.json").read_text(encoding="utf-8")
    )

    jsonschema.validate(signs, schema)


def test_rotation_matches_the_unity_convention() -> None:
    # Quaternion.Euler(0, 90, 0) は、上から見て時計回りなので北を東へ回す
    x, z = rotate_y((0.0, 1.0), 90.0)

    assert x == pytest.approx(1.0)
    assert z == pytest.approx(0.0, abs=1e-9)


def test_alignment_maps_the_sign_onto_its_detected_position() -> None:
    alignment = align_to_sign((20.0, 136.5), 353.0, (1.5, -2.0), (0.5, 0.866025))

    assert alignment.map_to_ar(20.0, 136.5) == pytest.approx((1.5, -2.0))


def test_alignment_maps_the_map_normal_onto_the_detected_normal() -> None:
    detected = (0.5, 0.866025)
    alignment = align_to_sign((20.0, 136.5), 353.0, (1.5, -2.0), detected)
    rad_vector = (-0.121869, 0.992546)  # 方位 353 度の単位ベクトル

    rotated = rotate_y(rad_vector, alignment.yaw_deg)

    assert azimuth_of(rotated) == pytest.approx(azimuth_of(detected), abs=1e-3)


def test_alignment_keeps_distances() -> None:
    alignment = align_to_sign((20.0, 136.5), 353.0, (1.5, -2.0), (0.5, 0.866025))
    a = alignment.map_to_ar(21.0, 141.0)
    b = alignment.map_to_ar(64.0, 146.5)

    assert (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 == pytest.approx(43.0**2 + 5.5**2)


def test_yaw_is_normalized() -> None:
    alignment = align_to_sign((0.0, 0.0), 5.0, (0.0, 0.0), (-0.087156, 0.996195))

    assert -180.0 < alignment.yaw_deg <= 180.0
    assert alignment.yaw_deg == pytest.approx(-10.0, abs=1e-3)


def test_correction_is_applied_on_the_map_side() -> None:
    plain = align_to_sign((20.0, 136.5), 353.0, (1.5, -2.0), (0.5, 0.866025))
    fixed = align_to_sign(
        (20.0, 136.5), 353.0, (1.5, -2.0), (0.5, 0.866025), 3.0, (0.5, -1.0)
    )

    assert fixed.yaw_deg == pytest.approx(plain.yaw_deg - 3.0)
    assert fixed.map_to_ar(20.5, 135.5) == pytest.approx((1.5, -2.0))


@pytest.mark.skipif(
    not (os.environ.get("EKINAVI_INTEGRATED") and os.environ.get("EKINAVI_SPLIT")),
    reason="地図データの場所が指定されていません",
)
def test_pack_can_be_rebuilt_from_the_shapefiles() -> None:
    from pack.build import build_pack

    pack = build_pack(
        os.environ["EKINAVI_INTEGRATED"],
        os.environ["EKINAVI_SPLIT"],
        SERVER_DIR / "station_data",
    )
    committed = json.loads(
        (SERVER_DIR / "station_data/pack.json").read_text(encoding="utf-8")
    )

    assert pack == committed
