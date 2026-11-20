"""Offline delivery replay on a caller-supplied map of confirmed edges."""

from collections import deque
from dataclasses import dataclass
from typing import NamedTuple

from planner_service.planner.grid import DIRECTIONS, State, as_state, successors
from planner_service.planner.oriented_astar import shortest_path
from planner_service.planner.task_dijkstra import ordered_cost
from planner_service.schemas import Action, DESTINATIONS, SummaryRequest

Cell = tuple[int, int]
GOALS = (DESTINATIONS["quyuan"], DESTINATIONS["zhaojun"])
COMPLETE_BONUS = 25


class DeliveryState(NamedTuple):
    pose: State
    carry: int | None
    waiting: tuple[Cell | None, Cell | None]

    @property
    def completed(self) -> bool:
        return self.carry is None and self.waiting == (None, None)


class Move(NamedTuple):
    action: Action
    switch: bool = False


@dataclass
class Episode:
    episode: int
    behavior: str
    epsilon: float
    states: list[DeliveryState]
    moves: list[Move]
    failure: str | None = None

    @property
    def completed(self) -> bool:
        return self.states[-1].completed

    @property
    def steps(self) -> int:
        return len(self.moves)

    @property
    def turns(self) -> int:
        return sum(move.action != "forward" for move in self.moves)

    @property
    def reward(self) -> int:
        return -self.steps + (COMPLETE_BONUS if self.completed else 0)

    @property
    def switch_steps(self) -> list[int]:
        return [step for step, move in enumerate(self.moves, 1) if move.switch]


def settle(pose: State, carry: int | None,
           waiting: tuple[Cell | None, Cell | None]) -> DeliveryState:
    """Resolve all zero-step deliveries and empty-load pickups at this cell."""
    while True:
        if carry is not None:
            if pose[:2] != GOALS[carry]:
                break
            carry = None
        carry = next((role for role, cell in enumerate(waiting) if cell == pose[:2]), None)
        if carry is None:
            break
        waiting = (None, waiting[1]) if carry == 0 else (waiting[0], None)
    return DeliveryState(pose, carry, waiting)


class DeliveryEnvironment:
    def __init__(self, request: SummaryRequest):
        self.request = request
        targets = request.initial_targets
        self.start = settle(as_state(request.start_pose), None,
                            ((targets.quyuan.x, targets.quyuan.z),
                             (targets.zhaojun.x, targets.zhaojun.z)))
        # The observation graph is fixed for one scenario. Cache physical and task
        # transitions per environment so no World or another request is retained.
        self.motion = {
            (x, z, theta): tuple(successors(request, (x, z, theta)))
            for x in range(16) for z in range(16) for theta in DIRECTIONS
        }
        self.transition_cache: dict[DeliveryState, dict[Move, DeliveryState]] = {}

    def switch_load(self, state: DeliveryState) -> DeliveryState | None:
        if state.carry is None:
            return None
        other = 1 - state.carry
        if state.waiting[other] != state.pose[:2]:
            return None
        cell = state.pose[:2]
        waiting = (cell, None) if other == 1 else (None, cell)
        return settle(state.pose, other, waiting)

    def transitions(self, state: DeliveryState) -> dict[Move, DeliveryState]:
        if state not in self.transition_cache:
            self.transition_cache[state] = self._transitions(state)
        return self.transition_cache[state]

    def _transitions(self, state: DeliveryState) -> dict[Move, DeliveryState]:
        if state.completed:
            return {}
        choices = {Move(action): settle(pose, state.carry, state.waiting)
                   for action, pose in self.motion[state.pose]}
        switched = self.switch_load(state)
        if switched is not None and switched != state:
            choices.update({Move(action, True): settle(pose, switched.carry, switched.waiting)
                            for action, pose in self.motion[state.pose]})
        return choices

    def replay(self, moves: list[Move]) -> list[DeliveryState]:
        states = [self.start]
        for step, move in enumerate(moves, 1):
            target = self.transitions(states[-1]).get(move)
            if target is None:
                raise ValueError(f"Illegal replay action at step {step}")
            states.append(target)
        return states


def exact_delivery(env: DeliveryEnvironment) -> Episode | None:
    """Independent unit-cost BFS over delivery states, including handoff choices."""
    queue = deque([env.start])
    parents: dict[DeliveryState, tuple[DeliveryState, Move] | None] = {env.start: None}
    while queue:
        state = queue.popleft()
        if state.completed:
            moves = []
            while (parent := parents[state]) is not None:
                state, move = parent
                moves.append(move)
            moves.reverse()
            return Episode(0, "exact_bfs", 0, env.replay(moves), moves)
        for move, target in env.transitions(state).items():
            if target not in parents:
                parents[target] = state, move
                queue.append(target)
    return None


class AStarDelivery:
    """Existing A* waypoint navigation and Dijkstra keep/switch baseline."""

    def __init__(self, env: DeliveryEnvironment):
        self.env = env
        self.routes: dict[tuple[Cell, State], tuple[int, Action | None]] = {}
        self.actions: dict[DeliveryState, Move] = {}

    def leg(self, pose: State, goal: Cell) -> tuple[int, Action | None]:
        key = goal, pose
        if key not in self.routes:
            path = shortest_path(self.env.request, pose, goal)
            if path is None:
                raise ValueError("A delivery waypoint is unreachable on the confirmed map")
            for i, state in enumerate(path.states[:-1]):
                self.routes.setdefault((goal, state), (len(path.actions) - i, path.actions[i]))
            self.routes[(goal, path.states[-1])] = 0, None
        return self.routes[key]

    def __call__(self, state: DeliveryState) -> Move:
        if state in self.actions:
            return self.actions[state]
        pose = state.pose
        carry = state.carry
        switched = self.env.switch_load(state)
        switch = False
        state_after_switch = state
        if carry is not None and switched is not None and switched != state:
            other = 1 - carry
            keep_cost = ordered_cost(self.env.request, pose, [GOALS[carry], pose[:2], GOALS[other]])
            switch_cost = ordered_cost(self.env.request, pose, [GOALS[other], pose[:2], GOALS[carry]])
            if switch_cost < keep_cost:
                switch = True
                state_after_switch = switched
        if state_after_switch.carry is not None:
            _, action = self.leg(pose, GOALS[state_after_switch.carry])
        else:
            _, action = min(self.leg(pose, goal) for goal in state_after_switch.waiting if goal is not None)
        assert action is not None
        move = Move(action, switch)
        self.actions[state] = move
        return move
