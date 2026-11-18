from copy import deepcopy
import ast
from pathlib import Path

from fastapi.testclient import TestClient
import pytest

from planner_service.app import app
from planner_service.schemas import MapInput
from planner_service.simulation.runner import local_decision, run_world
from planner_service.simulation.replay import validate_trace
from planner_service.simulation.sensor import scan
from planner_service.simulation.world import Figure, World, generate_world
from planner_service.tests.helpers import open_map, set_edge


def sensor_world(data=None, start=(0, 0, 0)):
    return World((data or open_map())["known_walls"],
                 (Figure((7, 7), "zhaojun"), Figure((8, 8), "quyuan")), 0, start)


def test_sensor_fov_range_and_occlusion():
    world = sensor_world(start=(5, 5, 0))
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    rays = scan(world, world.start, seen, known)
    assert len(rays) == 121
    assert rays[0]["angle_deg"] == -90
    assert rays[-1]["angle_deg"] == 90
    assert not seen[5][5] & 4  # rear south wall/edge is outside the FOV
    assert seen[5][5] & 1 and seen[5][5] & 2 and seen[5][5] & 8
    assert rays[60]["hit"] is False
    assert rays[60]["distance_m"] == 4.5
    assert not seen[5][10] & 1
    data = open_map()
    set_edge(data, 5, 7, 0, wall=True)
    world = sensor_world(data, (5, 5, 0))
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    rays = scan(world, world.start, seen, known)
    assert rays[60]["hit"] is True
    assert rays[60]["distance_m"] == 2.5
    assert known[5][7] & 1
    assert not seen[5][8] & 1  # no ray crosses the blocking wall
    MapInput(seen_walls=seen, known_walls=known)


def test_sensor_detects_wall_at_maximum_range_and_rotates():
    data = open_map()
    set_edge(data, 5, 9, 0, wall=True)
    world = sensor_world(data, (5, 5, 0))
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    assert scan(world, world.start, seen, known)[60]["distance_m"] == 4.5
    assert known[5][9] & 1
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    scan(world, (5, 5, 180), seen, known)
    assert seen[5][5] & 4
    assert not seen[5][5] & 1


@pytest.mark.parametrize("seed", [0, 1, 2, 3, 19, 42, 73, 99])
def test_two_unknown_figures_with_real_sensor_and_optional_wrong_prediction(seed):
    result = run_world(generate_world(seed), invert_first=seed % 2 == 0)
    assert result.success, (seed, result.failure, result.steps)
    assert result.failure is None
    assert set(result.discoveries) == {"quyuan", "zhaojun"}
    assert result.steps == result.forward + result.turns
    assert result.turns > 0
    if seed % 2 == 0:
        assert result.rejections == 1
    assert len([row for row in result.trace if row["action"] is not None]) == result.steps
    assert result.trace[-1]["status"] == "completed"
    assert validate_trace(result.trace) == dict(steps=result.steps, forward=result.forward, turns=result.turns)


def test_policy_boundary_has_only_observations_and_no_undiscovered_target_positions():
    world = generate_world(11)
    previous_slots = set()

    def inspect(payload):
        assert set(payload) == {"pose", "seen_walls", "known_walls", "step_count", "slots",
                                "carry_slot", "visited_cells", "observation_seq", "progress", "event"}
        assert "initial_targets" not in payload
        for slot_id in set(payload["slots"]) - previous_slots:
            slot = payload["slots"][slot_id]
            assert (slot["x"], slot["z"]) == (payload["pose"]["x"], payload["pose"]["z"])
            assert slot["recognition_source"] == "mock"
            assert payload["event"]["recognition_id"] == slot["recognition_id"]
        previous_slots.update(payload["slots"])
        original = deepcopy(payload)
        result = local_decision(payload)
        assert payload == original
        return result

    assert run_world(world, decision_fn=inspect).success


def test_api_driven_full_task_and_sensor_freeze_is_bounded():
    with TestClient(app) as client:
        def http_decision(payload):
            response = client.post("/next_action", json=payload)
            assert response.status_code == 200, response.text
            return response.json()

        result = run_world(generate_world(5), decision_fn=http_decision, invert_first=True)
    assert result.success and result.rejections == 1
    frozen = run_world(generate_world(5), freeze_sensor=True)
    assert not frozen.success
    assert frozen.failure == "awaiting_observation"
    assert frozen.steps == 4


def test_generation_reproducibility_and_unreachable_scene_rejected():
    assert generate_world(15) == generate_world(15)
    world = generate_world(15)
    world.walls = [[15] * 16 for _ in range(16)]
    with pytest.raises(ValueError, match="reachable"):
        world.validate()


def test_start_cell_trigger_and_both_discovery_orders():
    data = open_map()
    orders = []
    for first_role, second_role in (("quyuan", "zhaojun"), ("zhaojun", "quyuan")):
        world = World(data["known_walls"], (Figure((0, 0), first_role), Figure((0, 1), second_role)), 0)
        result = run_world(world)
        assert result.success
        orders.append(result.discoveries)
        # The second target is encountered while the first is still carried.
        assert any(row["decision"]["cost_scope"] == "confirmed_map" for row in result.trace)
    assert orders == [["quyuan", "zhaojun"], ["zhaojun", "quyuan"]]


def test_production_decision_modules_do_not_import_truth_or_read_files():
    root = Path(__file__).resolve().parents[1]
    files = [root / "service.py", root / "schemas.py", *sorted((root / "planner").glob("*.py"))]
    for path in files:
        tree = ast.parse(path.read_text(encoding="utf-8"))
        for node in ast.walk(tree):
            if isinstance(node, ast.ImportFrom):
                assert not (node.module or "").startswith(("planner_service.simulation", "planner_service.tests"))
            if isinstance(node, ast.Call):
                if isinstance(node.func, ast.Name):
                    assert node.func.id != "open"
                if isinstance(node.func, ast.Attribute):
                    assert node.func.attr not in ("read_text", "read_bytes", "open")
