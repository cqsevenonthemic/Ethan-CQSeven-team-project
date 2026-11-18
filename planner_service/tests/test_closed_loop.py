"""Mock Unity applies decisions and supplies new observations; no hardcoded actions."""

from fastapi.testclient import TestClient

from planner_service.app import app
from planner_service.planner.grid import DIRECTIONS
from planner_service.tests.helpers import observe, set_edge


def test_unknown_local_map_discovery_pickup_delivery_closed_loop():
    # Hidden synthetic world: a two-cell corridor; only the mock Unity can see it.
    world = {"seen_walls": [[15] * 16 for _ in range(16)],
             "known_walls": [[15] * 16 for _ in range(16)]}
    set_edge(world, 7, 6, 0, wall=False)
    body = observe()
    del body["slots"]["s1"]
    body["carry_slot"] = None
    body["seen_walls"] = [[0] * 16 for _ in range(16)]
    body["known_walls"] = [[0] * 16 for _ in range(16)]
    observed_completed = False
    visited = set()
    progress = None
    with TestClient(app) as client:
        for _ in range(20):
            x, z, theta = (body["pose"][key] for key in ("x", "z", "theta"))
            visited.add((x, z))
            body.update(visited_cells=sorted(visited), observation_seq=body["step_count"], progress=progress)
            # Observe this cell's edges; symmetrically update the shared observation map.
            for direction, (dx, dz, bit, _) in DIRECTIONS.items():
                if 0 <= x + dx < 16 and 0 <= z + dz < 16:
                    set_edge(body, x, z, direction, wall=bool(world["known_walls"][x][z] & bit))
            body["event"] = None
            if (x, z) == (7, 7) and "s1" not in body["slots"]:
                body["slots"]["s1"] = dict(x=7, z=7, pred_role="zhaojun", verified=False, state="waiting")
                body["event"] = dict(type="trigger_discovered", slot_id="s1", x=7, z=7,
                                     pred_role="zhaojun", confidence=0.99)
            response = client.post("/next_action", json=body)
            assert response.status_code == 200, response.text
            result = response.json()
            progress = result["progress"]
            body["slots"], body["carry_slot"] = result["slots"], result["carry_slot"]
            if result["status"] == "awaiting_delivery":
                assert body["carry_slot"] == "s1"
                assert (x, z) == (7, 7)
                body["slots"]["s1"].update(state="delivered", verified=True)
                body["carry_slot"] = None
                continue
            if result["done"]:
                assert result["status"] == "completed"
                assert result["remaining_tasks"] == 0
                observed_completed = True
                break
            action = result["action"]
            assert action is not None
            if action == "forward":
                dx, dz, bit, _ = DIRECTIONS[theta]
                assert body["seen_walls"][x][z] & bit
                assert not world["known_walls"][x][z] & bit
                body["pose"].update(x=x + dx, z=z + dz)
            else:
                body["pose"]["theta"] = (theta + (-90 if action == "turn_left" else 90)) % 360
            body["step_count"] += 1
            if body["carry_slot"]:
                body["slots"][body["carry_slot"]].update(x=body["pose"]["x"], z=body["pose"]["z"])
    assert observed_completed
    assert body["step_count"] == 1


def test_known_map_multi_action_delivery_then_unity_acknowledgement():
    body = observe()
    body["pose"] = {"x": 0, "z": 0, "theta": 180}
    body["slots"]["s1"].update(x=0, z=0)
    actions = []
    with TestClient(app) as client:
        for _ in range(50):
            response = client.post("/next_action", json=body)
            assert response.status_code == 200
            result = response.json()
            if result["status"] == "awaiting_delivery":
                assert body["pose"]["x"] == body["pose"]["z"] == 7
                assert result["done"] is False
                body["carry_slot"] = None
                body["slots"]["s1"].update(state="delivered", verified=True)
                completed = client.post("/next_action", json=body).json()
                assert completed["done"] is True
                assert completed["action"] is None
                break
            assert result["status"] == "planning"
            action = result["action"]
            actions.append(action)
            x, z, theta = (body["pose"][key] for key in ("x", "z", "theta"))
            if action == "forward":
                dx, dz, bit, _ = DIRECTIONS[theta]
                assert body["seen_walls"][x][z] & bit
                assert not body["known_walls"][x][z] & bit
                body["pose"].update(x=x + dx, z=z + dz)
            else:
                body["pose"]["theta"] = (theta + (-90 if action == "turn_left" else 90)) % 360
            body["step_count"] += 1
            body["slots"]["s1"].update(x=body["pose"]["x"], z=body["pose"]["z"])
        else:
            raise AssertionError("did not arrive within the action bound")
    assert len(actions) == 16  # 14 forward + 2 rotations; all generated by A*.
    assert actions.count("forward") == 14
