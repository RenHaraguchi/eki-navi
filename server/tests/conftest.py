"""テストで共有する準備。"""

from __future__ import annotations

from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.config import DEFAULT_PACK_PATH, Settings
from app.main import create_app
from app.station import Station

SERVER_DIR = Path(__file__).resolve().parent.parent
REPO_DIR = SERVER_DIR.parent


@pytest.fixture(scope="session")
def station() -> Station:
    """コミットしてある経路パックを読む。"""
    return Station.load(DEFAULT_PACK_PATH)


@pytest.fixture()
def client() -> TestClient:
    """辞書だけで動くサーバーにつなぐ。"""
    return TestClient(create_app(Settings()))
