"""看板の記録の ZIP を signs.json に取り込む処理（pack/import_survey.py）を確かめる。"""

from __future__ import annotations

import json
import zipfile
from pathlib import Path

from pack.import_survey import dump_signs, fill_text, merge_signs, read_survey_zip


def _sign(sign_id: str, text: str = "", **extra: object) -> dict[str, object]:
    return {"id": sign_id, "name": sign_id, "text": text, "imageName": f"sign_{sign_id.lower()}", **extra}


def _zip(tmp_path: Path, signs: list[dict[str, object]], photos: dict[str, bytes]) -> Path:
    path = tmp_path / "signs_B1.zip"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("signs.json", json.dumps({"signs": signs}, ensure_ascii=False))
        for name, data in photos.items():
            archive.writestr(f"photos/{name}.jpg", data)
    return path


def test_reads_signs_and_photos(tmp_path: Path) -> None:
    path = _zip(tmp_path, [_sign("S-B1-AAA")], {"sign_s-b1-aaa": b"jpeg"})

    signs, photos = read_survey_zip(path)

    assert [s["id"] for s in signs] == ["S-B1-AAA"]
    assert photos == {"sign_s-b1-aaa": b"jpeg"}


def test_fills_empty_text_from_the_photo() -> None:
    signs = [_sign("S-B1-AAA"), _sign("S-B1-BBB", text="← 小田急線"), _sign("S-B1-CCC")]
    photos = {"sign_s-b1-aaa": b"a"}

    missing = fill_text(signs, photos, lambda photo: "→ 京王線 Keio Line")

    assert signs[0]["text"] == "→ 京王線 Keio Line"
    assert signs[1]["text"] == "← 小田急線"  # 入っている文字は読み直さない
    assert missing == ["S-B1-CCC"]  # 写真がない板は空のまま


def test_without_a_reader_empty_text_is_reported() -> None:
    signs = [_sign("S-B1-AAA")]

    assert fill_text(signs, {"sign_s-b1-aaa": b"a"}, None) == ["S-B1-AAA"]


def test_merge_replaces_the_same_id_and_appends_new_ones() -> None:
    existing = [_sign("S-B1-H02-A", text="old"), _sign("S-2F-SOUTH", text="2f")]
    incoming = [_sign("S-B1-H02-A", text="new"), _sign("S-B1-NEW", text="n")]

    merged, replaced, added = merge_signs(existing, incoming)

    assert [s["id"] for s in merged] == ["S-B1-H02-A", "S-2F-SOUTH", "S-B1-NEW"]
    assert merged[0]["text"] == "new"
    assert replaced == ["S-B1-H02-A"]
    assert added == ["S-B1-NEW"]
    assert existing[0]["text"] == "old"  # 元の一覧は書き換えない


def test_dump_keeps_the_signs_json_style() -> None:
    text = dump_signs({"signs": [_sign("S-B1-AAA", text="小田急線")]})

    assert text.endswith("}\n")
    assert "小田急線" in text
    assert text.startswith('{\n  "signs": [\n')
