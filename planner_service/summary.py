"""Run bounded offline training for the exact observation snapshot in a request."""

from collections import OrderedDict
from dataclasses import dataclass
from threading import Lock

from planner_service.schemas import (
    EpisodeRecord, OptimalSteps, Pose, SummaryRequest, SummaryResponse, Trajectory,
)
from planner_service.simulation.delivery import AStarDelivery, DeliveryEnvironment, Episode, exact_delivery
from planner_service.simulation.q_learning import (
    TRAINING_EPISODES, TRAINING_SEED, TrainingRun, astar_baseline, train,
)

SUMMARY_SIZE = 40
CACHE_SIZE = 4
cache: OrderedDict[str, SummaryResponse] = OrderedDict()
cache_lock = Lock()
training_slot = Lock()


class SummaryUnreachable(ValueError):
    pass


class SummaryBusy(Exception):
    pass


@dataclass
class Experiment:
    env: DeliveryEnvironment
    exact: Episode
    baseline: Episode
    training: TrainingRun
    max_steps: int


def experiment(request: SummaryRequest, *, seed: int, episodes: int) -> Experiment:
    env = DeliveryEnvironment(request)
    exact = exact_delivery(env)
    if exact is None:
        raise SummaryUnreachable("Both figures cannot be delivered using the confirmed open edges")
    guide = AStarDelivery(env)
    baseline = astar_baseline(env, guide)
    if not baseline.completed:
        raise RuntimeError("The A* and Dijkstra baseline exceeded the delivery action budget")
    max_steps = max(96, min(4096, 3 * baseline.steps + 32))
    training = train(env, guide, seed=seed, episodes=episodes, max_steps=max_steps)
    return Experiment(env, exact, baseline, training, max_steps)


def coordinate_path(record: Episode) -> list[tuple[int, int]]:
    return [state.pose[:2] for state in record.states]


def record_model(record: Episode) -> EpisodeRecord:
    return EpisodeRecord(
        episode=record.episode, behavior=record.behavior, epsilon=record.epsilon,
        reward=record.reward, completed=record.completed, failure=record.failure,
        turns=record.turns, switch_steps=record.switch_steps,
        trajectory=Trajectory(
            poses=[Pose(x=x, z=z, theta=theta) for x, z, theta in
                   (state.pose for state in record.states)],
            actions=[move.action for move in record.moves], steps=record.steps,
        ),
    )


def best_completed(episodes: list[Episode]) -> Episode:
    # Prefer a Q-led training rollout on a tie with guided examples.
    return min((item for item in episodes if item.completed),
               key=lambda item: (item.steps, item.behavior != "epsilon_greedy", item.episode))


def sample_episodes(episodes: list[Episode], best: Episode) -> list[Episode]:
    others = [item for item in episodes if item.completed and item is not best]
    if len(others) >= SUMMARY_SIZE - 1:
        others = [others[i * (len(others) - 1) // (SUMMARY_SIZE - 2)]
                  for i in range(SUMMARY_SIZE - 1)]
    else:
        others.extend(item for item in episodes if not item.completed)
        others = others[:SUMMARY_SIZE - 1]
    return sorted([best, *others], key=lambda item: item.episode)


def build_summary(request: SummaryRequest) -> SummaryResponse:
    result = experiment(request, seed=TRAINING_SEED, episodes=TRAINING_EPISODES)
    best = best_completed(result.training.episodes)
    selected = sample_episodes(result.training.episodes, best)
    complete_map = all(mask == 15 for row in request.seen_walls for mask in row)
    return SummaryResponse(
        explored_trajectories=[coordinate_path(item) for item in selected],
        episode_steps=[item.steps for item in selected],
        optimal_path=coordinate_path(best),
        optimal_actions=[move.action for move in best.moves],
        optimal_steps=OptimalSteps(total=best.steps, forward=best.steps - best.turns, turn=best.turns),
        episode_records=[record_model(item) for item in selected],
        optimal_episode=best.episode,
        training_seed=TRAINING_SEED,
        training_episodes=TRAINING_EPISODES,
        exact_steps=result.exact.steps,
        map_scope="complete_map" if complete_map else "confirmed_subgraph",
        optimality_proven=best.steps == result.exact.steps,
    )


def plan(request: SummaryRequest) -> SummaryResponse:
    key = request.model_dump_json()
    with cache_lock:
        if key in cache:
            cache.move_to_end(key)
            return cache[key]
    if not training_slot.acquire(blocking=False):
        raise SummaryBusy
    try:
        # Another worker may have populated the cache since the first lookup.
        with cache_lock:
            if key in cache:
                cache.move_to_end(key)
                return cache[key]
        response = build_summary(request)
        with cache_lock:
            cache[key] = response
            if len(cache) > CACHE_SIZE:
                cache.popitem(last=False)
        return response
    finally:
        training_slot.release()
