"""呼び出し回数の上限。Cloud Run のインスタンスごとに数える。"""

from __future__ import annotations

import threading
import time
from collections.abc import Callable


class WindowLimiter:
    """一定の時間ごとに回数を数え、上限を超えたら断る。"""

    def __init__(
        self,
        limit: int,
        window_seconds: float,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        """上限と時間の幅を決める。

        Args:
            limit: 時間の幅あたりの上限。
            window_seconds: 時間の幅（秒）。
            clock: 現在時刻を返す関数。テストで差し替える。
        """
        self._limit = limit
        self._window = window_seconds
        self._clock = clock
        self._lock = threading.Lock()
        self._window_start = clock()
        self._count = 0

    def allow(self) -> bool:
        """1回分を数え、上限内なら True を返す。"""
        with self._lock:
            now = self._clock()
            if now - self._window_start >= self._window:
                self._window_start = now
                self._count = 0
            if self._count >= self._limit:
                return False
            self._count += 1
            return True
