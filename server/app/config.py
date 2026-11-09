"""環境変数からの設定。"""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path

DEFAULT_PACK_PATH = Path(__file__).resolve().parent.parent / "station_data/pack.json"
DEFAULT_MODEL = "gemini-3.5-flash-lite"
DEFAULT_TIMEOUT_SECONDS = 5.0
DEFAULT_DAILY_LIMIT = 1000
DEFAULT_RATE_PER_MINUTE = 600
DEFAULT_GEMINI_RATE_PER_MINUTE = 60
BACKENDS = ("stub", "apikey", "vertex")
INTEGRITY_MODES = ("off", "log", "enforce")
DEFAULT_INTEGRITY_PACKAGE = "com.ekinavi.app"
DEFAULT_INTEGRITY_MAX_AGE_SECONDS = 600.0


class ConfigError(Exception):
    """設定が足りないときの例外。起動時に止めるために使う。"""


@dataclass(frozen=True)
class Settings:
    """サーバーの設定。"""

    gemini_backend: str = "stub"
    gemini_api_key: str = ""
    gemini_model: str = DEFAULT_MODEL
    gemini_vision_model: str = ""
    gemini_timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS
    gemini_daily_limit: int = DEFAULT_DAILY_LIMIT
    google_project: str = ""
    google_location: str = "global"
    rate_per_minute: int = DEFAULT_RATE_PER_MINUTE
    gemini_rate_per_minute: int = DEFAULT_GEMINI_RATE_PER_MINUTE
    pack_path: Path = DEFAULT_PACK_PATH
    video_url: str = ""
    apk_url: str = ""
    integrity_mode: str = "off"
    integrity_package: str = DEFAULT_INTEGRITY_PACKAGE
    integrity_cert_digests: tuple[str, ...] = ()
    integrity_max_age_seconds: float = DEFAULT_INTEGRITY_MAX_AGE_SECONDS


def _safe_link(url: str) -> str:
    """ページに出すリンクを http と https に限る。"""
    return url if url.startswith(("https://", "http://")) else ""


def load_settings(env: Mapping[str, str] | None = None) -> Settings:
    """環境変数を読み、足りなければ起動時に止める。

    Args:
        env: 環境変数。省略時は os.environ。

    Returns:
        設定。

    Raises:
        ConfigError: 値が不正なとき、または必要な値がないとき。
    """
    source = os.environ if env is None else env
    backend = source.get("GEMINI_BACKEND", "stub")
    if backend not in BACKENDS:
        raise ConfigError(f"GEMINI_BACKEND は {BACKENDS} のどれかにしてください")
    integrity_mode = source.get("INTEGRITY_MODE", "off")
    if integrity_mode not in INTEGRITY_MODES:
        raise ConfigError(f"INTEGRITY_MODE は {INTEGRITY_MODES} のどれかにしてください")
    settings = Settings(
        gemini_backend=backend,
        gemini_api_key=source.get("GEMINI_API_KEY", ""),
        gemini_model=source.get("GEMINI_MODEL", DEFAULT_MODEL),
        gemini_vision_model=source.get("GEMINI_VISION_MODEL", ""),
        gemini_timeout_seconds=float(
            source.get("GEMINI_TIMEOUT_SECONDS", DEFAULT_TIMEOUT_SECONDS)
        ),
        gemini_daily_limit=int(source.get("GEMINI_DAILY_LIMIT", DEFAULT_DAILY_LIMIT)),
        google_project=source.get("GOOGLE_CLOUD_PROJECT", ""),
        google_location=source.get("GOOGLE_CLOUD_LOCATION", "global"),
        rate_per_minute=int(source.get("API_RATE_PER_MINUTE", DEFAULT_RATE_PER_MINUTE)),
        gemini_rate_per_minute=int(
            source.get("GEMINI_RATE_PER_MINUTE", DEFAULT_GEMINI_RATE_PER_MINUTE)
        ),
        pack_path=Path(source.get("PACK_PATH", str(DEFAULT_PACK_PATH))),
        video_url=_safe_link(source.get("DEMO_VIDEO_URL", "")),
        apk_url=_safe_link(source.get("DEMO_APK_URL", "")),
        integrity_mode=integrity_mode,
        integrity_package=source.get("INTEGRITY_PACKAGE", DEFAULT_INTEGRITY_PACKAGE),
        integrity_cert_digests=tuple(
            item.strip()
            for item in source.get("INTEGRITY_CERT_SHA256", "").split(",")
            if item.strip()
        ),
        integrity_max_age_seconds=float(
            source.get("INTEGRITY_MAX_AGE_SECONDS", DEFAULT_INTEGRITY_MAX_AGE_SECONDS)
        ),
    )
    if backend == "apikey" and not settings.gemini_api_key:
        raise ConfigError("GEMINI_BACKEND=apikey には GEMINI_API_KEY が必要です")
    if backend == "vertex" and not settings.google_project:
        raise ConfigError("GEMINI_BACKEND=vertex には GOOGLE_CLOUD_PROJECT が必要です")
    return settings
