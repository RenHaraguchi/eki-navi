"""API と内部の処理で共有する値の種類。"""

from __future__ import annotations

from typing import Literal

TurnDirection = Literal["left", "right"]
RouteSource = Literal["network", "curated"]
DestinationStatus = Literal["ok", "ambiguous", "out_of_scope", "unavailable"]
Engine = Literal["gemini", "dictionary", "stub"]
SignVisionStatus = Literal["ok", "not_found", "unavailable"]
