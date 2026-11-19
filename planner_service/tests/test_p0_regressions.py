"""Regressions from the course audit; no hidden target supplied to planning."""

from fastapi.testclient import TestClient

from planner_service.app import app
from planner_service.tests.helpers import observe, set_edge

client = TestClient(app)


def test_observed_but_unvisited_corridor_is_searched():
    body = observe()
    body.update(pose={"x": 0, "z": 0, "theta": 0}, carry_slot=None, slots={},
                visited_cells=[[0, 0]], known_walls=[[15] * 16 for _ in range(16)])
    set_edge(body, 0, 0, 0, wall=False)
    result = client.post("/next_action", json=body)
    assert result.status_code == 200
    assert result.json()["status"] == "exploring"
    assert result.json()["action"] == "forward"


def test_duplicate_verified_identity_cannot_complete():
    body = observe()
    body["carry_slot"] = None
    body["slots"] = {key: dict(x=8, z=8, pred_role="quyuan", verified=True, state="delivered")
                     for key in ("s1", "s2")}
    result = client.post("/next_action", json=body)
    assert result.status_code == 422


def test_second_person_while_carrying_is_a_normal_decision():
    body = observe()
    body["slots"]["s2"].update(x=7, z=6, state="waiting", verified=False)
    result = client.post("/next_action", json=body)
    assert result.status_code == 200
    assert result.json()["carry_slot"] in ("s1", "s2")


def test_rejection_corrects_prediction_without_mutating_snapshot():
    body = observe()
    body["pose"]["z"] = body["slots"]["s1"]["z"] = 7
    body["slots"]["s2"].update(x=3, z=4, pred_role="quyuan", verified=False, state="waiting")
    body["event"] = dict(type="delivery_rejected", slot_id="s1", rejected_at={"x": 7, "z": 7})
    first = client.post("/next_action", json=body)
    assert first.status_code == 200
    result = first.json()
    assert result["slots"]["s1"]["pred_role"] == "quyuan"
    assert result["slots"]["s1"]["verified"] is True
    assert result["slots"]["s2"]["pred_role"] == "zhaojun"
    assert client.post("/next_action", json=body).json() == result
    assert body["slots"]["s1"]["pred_role"] == "zhaojun"
