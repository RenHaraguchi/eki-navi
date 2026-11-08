"""API の入出力。アプリ（Unity の JsonUtility）が読めるように次を守る。

- 一番外側は必ずオブジェクトにする（JsonUtility は配列を直接読めない）
- 値がないときに null を返さない。文字列は ""、配列は []、距離は -1 にする
- 辞書を使わない（JsonUtility は読めない）。2D 表示の下地の /v1/map だけは例外で、
  ブラウザのページだけが使う
"""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

from app.vocab import (
    DestinationStatus,
    Engine,
    RouteSource,
    SignVisionStatus,
    TurnDirection,
)

MAX_ID_LENGTH = 120
MAX_TEXT_LENGTH = 200
# 長辺 1600px の JPEG（品質 85）で 0.5MB ほど。余裕を見て base64 で 4MB まで
MAX_IMAGE_BASE64_LENGTH = 4_000_000
MAX_SIGN_CANDIDATES = 30


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
    same_floor: bool = Field(
        default=False,
        description="true なら出発の階から出ない経路だけを返す。なければ no_route",
    )


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


class MapGate(ApiModel):
    """地図に名前を出す改札。位置は開口の中心の平均。"""

    id: str
    name: str
    operator: str
    x: float
    z: float


class MapResponse(ApiModel):
    """その階の通路の線と改札。ブラウザのページだけが使う。"""

    ordinal: float
    lines: list[list[list[float]]]
    gates: list[MapGate]


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


class SignIdentifyRequest(ApiModel):
    """カメラの画像から案内板を見分ける問い合わせ。"""

    image_base64: str = Field(min_length=1, max_length=MAX_IMAGE_BASE64_LENGTH)
    mime_type: Literal["image/jpeg", "image/png"] = "image/jpeg"
    candidate_sign_ids: list[
        Annotated[str, Field(min_length=1, max_length=MAX_ID_LENGTH)]
    ] = Field(min_length=1, max_length=MAX_SIGN_CANDIDATES)


class ImageBox(ApiModel):
    """画像の中の枠。0〜1 に正規化し、左上が原点。見つからないときは全部 0。"""

    x_min: float
    y_min: float
    x_max: float
    y_max: float


class ImagePoint(ApiModel):
    """画像の中の点。0〜1 に正規化し、左上が原点。"""

    x: float
    y: float


class SignIdentifyResponse(ApiModel):
    """案内板を見分けた結果。

    status の意味:
    - ok: signId の板が box の位置に写っている。corners は板の左上、右上、右下、左下の角
      （答えがないか、形がおかしければ []。そのときは box だけで位置を求める）
    - not_found: 候補の板は写っていない（読んだ文字がどの候補とも十分に重ならない）。
      かざし直してもらう。text のない候補は照らし合わせられない
    - unavailable: 見分けられなかった（Gemini を使わない設定、通信の失敗、回数の上限）
    """

    status: SignVisionStatus
    sign_id: str
    box: ImageBox
    corners: list[ImagePoint]
    read_text: str = Field(description="Gemini が読んだ板の文字。確かめるために返す")
    match_score: float = Field(
        description="読んだ文字といちばん近い候補の重なり（0〜1）"
    )
    engine: Engine
    latency_ms: int
    pack_version: str
