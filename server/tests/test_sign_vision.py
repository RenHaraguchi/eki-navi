"""カメラの画像から案内板を見分ける API のテスト。Gemini は呼ばない。"""

from __future__ import annotations

import base64
import json
from typing import Any

import httpx
import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from app.sign_vision import (
    Box,
    GeminiSignIdentifier,
    SignCandidate,
    SignIdentification,
    SignVisionError,
    build_request_body,
    candidates_of,
    parse_response,
)

JPEG = b"\xff\xd8\xff\xe0" + b"\x00" * 32
CANDIDATES = (
    SignCandidate("S-A", "A の板", "京王線 →"),
    SignCandidate("S-B", "B の板", ""),
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


def _client(identifier: FakeIdentifier | None) -> TestClient:
    return TestClient(create_app(Settings(), sign_identifier=identifier))


def _body(**overrides: object) -> dict[str, object]:
    body: dict[str, object] = {
        "imageBase64": base64.b64encode(JPEG).decode("ascii"),
        "mimeType": "image/jpeg",
        "candidateSignIds": ["S-B1-WEST", "S-B1-CENTER"],
    }
    body.update(overrides)
    return body


def test_request_lists_candidates_and_limits_the_answer() -> None:
    body = build_request_body(JPEG, "image/jpeg", CANDIDATES)

    instruction = body["systemInstruction"]["parts"][0]["text"]  # type: ignore[index]
    assert "S-A: A の板（書いてある文字: 京王線 →）" in instruction
    assert "S-B: B の板\n" in instruction + "\n"
    schema = body["generationConfig"]["responseSchema"]  # type: ignore[index]
    assert schema["properties"]["signId"]["enum"] == ["S-A", "S-B", "none"]
    image = body["contents"][0]["parts"][0]["inlineData"]  # type: ignore[index]
    assert base64.b64decode(image["data"]) == JPEG


def test_parse_converts_the_box_to_zero_to_one() -> None:
    found = parse_response(
        _payload({"signId": "S-A", "box2d": [100, 200, 300, 600]}), CANDIDATES
    )

    assert found == SignIdentification("S-A", Box(0.2, 0.1, 0.6, 0.3))


def test_parse_reads_the_corners() -> None:
    found = parse_response(
        _payload(
            {
                "signId": "S-A",
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
        _payload({"signId": "S-A", "box2d": [100, 200, 300, 600], "corners": corners}),
        CANDIDATES,
    )

    assert found.box is not None
    assert found.corners == ()


def test_parse_returns_empty_when_no_sign_is_seen() -> None:
    found = parse_response(_payload({"signId": "none"}), CANDIDATES)

    assert found == SignIdentification("", None)


@pytest.mark.parametrize(
    "answer",
    [
        {"signId": "S-X", "box2d": [0, 0, 10, 10]},  # 候補にない
        {"signId": "S-A"},  # 枠がない
        {"signId": "S-A", "box2d": [500, 0, 400, 10]},  # 上下が逆
        {"signId": "S-A", "box2d": [0, 0, 10, 1200]},  # 画像の外
    ],
)
def test_parse_rejects_bad_answers(answer: dict[str, Any]) -> None:
    with pytest.raises(SignVisionError):
        parse_response(_payload(answer), CANDIDATES)


def test_candidates_keep_only_known_ids() -> None:
    signs = [
        {"id": "S-A", "name": "A", "ordinal": -1},
        {"id": "S-B", "name": "B", "ordinal": -1, "text": "西口"},
    ]

    chosen = candidates_of(signs, ["S-B", "S-Z"], None)

    assert chosen == (SignCandidate("S-B", "B", "西口"),)


def test_api_returns_the_sign_and_box() -> None:
    identifier = FakeIdentifier(
        SignIdentification(
            "S-B1-WEST",
            Box(0.1, 0.2, 0.5, 0.4),
            ((0.1, 0.2), (0.5, 0.22), (0.5, 0.4), (0.1, 0.38)),
        )
    )

    response = _client(identifier).post("/v1/sign/identify", json=_body())

    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "ok"
    assert data["signId"] == "S-B1-WEST"
    assert data["box"] == {"xMin": 0.1, "yMin": 0.2, "xMax": 0.5, "yMax": 0.4}
    assert data["corners"][1] == {"x": 0.5, "y": 0.22}
    assert data["engine"] == "gemini"
    assert [c.id for c in identifier.seen] == ["S-B1-WEST", "S-B1-CENTER"]


def test_api_reports_not_found() -> None:
    response = _client(FakeIdentifier(SignIdentification("", None))).post(
        "/v1/sign/identify", json=_body()
    )

    data = response.json()
    assert data["status"] == "not_found"
    assert data["signId"] == ""
    assert data["box"] == {"xMin": 0.0, "yMin": 0.0, "xMax": 0.0, "yMax": 0.0}
    assert data["corners"] == []


def test_api_is_unavailable_without_gemini() -> None:
    response = _client(None).post("/v1/sign/identify", json=_body())

    data = response.json()
    assert data["status"] == "unavailable"
    assert data["engine"] == "stub"


def test_api_is_unavailable_when_gemini_fails() -> None:
    response = _client(FakeIdentifier(SignVisionError("x"))).post(
        "/v1/sign/identify", json=_body()
    )

    assert response.json()["status"] == "unavailable"


def test_api_rejects_an_image_that_is_not_jpeg() -> None:
    png = base64.b64encode(b"\x89PNG\r\n\x1a\n" + b"\x00" * 8).decode("ascii")

    response = _client(None).post("/v1/sign/identify", json=_body(imageBase64=png))

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_image"


def test_api_rejects_unknown_candidates() -> None:
    response = _client(None).post(
        "/v1/sign/identify", json=_body(candidateSignIds=["S-NOPE"])
    )

    assert response.status_code == 404
    assert response.json()["error"]["code"] == "unknown_sign"


def test_api_requires_candidates() -> None:
    response = _client(None).post("/v1/sign/identify", json=_body(candidateSignIds=[]))

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
        return httpx.Response(200, json=_payload({"signId": "none"}), request=request)

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
