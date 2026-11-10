"""Play Integrity のトークンを確かめ、アプリからの要求かどうかを判定する。

アプリは Play Integrity API でトークンを受け取り、X-Integrity-Token ヘッダーで送る。
サーバーは Google の decodeIntegrityToken でトークンを読み、次を確かめる。
- 要求したアプリのパッケージ名が設定と同じ
- アプリの署名証明書が設定にある（設定したときだけ）
- 端末の判定に MEETS_*_INTEGRITY が1つ以上ある
- トークンが作られてから INTEGRITY_MAX_AGE_SECONDS 以内

Play ストアの外で配る APK では appRecognitionVerdict が UNRECOGNIZED_VERSION なので、
アプリの判定は使わず、署名証明書で確かめる。

INTEGRITY_MODE の値:
- off: 何もしない（初期値）
- log: 判定してログに残すだけで、要求は止めない
- enforce: 判定に通らない要求を 401 で止める
"""

from __future__ import annotations

import base64
import logging
import time
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

import httpx

from app.config import Settings

LOGGER = logging.getLogger("ekinavi.integrity")
DECODE_URL = "https://playintegrity.googleapis.com/v1/{package}:decodeIntegrityToken"
# Play Integrity の読み取りに要るスコープ付きで、Cloud Run のトークンをもらう
METADATA_SCOPED_URL = (
    "http://metadata.google.internal/computeMetadata/v1/instance"
    "/service-accounts/default/token"
    "?scopes=https://www.googleapis.com/auth/playintegrity"
)
TOKEN_REFRESH_MARGIN_SECONDS = 60.0
MODES = ("off", "log", "enforce")
DEVICE_PASS = {
    "MEETS_BASIC_INTEGRITY",
    "MEETS_DEVICE_INTEGRITY",
    "MEETS_STRONG_INTEGRITY",
}
CACHE_LIMIT = 256


class IntegrityError(Exception):
    """トークンを読めない、または判定に通らないときの例外。"""


Decoder = Callable[[str], dict[str, Any]]
Clock = Callable[[], float]


@dataclass(frozen=True)
class Verdict:
    """判定の結果。"""

    ok: bool
    reason: str


def normalize_digest(value: str) -> str:
    """証明書の SHA-256 を、Play Integrity が返す形（base64url、= なし）にそろえる。

    keytool が出す "AB:CD:..." の16進でも、base64url でも受け付ける。
    """
    text = value.strip()
    hex_text = text.replace(":", "")
    if len(hex_text) == 64:
        try:
            raw = bytes.fromhex(hex_text)
        except ValueError:
            raw = b""
        if raw:
            return base64.urlsafe_b64encode(raw).decode().rstrip("=")
    return text.rstrip("=")


def check_payload(
    payload: dict[str, Any],
    package: str,
    digests: frozenset[str],
    max_age_seconds: float,
    now_ms: float,
) -> Verdict:
    """decodeIntegrityToken の tokenPayloadExternal を判定する。

    Args:
        payload: tokenPayloadExternal の中身。
        package: 期待するパッケージ名。
        digests: 許す署名証明書の SHA-256（base64url）。空なら確かめない。
        max_age_seconds: トークンの古さの上限。
        now_ms: 今の時刻（ミリ秒）。

    Returns:
        判定の結果。
    """
    request = payload.get("requestDetails") or {}
    if request.get("requestPackageName") != package:
        return Verdict(False, "package_mismatch")
    try:
        issued_ms = float(request.get("timestampMillis", 0))
    except (TypeError, ValueError):
        issued_ms = 0.0
    if not issued_ms or now_ms - issued_ms > max_age_seconds * 1000.0:
        return Verdict(False, "token_too_old")
    if digests:
        app = payload.get("appIntegrity") or {}
        seen = {
            normalize_digest(str(item))
            for item in app.get("certificateSha256Digest") or []
        }
        if not seen & digests:
            return Verdict(False, "certificate_mismatch")
    device = payload.get("deviceIntegrity") or {}
    labels = set(device.get("deviceRecognitionVerdict") or [])
    if not labels & DEVICE_PASS:
        return Verdict(False, "device_not_trusted")
    return Verdict(True, "ok")


