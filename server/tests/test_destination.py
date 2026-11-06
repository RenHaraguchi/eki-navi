"""行き先の判定を確かめる。Gemini の通信は差し替える。"""

from __future__ import annotations

import json
import logging
from dataclasses import replace
from typing import Any

import httpx
import pytest

from app.catalog import build_targets, match_dictionary
from app.config import Settings
from app.destination import DestinationResolver
from app.gemini import (
    Classification,
    ClassifierError,
    GeminiClassifier,
    MetadataToken,
    build_request_body,
    parse_response,
)
from app.limits import WindowLimiter
from app.station import Station

SIGN = "S-B1-CENTER"


class _Fixed:
    engine = "gemini"

    def __init__(self, result: Classification | Exception) -> None:
        self.result = result
        self.calls = 0

    def classify(self, text: str) -> Classification:
        self.calls += 1
        if isinstance(self.result, Exception):
            raise self.result
        return self.result


def _resolver(
    station: Station, classifier: Any = None, daily_limit: int = 10
) -> DestinationResolver:
    return DestinationResolver(
        station,
        build_targets(station.pack),
        classifier,
        WindowLimiter(daily_limit, 86400.0),
    )


@pytest.mark.parametrize(
    "text", ["京王線に乗りたい", "Keio Line please", "ｹｲｵｳ線ではなく京王線"]
)
def test_dictionary_finds_a_line(station: Station, text: str) -> None:
    result = _resolver(station).resolve(text, SIGN)

    assert result.status == "ok"
    assert result.engine == "stub"
    assert result.candidates[0].gate["id"] == "B1:京王:京王西口"


def test_candidates_are_sorted_by_distance(station: Station) -> None:
    result = _resolver(station).resolve("京王線", SIGN)

    lengths = [item.length_meters for item in result.candidates]
    assert lengths == sorted(lengths)
    assert lengths[0] > 0


def test_same_name_on_two_floors_is_ambiguous(station: Station) -> None:
    result = _resolver(station).resolve("東改札に行きたい", SIGN)

    assert result.status == "ambiguous"
    assert {item.gate["operator"] for item in result.candidates} == {
        "JR東日本",
        "東京メトロ",
    }


def test_longest_alias_wins(station: Station) -> None:
    found = match_dictionary("西口地下改札まで", build_targets(station.pack))

    assert [target.id for target in found] == ["gate:B1:小田急:西口地下改札"]


def test_unknown_text_is_unavailable_with_the_dictionary(station: Station) -> None:
    result = _resolver(station).resolve("よくわからない文章", SIGN)

    assert result.status == "unavailable"
    assert result.candidates == ()


def test_length_is_unknown_without_a_sign(station: Station) -> None:
    result = _resolver(station).resolve("京王線")

    assert result.candidates[0].length_meters == -1.0


def test_gemini_answer_is_expanded_to_gates(station: Station) -> None:
    classifier = _Fixed(Classification("ok", ("line:busta",)))

    result = _resolver(station, classifier).resolve("空港に行くバスに乗りたい", SIGN)

    assert result.engine == "gemini"
    assert result.status == "ok"
    assert result.candidates[0].gate["id"] == "3F:バスタ新宿:バスタ新宿"


def test_gemini_ambiguous_returns_the_nearest_gate_of_each_target(
    station: Station,
) -> None:
    classifier = _Fixed(Classification("ambiguous", ("line:keio", "line:keio-new")))

    result = _resolver(station, classifier).resolve("京王", SIGN)

    assert result.status == "ambiguous"
    assert [item.gate["id"] for item in result.candidates] == [
        "B1:京王:京王西口",
        "B1:京王:京王新線口",
    ]


def test_duplicate_targets_keep_the_nearest_first(station: Station) -> None:
    # 路線と改札が同じ改札を指しても、順序が崩れないこと
    classifier = _Fixed(
        Classification(
            "ambiguous", ("line:keio-new", "line:keio", "gate:B1:京王:京王西口")
        )
    )

    result = _resolver(station, classifier).resolve("京王", SIGN)

    assert [item.gate["id"] for item in result.candidates] == [
        "B1:京王:京王西口",
        "B1:京王:京王新線口",
    ]


def test_same_gate_from_line_and_gate_is_not_ambiguous(station: Station) -> None:
    result = _resolver(station).resolve("バスタ新宿に行きたい", SIGN)

    assert result.status == "ok"
    assert len(result.candidates) == 1


def test_several_gates_without_a_start_are_ambiguous(station: Station) -> None:
    # 出発が分からないと近い改札を選べない
    result = _resolver(station).resolve("小田急線")

    assert result.status == "ambiguous"
    assert len(result.candidates) == 3


def test_gemini_out_of_scope(station: Station) -> None:
    classifier = _Fixed(Classification("out_of_scope", ()))

    result = _resolver(station, classifier).resolve("今日の天気は", SIGN)

    assert result.status == "out_of_scope"


def test_gemini_failure_falls_back_to_the_dictionary(station: Station) -> None:
    classifier = _Fixed(ClassifierError("失敗"))

    result = _resolver(station, classifier).resolve("京王線", SIGN)

    assert result.engine == "dictionary"
    assert result.status == "ok"


