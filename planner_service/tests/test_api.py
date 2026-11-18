import copy
import base64
import importlib
import json
from pathlib import Path
import struct
import zlib

import pytest
from fastapi.testclient import TestClient

from planner_service.app import app
from planner_service.schemas import ActiveResponse, TerminalResponse
from planner_service.tests.helpers import observe, set_edge

CONTRACTS = Path(__file__).resolve().parents[2] / "contracts"
client = TestClient(app)


def load(name):
    return json.loads((CONTRACTS / name).read_text(encoding="utf-8"))


@pytest.mark.parametrize("request_name,response_name,status", [
    ("sample_observe.json", "sample_action.json", "planning"),
    ("sample_completed_request.json", "sample_completed_response.json", "completed"),
    ("sample_awaiting_request.json", "sample_awaiting_response.json", "awaiting_delivery"),
    ("sample_unreachable_request.json", "sample_unreachable_response.json", "unreachable"),
    ("sample_exploring_request.json", "sample_exploring_response.json", "exploring"),
    ("sample_keep_request.json", "sample_keep_response.json", "planning"),
    ("sample_switch_request.json", "sample_switch_response.json", "planning"),
    ("sample_rejected_request.json", "sample_rejected_response.json", "planning"),
    ("sample_stalled_request.json", "sample_stalled_response.json", "awaiting_observation"),
    ("sample_legacy_request.json", "sample_legacy_response.json", "awaiting_observation"),
])
def test_shared_contract_examples(request_name, response_name, status):
    result = client.post("/next_action", json=load(request_name))
    assert result.status_code == 200
    body = result.json()
    assert body == load(response_name)
    assert body["status"] == status
    model = ActiveResponse if status in ("planning", "exploring") else TerminalResponse
    model.model_validate(body)
    trajectory = body["trajectory"]
    assert len(trajectory["poses"]) == len(trajectory["actions"]) + 1
    assert trajectory["steps"] == len(trajectory["actions"])
    if request_name in ("sample_observe.json", "sample_keep_request.json", "sample_switch_request.json"):
        assert body["action"] == "forward"
        assert body["plan_path"] == [[7, 6], [7, 7]]
    elif status == "planning":
        assert body["action"] in ("forward", "turn_left", "turn_right")
    elif status == "exploring":
        assert body["action"] in ("turn_left", "turn_right")
    else:
        assert body["action"] is None
        assert body["done"] is (status == "completed")


@pytest.mark.parametrize("mutation", [
    lambda b: b["pose"].update(x=16),
    lambda b: b["pose"].update(x=True),
    lambda b: b["pose"].update(z="6"),
    lambda b: b["pose"].update(theta=45),
    lambda b: b.update(step_count=-1),
    lambda b: b.update(step_count=False),
    lambda b: b.update(seen_walls=[[15]]),
    lambda b: b["known_walls"][1].__setitem__(1, 16),
    lambda b: b["known_walls"][1].__setitem__(1, True),
    lambda b: b["seen_walls"][0].__setitem__(0, 0),
    lambda b: b["seen_walls"][5].__setitem__(5, 14),
    lambda b: b["known_walls"][5].__setitem__(5, 1),
    lambda b: b["known_walls"][0].__setitem__(0, 0),
    lambda b: b.update(carry_slot=None),
    lambda b: b["slots"]["s1"].update(x=0),
    lambda b: b["slots"]["s2"].update(verified=False),
    lambda b: b["slots"].update(s3=b["slots"]["s2"]),
    lambda b: b.update(event={"type": "unknown"}),
    lambda b: b.update(event={"type": "trigger_discovered", "slot_id": "s2", "x": 8,
                             "z": 8, "pred_role": "quyuan", "confidence": 0.9}),
])
def test_invalid_observations(mutation):
    body = observe()
    mutation(body)
    response = client.post("/next_action", json=body)
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "INVALID_INPUT"
    assert "action" not in response.json()


def test_missing_body_and_malformed_json():
    assert client.post("/next_action").status_code == 422
    response = client.post("/next_action", content="{", headers={"Content-Type": "application/json"})
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "INVALID_INPUT"


