"""Run reproducible offline Q-learning, save traces and evaluate a frozen Q-table."""

import argparse
from collections import Counter
import json
from pathlib import Path
from statistics import mean
from time import perf_counter

from planner_service.schemas import SummaryRequest
from planner_service.simulation.delivery import DeliveryEnvironment, Episode
from planner_service.simulation.q_learning import (
    TRAINING_EPISODES, TRAINING_SEED, evaluate, load_policy, save_policy,
)
from planner_service.simulation.world import generate_world
from planner_service.summary import best_completed, coordinate_path, experiment

EVALUATION_SEEDS = (7, 17, 29, 43, 67)


def full_map_request(seed: int) -> SummaryRequest:
    world = generate_world(seed)
    return SummaryRequest(
        start_pose=dict(zip(("x", "z", "theta"), world.start)),
        seen_walls=[[15] * 16 for _ in range(16)], known_walls=world.walls,
        initial_targets={figure.role: dict(zip(("x", "z"), figure.cell)) for figure in world.figures},
    )


def observed_request(path: Path) -> SummaryRequest:
    """Reconstruct the post-completion snapshot using only the recorded Act 1 trace."""
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    initial, start = {}, None
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            row = json.loads(line)
            if start is None:
                start = row["pose_before"]
            for x, z, observed, walls in row["wall_updates"]:
                seen[x][z], known[x][z] = observed, walls
            event = row["event"]
            if event and event["type"] == "trigger_discovered":
                initial[event["slot_id"]] = {"x": event["x"], "z": event["z"]}
    if start is None or row["status"] != "completed":
        raise ValueError(f"{path}: an Act 1 completion trace is required")
    slots = row["environment_slots"]
    targets = {slot["pred_role"]: initial[key] for key, slot in slots.items()
               if slot["state"] == "delivered" and slot["verified"]}
    return SummaryRequest(start_pose=start, seen_walls=seen, known_walls=known,
                          initial_targets=targets)


def episode_json(record: Episode) -> dict:
    return dict(episode=record.episode, behavior=record.behavior, epsilon=record.epsilon,
                completed=record.completed, failure=record.failure, reward=record.reward,
                steps=record.steps, forward=record.steps - record.turns, turns=record.turns,
                switch_steps=record.switch_steps, path=coordinate_path(record),
                poses=[state.pose for state in record.states],
                actions=[move.action for move in record.moves])


def metrics(records: list[Episode]) -> dict:
    finished = [record for record in records if record.completed]
    return dict(episodes=len(records), completed=len(finished),
                completion_rate=round(len(finished) / len(records), 6),
                failures=dict(Counter(record.failure for record in records if record.failure)),
                mean_reward=round(mean(record.reward for record in records), 3),
                mean_steps=round(mean(record.steps for record in records), 3),
                mean_turns=round(mean(record.turns for record in records), 3),
                mean_completed_steps=round(mean(record.steps for record in finished), 3) if finished else None)


def evaluate_saved(directory: Path) -> tuple[DeliveryEnvironment, int, list[Episode]]:
    request = SummaryRequest.model_validate_json((directory / "request.json").read_bytes())
    env = DeliveryEnvironment(request)
    q, max_steps = load_policy(directory / "policy.json", env)
    records = [evaluate(env, q, seed=seed, max_steps=max_steps) for seed in EVALUATION_SEEDS]
    for record in records:
        if env.replay(record.moves) != record.states:
            raise AssertionError("The saved Q-table evaluation failed replay validation")
    return env, len(q), records


def run_scenario(name: str, request: SummaryRequest, output: Path, *,
                 seed: int, episodes: int) -> dict:
    directory = output / name
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "request.json").write_text(request.model_dump_json() + "\n", encoding="utf-8")
    started = perf_counter()
    result = experiment(request, seed=seed, episodes=episodes)
    training_seconds = perf_counter() - started
    records = result.training.episodes
    best = best_completed(records)
    policy_path = directory / "policy.json"
    save_policy(policy_path, request, result.training.q, seed=seed, episodes=episodes,
                max_steps=result.max_steps)
    evaluator, q_states, evaluations = evaluate_saved(directory)
    with (directory / "training.jsonl").open("w", encoding="utf-8") as stream:
        for record in records:
            if evaluator.replay(record.moves) != record.states:
                raise AssertionError(f"{name}: training episode {record.episode} replay differs")
            stream.write(json.dumps(episode_json(record), separators=(",", ":")) + "\n")
    for record in (result.baseline, result.exact):
        if evaluator.replay(record.moves) != record.states:
            raise AssertionError(f"{name}: {record.behavior} replay differs")
    comparison = dict(
        astar_dijkstra=episode_json(result.baseline), exact_bfs=episode_json(result.exact),
        evaluation=[dict(seed=sample, **episode_json(record))
                    for sample, record in zip(EVALUATION_SEEDS, evaluations)],
    )
    (directory / "comparison.json").write_text(json.dumps(comparison, indent=2) + "\n", encoding="utf-8")
    return dict(
        name=name, complete_map=all(value == 15 for row in request.seen_walls for value in row),
        max_steps=max_steps, training_seconds=round(training_seconds, 4),
        q_states=q_states, td_updates=result.training.updates, training=metrics(records),
        first_quarter=metrics(records[:max(1, episodes // 4)]),
        last_quarter=metrics(records[-max(1, episodes // 4):]),
        best_episode=best.episode, best_behavior=best.behavior, best_steps=best.steps,
        best_turns=best.turns, astar_steps=result.baseline.steps, astar_turns=result.baseline.turns,
        exact_steps=result.exact.steps, exact_turns=result.exact.turns,
        evaluation=metrics(evaluations),
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, default=Path("contracts/sample_summary_request.json"))
    parser.add_argument("--world-seeds", type=int, nargs="*", default=[0, 1, 2, 3, 4, 5, 42, 99])
    parser.add_argument("--observed-dir", type=Path, help="Directory of completed P0 benchmark traces")
    parser.add_argument("--seed", type=int, default=TRAINING_SEED)
    parser.add_argument("--episodes", type=int, default=TRAINING_EPISODES)
    parser.add_argument("--output", type=Path, default=Path("planner_service/artifacts/p2/rl"))
    parser.add_argument("--evaluate-dir", type=Path, help="Evaluate a saved scenario without training")
    args = parser.parse_args()
    if args.evaluate_dir:
        _, q_states, records = evaluate_saved(args.evaluate_dir)
        print(json.dumps(dict(q_states=q_states, evaluation_seeds=EVALUATION_SEEDS,
                              **metrics(records)), indent=2))
        return
    if args.episodes < 1:
        parser.error("--episodes must be positive")
    scenarios = {"sample": SummaryRequest.model_validate_json(args.request.read_bytes())}
    for seed in args.world_seeds:
        scenarios[f"full_{seed:04d}"] = full_map_request(seed)
        if args.observed_dir:
            trace = args.observed_dir / f"seed_{seed:04d}.jsonl"
            scenarios[f"observed_{seed:04d}"] = observed_request(trace)
    report = dict(training_seed=args.seed, episodes_per_scenario=args.episodes,
                  evaluation_seeds=EVALUATION_SEEDS, scenarios=[])
    for name, request in scenarios.items():
        result = run_scenario(name, request, args.output, seed=args.seed, episodes=args.episodes)
        report["scenarios"].append(result)
        print(f"{name}: best={result['best_steps']}, exact={result['exact_steps']}, "
              f"A*={result['astar_steps']}, greedy={result['evaluation']['mean_steps']}", flush=True)
    (args.output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
