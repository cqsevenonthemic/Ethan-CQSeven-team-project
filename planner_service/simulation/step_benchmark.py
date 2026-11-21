"""Paired online-planning benchmark on fixed, evaluator-owned random worlds."""

import argparse
from collections import Counter
from concurrent.futures import ProcessPoolExecutor
from functools import partial
import json
from math import ceil
from pathlib import Path
from statistics import mean, median

from planner_service.planner.task_frontier import ExplorationStrategy
from planner_service.schemas import ObserveRequest
from planner_service.service import decide
from planner_service.simulation.replay import validate_trace
from planner_service.simulation.runner import run_world
from planner_service.simulation.world import generate_world


def evaluate(seed, *, strategies, start_seed, fault_every):
    world = generate_world(seed)
    inverted = fault_every > 0 and (seed - start_seed) % fault_every == 0
    rows = []
    for strategy in strategies:
        def decision(payload):
            return decide(ObserveRequest.model_validate(payload), exploration=strategy).model_dump(mode="json")

        result = run_world(world, invert_first=inverted, decision_fn=decision)
        replay = validate_trace(result.trace, require_complete=result.success)
        assert replay == dict(steps=result.steps, forward=result.forward, turns=result.turns), (seed, strategy)
        executed = [row for row in result.trace if row["executed"]]
        counts = Counter(row["action"] for row in executed)
        edges = []
        for row in executed:
            if row["action"] == "forward":
                points = [(row[key]["x"], row[key]["z"]) for key in ("pose_before", "pose_after")]
                edges.append(tuple(sorted(points)))
        rows.append(dict(seed=seed, strategy=strategy, success=result.success,
                         failure=result.failure, steps=result.steps,
                         forward=counts["forward"], left=counts["turn_left"], right=counts["turn_right"],
                         turns=result.turns, repeated_edges=len(edges) - len(set(edges)),
                         invert_first=inverted, rejections=result.rejections, switches=result.switches,
                         verified_trace=True))
    return rows


def summarize(rows):
    successful = [row for row in rows if row["success"]]
    steps = sorted(row["steps"] for row in successful)
    def average(key):
        return round(mean(row[key] for row in successful), 3) if successful else None

    return dict(completed=len(steps), count=len(rows), success_rate=len(steps) / len(rows),
                mean_steps=average("steps"), median_steps=median(steps) if steps else None,
                p90_steps=steps[ceil(0.9 * len(steps)) - 1] if steps else None,
                max_steps=steps[-1] if steps else None, mean_forward=average("forward"),
                mean_left=average("left"), mean_right=average("right"), mean_turns=average("turns"),
                mean_repeated_edges=average("repeated_edges"),
                failures=[row for row in rows if not row["success"]])


def benchmark(start_seed, count, fault_every, strategies, jobs):
    policies = (ExplorationStrategy.BASELINE, *strategies)
    evaluate_seed = partial(evaluate, strategies=policies, start_seed=start_seed, fault_every=fault_every)
    seeds = range(start_seed, start_seed + count)
    if jobs == 1:
        paired = list(map(evaluate_seed, seeds))
    else:
        with ProcessPoolExecutor(max_workers=jobs) as pool:
            paired = list(pool.map(evaluate_seed, seeds, chunksize=1))
    by_policy = {strategy: summarize([pair[i] for pair in paired]) for i, strategy in enumerate(policies)}
    comparisons = {}
    for i, strategy in enumerate(strategies, start=1):
        deltas = sorted([(pair[i]["steps"] - pair[0]["steps"], pair[0]["seed"], pair[0]["steps"], pair[i]["steps"])
                         for pair in paired if pair[i]["success"] and pair[0]["success"]])
        base = sum(entry[2] for entry in deltas)
        comparisons[strategy] = dict(
            paired_completions=len(deltas), faster=sum(entry[0] < 0 for entry in deltas),
            unchanged=sum(entry[0] == 0 for entry in deltas), slower=sum(entry[0] > 0 for entry in deltas),
            mean_step_change=round(mean(entry[0] for entry in deltas), 3) if deltas else None,
            step_reduction_percent=round(-100 * sum(entry[0] for entry in deltas) / base, 3) if base else None,
            worst_regressions=[dict(delta=d, seed=s, baseline=o, candidate=n)
                               for d, s, o, n in deltas[::-1][:5] if d > 0])
    return dict(start_seed=start_seed, count=count, fault_every=fault_every, policies=by_policy,
                comparisons=comparisons, paired_runs=paired)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--start-seed", type=int, required=True)
    parser.add_argument("--count", type=int, required=True)
    parser.add_argument("--fault-every", type=int, default=3)
    parser.add_argument("--jobs", type=int, default=1)
    parser.add_argument("--strategy", type=ExplorationStrategy, nargs="+",
                        default=[ExplorationStrategy.KNOWN_TASKS, ExplorationStrategy.CARRIED_TASK],
                        choices=[ExplorationStrategy.KNOWN_TASKS, ExplorationStrategy.CARRIED_TASK])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if min(args.count, args.jobs) < 1 or min(args.start_seed, args.fault_every) < 0:
        parser.error("count and jobs must be positive; seed and fault interval must be nonnegative")
    result = benchmark(args.start_seed, args.count, args.fault_every, args.strategy, args.jobs)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: val for key, val in result.items() if key != "paired_runs"}, indent=2))


if __name__ == "__main__":
    main()
