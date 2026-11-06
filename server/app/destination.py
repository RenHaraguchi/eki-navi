"""文章から行き先の改札を決める。"""

from __future__ import annotations

import json
import logging
import time
from dataclasses import dataclass

from app.catalog import Target, match_dictionary
from app.gemini import Classification, Classifier, ClassifierError
from app.limits import WindowLimiter
from app.station import Station
from app.vocab import DestinationStatus, Engine

LOGGER = logging.getLogger("ekinavi.destination")
MAX_CANDIDATES = 3
UNKNOWN_LENGTH = (
    -1.0
)  # JsonUtility は null を読めないので、距離が不明なときは -1 にする


@dataclass(frozen=True)
class ResolvedCandidate:
    """行き先の候補。"""

    gate: dict[str, object]
    length_meters: float


@dataclass(frozen=True)
class Resolution:
    """判定の結果。"""

    status: DestinationStatus
    candidates: tuple[ResolvedCandidate, ...]
    engine: Engine
    latency_ms: int


class DestinationResolver:
    """Gemini か辞書で行き先を判定し、案内板からの距離で候補を並べる。"""

    def __init__(
        self,
        station: Station,
        targets: tuple[Target, ...],
        classifier: Classifier | None,
        daily_limit: WindowLimiter,
    ) -> None:
        """判定に使うものを受け取る。

        Args:
            station: 駅のデータ。
            targets: 行き先の目録。
            classifier: Gemini の判定処理。None なら辞書だけで動く。
            daily_limit: Gemini の1日の呼び出し上限。
        """
        self._station = station
        self._targets = targets
        self._by_id = {target.id: target for target in targets}
        self._classifier = classifier
        self._daily_limit = daily_limit

    def _classify(self, text: str) -> tuple[Classification | None, Engine]:
        """Gemini で判定する。使えないときは辞書に切り替える。"""
        if self._classifier is None:
            return self._from_dictionary(text), "stub"
        if not self._daily_limit.allow():
            LOGGER.warning("Gemini の1日の上限に達したので辞書に切り替えます")
            return self._from_dictionary(text), "dictionary"
        try:
            return self._classifier.classify(text), self._classifier.engine
        except ClassifierError as error:
            LOGGER.warning("Gemini を使えないので辞書に切り替えます: %s", error)
            return self._from_dictionary(text), "dictionary"

    def _from_dictionary(self, text: str) -> Classification | None:
        found = match_dictionary(text, self._targets)
        if not found:
            return None
        status = "ok" if len(found) == 1 else "ambiguous"
        return Classification(status, tuple(target.id for target in found))

    def _rank(
        self, gate_ids: tuple[str, ...], from_sign_id: str
    ) -> list[ResolvedCandidate]:
        """改札を候補にする。案内板が分かれば近い順に並べ、行けない改札は除く。"""
        candidates = []
        for gate_id in gate_ids:
            gate = self._station.gates.get(gate_id)
            if gate is None:
                continue
            length = UNKNOWN_LENGTH
            if from_sign_id:
                measured = self._station.route_length(from_sign_id, gate_id)
                if measured is None:
                    continue
                length = measured
            candidates.append(ResolvedCandidate(gate, length))
        if from_sign_id:
            candidates.sort(key=lambda item: item.length_meters)
        return candidates

    def _expand(
        self, classification: Classification, from_sign_id: str
    ) -> tuple[ResolvedCandidate, ...]:
        """行き先を改札に展開する。

        行き先が1つなら、その改札を近い順に返す。複数なら、行き先ごとに最も近い改札を返す。
        """
        targets = [self._by_id[i] for i in classification.target_ids]
        if len(targets) == 1:
            ranked = self._rank(targets[0].gate_ids, from_sign_id)
        else:
            ranked = [
                best[0]
                for target in targets
                if (best := self._rank(target.gate_ids, from_sign_id))
            ]
        # 路線と改札の両方が同じ改札を指すことがあるので、重複を除く
        unique: dict[str, ResolvedCandidate] = {}
        for item in ranked:
            unique.setdefault(str(item.gate["id"]), item)
        ordered = list(unique.values())
        if from_sign_id:
            ordered.sort(key=lambda item: item.length_meters)
        return tuple(ordered[:MAX_CANDIDATES])

    def resolve(self, text: str, from_sign_id: str = "") -> Resolution:
        """文章を判定する。

        Args:
            text: 利用者の入力。
            from_sign_id: 出発の案内板。分かれば近い改札を優先する。

        Returns:
            判定の結果。
        """
        started = time.perf_counter()
        classification, engine = self._classify(text)
        status, candidates = self._conclude(classification, from_sign_id)
        latency_ms = int((time.perf_counter() - started) * 1000)
        # 利用者の入力はログに残さない（修正版の7章）
        LOGGER.info(
            json.dumps(
                {
                    "event": "destination",
                    "engine": engine,
                    "status": status,
                    "candidates": len(candidates),
                    "latencyMs": latency_ms,
                }
            )
        )
        return Resolution(status, candidates, engine, latency_ms)

    def _conclude(
        self, classification: Classification | None, from_sign_id: str
    ) -> tuple[DestinationStatus, tuple[ResolvedCandidate, ...]]:
        if classification is None:
            return "unavailable", ()
        if classification.status == "out_of_scope" or not classification.target_ids:
            return "out_of_scope", ()
        candidates = self._expand(classification, from_sign_id)
        if not candidates:
            return "unavailable", ()
        if len(candidates) == 1:
            return "ok", candidates
        several_targets = len(classification.target_ids) > 1
        # 出発が分からないと近い改札を選べないので、利用者に選んでもらう
        if classification.status == "ambiguous" or several_targets or not from_sign_id:
            return "ambiguous", candidates
        return "ok", candidates
