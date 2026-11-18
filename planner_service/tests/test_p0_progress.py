from copy import deepcopy

from fastapi.testclient import TestClient
import pytest

from planner_service.app import app
from planner_service.tests.helpers import observe, set_edge

client = TestClient(app)


def unknown_request():
    return dict(pose=dict(x=0, z=0, theta=0), step_count=0, slots={}, carry_slot=None,
                visited_cells=[[0, 0]], observation_seq=0,
                seen_walls=[[0] * 16 for _ in range(16)], known_walls=[[0] * 16 for _ in range(16)])


def test_stale_scan_rotations_stop_and_passive_retries_do_not_resume():
    body = unknown_request()
    actions = []
    for _ in range(8):
        response = client.post("/next_action", json=body)
        assert response.status_code == 200
        result = response.json()
        assert client.post("/next_action", json=body).json() == result
        body["progress"] = result["progress"]
        if result["action"] is None:
            assert result["status"] == "awaiting_observation"
            break
        action = result["action"]
        assert action in ("turn_left", "turn_right")
        actions.append(action)
        body["pose"]["theta"] = (body["pose"]["theta"] + (-90 if action == "turn_left" else 90)) % 360
        body["step_count"] += 1
        body["observation_seq"] += 1
    assert len(actions) == 4
    for _ in range(5):
        body["observation_seq"] += 1
        result = client.post("/next_action", json=body).json()
        assert result["status"] == "awaiting_observation"
        assert result["action"] is None
        body["progress"] = result["progress"]
    # Actual information changes recover without resetting service state.
    set_edge(body, 0, 0, 0, wall=False)
    body["observation_seq"] += 1
    result = client.post("/next_action", json=body).json()
    assert result["status"] == "exploring"
    assert result["action"] == "forward"
    assert result["progress"]["scan_attempts"] == 0


def test_scan_stall_does_not_block_safe_unvisited_search():
    body = unknown_request()
    set_edge(body, 0, 0, 0, wall=False)
    result = client.post("/next_action", json=body).json()
    body["progress"] = result["progress"] | {"scan_attempts": 4}
    result = client.post("/next_action", json=body).json()
    assert result["action"] == "forward"
    assert result["status"] == "exploring"


def test_action_without_new_scan_waits_and_new_sequence_recovers():
    body = unknown_request()
    result = client.post("/next_action", json=body).json()
    body.update(progress=result["progress"], step_count=1)
    body["pose"]["theta"] = 90
    waiting = client.post("/next_action", json=body).json()
    assert waiting["status"] == "awaiting_observation"
    assert waiting["action"] is None
    body["observation_seq"] += 1
    assert client.post("/next_action", json=body).json()["action"] is not None


def test_legacy_missing_coverage_cannot_claim_search_exhausted():
    body = unknown_request()
    del body["visited_cells"]
    body["seen_walls"] = [[15] * 16 for _ in range(16)]
    body["known_walls"] = [[15] * 16 for _ in range(16)]
    result = client.post("/next_action", json=body).json()
    assert result["status"] == "awaiting_observation"
    assert result["done"] is False


def test_exhausted_is_not_completed_and_repeated_visits_are_a_set():
    body = unknown_request()
    body["visited_cells"] = [[0, 0], [0, 0]]
    body["seen_walls"] = [[15] * 16 for _ in range(16)]
    body["known_walls"] = [[15] * 16 for _ in range(16)]
    result = client.post("/next_action", json=body).json()
    assert result["status"] == "unreachable"
    assert result["done"] is False
    assert result["remaining_tasks"] == 2


@pytest.mark.parametrize("mutation", [
    lambda b: b.update(visited_cells=[]),
    lambda b: b.update(visited_cells=[[16, 0]]),
    lambda b: b.update(visited_cells=[[True, 0]]),
    lambda b: b.update(observation_seq=-1),
    lambda b: b.update(progress={"last_step_count": 1}),
    lambda b: b.update(progress={"last_observation_seq": 1}),
    lambda b: b.update(progress={"scan_attempts": 5}),
])
def test_invalid_observation_progress(mutation):
    body = unknown_request()
    mutation(body)
    assert client.post("/next_action", json=body).status_code == 422


def test_unverified_duplicate_predictions_are_allowed():
    body = observe()
    body["slots"]["s2"].update(state="waiting", verified=False, pred_role="zhaojun", x=4, z=5)
    assert client.post("/next_action", json=body).status_code == 200


def test_contradictory_rejection_and_recognition_association_are_rejected():
    body = observe()
    body["pose"]["z"] = body["slots"]["s1"]["z"] = 7
    body["event"] = dict(type="delivery_rejected", slot_id="s1", rejected_at=dict(x=7, z=7))
    # The other role is already verified, so this reversal would contradict it.
    assert client.post("/next_action", json=body).status_code == 422
    body = observe()
    body["event"] = dict(type="trigger_discovered", slot_id="s1", x=7, z=6,
                         pred_role="zhaojun", confidence=0.9, recognition_id="different-image")
    assert client.post("/next_action", json=body).status_code == 422


def test_confirmed_map_costs_preserve_orientation_and_switch_only_if_better():
    body = observe()
    body["slots"]["s2"].update(state="waiting", verified=False, x=7, z=6)
    before = deepcopy(body)
    result = client.post("/next_action", json=body).json()
    assert result["decision"]["cost_keep"] == 9
    assert result["decision"]["cost_switch"] == 11
    assert result["decision"]["switched"] is False
    assert body == before
    body["slots"]["s1"]["pred_role"] = "quyuan"
    body["slots"]["s2"]["pred_role"] = "zhaojun"
    result = client.post("/next_action", json=body).json()
    assert result["decision"]["cost_switch"] < result["decision"]["cost_keep"]
    assert result["decision"]["switched"] is True
    assert result["carry_slot"] == "s2"
    assert result["slots"]["s1"]["state"] == "waiting"


def test_incomplete_map_uses_deterministic_keep_not_hidden_costs():
    body = observe()
    body["slots"]["s2"].update(state="waiting", verified=False, x=7, z=6)
    body.update(seen_walls=[[0] * 16 for _ in range(16)], known_walls=[[0] * 16 for _ in range(16)],
                visited_cells=[[7, 6]])
    result = client.post("/next_action", json=body).json()
    assert result["carry_slot"] == "s1"
    assert result["decision"]["switched"] is False
    assert result["decision"]["cost_keep"] is None
    assert result["decision"]["cost_switch"] is None