class MetadataScopedToken:
    """Cloud Run のサービスアカウントの短期トークンを、期限まで使い回す。"""

    def __init__(self, timeout_seconds: float) -> None:
        """タイムアウトを決める。"""
        self._timeout = timeout_seconds
        self._token = ""  # nosec B105 - 取得前の空の値
        self._expires_at = 0.0

    def get(self) -> str:
        """トークンを返す。

        Raises:
            IntegrityError: 取得できないとき。
        """
        if self._token and time.monotonic() < self._expires_at:
            return self._token
        try:
            response = httpx.get(
                METADATA_SCOPED_URL,
                headers={"Metadata-Flavor": "Google"},
                timeout=self._timeout,
            )
            response.raise_for_status()
            data = response.json()
            self._token = data["access_token"]
            lifetime = float(data["expires_in"])
        except (httpx.HTTPError, ValueError, KeyError, TypeError) as error:
            raise IntegrityError("認証トークンを取得できません") from error
        self._expires_at = time.monotonic() + lifetime - TOKEN_REFRESH_MARGIN_SECONDS
        return self._token


def google_decoder(package: str, timeout_seconds: float) -> Decoder:
    """Google の decodeIntegrityToken を呼ぶ関数を作る。"""
    token = MetadataScopedToken(timeout_seconds)
    url = DECODE_URL.format(package=package)

    def decode(integrity_token: str) -> dict[str, Any]:
        try:
            response = httpx.post(
                url,
                json={"integrity_token": integrity_token},
                headers={"Authorization": f"Bearer {token.get()}"},
                timeout=timeout_seconds,
            )
            response.raise_for_status()
            payload = response.json()["tokenPayloadExternal"]
        except httpx.HTTPStatusError as error:
            raise IntegrityError(
                f"トークンを読めません（HTTP {error.response.status_code}）"
            ) from error
        except (httpx.HTTPError, ValueError, KeyError, TypeError) as error:
            raise IntegrityError("トークンを読めません") from error
        if not isinstance(payload, dict):
            raise IntegrityError("トークンの中身の形が想定と違います")
        return payload

    return decode


class IntegrityGate:
    """要求を通すかどうかを決める。読んだトークンの結果は古くなるまで覚えておく。"""

    def __init__(
        self,
        settings: Settings,
        decoder: Decoder | None = None,
        clock: Clock = time.time,
    ) -> None:
        """設定と、トークンを読む処理を受け取る。

        Args:
            settings: サーバーの設定。
            decoder: トークンを読む処理。テストで差し替える。省略時は Google を呼ぶ。
            clock: 今の時刻（秒）。テストで差し替える。
        """
        self.mode = settings.integrity_mode
        self._package = settings.integrity_package
        self._digests = frozenset(
            normalize_digest(item) for item in settings.integrity_cert_digests if item
        )
        self._max_age = settings.integrity_max_age_seconds
        self._clock = clock
        self._decoder = decoder
        if self._decoder is None and self.mode != "off":
            self._decoder = google_decoder(
                self._package, settings.gemini_timeout_seconds
            )
        self._cache: dict[str, dict[str, Any]] = {}

    def _payload(self, token: str) -> dict[str, Any]:
        cached = self._cache.get(token)
        if cached is not None:
            return cached
        if self._decoder is None:
            raise IntegrityError("トークンを読む処理がありません")
        payload = self._decoder(token)
        if len(self._cache) >= CACHE_LIMIT:
            self._cache.clear()
        self._cache[token] = payload
        return payload

    def check(self, token: str) -> Verdict:
        """トークンを判定する。mode が off なら常に通す。

        Args:
            token: X-Integrity-Token ヘッダーの値。

        Returns:
            判定の結果。ok が False でも、mode が log なら呼び出し側は止めない。
        """
        if self.mode == "off":
            return Verdict(True, "off")
        if not token:
            verdict = Verdict(False, "missing_token")
        else:
            try:
                payload = self._payload(token)
            except IntegrityError as error:
                verdict = Verdict(False, f"decode_failed: {error}")
            else:
                verdict = check_payload(
                    payload,
                    self._package,
                    self._digests,
                    self._max_age,
                    self._clock() * 1000.0,
                )
        LOGGER.info(
            "Play Integrity: mode=%s ok=%s reason=%s",
            self.mode,
            verdict.ok,
            verdict.reason,
        )
        return verdict

    def allows(self, token: str) -> bool:
        """要求を通すなら True。log のときは判定に関係なく通す。"""
        verdict = self.check(token)
        return verdict.ok or self.mode != "enforce"
