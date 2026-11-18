"""Reachable frontier/observation selection on the confirmed local map."""

from collections import deque

from planner_service.planner.grid import DIRECTIONS, State, successors
from planner_service.planner.oriented_astar import Path
from planner_service.schemas import MapInput


def cover_unvisited(grid: MapInput, start: State, visited: set[tuple[int, int]]) -> Path | None:
    """Search coverage is physical entry, not visibility of the surrounding walls."""
    queue = deque([start])
    parents = {start: None}
    while queue:
        current = queue.popleft()
        if current[:2] not in visited:
            states, actions = [current], []
            while parents[current] is not None:
                previous, action = parents[current]
                states.append(previous)
                actions.append(action)
                current = previous
            return Path(states[::-1], actions[::-1])
        for action, target in successors(grid, current):
            if target not in parents:
                parents[target] = current, action
                queue.append(target)
    return None


def explore(grid: MapInput, start: State) -> Path | None:
    """BFS finds the cheapest nonempty action sequence facing an unknown edge.

    Rotations are real actions. The caller must rescan after each executed action.
    Only confirmed open edges are traversed; no invisible map is consulted.
    """
    queue = deque([start])
    parents = {start: None}
    while queue:
        current = queue.popleft()
        x, z, theta = current
        dx, dz, bit, _ = DIRECTIONS[theta]
        if (current != start and 0 <= x + dx < 16 and 0 <= z + dz < 16
                and not grid.seen_walls[x][z] & bit):
            states, actions = [current], []
            while parents[current] is not None:
                previous, action = parents[current]
                states.append(previous)
                actions.append(action)
                current = previous
            return Path(states[::-1], actions[::-1])
        for action, target in successors(grid, current):
            if target not in parents:
                parents[target] = current, action
                queue.append(target)
    # If the sole unknown edge is already ahead, rotate to obtain a fresh scan.
    x, z, theta = start
    dx, dz, bit, _ = DIRECTIONS[theta]
    if 0 <= x + dx < 16 and 0 <= z + dz < 16 and not grid.seen_walls[x][z] & bit:
        return Path([start, (x, z, (theta - 90) % 360)], ["turn_left"])
    return None
