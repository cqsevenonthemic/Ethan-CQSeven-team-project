"""Hypothetical keep/switch task costs on the confirmed observation graph only."""

from heapq import heappop, heappush

from planner_service.planner.grid import State, successors
from planner_service.schemas import MapInput


def ordered_cost(grid: MapInput, start: State, goals: list[tuple[int, int]]) -> int | None:
    """Dijkstra includes arrival orientation across all legs; pickup/drop cost zero.

    A state includes the next waypoint. Reaching a waypoint is a zero-cost task
    transition; forward and each 90-degree rotation cost one. Unknown edges stay
    blocked. None means this complete hypothetical route is not yet known.
    """
    initial = (0, start)
    costs = {initial: 0}
    queue = [(0, initial)]
    while queue:
        cost, node = heappop(queue)
        if costs[node] != cost:
            continue
        leg, state = node
        if leg == len(goals):
            return cost
        if state[:2] == goals[leg]:
            transitions = [(0, (leg + 1, state))]
        else:
            transitions = [(1, (leg, target)) for _, target in successors(grid, state)]
        for increment, target in transitions:
            candidate = cost + increment
            if candidate < costs.get(target, float("inf")):
                costs[target] = candidate
                heappush(queue, (candidate, target))
    return None