def test_daily_limit_stops_calling_gemini(station: Station) -> None:
    classifier = _Fixed(Classification("ok", ("line:keio",)))
    resolver = _resolver(station, classifier, daily_limit=1)

    assert resolver.resolve("京王線", SIGN).engine == "gemini"
    assert resolver.resolve("京王線", SIGN).engine == "dictionary"
    assert classifier.calls == 1


def test_input_text_is_not_logged(
    station: Station, caplog: pytest.LogCaptureFixture
) -> None:
    private_text = "予約番号12345の京王線"

    with caplog.at_level(logging.INFO):
        _resolver(station).resolve(private_text, SIGN)

    assert "12345" not in caplog.text


def test_request_body_limits_answers_to_the_catalog(station: Station) -> None:
    targets = build_targets(station.pack)

    body = build_request_body("京王線", targets)

    schema = body["generationConfig"]["responseSchema"]  # type: ignore[index]
    allowed = schema["properties"]["targetIds"]["items"]["enum"]
    assert "line:keio" in allowed
    assert body["contents"][0]["parts"][0]["text"] == "京王線"  # type: ignore[index]


def _gemini_payload(answer: dict[str, Any]) -> dict[str, Any]:
    return {"candidates": [{"content": {"parts": [{"text": json.dumps(answer)}]}}]}


def test_parse_response_accepts_a_valid_answer(station: Station) -> None:
    payload = _gemini_payload({"status": "ok", "targetIds": ["line:keio"]})

    result = parse_response(payload, build_targets(station.pack))

    assert result == Classification("ok", ("line:keio",))


@pytest.mark.parametrize(
    "payload",
    [
        {},
        {"candidates": []},
        {"candidates": [{"content": {"parts": [{"text": "JSON ではない"}]}}]},
        _gemini_payload({"status": "ok"}),
        _gemini_payload({"status": "ok", "targetIds": ["line:存在しない"]}),
        _gemini_payload({"status": "なにか", "targetIds": []}),
    ],
)
def test_parse_response_rejects_invalid_answers(
    station: Station, payload: dict[str, Any]
) -> None:
    with pytest.raises(ClassifierError):
        parse_response(payload, build_targets(station.pack))


def _response(status: int, body: dict[str, Any]) -> httpx.Response:
    request = httpx.Request("POST", "https://example.test")
    return httpx.Response(status, json=body, request=request)


def test_api_key_is_sent_in_a_header_not_the_url(
    station: Station, monkeypatch: pytest.MonkeyPatch
) -> None:
    captured: dict[str, Any] = {}

    def fake_post(url: str, **kwargs: Any) -> httpx.Response:
        captured.update(url=url, **kwargs)
        return _response(
            200, _gemini_payload({"status": "ok", "targetIds": ["line:keio"]})
        )

    monkeypatch.setattr(httpx, "post", fake_post)
    settings = replace(Settings(), gemini_backend="apikey", gemini_api_key="dummy-key")

    result = GeminiClassifier(settings, build_targets(station.pack)).classify("京王線")

    assert result.status == "ok"
    assert "dummy-key" not in captured["url"]
    assert captured["headers"]["x-goog-api-key"] == "dummy-key"


def test_http_error_does_not_leak_the_key(
    station: Station, monkeypatch: pytest.MonkeyPatch
) -> None:
    def fake_post(url: str, **kwargs: Any) -> httpx.Response:
        raise httpx.ConnectError("https://example.test?key=dummy-key")

    monkeypatch.setattr(httpx, "post", fake_post)
    settings = replace(Settings(), gemini_backend="apikey", gemini_api_key="dummy-key")

    with pytest.raises(ClassifierError) as error:
        GeminiClassifier(settings, build_targets(station.pack)).classify("京王線")

    assert "dummy-key" not in str(error.value)
    assert error.value.__cause__ is None


def test_token_response_without_fields_is_a_classifier_error(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(httpx, "get", lambda url, **kwargs: _response(200, {}))

    with pytest.raises(ClassifierError):
        MetadataToken(1.0).get()


def test_vertex_uses_the_service_account_token(
    station: Station, monkeypatch: pytest.MonkeyPatch
) -> None:
    captured: dict[str, Any] = {}

    def fake_post(url: str, **kwargs: Any) -> httpx.Response:
        captured.update(url=url, **kwargs)
        return _response(
            200, _gemini_payload({"status": "ok", "targetIds": ["line:keio"]})
        )

    monkeypatch.setattr(httpx, "post", fake_post)
    monkeypatch.setattr(MetadataToken, "get", lambda self: "short-token")
    settings = replace(Settings(), gemini_backend="vertex", google_project="demo")

    GeminiClassifier(settings, build_targets(station.pack)).classify("京王線")

    assert captured["url"].startswith(
        "https://aiplatform.googleapis.com/v1/projects/demo/locations/global/"
    )
    assert captured["headers"]["Authorization"] == "Bearer short-token"
