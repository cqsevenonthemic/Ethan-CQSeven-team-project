"""Sample-based, off-policy tabular Q-learning for offline delivery simulations."""

from dataclasses import dataclass, field
from hashlib import sha256
import json
from pathlib import Path
from random import Random
from typing import Callable

from planner_service.schemas import SummaryRequest
from planner_service.simulation.delivery import (
    AStarDelivery, COMPLETE_BONUS, DeliveryEnvironment, DeliveryState, Episode, Move,
)

TRAINING_SEED = 20261120
TRAINING_EPISODES = 160
POLICY_VERSION = 1
ALPHA = 0.65
GAMMA = 1.0
UNOBSERVED = -8192.0

Policy = dict[DeliveryState, dict[Move, float]]
Controller = Callable[[DeliveryState, dict[Move, DeliveryState]], Move]


@dataclass
class TrainingRun:
    episodes: list[Episode] = field(default_factory=list)
    q: Policy = field(default_factory=dict)
    updates: int = 0


def scenario_id(request: SummaryRequest) -> str:
    return sha256(request.model_dump_json().encode("utf-8")).hexdigest()


def greedy(q: Policy, state: DeliveryState, choices: dict[Move, DeliveryState],
           rng: Random) -> Move:
    row = q.get(state, {})
    best = max(row.get(move, UNOBSERVED) for move in choices)
    return rng.choice([move for move in choices if row.get(move, UNOBSERVED) == best])


def rollout(env: DeliveryEnvironment, choose: Controller, max_steps: int, *,
            episode: int = 0, behavior: str, epsilon: float = 0.0) -> Episode:
    states = [env.start]
    moves = []
    while not states[-1].completed and len(moves) < max_steps:
        state = states[-1]
        choices = env.transitions(state)
        move = choose(state, choices)
        states.append(choices[move])
        moves.append(move)
    failure = None if states[-1].completed else "action_budget_exceeded"
    return Episode(episode, behavior, epsilon, states, moves, failure)


def astar_baseline(env: DeliveryEnvironment, guide: AStarDelivery) -> Episode:
    return rollout(env, lambda state, _: guide(state), 4096, behavior="astar_dijkstra")


def train(env: DeliveryEnvironment, guide: AStarDelivery, *, seed: int,
          episodes: int, max_steps: int) -> TrainingRun:
    if episodes < 1 or max_steps < 1:
        raise ValueError("episodes and max_steps must be positive")
    rng = Random(seed)
    run = TrainingRun()

    for number in range(1, episodes + 1):
        # One demonstration is executed in the same environment as all later
        # transitions. Guided behavior generates data, never A*-derived Q values.
        progress = (number - 1) / max(1, episodes - 1)
        epsilon = 0.0 if number == 1 else 0.24 * (1 - progress) + 0.02 * progress
        behavior = ("astar_demonstration" if number == 1 else
                    "epsilon_greedy" if number % 4 == 0 else "astar_guided")

        def choose(state: DeliveryState, choices: dict[Move, DeliveryState]) -> Move:
            if behavior == "astar_demonstration":
                return guide(state)
            if rng.random() < epsilon:
                return rng.choice(tuple(choices))
            if behavior == "astar_guided" and rng.random() < 0.80:
                return guide(state)
            return greedy(run.q, state, choices, rng)

        record = rollout(env, choose, max_steps, episode=number, behavior=behavior,
                         epsilon=epsilon)
        run.episodes.append(record)
        # Replay observed transitions backward once. Undiscounted time-limit
        # truncations bootstrap from their successor; only delivery is terminal.
        for index in reversed(range(record.steps)):
            state, target = record.states[index:index + 2]
            move = record.moves[index]
            row = run.q.setdefault(state, {})
            if target.completed:
                td_target = -1 + COMPLETE_BONUS
            else:
                next_row = run.q.get(target, {})
                td_target = -1 + GAMMA * max(next_row.get(option, UNOBSERVED)
                                             for option in env.transitions(target))
            # The first observation establishes a value from actual experience.
            # Untried actions are pessimistic to avoid untrained greedy detours.
            old = row.get(move)
            row[move] = td_target if old is None else old + ALPHA * (td_target - old)
            run.updates += 1
    return run


def evaluate(env: DeliveryEnvironment, q: Policy, *, seed: int, max_steps: int) -> Episode:
    rng = Random(seed)
    return rollout(env, lambda state, choices: greedy(q, state, choices, rng),
                   max_steps, behavior="greedy_evaluation")


def save_policy(path: Path, request: SummaryRequest, q: Policy, *,
                seed: int, episodes: int, max_steps: int):
    data = {
        "version": POLICY_VERSION,
        "scenario_id": scenario_id(request),
        "seed": seed,
        "episodes": episodes,
        "max_steps": max_steps,
        "alpha": ALPHA,
        "gamma": GAMMA,
        "unobserved_value": UNOBSERVED,
        "table": [[state, [[move, value] for move, value in row.items()]]
                  for state, row in q.items()],
    }
    path.write_text(json.dumps(data, separators=(",", ":")) + "\n", encoding="utf-8")


def load_policy(path: Path, env: DeliveryEnvironment) -> tuple[Policy, int]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if (data["version"] != POLICY_VERSION
            or data["scenario_id"] != scenario_id(env.request)):
        raise ValueError("The Q-table was trained for a different version or scenario")
    q = {}
    for (pose, carry, waiting), row in data["table"]:
        state = DeliveryState(tuple(pose), carry,
                              tuple(tuple(cell) if cell is not None else None for cell in waiting))
        q[state] = {Move(*move): value for move, value in row}
    return q, data["max_steps"]
