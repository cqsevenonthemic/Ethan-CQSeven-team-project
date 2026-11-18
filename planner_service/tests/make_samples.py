"""Regenerate synthetic shared examples using the actual decision service."""

import base64
import json
from pathlib import Path
import struct
import zlib

from planner_service.schemas import ObserveRequest
from planner_service.service import decide
from planner_service.tests.helpers import observe, open_map


def samples():
    result = {}

    def pair(request_name, response_name, body):
        body.setdefault("visited_cells", [[body["pose"]["x"], body["pose"]["z"]]])
        body.setdefault("observation_seq", 0)
        result[request_name] = body
        result[response_name] = decide(ObserveRequest.model_validate(body)).model_dump(mode="json")

    pair("sample_observe.json", "sample_action.json", observe())
    body = observe()
    body["pose"]["z"] = body["slots"]["s1"]["z"] = 7
    pair("sample_awaiting_request.json", "sample_awaiting_response.json", body)
    body = observe()
    body["pose"]["z"] = body["slots"]["s1"]["z"] = 7
    body["slots"]["s1"].update(state="delivered", verified=True)
    body["carry_slot"] = None
    pair("sample_completed_request.json", "sample_completed_response.json", body)
    body = observe()
    body["known_walls"] = [[15] * 16 for _ in range(16)]
    pair("sample_unreachable_request.json", "sample_unreachable_response.json", body)
    body = dict(pose={"x": 0, "z": 0, "theta": 0}, step_count=0,
                seen_walls=[[0] * 16 for _ in range(16)], known_walls=[[0] * 16 for _ in range(16)],
                slots={}, carry_slot=None, event=None)
    pair("sample_exploring_request.json", "sample_exploring_response.json", body)
    body = dict(pose={"x": 0, "z": 0, "theta": 0}, step_count=0,
                seen_walls=[[0] * 16 for _ in range(16)], known_walls=[[0] * 16 for _ in range(16)],
                slots={}, carry_slot=None, event=None)
    pair("sample_legacy_request.json", "sample_legacy_response.json", body | {"visited_cells": None})
    body = observe()
    body["slots"]["s2"].update(x=7, z=6, state="waiting", verified=False)
    pair("sample_keep_request.json", "sample_keep_response.json", body)
    body = observe()
    body["slots"]["s1"]["pred_role"] = "quyuan"
    body["slots"]["s2"].update(x=7, z=6, state="waiting", verified=False, pred_role="zhaojun")
    pair("sample_switch_request.json", "sample_switch_response.json", body)
    body = observe()
    body["pose"]["z"] = body["slots"]["s1"]["z"] = 7
    body["slots"]["s2"].update(x=3, z=4, state="waiting", verified=False)
    body["event"] = dict(type="delivery_rejected", slot_id="s1", rejected_at=dict(x=7, z=7))
    pair("sample_rejected_request.json", "sample_rejected_response.json", body)
    body = dict(pose={"x": 0, "z": 0, "theta": 0}, step_count=0,
                seen_walls=[[0] * 16 for _ in range(16)], known_walls=[[0] * 16 for _ in range(16)],
                slots={}, carry_slot=None, event=None, visited_cells=[[0, 0]], observation_seq=0)
    for _ in range(4):
        scan_result = decide(ObserveRequest.model_validate(body))
        body["progress"] = scan_result.progress.model_dump(mode="json")
        body["pose"]["theta"] = (body["pose"]["theta"] + (-90 if scan_result.action == "turn_left" else 90)) % 360
        body["step_count"] += 1
        body["observation_seq"] += 1
    pair("sample_stalled_request.json", "sample_stalled_response.json", body)
    # Encode a complete RGB 1x1 PNG with correct chunk CRCs, not just a signature.
    def chunk(kind, content):
        return (struct.pack(">I", len(content)) + kind + content
                + struct.pack(">I", zlib.crc32(kind + content)))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(b"\x00\xff\xff\xff"))
           + chunk(b"IEND", b""))
    result["sample_recognize_request.json"] = {"image_base64": base64.b64encode(png).decode("ascii")}
    result["sample_summary_request.json"] = dict(
        **open_map(), start_pose={"x": 0, "z": 0, "theta": 0},
        initial_targets={"quyuan": {"x": 12, "z": 4}, "zhaojun": {"x": 4, "z": 11}},
    )
    result["sample_error.json"] = {"error": {"code": "VISION_NOT_IMPLEMENTED", "message": "ONNX recognition is not implemented yet"}}
    return result


if __name__ == "__main__":
    destination = Path(__file__).resolve().parents[2] / "contracts"
    for filename, content in samples().items():
        (destination / filename).write_text(json.dumps(content, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
