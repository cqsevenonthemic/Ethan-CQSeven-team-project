"""Run from the repository root: python -m uvicorn planner_service.app:app."""

import logging

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from planner_service.schemas import (
    ActiveResponse, ErrorResponse, ObserveRequest, RecognizeRequest, SummaryRequest,
    TerminalResponse,
)
from planner_service.service import FeatureUnavailable, decide

logger = logging.getLogger(__name__)
app = FastAPI(title="Micromouse Planner", version="0.2.0")


def error(status: int, code: str, message: str) -> JSONResponse:
    return JSONResponse(status_code=status, content={"error": {"code": code, "message": message}})


@app.exception_handler(RequestValidationError)
async def validation_error(request: Request, exc: RequestValidationError):
    # Do not echo request bodies (especially images) or Pydantic input values.
    return error(422, "INVALID_INPUT", "; ".join(
        f"{'.'.join(map(str, item['loc']))}: {item['msg']}" for item in exc.errors()
    ))


@app.exception_handler(FeatureUnavailable)
async def unavailable(request: Request, exc: FeatureUnavailable):
    return error(501, exc.code, exc.message)


@app.exception_handler(Exception)
async def unexpected_error(request: Request, exc: Exception):
    logger.error("Unhandled planner error", exc_info=(type(exc), exc, exc.__traceback__))
    return error(500, "INTERNAL_ERROR", "Unexpected planner error; no action was issued")


@app.get("/health")
def health():
    return {"status": "ok", "version": "0.2.0",
            "capabilities": ["confirmed_map_astar", "visited_cell_search", "two_target_tasks",
                             "delivery_correction", "bounded_scan_progress"]}


@app.post("/next_action", response_model=ActiveResponse | TerminalResponse,
          responses={422: {"model": ErrorResponse}, 501: {"model": ErrorResponse},
                     500: {"model": ErrorResponse}})
def next_action(body: ObserveRequest):
    return decide(body)


@app.post("/recognize", responses={422: {"model": ErrorResponse}, 501: {"model": ErrorResponse}})
def recognize(body: RecognizeRequest):
    raise FeatureUnavailable("VISION_NOT_IMPLEMENTED", "ONNX recognition is not implemented yet")


@app.post("/plan_summary", responses={422: {"model": ErrorResponse}, 501: {"model": ErrorResponse}})
def plan_summary(body: SummaryRequest):
    raise FeatureUnavailable("RL_NOT_IMPLEMENTED", "Q-Learning summary is not implemented yet")
