"""Run from the repository root: python -m uvicorn planner_service.app:app."""

import logging
import os

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from planner_service.planner.task_frontier import ExplorationStrategy
from planner_service.schemas import (
    ActiveResponse, ErrorResponse, ObserveRequest, RecognizeRequest, RecognizeResponse,
    SummaryRequest, SummaryResponse, TerminalResponse,
)
from planner_service.service import FeatureUnavailable, decide
from planner_service.summary import SummaryBusy, SummaryUnreachable, plan
from planner_service.vision.inference import ModelUnavailable, VisionInferenceFailed, recognizer_for

logger = logging.getLogger(__name__)
app = FastAPI(title="Micromouse Planner", version="0.2.0")
exploration_strategy = ExplorationStrategy(os.getenv("PLANNER_EXPLORATION", "baseline"))


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
                             "delivery_correction", "bounded_scan_progress", "offline_q_learning"]}


@app.post("/next_action", response_model=ActiveResponse | TerminalResponse,
          responses={422: {"model": ErrorResponse}, 501: {"model": ErrorResponse},
                     500: {"model": ErrorResponse}})
def next_action(body: ObserveRequest):
    return decide(body, exploration=exploration_strategy)


@app.post("/recognize", response_model=RecognizeResponse,
          responses={422: {"model": ErrorResponse}, 501: {"model": ErrorResponse},
                     500: {"model": ErrorResponse}})
def recognize(body: RecognizeRequest):
    """Run the configured model using the shared two-label preprocessing."""
    model_path = os.getenv("PLANNER_ONNX_MODEL")
    try:
        if not model_path:
            raise ModelUnavailable("A trained ONNX model is not configured")
        result = recognizer_for(model_path).predict_base64(body.image_base64)
    except ModelUnavailable as exc:
        if model_path:
            logger.warning("Configured ONNX recognition is unavailable: %s", exc)
        raise FeatureUnavailable("VISION_NOT_IMPLEMENTED", "ONNX recognition is not implemented yet") from exc
    except VisionInferenceFailed as exc:
        logger.error("ONNX recognition failed", exc_info=True)
        raise FeatureUnavailable("VISION_INFERENCE_FAILED", "Image recognition failed") from exc
    except ValueError as exc:
        return error(422, "INVALID_INPUT", str(exc))
    result["status"] = "low_confidence" if result["confidence"] < 0.60 else "recognized"
    return result


@app.post("/plan_summary", response_model=SummaryResponse,
          responses={422: {"model": ErrorResponse}, 503: {"model": ErrorResponse},
                     500: {"model": ErrorResponse}})
def plan_summary(body: SummaryRequest):
    try:
        return plan(body)
    except SummaryUnreachable as exc:
        return error(422, "INVALID_INPUT", str(exc))
    except SummaryBusy:
        return error(503, "SUMMARY_BUSY", "Another offline training request is in progress")