def test_remaining_includes_undiscovered_and_forward_never_uses_unknown():
    body = observe()
    del body["slots"]["s2"]
    set_edge(body, 7, 6, 0, seen=False)
    result = client.post("/next_action", json=body).json()
    assert result["remaining_tasks"] == 2
    assert result["done"] is False
    # A safe detour may exist, but the immediate unknown edge may not be crossed.
    assert result["action"] != "forward"


def test_waiting_pickup_and_discovery():
    body = observe()
    body["carry_slot"] = None
    body["slots"]["s1"]["state"] = "waiting"
    body["event"] = dict(type="trigger_discovered", slot_id="s1", x=7, z=6,
                         pred_role="zhaojun", confidence=0.9)
    response = client.post("/next_action", json=body)
    assert response.status_code == 200
    assert response.json()["carry_slot"] == "s1"
    assert response.json()["slots"]["s1"]["state"] == "carried"
    assert body["slots"]["s1"]["state"] == "waiting"


def test_walk_to_waiting_target_then_pick_up():
    body = observe()
    body["carry_slot"] = None
    body["slots"]["s1"].update(state="waiting", z=5)
    response = client.post("/next_action", json=body).json()
    assert response["status"] == "planning"
    assert response["carry_slot"] is None
    assert response["trajectory"]["poses"][-1] == {"x": 7, "z": 5, "theta": 180}


def test_retries_and_interleaved_snapshots_are_stateless():
    body = observe()
    before = copy.deepcopy(body)
    first = client.post("/next_action", json=body).json()
    client.post("/next_action", json=load("sample_completed_request.json"))
    assert client.post("/next_action", json=body).json() == first
    assert body == before


def test_vision_and_rl_remain_explicitly_unavailable():
    response = client.post("/recognize", json=load("sample_recognize_request.json"))
    assert response.status_code == 501
    assert response.json() == load("sample_error.json")
    response = client.post("/plan_summary", json=load("sample_summary_request.json"))
    assert response.status_code == 501
    assert response.json()["error"]["code"] == "RL_NOT_IMPLEMENTED"


@pytest.mark.parametrize("image", ["not base64!", "data:image/png;base64,AAAA", "YQ=="])
def test_invalid_image_encoding(image):
    response = client.post("/recognize", json={"image_base64": image})
    assert response.status_code == 422
    assert "input" not in response.json()["error"]


def test_invalid_summary_and_additive_input_fields():
    body = load("sample_summary_request.json")
    del body["initial_targets"]["quyuan"]
    assert client.post("/plan_summary", json=body).status_code == 422
    body = observe()
    body["future_metadata"] = {"client": "Unity"}
    assert client.post("/next_action", json=body).status_code == 200


def test_unexpected_exception_is_safe(monkeypatch):
    module = importlib.import_module("planner_service.app")

    def fail(_):
        raise RuntimeError("private implementation detail")

    monkeypatch.setattr(module, "decide", fail)
    with TestClient(app, raise_server_exceptions=False) as safe_client:
        response = safe_client.post("/next_action", json=observe())
    assert response.status_code == 500
    assert response.json()["error"]["code"] == "INTERNAL_ERROR"
    assert "private" not in response.text


def test_health_and_openapi():
    assert client.get("/health").json()["status"] == "ok"
    schema = client.get("/openapi.json").json()
    assert all(path in schema["paths"] for path in ("/next_action", "/recognize", "/plan_summary"))


def test_shared_image_is_a_complete_decodable_png():
    png = base64.b64decode(load("sample_recognize_request.json")["image_base64"], validate=True)
    assert png[:8] == b"\x89PNG\r\n\x1a\n"
    offset, kinds, compressed = 8, [], b""
    while offset < len(png):
        size = struct.unpack(">I", png[offset:offset + 4])[0]
        kind = png[offset + 4:offset + 8]
        content = png[offset + 8:offset + 8 + size]
        crc = struct.unpack(">I", png[offset + 8 + size:offset + 12 + size])[0]
        assert zlib.crc32(kind + content) == crc
        kinds.append(kind)
        if kind == b"IHDR":
            assert struct.unpack(">IIBBBBB", content) == (1, 1, 8, 2, 0, 0, 0)
        if kind == b"IDAT":
            compressed += content
        offset += size + 12
    assert offset == len(png)
    assert kinds == [b"IHDR", b"IDAT", b"IEND"]
    assert zlib.decompress(compressed) == b"\x00\xff\xff\xff"
