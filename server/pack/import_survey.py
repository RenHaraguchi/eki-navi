"""看板の記録のページ（地下1階）が書き出した ZIP を、signs.json に取り込む。

ZIP の中身は signs.json（eki-navi の signs.json と同じ形）と photos/<imageName>.jpg。
記録のページは板の文字（text）を空のまま書き出すので、--read-text を付けると写真を Gemini に読ませて埋める。
サーバーは text で板を見分けるので、text が空の板は見分けられない。

取り込むと、server/station_data/signs.json と Assets/EkiNavi/Data/signs.json を同じ中身で書き直す
（同じ id の板は置き換え、新しい板は後ろに足す）。写真は --photos-dir に書き出す（リポジトリには入れない）。
そのあと経路パック（server/station_data/pack.json）と contracts/ を作り直す。

実行例（server フォルダで）:
    GEMINI_BACKEND=apikey GEMINI_API_KEY=... python -m pack.import_survey signs_B1_xxx.zip \
        --read-text --photos-dir ../../survey_photos \
        --integrated <統合版のフォルダ> --split <分割版のフォルダ>
"""

from __future__ import annotations

import argparse
import json
import sys
import zipfile
from pathlib import Path
from typing import Any, Callable

SERVER_DIR = Path(__file__).resolve().parent.parent
REPO_DIR = SERVER_DIR.parent
SERVER_SIGNS = SERVER_DIR / "station_data" / "signs.json"
APP_SIGNS = REPO_DIR / "Assets" / "EkiNavi" / "Data" / "signs.json"
SCHEMA = REPO_DIR / "contracts" / "signs.schema.json"

# 写真から板の文字を読む関数（写真の JPEG → 読んだ文字）
TextReader = Callable[[bytes], str]


def read_survey_zip(path: Path) -> tuple[list[dict[str, Any]], dict[str, bytes]]:
    """ZIP から、記録した板と写真（imageName → JPEG）を取り出す。"""
    with zipfile.ZipFile(path) as archive:
        doc = json.loads(archive.read("signs.json").decode("utf-8"))
        photos = {}
        for name in archive.namelist():
            if name.startswith("photos/") and name.lower().endswith(".jpg"):
                photos[Path(name).stem] = archive.read(name)
    signs = doc.get("signs", [])
    if not isinstance(signs, list):
        raise ValueError("signs.json の signs が配列ではありません")
    return signs, photos


def fill_text(
    signs: list[dict[str, Any]], photos: dict[str, bytes], reader: TextReader | None
) -> list[str]:
    """text が空の板に、写真から読んだ文字を入れる。

    Returns:
        text が空のまま残った板の id。
    """
    missing = []
    for sign in signs:
        if str(sign.get("text", "")).strip():
            continue
        photo = photos.get(str(sign.get("imageName", "")))
        if reader is not None and photo is not None:
            text = reader(photo).strip()
            if text:
                sign["text"] = text
                continue
        missing.append(str(sign.get("id", "")))
    return missing


def merge_signs(
    existing: list[dict[str, Any]], incoming: list[dict[str, Any]]
) -> tuple[list[dict[str, Any]], list[str], list[str]]:
    """同じ id の板は置き換え、新しい板は後ろに足す。

    Returns:
        取り込んだあとの板、置き換えた id、足した id。
    """
    merged = [dict(sign) for sign in existing]
    index = {sign["id"]: i for i, sign in enumerate(merged)}
    replaced, added = [], []
    for sign in incoming:
        sign_id = sign["id"]
        if sign_id in index:
            merged[index[sign_id]] = dict(sign)
            replaced.append(sign_id)
        else:
            index[sign_id] = len(merged)
            merged.append(dict(sign))
            added.append(sign_id)
    return merged, replaced, added


def dump_signs(doc: dict[str, Any]) -> str:
    """signs.json の書き方（2 文字の字下げ、日本語はそのまま、最後に改行）。"""
    return json.dumps(doc, ensure_ascii=False, indent=2) + "\n"


def gemini_reader() -> TextReader:
    """サーバーと同じ設定（環境変数）で、Gemini に写真の文字を読ませる。"""
    from app.config import load_settings
    from app.sign_vision import GeminiSignIdentifier

    settings = load_settings()
    if settings.gemini_backend == "stub":
        raise SystemExit("GEMINI_BACKEND（apikey か vertex）と、apikey なら GEMINI_API_KEY を設定してください")
    identifier = GeminiSignIdentifier(settings)

    def read(photo: bytes) -> str:
        # 候補は渡さない（読んだ文字だけを使う）
        return identifier.identify(photo, "image/jpeg", ()).read_text

    return read


def main(argv: list[str] | None = None) -> int:
    """コマンドラインの入口。"""
    parser = argparse.ArgumentParser(description="看板の記録の ZIP を signs.json に取り込む")
    parser.add_argument("zip", help="記録のページが書き出した ZIP")
    parser.add_argument("--read-text", action="store_true", help="text が空の板を写真から Gemini で読む")
    parser.add_argument("--photos-dir", help="写真を書き出すフォルダ（省略したら書き出さない）")
    parser.add_argument("--integrated", help="統合版のフォルダ（渡すと経路パックを作り直す）")
    parser.add_argument("--split", help="分割版のフォルダ")
    parser.add_argument("--dry-run", action="store_true", help="書き込まずに結果だけ出す")
    args = parser.parse_args(argv)

    incoming, photos = read_survey_zip(Path(args.zip))
    reader = gemini_reader() if args.read_text else None
    missing = fill_text(incoming, photos, reader)

    doc = json.loads(SERVER_SIGNS.read_text(encoding="utf-8"))
    merged, replaced, added = merge_signs(doc["signs"], incoming)
    doc["signs"] = merged
    for sign in incoming:
        print(f"{sign['id']}: ({sign.get('x')}, {sign.get('z')}) 向き {sign.get('azimuthDeg')} 度、文字: {sign.get('text', '')[:60]}")
    print(f"置き換え {len(replaced)} 件 {replaced}、追加 {len(added)} 件 {added}")
    if missing:
        print(f"要確認: 文字が空のままの板 {missing}（サーバーはこの板を見分けられない）")
    # 書き込む前に、経路パックを作るときと同じ形の決まりで確かめる（壊れた signs.json を残さないため）
    import jsonschema

    jsonschema.validate(doc, json.loads(SCHEMA.read_text(encoding="utf-8")))
    if args.dry_run:
        return 0

    text = dump_signs(doc)
    # Windows でも LF で書く（リポジトリの signs.json は LF）
    SERVER_SIGNS.write_text(text, encoding="utf-8", newline="\n")
    APP_SIGNS.write_text(text, encoding="utf-8", newline="\n")
    print(f"書き直しました: {SERVER_SIGNS} と {APP_SIGNS}")

    if args.photos_dir:
        out = Path(args.photos_dir)
        out.mkdir(parents=True, exist_ok=True)
        for name, data in photos.items():
            (out / f"{name}.jpg").write_bytes(data)
        print(f"写真 {len(photos)} 枚を {out} に書き出しました")

    if args.integrated and args.split:
        from app import export_contracts
        from pack import build

        build.main(["--integrated", args.integrated, "--split", args.split])
        export_contracts.main([])
    else:
        print("経路パックはまだ古いままです。--integrated と --split を付けて作り直してください")
    return 0


if __name__ == "__main__":
    sys.exit(main())
