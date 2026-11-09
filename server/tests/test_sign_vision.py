"""カメラの画像から案内板を見分ける API のテスト。Gemini は呼ばない。"""

from __future__ import annotations

import base64
import json
from pathlib import Path
from typing import Any

import httpx
import pytest
from fastapi.testclient import TestClient

from app.config import DEFAULT_PACK_PATH, Settings
from app.main import create_app
from app.sign_vision import (
    Box,
    GeminiSignIdentifier,
    SignCandidate,
    SignIdentification,
    SignVisionError,
    best_match,
    build_request_body,
    candidates_of,
    first_arrow,
    is_match,
    leading_arrow,
    order_similarity,
    parse_response,
    readings_of,
    text_match_score,
    text_overlap,
)

JPEG = b"\xff\xd8\xff\xe0" + b"\x00" * 32
SIGN_A = (
    "↑ 小田急線 Odakyu Line｜京王線 Keio Line｜京王新線 通路 Keio New Line Concourse｜"
    "京王新線 Keio New Line｜都営大江戸線新宿駅 Oedo Line, Shinjuku Sta.｜"
    "都営新宿線 Shinjuku Line｜都庁第二本庁舎方面 for TMG No.2 Bldg.（白地に黒い文字）"
)
CANDIDATES = (
    SignCandidate("S-A", "A の板", SIGN_A),
    SignCandidate("S-B", "B の板", "← 新宿御苑方面 東口｜ニュウマン｜バスタ新宿 →"),
    SignCandidate("S-C", "文字のない板", ""),
)

# 2026-11-07 の写真を、候補を見せずに Gemini に読ませた結果
READ_SIGN_A = (
    "↑ 小田急線 Odakyu Line 京王線 Keio Line 京王新線 通路 Keio New Line Concourse "
    "京王新線 Keio New Line 都営新宿線 Shinjuku Line 大江戸線新宿駅 "
    "Oedo Line, Shinjuku Sta. 都庁第二本庁舎方面 for TMG No.2 Bldg."
)
READ_OTHER_SIGN = "← 甲州街道 小田急線 京王線 →"
READ_JR_GATE = "JR 新宿駅 Shinjuku Station 南改札 South Gate JY JC JB JA JS"
# 遠くから撮った写真で、英語の部分を読まなかったとき
READ_SIGN_A_FAR = (
    "↑ 小田急線 | 京王線 ⑧ 京王新線・都営大江戸線新宿駅 Ⓞ 都庁前・都庁第二本庁舎方面"
)
# 同じ路線名を多く含む、別の大きな板
READ_BIG_OTHER_SIGN = (
    "←新宿御苑方面 東口 ↑ 高速・空港バス(バスタ新宿) ↗ 副都心線新宿三丁目駅 "
    "ニュウマン ミライナタワー方面 大江戸線新宿駅 京王新線 京王新線 小田急線 京王線 → "
    "都庁第二本庁舎方面 都営新宿線"
)


def _payload(answer: dict[str, Any]) -> dict[str, object]:
    return {"candidates": [{"content": {"parts": [{"text": json.dumps(answer)}]}}]}


class FakeIdentifier:
    """決まった答えを返す。受け取った候補を覚えておく。"""

    def __init__(self, result: SignIdentification | Exception) -> None:
        self.result = result
        self.seen: tuple[SignCandidate, ...] = ()

    def identify(
        self, image: bytes, mime_type: str, candidates: tuple[SignCandidate, ...]
    ) -> SignIdentification:
        self.seen = candidates
        if isinstance(self.result, Exception):
            raise self.result
        return self.result


@pytest.fixture()
def pack_path(tmp_path: Path) -> Path:
    """コミットしてある経路パックの、地下1階の2枚を「推定」にしたもの（仮の板は候補にならないため）。"""
    pack = json.loads(DEFAULT_PACK_PATH.read_text(encoding="utf-8"))
    for sign in pack["signs"]:
        if sign["id"] in ("S-B1-WEST", "S-B1-CENTER"):
            sign["confidence"] = "推定"
    path = tmp_path / "pack.json"
    path.write_text(json.dumps(pack, ensure_ascii=False), encoding="utf-8")
    return path


def _client(identifier: FakeIdentifier | None, pack_path: Path) -> TestClient:
    return TestClient(
        create_app(Settings(pack_path=pack_path), sign_identifier=identifier)
    )


def _body(**overrides: object) -> dict[str, object]:
    body: dict[str, object] = {
        "imageBase64": base64.b64encode(JPEG).decode("ascii"),
        "mimeType": "image/jpeg",
        "candidateSignIds": ["S-B1-WEST", "S-B1-CENTER", "S-B1-EAST"],
    }
    body.update(overrides)
    return body


