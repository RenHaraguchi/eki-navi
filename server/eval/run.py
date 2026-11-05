"""行き先判定を、評価用の文で Gemini と辞書の両方にかけて比べる。

使い方（無料枠のキーで試すときも、入力は cases.json の作った文だけにする）:
    GEMINI_BACKEND=apikey python -m eval.run
"""

from __future__ import annotations

import argparse
import json
import statistics
import time
from collections.abc import Iterable
from pathlib import Path

from app.catalog import Target, build_targets, match_dictionary
from app.config import Settings, load_settings
from app.gemini import Classification, ClassifierError, GeminiClassifier
from app.station import Station

CASES_PATH = Path(__file__).resolve().parent / "cases.json"
OUT_OF_SCOPE = "out_of_scope"


def _gates_of(target_ids: Iterable[str], targets: tuple[Target, ...]) -> frozenset[str]:
    """行き先の ID を改札の集合にする。路線と改札のどちらで答えても同じに扱うため。"""
    by_id = {target.id: target for target in targets}
    return frozenset(gate for i in target_ids for gate in by_id[i].gate_ids)


def _dictionary_answer(text: str, targets: tuple[Target, ...]) -> frozenset[str]:
    return _gates_of([t.id for t in match_dictionary(text, targets)], targets)


def _gemini_answer(
    classification: Classification, targets: tuple[Target, ...]
) -> frozenset[str]:
    if classification.status == OUT_OF_SCOPE:
        return frozenset()
    return _gates_of(classification.target_ids, targets)


def _expected_gates(expected: str, targets: tuple[Target, ...]) -> frozenset[str]:
    return frozenset() if expected == OUT_OF_SCOPE else _gates_of([expected], targets)


def _show(gates: frozenset[str]) -> str:
    return "、".join(sorted(gates)) or OUT_OF_SCOPE


def run(settings: Settings, interval_seconds: float) -> None:
    """評価を実行して結果を表示する。

    Args:
        settings: サーバーの設定。
        interval_seconds: 呼び出しの間隔。無料枠の回数制限を避けるために空ける。
    """
    station = Station.load(settings.pack_path)
    targets = build_targets(station.pack)
    classifier = GeminiClassifier(settings, targets)
    cases = json.loads(CASES_PATH.read_text(encoding="utf-8"))["cases"]
    gemini_ok = dictionary_ok = failures = 0
    latencies: list[float] = []
    for case in cases:
        expected = _expected_gates(case["expected"], targets)
        dictionary = _dictionary_answer(case["text"], targets)
        dictionary_ok += dictionary == expected
        started = time.perf_counter()
        try:
            answer = _gemini_answer(classifier.classify(case["text"]), targets)
            shown = _show(answer)
        except ClassifierError as error:
            answer, shown = None, f"失敗: {error}"
            failures += 1
        latencies.append(time.perf_counter() - started)
        gemini_ok += answer == expected
        mark = "o" if answer == expected else "x"
        print(f"{mark} {case['text']} → {shown}（辞書 {_show(dictionary)}）")
        time.sleep(interval_seconds)
    print(
        f"Gemini {gemini_ok}/{len(cases)}、辞書 {dictionary_ok}/{len(cases)}、"
        f"失敗 {failures}、遅延の中央値 {statistics.median(latencies):.2f} 秒、"
        f"最大 {max(latencies):.2f} 秒"
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="行き先判定を評価する")
    parser.add_argument(
        "--interval", type=float, default=5.0, help="呼び出しの間隔（秒）"
    )
    run(load_settings(), parser.parse_args().interval)
