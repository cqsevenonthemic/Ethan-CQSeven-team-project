"""Compare A* against an independent unit-cost BFS, not a second A*."""

from collections import deque
import random

import pytest

from planner_service.planner.grid import can_forward
from planner_service.planner.oriented_astar import shortest_path
from planner_service.schemas import MapInput
from planner_service.tests.helpers import open_map, set_edge


def oracle(grid, start, goal, goal_theta=None):
    queue = deque([(start, 0)])
    visited = {start}
    deltas = [(0, 1), (1, 0), (0, -1), (-1, 0)]
    while queue:
        (x, z, theta), cost = queue.popleft()
        if (x, z) == goal and (goal_theta is None or theta == goal_theta):
            return cost
        next_states = [(x, z, (theta + 90) % 360), (x, z, (theta + 270) % 360)]
        dx, dz = deltas[theta // 90]
        nx, nz = x + dx, z + dz
        bit = 1 << (theta // 90)
        if (0 <= nx < 16 and 0 <= nz < 16
                and grid.seen_walls[x][z] & bit and not grid.known_walls[x][z] & bit):
            next_states.append((nx, nz, theta))
        for state in next_states:
            if state not in visited:
                visited.add(state)
                queue.append((state, cost + 1))
    return None


@pytest.mark.parametrize("seed", range(40))
def test_astar_matches_bfs_on_seeded_mazes(seed):
    rng = random.Random(seed)
    data = open_map()
    for x in range(16):
        for z in range(16):
            if x < 15 and rng.random() < 0.3:
                set_edge(data, x, z, 90, wall=True)
            if z < 15 and rng.random() < 0.3:
                set_edge(data, x, z, 0, wall=True)
    grid = MapInput.model_validate(data)
    start = (rng.randrange(16), rng.randrange(16), rng.choice([0, 90, 180, 270]))
    goal = (rng.randrange(16), rng.randrange(16))
    expected = oracle(grid, start, goal)
    path = shortest_path(grid, start, goal)
    assert (None if path is None else len(path.actions)) == expected
    if path:
        assert path.states[0] == start
        assert path.states[-1][:2] == goal
        assert len(path.states) == len(path.actions) + 1
        for before, action, after in zip(path.states, path.actions, path.states[1:]):
            x, z, theta = before
            if action == "forward":
                assert can_forward(grid, before)
                assert after[2] == theta
                assert abs(after[0] - x) + abs(after[1] - z) == 1
            else:
                assert after == (x, z, (theta + (-90 if action == "turn_left" else 90)) % 360)


def test_rotation_counts_and_arrival_orientation():
    grid = MapInput.model_validate(open_map())
    assert len(shortest_path(grid, (0, 0, 180), (0, 1)).actions) == 3
    assert len(shortest_path(grid, (0, 0, 0), (0, 0), goal_theta=180).actions) == 2
    assert shortest_path(grid, (0, 0, 0), (0, 0)).actions == []


def test_unknown_is_evaluation_only_and_boundaries_never_crossed():
    grid = MapInput(seen_walls=[[0] * 16 for _ in range(16)],
                    known_walls=[[0] * 16 for _ in range(16)])
    assert shortest_path(grid, (0, 0, 0), (0, 1)) is None
    assert shortest_path(grid, (0, 0, 0), (0, 1), optimistic=True).actions == ["forward"]
    assert not can_forward(grid, (0, 0, 180), optimistic=True)
    assert not can_forward(grid, (15, 15, 90), optimistic=True)


def test_wall_detour_and_fully_sealed_cell():
    data = open_map()
    set_edge(data, 0, 0, 0, wall=True)
    grid = MapInput.model_validate(data)
    path = shortest_path(grid, (0, 0, 0), (0, 1))
    assert len(path.actions) == oracle(grid, (0, 0, 0), (0, 1))
    assert len(path.actions) > 1
    set_edge(data, 0, 0, 90, wall=True)
    assert shortest_path(MapInput.model_validate(data), (0, 0, 0), (0, 1)) is None
