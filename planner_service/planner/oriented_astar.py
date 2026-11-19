"""A* over (x, z, theta), with unit movement and rotation costs."""

from dataclasses import dataclass
from heapq import heappop, heappush
from itertools import count

from planner_service.planner.grid import State, successors
from planner_service.schemas import Action, MapInput


@dataclass
class Path:
    states: list[State]
    actions: list[Action]


def shortest_path(grid: MapInput, start: State, goal: tuple[int, int],
                  goal_theta: int | None = None, *, optimistic: bool = False) -> Path | None:
    """Unknown edges are blocked by default; optimistic mode is evaluation-only."""
    def heuristic(state: State) -> int:
        return abs(state[0] - goal[0]) + abs(state[1] - goal[1])

    sequence = count()
    queue = [(heuristic(start), 0, next(sequence), start)]
    costs = {start: 0}
    parents: dict[State, tuple[State, Action]] = {}
    while queue:
        _, cost, _, current = heappop(queue)
        if costs[current] != cost:
            continue
        if current[:2] == goal and (goal_theta is None or current[2] == goal_theta):
            states, actions = [current], []
            while current in parents:
                previous, action = parents[current]
                actions.append(action)
                states.append(previous)
                current = previous
            return Path(list(reversed(states)), list(reversed(actions)))
        for action, target in successors(grid, current, optimistic=optimistic):
            candidate = cost + 1
            if candidate < costs.get(target, float("inf")):
                costs[target] = candidate
                parents[target] = current, action
                heappush(queue, (candidate + heuristic(target), candidate,
                                 next(sequence), target))
    return None
