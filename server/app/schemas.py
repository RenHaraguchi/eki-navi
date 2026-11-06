"""API の入出力。アプリ（Unity の JsonUtility）が読めるように次を守る。

- 一番外側は必ずオブジェクトにする（JsonUtility は配列を直接読めない）
- 値がないときに null を返さない。文字列は ""、配列は []、距離は -1 にする
- 辞書を使わない（JsonUtility は読めない）。2D 表示の下地の /v1/map だけは例外で、
  ブラウザのページだけが使う
"""

from __future__ import annotations

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

from app.vocab import DestinationStatus, Engine, RouteSource, TurnDirection

MAX_ID_LENGTH = 120
MAX_TEXT_LENGTH = 200


class ApiModel(BaseModel):
    """JSON のキーは camelCase にする（Unity の RouteData.cs に合わせる）。"""

    model_config = ConfigDict(
        alias_generator=to_camel, populate_by_name=True, frozen=True
    )


class GeoPoint(ApiModel):
    """座標の固定原点。"""

    lat: float
    lon: float


class RouteEndpoint(ApiModel):
    """経路の出発と到着。"""

    id: str
    name: str
    ordinal: float
    operator: str = ""
    floor: str = ""


class RoutePointOut(ApiModel):
    """経路の点。固定原点からのメートルで、x = 東、z = 北。"""

    x: float
    z: float
    ordinal: float


class VerticalMoveOut(ApiModel):
    """階段、エスカレーター、エレベーターの区間。添字は points の位置。"""

    kind: str
    from_index: int
    to_index: int


class TurnOut(ApiModel):
    """曲がり角。2D 表示の「次の曲がり角」と振動に使う。"""

    point_index: int
    direction: TurnDirection
    angle_deg: float
    distance_from_start: float


class GoalLine(ApiModel):
    """到着の判定に使う改札の開口線の両端。"""

    ax: float
    az: float
    bx: float
    bz: float


class SignOnRoute(ApiModel):
    """経路の近くにある案内板。止まった後の画面で、近い案内板を示すために使う。"""

    sign_id: str
    along_meters: float
    offset_meters: float


class RouteRequest(ApiModel):
    """経路の問い合わせ。出発は案内板か改札のどちらか一方を入れる。"""

    to_gate_id: str = Field(min_length=1, max_length=MAX_ID_LENGTH)
    from_sign_id: str = Field(default="", max_length=MAX_ID_LENGTH)
    from_gate_id: str = Field(default="", max_length=MAX_ID_LENGTH)


class RouteResponse(ApiModel):
    """経路。RouteData.cs が読むキーに、追加のキーを足している。"""

    origin: GeoPoint
    start: RouteEndpoint
    goal: RouteEndpoint
    length_meters: float
    points: list[RoutePointOut]
    vertical_moves: list[VerticalMoveOut]
    turns: list[TurnOut]
    goal_line: GoalLine
    start_offset_meters: float
    signs_on_route: list[SignOnRoute]
    source: RouteSource
    pack_version: str


class DestinationRequest(ApiModel):
    """行き先の問い合わせ。fromSignId があれば、近い改札から順に返す。"""

    text: str = Field(min_length=1, max_length=MAX_TEXT_LENGTH)
    from_sign_id: str = Field(default="", max_length=MAX_ID_LENGTH)


class Candidate(ApiModel):
    """行き先の候補。lengthMeters は出発が不明なら -1。"""

    gate_id: str
    name: str
    operator: str
    ordinal: float
    floor: str
    length_meters: float


class DestinationResponse(ApiModel):
    """行き先の判定の結果。

    status の意味:
    - ok: candidates の先頭をそのまま使ってよい
    - ambiguous: 利用者に candidates から選んでもらう
    - out_of_scope: 対応外。行き先の一覧を出す
    - unavailable: 判定できなかった。行き先の一覧を出す
    """

    status: DestinationStatus
    candidates: list[Candidate]
    engine: Engine
    latency_ms: int
    pack_version: str


class GateOut(ApiModel):
    """改札の一覧の1件。"""

    id: str
    name: str
    operator: str
    ordinal: float
    floor: str


class SignOut(ApiModel):
    """案内板の一覧の1件。意味は contracts/signs.schema.json を参照。"""

    id: str
    name: str
    image_name: str
    x: float
    z: float
    ordinal: float
    azimuth_deg: float
    width_meters: float
    height_meters: float
    center_height_meters: float
    mount: str
    correction_yaw_deg: float
    correction_x: float
    correction_z: float
    confidence: str


class LineOut(ApiModel):
    """路線や施設と改札の対応。confirmed は公式の構内図と照合済みかどうか。"""

    id: str
    name: str
    aliases: list[str]
    gate_ids: list[str]
    confirmed: bool


class StationResponse(ApiModel):
    """駅の基本データ。アプリに同梱すれば、通信なしで起動できる。"""

    pack_version: str
    origin: GeoPoint
    attribution: str
    gates: list[GateOut]
    signs: list[SignOut]
    lines: list[LineOut]


class SiteResponse(ApiModel):
    """審査員が開くページの表示内容。"""

    pack_version: str
    attribution: str
    video_url: str
    apk_url: str
    engine: str


class MapResponse(ApiModel):
    """その階の通路の線。ブラウザのページだけが使う。"""

    ordinal: float
    lines: list[list[list[float]]]


class HealthResponse(ApiModel):
    """動作確認用。"""

    status: str
    pack_version: str


class ErrorDetail(ApiModel):
    """エラーの中身。code はアプリの分岐に使い、message は開発者向け。"""

    code: str
    message: str


class ErrorResponse(ApiModel):
    """エラーの共通形式。"""

    error: ErrorDetail
