"""Play Integrity の判定のテスト。Google は呼ばない。"""

from __future__ import annotations

import base64
from dataclasses import replace
from typing import Any

import pytest
from fastapi.testclient import TestClient

from app.config import ConfigError, Settings, load_settings
from app.integrity import IntegrityError, IntegrityGate, check_payload, normalize_digest
from app.main import create_app

NOW = 1_800_000_000.0
DIGEST_HEX = ":".join(["AB"] * 32)
DIGEST_B64 = base64.urlsafe_b64encode(bytes([0xAB] * 32)).decode().rstrip("=")
JPEG = b"\xff\xd8\xff\xe0" + b"\x00" * 32


def _payload(**overrides: Any) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "requestDetails": {
            "requestPackageName": "com.ekinavi.app",
            "timestampMillis": str(int((NOW - 30) * 1000)),
        },
        "appIntegrity": {
            "appRecognitionVerdict": "UNRECOGNIZED_VERSION",
            "certificateSha256Digest": [DIGEST_B64],
        },
        "deviceIntegrity": {"deviceRecognitionVerdict": ["MEETS_DEVICE_INTEGRITY"]},
    }
    payload.update(overrides)
    return payload


def _check(payload: dict[str, Any], digests: frozenset[str] = frozenset()) -> str:
    return check_payload(payload, "com.ekinavi.app", digests, 600, NOW * 1000).reason


def test_digest_accepts_keytool_hex_and_base64url() -> None:
    assert normalize_digest(DIGEST_HEX) == DIGEST_B64
    assert normalize_digest(DIGEST_B64 + "=") == DIGEST_B64


def test_sideloaded_app_on_a_real_device_passes() -> None:
    assert _check(_payload(), frozenset({DIGEST_B64})) == "ok"


def test_other_package_fails() -> None:
    payload = _payload(
        requestDetails={"requestPackageName": "x.y", "timestampMillis": str(NOW)}
    )
    assert _check(payload) == "package_mismatch"


def test_old_token_fails() -> None:
    old = {
        "requestPackageName": "com.ekinavi.app",
        "timestampMillis": str(int((NOW - 3600) * 1000)),
    }
    assert _check(_payload(requestDetails=old)) == "token_too_old"


def test_other_signing_key_fails() -> None:
    other = normalize_digest(":".join(["CD"] * 32))
    assert _check(_payload(), frozenset({other})) == "certificate_mismatch"


def test_emulator_or_rooted_device_fails() -> None:
    payload = _payload(deviceIntegrity={"deviceRecognitionVerdict": []})
    assert _check(payload) == "device_not_trusted"


def test_mode_is_validated() -> None:
    with pytest.raises(ConfigError):
        load_settings({"INTEGRITY_MODE": "on"})
    settings = load_settings(
        {"INTEGRITY_MODE": "log", "INTEGRITY_CERT_SHA256": f"{DIGEST_HEX}, x"}
    )
    assert settings.integrity_mode == "log"
    assert settings.integrity_cert_digests == (DIGEST_HEX, "x")


def test_off_never_calls_google() -> None:
    def decoder(_: str) -> dict[str, Any]:
        raise AssertionError("呼ばれないはず")

    gate = IntegrityGate(Settings(), decoder)
    assert gate.allows("")


def test_decoded_token_is_reused() -> None:
    calls: list[str] = []

    def decoder(token: str) -> dict[str, Any]:
        calls.append(token)
        return _payload()

    gate = IntegrityGate(
        replace(Settings(), integrity_mode="enforce"), decoder, clock=lambda: NOW
    )
    assert gate.allows("t1")
    assert gate.allows("t1")
    assert calls == ["t1"]


def _client(mode: str, decoder: Any) -> TestClient:
    settings = replace(Settings(), integrity_mode=mode)
    return TestClient(create_app(settings, integrity_decoder=decoder))


def _body() -> dict[str, object]:
    return {
        "imageBase64": base64.b64encode(JPEG).decode("ascii"),
        "mimeType": "image/jpeg",
        "candidateSignIds": ["S-B1-WEST"],
    }


def _broken(_: str) -> dict[str, Any]:
    raise IntegrityError("読めません")


def test_log_mode_lets_requests_through() -> None:
    # 判定に失敗しても 401 にはならず、いつもの処理に進む
    response = _client("log", _broken).post("/v1/sign/identify", json=_body())
    assert response.status_code != 401


def test_enforce_mode_blocks_requests_without_a_valid_token() -> None:
    client = _client("enforce", _broken)
    missing = client.post("/v1/sign/identify", json=_body())
    broken = client.post(
        "/v1/sign/identify", json=_body(), headers={"X-Integrity-Token": "t"}
    )
    assert missing.status_code == 401
    assert broken.status_code == 401
    assert missing.json()["error"]["code"] == "unauthorized"
