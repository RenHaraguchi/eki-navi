"""API の入出力を確かめる。"""

from __future__ import annotations

from dataclasses import replace

import pytest
from fastapi.testclient import TestClient

from app.config import ConfigError, Settings, load_settings
from app.main import create_app

EAST = "B1:JR東日本:東改札"


def test_healthz_reports_the_pack_version(client: TestClient) -> None:
    body = client.get("/healthz").json()

    assert body["status"] == "ok"
    assert body["packVersion"]


def test_route_from_sign(client: TestClient) -> None:
    response = client.post(
        "/v1/route", json={"fromSignId": "S-B1-CENTER", "toGateId": EAST}
    )

    assert response.status_code == 200
    body = response.json()
    assert body["goal"]["id"] == EAST
    assert body["points"][0].keys() == {"x", "z", "ordinal"}
    assert body["signsOnRoute"]


def test_unknown_gate_uses_the_common_error_format(client: TestClient) -> None:
    response = client.post(
        "/v1/route", json={"fromSignId": "S-B1-CENTER", "toGateId": "ない"}
    )

    assert response.status_code == 404
    assert response.json()["error"]["code"] == "unknown_gate"


@pytest.mark.parametrize(
    ("path", "body"),
    [
        ("/v1/route", {"fromSignId": "S-B1-CENTER"}),
        ("/v1/route", {"fromSignId": "S-B1-CENTER", "toGateId": "x" * 500}),
        ("/v1/destination", {"text": ""}),
        ("/v1/destination", {"text": "あ" * 201}),
    ],
)
def test_invalid_input_is_rejected_without_echoing_it(
    client: TestClient, path: str, body: dict[str, str]
) -> None:
    response = client.post(path, json=body)

    assert response.status_code == 422
    assert response.json() == {
        "error": {"code": "invalid_request", "message": "入力の形式が正しくありません"}
    }


def test_both_starts_are_rejected(client: TestClient) -> None:
    response = client.post(
        "/v1/route",
        json={"fromSignId": "S-B1-WEST", "fromGateId": EAST, "toGateId": EAST},
    )

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "invalid_request"


def test_destination_rejects_an_unknown_sign(client: TestClient) -> None:
    response = client.post(
        "/v1/destination", json={"text": "京王線", "fromSignId": "ない"}
    )

    assert response.status_code == 404
    assert response.json()["error"]["code"] == "unknown_sign"


def test_rate_limit_applies_to_the_route_api() -> None:
    client = TestClient(create_app(replace(Settings(), rate_per_minute=1)))
    body = {"fromSignId": "S-B1-CENTER", "toGateId": EAST}

    assert client.post("/v1/route", json=body).status_code == 200
    limited = client.post("/v1/route", json=body)

    assert limited.status_code == 429
    assert limited.json()["error"]["code"] == "rate_limited"
    assert limited.headers["x-content-type-options"] == "nosniff"


def test_rate_limit_for_gemini_is_separate() -> None:
    settings = replace(Settings(), gemini_rate_per_minute=1)
    client = TestClient(create_app(settings))
    body = {"text": "京王線"}

    assert client.post("/v1/destination", json=body).status_code == 200
    assert client.post("/v1/destination", json=body).status_code == 429
    route = {"fromSignId": "S-B1-CENTER", "toGateId": EAST}
    assert client.post("/v1/route", json=route).status_code == 200


def test_reading_station_data_is_not_limited() -> None:
    client = TestClient(create_app(replace(Settings(), rate_per_minute=1)))

    assert client.get("/v1/station").status_code == 200
    assert client.get("/v1/station").status_code == 200
    assert client.get("/v1/site").status_code == 200


def test_unknown_path_uses_the_common_error_format(client: TestClient) -> None:
    response = client.get("/v1/ない")

    assert response.status_code == 404
    assert response.json()["error"]["code"] == "http_error"


def test_error_messages_do_not_echo_input(client: TestClient) -> None:
    response = client.post(
        "/v1/route", json={"fromSignId": "S-B1-CENTER", "toGateId": "<script>"}
    )

    assert "<script>" not in response.text


def test_unexpected_error_uses_the_common_error_format() -> None:
    app = create_app(Settings())

    @app.get("/boom")
    def boom() -> None:
        raise RuntimeError("内部の詳細")

    client = TestClient(app, raise_server_exceptions=False)
    response = client.get("/boom")

    assert response.status_code == 500
    assert response.json()["error"]["code"] == "internal_error"
    assert "内部の詳細" not in response.text


def test_page_is_served_with_a_content_security_policy(client: TestClient) -> None:
    response = client.get("/")

    assert response.status_code == 200
    assert "script-src 'self'" in response.headers["content-security-policy"]
    assert response.headers["x-content-type-options"] == "nosniff"
    assert client.get("/static/app.js").status_code == 200


def test_map_returns_lines_for_a_known_floor(client: TestClient) -> None:
    assert client.get("/v1/map", params={"ordinal": -1}).json()["lines"]
    assert client.get("/v1/map", params={"ordinal": 99}).status_code == 404


def test_settings_require_credentials_for_real_backends() -> None:
    with pytest.raises(ConfigError):
        load_settings({"GEMINI_BACKEND": "apikey"})
    with pytest.raises(ConfigError):
        load_settings({"GEMINI_BACKEND": "vertex"})
    with pytest.raises(ConfigError):
        load_settings({"GEMINI_BACKEND": "なにか"})


def test_settings_drop_links_that_are_not_http() -> None:
    settings = load_settings(
        {
            "DEMO_VIDEO_URL": "javascript:alert(1)",
            "DEMO_APK_URL": "https://example.com/a",
        }
    )

    assert settings.video_url == ""
    assert settings.apk_url == "https://example.com/a"


def test_route_can_stay_on_the_same_floor(client: TestClient) -> None:
    body = {
        "fromGateId": "2F:JR東日本:新南口改札",
        "toGateId": "2F:JR東日本:東南改札",
    }
    normal = client.post("/v1/route", json=body).json()
    flat = client.post("/v1/route", json={**body, "sameFloor": True}).json()

    assert {p["ordinal"] for p in normal["points"]} != {2.0}
    assert {p["ordinal"] for p in flat["points"]} == {2.0}


def test_survey_page_is_served_with_a_content_security_policy(
    client: TestClient,
) -> None:
    response = client.get("/survey")

    assert response.status_code == 200
    assert "script-src 'self'" in response.headers["content-security-policy"]
    assert client.get("/static/survey.js").status_code == 200
    gates = client.get("/v1/map", params={"ordinal": 2}).json()["gates"]
    assert {"id", "name", "operator", "x", "z"} <= gates[0].keys()
