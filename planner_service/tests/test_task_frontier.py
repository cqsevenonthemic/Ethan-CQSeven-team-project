import pytest

from planner_service.planner.task_frontier import ExplorationStrategy
from planner_service.schemas import ObserveRequest
from planner_service.service import decide
from planner_service.tests.helpers import observe, set_edge


def frontier_snapshot(*, second_discovered):
    body = observe()
    body.update(seen_walls=[[15] * 16 for _ in range(16)], known_walls=[[15] * 16 for _ in range(16)],
                visited_cells=[[7, 6]], observation_seq=1)
    body["pose"]["theta"] = 90
    set_edge(body, 7, 6, 90)
    set_edge(body, 7, 6, 0, seen=False)
    if not second_discovered:
        del body["slots"]["s2"]
    return body


@pytest.mark.parametrize(("strategy", "second_discovered", "action"), [
    (ExplorationStrategy.BASELINE, True, "forward"),
    (ExplorationStrategy.KNOWN_TASKS, False, "forward"),
    (ExplorationStrategy.KNOWN_TASKS, True, "turn_left"),
    (ExplorationStrategy.CARRIED_TASK, False, "turn_left"),
])
def test_frontier_policy_approaches_goal_without_traversing_unknown(strategy, second_discovered, action):
    request = ObserveRequest.model_validate(frontier_snapshot(second_discovered=second_discovered))
    result = decide(request, exploration=strategy)
    assert result.action == action
    if action == "turn_left":
        assert result.trajectory.actions == ["turn_left"]
        assert result.trajectory.poses[-1].model_dump() == {"x": 7, "z": 6, "theta": 0}
        assert result.progress.pending_scan
        assert set(result.slots) == set(request.slots)


def test_task_frontier_falls_back_to_safe_coverage_after_scan_limit():
    body = frontier_snapshot(second_discovered=True)
    mode = ExplorationStrategy.KNOWN_TASKS
    first = decide(ObserveRequest.model_validate(body), exploration=mode)
    body["progress"] = first.progress.model_dump() | {"scan_attempts": 4}
    result = decide(ObserveRequest.model_validate(body), exploration=mode)
    assert result.action == "forward"
    assert result.trajectory.poses[-1].model_dump() == {"x": 8, "z": 6, "theta": 90}


def test_task_frontier_uses_confirmed_route_when_goal_edge_is_observed():
    body = frontier_snapshot(second_discovered=True)
    set_edge(body, 7, 6, 0)
    result = decide(ObserveRequest.model_validate(body), exploration=ExplorationStrategy.CARRIED_TASK)
    assert result.status == "planning"
    assert result.trajectory.actions == ["turn_left", "forward"]