def test_request_does_not_show_the_candidates() -> None:
    # 候補を見せると、読めない文字を候補の説明で埋めてしまうため
    body = build_request_body(JPEG, "image/jpeg")

    instruction = body["systemInstruction"]["parts"][0]["text"]  # type: ignore[index]
    assert "S-A" not in instruction
    assert "小田急線" not in instruction
    schema = body["generationConfig"]["responseSchema"]  # type: ignore[index]
    assert schema["required"] == ["readText"]
    config = body["generationConfig"]  # type: ignore[index]
    assert config["thinkingConfig"] == {"thinkingLevel": "low"}
    image = body["contents"][0]["parts"][0]["inlineData"]  # type: ignore[index]
    assert base64.b64decode(image["data"]) == JPEG


def test_photos_of_the_sign_match_and_other_signs_do_not() -> None:
    assert is_match(READ_SIGN_A, SIGN_A)
    assert is_match(READ_SIGN_A_FAR, SIGN_A), "英語を読まなくても合う"
    assert not is_match(READ_OTHER_SIGN, SIGN_A)
    assert not is_match(READ_JR_GATE, SIGN_A)
    precision, recall = text_overlap(READ_BIG_OTHER_SIGN, SIGN_A)
    assert recall > 0.75 and precision < 0.5, "路線名は多く重なるが、ほかの文字が多い"
    assert not is_match(READ_BIG_OTHER_SIGN, SIGN_A)


def test_score_ignores_width_spaces_and_the_appearance_note() -> None:
    assert (
        text_match_score("ＡＢＣ　京王線　新宿", "abc 京王線 新宿（白地に黒い文字）")
        == 1.0
    )
    assert text_match_score("ABC Line", "abc line"), "日本語のない板は英字で比べる"
    assert text_match_score("", SIGN_A) == 0.0
    assert text_match_score("京王線", "") == 0.0


SIGNS_SAME_BUT_ARROW = (
    SignCandidate(
        "S-UP", "上", "↑ 京王線｜京王新線通路｜都営新宿線｜大江戸線新宿駅｜南口"
    ),
    SignCandidate(
        "S-LEFT", "左", "← 京王線｜↰ 京王新線通路｜都営新宿線｜大江戸線新宿駅｜南口"
    ),
    SignCandidate(
        "S-RIGHT", "右", "→ 京王線｜京王新線通路｜都営新宿線｜大江戸線新宿駅｜南口"
    ),
)


@pytest.mark.parametrize(
    ("read_text", "expected"),
    [
        ("↑ 京王線 京王新線通路 都営新宿線 大江戸線新宿駅 南口", "S-UP"),
        ("←京王線 京王新線通路 都営新宿線 大江戸線新宿駅 南口", "S-LEFT"),
        ("→ 京王線 Keio Line 京王新線通路 都営新宿線 大江戸線新宿駅 南口", "S-RIGHT"),
        ("↓ 京王線 京王新線通路 都営新宿線 大江戸線新宿駅 南口", ""),
    ],
)
def test_signs_that_differ_only_by_the_arrow(read_text: str, expected: str) -> None:
    assert best_match(read_text, SIGNS_SAME_BUT_ARROW)[0] == expected


def test_turn_arrows_count_as_their_direction() -> None:
    assert first_arrow("↰ 京王新線通路") == "←"
    assert first_arrow("京王線 ↱") == "→"
    assert first_arrow("京王線") == ""


def test_best_match_skips_candidates_without_text() -> None:
    assert best_match(READ_SIGN_A, CANDIDATES)[0] == "S-A"
    assert best_match("文字のない板", CANDIDATES) == ("", 0.0)


def test_parse_matches_the_read_text_and_converts_the_box() -> None:
    found = parse_response(
        _payload({"readText": READ_SIGN_A, "box2d": [100, 200, 300, 600]}), CANDIDATES
    )

    assert found.sign_id == "S-A"
    assert found.box == Box(0.2, 0.1, 0.6, 0.3)
    assert found.read_text == READ_SIGN_A
    assert found.score > 0.9


def test_parse_reads_the_corners() -> None:
    found = parse_response(
        _payload(
            {
                "readText": READ_SIGN_A,
                "box2d": [100, 200, 300, 600],
                "corners": [[120, 200], [100, 600], [300, 600], [280, 200]],
            }
        ),
        CANDIDATES,
    )

    assert found.corners == ((0.2, 0.12), (0.6, 0.1), (0.6, 0.3), (0.2, 0.28))


