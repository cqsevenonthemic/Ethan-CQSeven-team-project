"""Rank confirmed approaches to unknown edges by estimated task cost."""

from collections import deque
from enum import StrEnum
from heapq import heappop, heappush

from planner_service.planner.grid import DIRECTIONS, State, successors
from planner_service.planner.oriented_astar import Path
from planner_service.schemas import Action, MapInput


class ExplorationStrategy(StrEnum):
    BASELINE = "baseline"
    KNOWN_TASKS = "known_tasks"
    CARRIED_TASK = "carried_task"


# An unobserved edge is a hypothesis. Charge an observation cost in addition
# to its traversal before using it to rank possible places to scan.
UNKNOWN_EDGE_COST = 3


def _estimated_goal_distances(grid: MapInput, goal: tuple[int, int]) -> dict[tuple[int, int], int]:
    """Reverse Dijkstra on possible cells; used for ranking, never execution."""
    pending = [(0, goal)]
    distance = {goal: 0}
    while pending:
        cost, cell = heappop(pending)
        if distance[cell] != cost:
            continue
        x, z = cell
        for dx, dz, bit, opposite in DIRECTIONS.values():
            neighbor = nx, nz = x + dx, z + dz
            if not (0 <= nx < 16 and 0 <= nz < 16):
                continue
            if grid.known_walls[x][z] & bit or grid.known_walls[nx][nz] & opposite:
                continue
            confirmed = grid.seen_walls[x][z] & bit and grid.seen_walls[nx][nz] & opposite
            candidate = cost + (1 if confirmed else UNKNOWN_EDGE_COST)
            if neighbor not in distance or candidate < distance[neighbor]:
                distance[neighbor] = candidate
                heappush(pending, (candidate, neighbor))
    return distance


def task_frontier(grid: MapInput, start: State, goal: tuple[int, int]) -> Path | None:
    """Find a confirmed path ending at a viewpoint facing an unknown edge."""
    estimated = _estimated_goal_distances(grid, goal)
    queue = deque([start])
    depth = {start: 0}
    parents: dict[State, tuple[State, Action]] = {}
    best: tuple[int, int] | None = None
    viewpoint = None
    while queue:
        current = queue.popleft()
        cost = depth[current]
        if best is not None and cost + UNKNOWN_EDGE_COST > best[0]:
            break
        x, z, theta = current
        dx, dz, bit, _ = DIRECTIONS[theta]
        remaining = estimated.get((x + dx, z + dz))
        if remaining is not None and not grid.seen_walls[x][z] & bit:
            score = cost + UNKNOWN_EDGE_COST + remaining, cost
            if best is None or score < best:
                best, viewpoint = score, current
        for action, target in successors(grid, current):
            if target not in depth:
                depth[target] = cost + 1
                parents[target] = current, action
                queue.append(target)
    if viewpoint is None:
        return None
    if viewpoint == start:
        # The best edge is already in front. Rotate for a new scan if the
        # previous observation still left it unknown.
        x, z, theta = start
        return Path([start, (x, z, (theta - 90) % 360)], ["turn_left"])
    states, actions = [viewpoint], []
    while viewpoint != start:
        viewpoint, action = parents[viewpoint]
        states.append(viewpoint)
        actions.append(action)
    return Path(states[::-1], actions[::-1])
