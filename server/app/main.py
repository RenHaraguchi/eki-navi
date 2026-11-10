"""経路 API、行き先 API、審査員が開くページを持つサーバー。

起動: uvicorn app.main:create_app --factory --port 8080
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

from fastapi import FastAPI, Header, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from starlette.exceptions import HTTPException as StarletteHTTPException
from starlette.middleware.base import RequestResponseEndpoint

from app import schemas
from app.catalog import build_targets
from app.config import Settings, load_settings
from app.destination import DestinationResolver, ResolvedCandidate
from app.gemini import Classifier, GeminiClassifier
from app.integrity import Decoder, IntegrityGate
from app.limits import WindowLimiter
from app.sign_vision import (
    GeminiSignIdentifier,
    InvalidImageError,
    SignIdentifier,
    candidates_of,
    decode_image,
    run_identify,
)
from app.station import PlannedRoute, Station, StationError

LOGGER = logging.getLogger("ekinavi")
STATIC_DIR = Path(__file__).resolve().parent / "static"
SECONDS_PER_DAY = 86400.0
SECONDS_PER_MINUTE = 60.0
PAGE_CSP = (
    "default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; "
    "img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"
)
HTTP_STATUS_BY_CODE = {
    "unknown_sign": 404,
    "unknown_gate": 404,
    "unknown_floor": 404,
    "no_route": 404,
    "invalid_request": 422,
}


def _error_response(status: int, code: str, message: str) -> JSONResponse:
    body = schemas.ErrorResponse(error=schemas.ErrorDetail(code=code, message=message))
    return JSONResponse(status_code=status, content=body.model_dump(by_alias=True))


def _endpoint_of_gate(gate: dict[str, Any]) -> schemas.RouteEndpoint:
    return schemas.RouteEndpoint(
        id=gate["id"],
        name=gate["name"],
        ordinal=gate["ordinal"],
        operator=gate["operator"],
        floor=gate["floor"],
    )


def _route_response(station: Station, plan: PlannedRoute) -> schemas.RouteResponse:
    opening = plan.goal_opening
    return schemas.RouteResponse(
        origin=schemas.GeoPoint(**station.pack["origin"]),
        start=schemas.RouteEndpoint(
            id=plan.start.endpoint_id,
            name=plan.start.endpoint_name,
            ordinal=plan.start.ordinal,
            operator=plan.start.operator,
            floor=plan.start.floor,
        ),
        goal=_endpoint_of_gate(plan.goal),
        length_meters=plan.length_meters,
        points=[
            schemas.RoutePointOut(x=round(p.x, 3), z=round(p.z, 3), ordinal=p.ordinal)
            for p in plan.points
        ],
        vertical_moves=[
            schemas.VerticalMoveOut(
                kind=m.kind, from_index=m.from_index, to_index=m.to_index
            )
            for m in plan.moves
        ],
        turns=[
            schemas.TurnOut(
                point_index=t.point_index,
                direction=t.direction,
                angle_deg=t.angle_deg,
                distance_from_start=t.distance_from_start,
            )
            for t in plan.turns
        ],
        goal_line=schemas.GoalLine(
            ax=opening["ax"], az=opening["az"], bx=opening["bx"], bz=opening["bz"]
        ),
        start_offset_meters=plan.start.offset_meters,
        signs_on_route=[
            schemas.SignOnRoute(**item)
            for item in station.signs_near_route(plan.points)
        ],
        source=plan.source,
        pack_version=station.version,
    )


def _candidate(item: ResolvedCandidate) -> schemas.Candidate:
    gate = item.gate
    return schemas.Candidate(
        gate_id=str(gate["id"]),
        name=str(gate["name"]),
        operator=str(gate["operator"]),
        ordinal=float(gate["ordinal"]),  # type: ignore[arg-type]
        floor=str(gate["floor"]),
        length_meters=item.length_meters,
    )


def _station_response(station: Station) -> schemas.StationResponse:
    pack = station.pack
    return schemas.StationResponse(
        pack_version=station.version,
        origin=schemas.GeoPoint(**pack["origin"]),
        attribution=pack["attribution"],
        gates=[
            schemas.GateOut(
                id=g["id"],
                name=g["name"],
                operator=g["operator"],
                ordinal=g["ordinal"],
                floor=g["floor"],
            )
            for g in pack["gates"]
        ],
        signs=[
            schemas.SignOut.model_validate(
                {k: v for k, v in sign.items() if k != "note"}
            )
            for sign in pack["signs"]
        ],
        lines=[schemas.LineOut.model_validate(line) for line in pack["lines"]],
    )


def _add_exception_handlers(app: FastAPI) -> None:
    """エラーを共通の形式にそろえる。入力の内容は返さない。"""

    @app.exception_handler(StationError)
    async def _station_error(_: Request, error: StationError) -> JSONResponse:
        status = HTTP_STATUS_BY_CODE.get(error.code, 400)
        return _error_response(status, error.code, error.message)

    @app.exception_handler(RequestValidationError)
    async def _validation_error(_: Request, __: RequestValidationError) -> JSONResponse:
        return _error_response(422, "invalid_request", "入力の形式が正しくありません")

    @app.exception_handler(StarletteHTTPException)
    async def _http_error(_: Request, error: StarletteHTTPException) -> JSONResponse:
        return _error_response(error.status_code, "http_error", str(error.detail))

    @app.exception_handler(Exception)
    async def _unexpected_error(_: Request, error: Exception) -> JSONResponse:
        LOGGER.exception("想定していないエラーです: %s", type(error).__name__)
        return _error_response(500, "internal_error", "サーバーでエラーが起きました")


def _add_middleware(app: FastAPI, settings: Settings) -> None:
    """回数の上限と、応答のヘッダーを足す。

    上限はインスタンスごとの合計で、利用者ごとではない。費用の上限として使う。
    ページの表示で読むだけの API は数えない。
    """
    limits = {
        "/v1/route": WindowLimiter(settings.rate_per_minute, SECONDS_PER_MINUTE),
        "/v1/destination": WindowLimiter(
            settings.gemini_rate_per_minute, SECONDS_PER_MINUTE
        ),
        "/v1/sign/identify": WindowLimiter(
            settings.gemini_rate_per_minute, SECONDS_PER_MINUTE
        ),
    }

    @app.middleware("http")
    async def _guard(request: Request, call_next: RequestResponseEndpoint) -> Response:
        limiter = limits.get(request.url.path)
        response: Response
        if limiter is not None and not limiter.allow():
            response = _error_response(429, "rate_limited", "回数の上限に達しました")
        else:
            response = await call_next(request)
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["Referrer-Policy"] = "no-referrer"
        return response


def _build_classifier(
    settings: Settings, station: Station, classifier: Classifier | None
) -> Classifier | None:
    if classifier is not None:
        return classifier
    if settings.gemini_backend == "stub":
        return None
    return GeminiClassifier(settings, build_targets(station.pack))


def _build_sign_identifier(
    settings: Settings, identifier: SignIdentifier | None
) -> SignIdentifier | None:
    if identifier is not None:
        return identifier
    if settings.gemini_backend == "stub":
        return None
    return GeminiSignIdentifier(settings)


def _add_sign_vision_route(
    app: FastAPI,
    station: Station,
    identifier: SignIdentifier | None,
    daily: WindowLimiter,
    gate: IntegrityGate,
) -> None:
    """カメラの画像から案内板を見分ける API。"""

    @app.post("/v1/sign/identify", response_model=schemas.SignIdentifyResponse)
    def identify_sign(
        body: schemas.SignIdentifyRequest,
        x_integrity_token: str = Header(default="", alias="X-Integrity-Token"),
    ) -> schemas.SignIdentifyResponse | JSONResponse:
        if not gate.allows(x_integrity_token):
            return _error_response(401, "unauthorized", "アプリの要求ではありません")
        try:
            image = decode_image(body.image_base64, body.mime_type)
        except InvalidImageError as error:
            return _error_response(422, "invalid_image", str(error))
        candidates = candidates_of(
            station.pack["signs"], list(body.candidate_sign_ids), None
        )
        if not candidates:
            raise StationError("unknown_sign", "候補の案内板が見つかりません")
        result = run_identify(
            identifier,
            image,
            body.mime_type,
            candidates,
            allow=identifier is not None and daily.allow(),
        )
        if result.status == "unavailable" and identifier is not None:
            LOGGER.warning("案内板を見分けられませんでした（%d ms）", result.latency_ms)
        box = result.box
        return schemas.SignIdentifyResponse(
            status=result.status,  # type: ignore[arg-type]
            sign_id=result.sign_id,
            box=schemas.ImageBox(
                x_min=box.x_min if box else 0.0,
                y_min=box.y_min if box else 0.0,
                x_max=box.x_max if box else 0.0,
                y_max=box.y_max if box else 0.0,
            ),
            corners=[schemas.ImagePoint(x=x, y=y) for x, y in result.corners],
            read_text=result.read_text,
            match_score=round(result.score, 3),
            engine="gemini" if identifier is not None else "stub",
            latency_ms=result.latency_ms,
            pack_version=station.version,
        )


def _add_page_routes(app: FastAPI, station: Station, settings: Settings) -> None:
    """審査員が開くページと、その表示内容。"""

    @app.get("/", include_in_schema=False)
    def page() -> FileResponse:
        return FileResponse(
            STATIC_DIR / "index.html", headers={"Content-Security-Policy": PAGE_CSP}
        )

    @app.get("/v1/site", response_model=schemas.SiteResponse)
    def site() -> schemas.SiteResponse:
        return schemas.SiteResponse(
            pack_version=station.version,
            attribution=station.pack["attribution"],
            video_url=settings.video_url,
            apk_url=settings.apk_url,
            engine=settings.gemini_backend,
        )

    @app.get("/v1/map", response_model=schemas.MapResponse)
    def floor_map(ordinal: float) -> schemas.MapResponse:
        if ordinal not in station.floors():
            raise StationError("unknown_floor", "その階のデータがありません")
        return schemas.MapResponse(
            ordinal=ordinal,
            lines=station.map_lines(ordinal),
            gates=[schemas.MapGate(**gate) for gate in station.map_gates(ordinal)],
        )

    @app.get("/survey", include_in_schema=False)
    def survey_page() -> FileResponse:
        return FileResponse(
            STATIC_DIR / "survey.html", headers={"Content-Security-Policy": PAGE_CSP}
        )


def _add_api_routes(
    app: FastAPI, station: Station, resolver: DestinationResolver
) -> None:
    """アプリが呼ぶ API。"""

    @app.get("/healthz", response_model=schemas.HealthResponse)
    def healthz() -> schemas.HealthResponse:
        return schemas.HealthResponse(status="ok", pack_version=station.version)

    @app.get("/v1/station", response_model=schemas.StationResponse)
    def station_data() -> schemas.StationResponse:
        return _station_response(station)

    @app.post("/v1/route", response_model=schemas.RouteResponse)
    def route(body: schemas.RouteRequest) -> schemas.RouteResponse:
        plan = station.plan(
            body.to_gate_id,
            from_sign_id=body.from_sign_id,
            from_gate_id=body.from_gate_id,
            same_floor=body.same_floor,
        )
        return _route_response(station, plan)

    @app.post("/v1/destination", response_model=schemas.DestinationResponse)
    def destination(body: schemas.DestinationRequest) -> schemas.DestinationResponse:
        if body.from_sign_id and body.from_sign_id not in station.signs:
            raise StationError("unknown_sign", "案内板が見つかりません")
        result = resolver.resolve(body.text, body.from_sign_id)
        return schemas.DestinationResponse(
            status=result.status,
            candidates=[_candidate(item) for item in result.candidates],
            engine=result.engine,
            latency_ms=result.latency_ms,
            pack_version=station.version,
        )


def create_app(
    settings: Settings | None = None,
    classifier: Classifier | None = None,
    sign_identifier: SignIdentifier | None = None,
    integrity_decoder: Decoder | None = None,
) -> FastAPI:
    """サーバーを組み立てる。

    Args:
        settings: 設定。省略時は環境変数から読む。
        classifier: Gemini の判定処理。テストで差し替えるときに渡す。
        sign_identifier: 案内板を見分ける処理。テストで差し替えるときに渡す。
        integrity_decoder: Play Integrity のトークンを読む処理。テストで差し替える。

    Returns:
        FastAPI のアプリ。
    """
    logging.basicConfig(level=logging.INFO)
    resolved = settings or load_settings()
    station = Station.load(resolved.pack_path)
    targets = build_targets(station.pack)
    resolver = DestinationResolver(
        station,
        targets,
        _build_classifier(resolved, station, classifier),
        WindowLimiter(resolved.gemini_daily_limit, SECONDS_PER_DAY),
    )
    app = FastAPI(title="駅ナビAR API", version=station.version)
    _add_exception_handlers(app)
    _add_middleware(app, resolved)
    _add_api_routes(app, station, resolver)
    _add_sign_vision_route(
        app,
        station,
        _build_sign_identifier(resolved, sign_identifier),
        WindowLimiter(resolved.gemini_daily_limit, SECONDS_PER_DAY),
        IntegrityGate(resolved, integrity_decoder),
    )
    _add_page_routes(app, station, resolved)
    app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")
    LOGGER.info(
        "起動しました: 版 %s、行き先の判定 %s、Play Integrity %s",
        station.version,
        resolved.gemini_backend,
        resolved.integrity_mode,
    )
    return app