@pytest.mark.parametrize(
    "corners",
    [
        [[100, 600], [120, 200], [300, 600], [280, 200]],  # 左右が逆
        [[120, 200], [100, 600], [300, 600]],  # 3つしかない
        [[120, 200], [100, 600], [300, 1600], [280, 200]],  # 画像の外
    ],
)
def test_parse_drops_bad_corners_but_keeps_the_box(corners: list[list[int]]) -> None:
    found = parse_response(
        _payload(
            {"readText": READ_SIGN_A, "box2d": [100, 200, 300, 600], "corners": corners}
        ),
        CANDIDATES,
    )

    assert found.box is not None
    assert found.corners == ()


@pytest.mark.parametrize(
    "read_text", [READ_OTHER_SIGN, READ_JR_GATE, READ_BIG_OTHER_SIGN, ""]
)
def test_parse_returns_empty_for_other_signs(read_text: str) -> None:
    found = parse_response(
        _payload({"readText": read_text, "box2d": [100, 200, 300, 600]}), CANDIDATES
    )

    assert found.sign_id == ""
    assert found.box is None
    assert found.read_text == read_text


@pytest.mark.parametrize(
    "answer",
    [
        {"box2d": [0, 0, 10, 10]},  # 読んだ文字がない
        {"readText": READ_SIGN_A},  # 板が見つかったのに枠がない
        {"readText": READ_SIGN_A, "box2d": [500, 0, 400, 10]},  # 上下が逆
        {"readText": READ_SIGN_A, "box2d": [0, 0, 10, 1200]},  # 画像の外
    ],
)
def test_parse_rejects_bad_answers(answer: dict[str, Any]) -> None:
    with pytest.raises(SignVisionError):
        parse_response(_payload(answer), CANDIDATES)


def test_candidates_keep_only_known_ids_and_skip_placeholders() -> None:
    signs = [
        {"id": "S-A", "name": "A", "ordinal": -1, "confidence": "仮"},
        {"id": "S-B", "name": "B", "ordinal": -1, "text": "西口", "confidence": "推定"},
        {"id": "S-C", "name": "C", "ordinal": -1},
    ]

    chosen = candidates_of(signs, ["S-A", "S-B", "S-Z"], None)

    assert chosen == (SignCandidate("S-B", "B", "西口"),)


def test_api_returns_the_sign_and_box(pack_path: Path) -> None:
    identifier = FakeIdentifier(
        SignIdentification(
            "S-B1-WEST",
            Box(0.1, 0.2, 0.5, 0.4),
            ((0.1, 0.2), (0.5, 0.22), (0.5, 0.4), (0.1, 0.38)),
            "読んだ文字",
            0.91234,
        )
    )

    response = _client(identifier, pack_path).post("/v1/sign/identify", json=_body())

    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "ok"
    assert data["signId"] == "S-B1-WEST"
    assert data["box"] == {"xMin": 0.1, "yMin": 0.2, "xMax": 0.5, "yMax": 0.4}
    assert data["corners"][1] == {"x": 0.5, "y": 0.22}
    assert data["readText"] == "読んだ文字"
    assert data["matchScore"] == 0.912
    assert data["engine"] == "gemini"
    # S-B1-EAST は仮の板なので候補に入らない
    assert [c.id for c in identifier.seen] == ["S-B1-WEST", "S-B1-CENTER"]


def test_api_reports_not_found(pack_path: Path) -> None:
    identifier = FakeIdentifier(SignIdentification("", None, (), "別の板", 0.2))

    response = _client(identifier, pack_path).post("/v1/sign/identify", json=_body())

    data = response.json()
    assert data["status"] == "not_found"
    assert data["signId"] == ""
    assert data["box"] == {"xMin": 0.0, "yMin": 0.0, "xMax": 0.0, "yMax": 0.0}
    assert data["corners"] == []
    assert data["readText"] == "別の板"


def test_api_is_unavailable_without_gemini(pack_path: Path) -> None:
    response = _client(None, pack_path).post("/v1/sign/identify", json=_body())

    data = response.json()
    assert data["status"] == "unavailable"
    assert data["engine"] == "stub"


def test_api_is_unavailable_when_gemini_fails(pack_path: Path) -> None:
    response = _client(FakeIdentifier(SignVisionError("x")), pack_path).post(
        "/v1/sign/identify", json=_body()
    )

    assert response.json()["status"] == "unavailable"


def test_api_rejects_an_image_that_is_not_jpeg(pack_path: Path) -> None:
    png = base64.b64encode(b"\x89PNG\r\n\x1a\n" + b"\x00" * 8).decode("ascii")

    response = _client(None, pack_path).post(
        "/v1/sign/identify", json=_body(imageBase64=png)
    )

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_image"


