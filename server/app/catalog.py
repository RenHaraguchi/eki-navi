"""行き先の目録。Gemini の答えの範囲と、辞書での判定を作る。"""

from __future__ import annotations

import unicodedata
from dataclasses import dataclass
from typing import Any

LINE_PREFIX = "line:"
GATE_PREFIX = "gate:"


@dataclass(frozen=True)
class Target:
    """行き先の単位。路線や施設は複数の改札を持つ。"""

    id: str
    label: str
    aliases: tuple[str, ...]
    gate_ids: tuple[str, ...]


def normalize(text: str) -> str:
    """比較用に整える。全角と半角、大文字と小文字の違いをなくす。"""
    return unicodedata.normalize("NFKC", text).casefold()


def build_targets(pack: dict[str, Any]) -> tuple[Target, ...]:
    """経路パックから行き先の目録を作る。

    Args:
        pack: 経路パック。

    Returns:
        路線・施設と、改札の一覧。
    """
    lines = tuple(
        Target(
            id=f"{LINE_PREFIX}{line['id']}",
            label=line["name"],
            aliases=tuple(line["aliases"]),
            gate_ids=tuple(line["gateIds"]),
        )
        for line in pack["lines"]
    )
    gates = tuple(
        Target(
            id=f"{GATE_PREFIX}{gate['id']}",
            label=f"{gate['operator']} {gate['name']}（{gate['floor']}）",
            aliases=(gate["name"],),
            gate_ids=(gate["id"],),
        )
        for gate in pack["gates"]
    )
    return lines + gates


def match_dictionary(text: str, targets: tuple[Target, ...]) -> list[Target]:
    """文章に含まれる別名から行き先を探す。

    長い別名を優先する。「西口地下改札」に含まれる「改札」のような短い語で誤らないため、
    最も長く一致した別名の行き先だけを返す。

    Args:
        text: 利用者の入力。
        targets: 行き先の目録。

    Returns:
        一致した行き先。なければ空。
    """
    query = normalize(text)
    best_length = 0
    best: list[Target] = []
    for target in targets:
        for alias in (target.label, *target.aliases):
            needle = normalize(alias)
            if needle not in query or len(needle) < best_length:
                continue
            if len(needle) > best_length:
                best_length, best = len(needle), []
            if target not in best:
                best.append(target)
    return best