def test_api_rejects_unknown_or_placeholder_candidates(pack_path: Path) -> None:
    for ids in (["S-NOPE"], ["S-B1-EAST"]):
        response = _client(None, pack_path).post(
            "/v1/sign/identify", json=_body(candidateSignIds=ids)
        )

        assert response.status_code == 404
        assert response.json()["error"]["code"] == "unknown_sign"


def test_api_requires_candidates(pack_path: Path) -> None:
    response = _client(None, pack_path).post(
        "/v1/sign/identify", json=_body(candidateSignIds=[])
    )

    assert response.status_code == 422


def test_gemini_identifier_hides_the_key_on_errors(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def fake_post(url: str, **kwargs: Any) -> httpx.Response:
        raise httpx.ConnectError("https://example.test?key=dummy-key")

    monkeypatch.setattr(httpx, "post", fake_post)
    identifier = GeminiSignIdentifier(
        Settings(gemini_backend="apikey", gemini_api_key="dummy-key")
    )

    with pytest.raises(SignVisionError) as caught:
        identifier.identify(JPEG, "image/jpeg", CANDIDATES)

    assert "dummy-key" not in str(caught.value)


def test_gemini_identifier_uses_the_vision_model(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    seen: dict[str, str] = {}

    def fake_post(url: str, **kwargs: Any) -> httpx.Response:
        seen["url"] = url
        request = httpx.Request("POST", url)
        return httpx.Response(200, json=_payload({"readText": ""}), request=request)

    monkeypatch.setattr(httpx, "post", fake_post)
    identifier = GeminiSignIdentifier(
        Settings(
            gemini_backend="apikey",
            gemini_api_key="dummy-key",
            gemini_vision_model="vision-model",
        )
    )

    found = identifier.identify(JPEG, "image/jpeg", CANDIDATES)

    assert found.sign_id == ""
    assert "/models/vision-model:" in seen["url"]


# 同じ梁の表と裏。同じ文字を逆の順で持つ
# （2026-11-09 の地下1階 UHV と DBZ を短くしたもの）
SIGN_FRONT = SignCandidate(
    "S-FRONT",
    "表",
    "← 中央東改札 中央西改札 小田急線 ｜ 東改札 西改札 西武新宿線 →",
)
SIGN_BACK = SignCandidate(
    "S-BACK",
    "裏",
    "← 東改札 西改札 西武新宿線 ｜ 中央東改札 中央西改札 小田急線 →",
)


@pytest.mark.parametrize(
    ("read_text", "expected"),
    [
        (
            "← 中央東改札 中央西改札 小田急線 東改札 西改札 西武新宿線 →",
            "S-FRONT",
        ),
        (
            "← 東改札 西改札 西武新宿線 中央東改札 中央西改札 小田急線 →",
            "S-BACK",
        ),
    ],
)
def test_front_and_back_with_the_same_words_are_told_apart_by_order(
    read_text: str, expected: str
) -> None:
    assert best_match(read_text, (SIGN_FRONT, SIGN_BACK))[0] == expected


def test_order_similarity_uses_the_closest_reading() -> None:
    joined = "↑ 中央東改札 ｜ ↑ 中央東改札 埼京線 湘南新宿ライン"
    assert order_similarity("↑ 中央東改札 埼京線 湘南新宿ライン", joined) == 1.0
    assert order_similarity("", joined) == 0.0


def test_readings_include_the_whole_text_and_each_read() -> None:
    assert readings_of("A ｜ B") == ["A ｜ B", "A", "B"]
    assert readings_of("A") == ["A"]


def test_only_the_leading_arrow_is_compared() -> None:
    # U ターンの矢印など、先頭の矢印を登録していない板は、後ろの矢印で弾かない
    assert leading_arrow("9・10 中央本線 →") == ""
    assert leading_arrow("← 9・10 中央本線") == "←"
    assert is_match(
        "← 9・10 中央本線 中央線 湘南新宿ライン 埼京線",
        "9・10 中央本線 中央線 湘南新宿ライン 埼京線 →",
    )


def test_front_and_back_without_arrows_are_told_apart_by_order() -> None:
    # 矢印がなく、文字の重なりがほぼ同じでも、並び順で見分ける
    front = SignCandidate(
        "S-F", "表", "中央東改札 中央西改札 小田急線 ｜ 東改札 西改札 西武新宿線"
    )
    back = SignCandidate(
        "S-B", "裏", "東改札 西改札 西武新宿線 ｜ 中央東改札 中央西改札 小田急線"
    )
    read_front = "中央東改札 中央西改札 小田急線 東改札 西改札 西武新宿線"
    read_back = "東改札 西改札 西武新宿線 中央東改札 中央西改札 小田急線"
    assert best_match(read_front, (front, back))[0] == "S-F"
    assert best_match(read_back, (front, back))[0] == "S-B"
