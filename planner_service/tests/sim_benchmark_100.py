"""Explicit P0 Mock-recognition benchmark; real sensor observations, no RL claims.

python -m planner_service.tests.sim_benchmark_100 --count 100 --output planner_service/artifacts/p0
"""

import argparse
from collections import Counter
from dataclasses import asdict
import json
from pathlib import Path
from statistics import mean

from planner_service.simulation.runner import run_world
from planner_service.simulation.replay import validate_trace
from planner_service.simulation.world import generate_world


def benchmark(count=100, start_seed=0, fault_every=3, output: Path | None = None):
    if count < 1 or start_seed < 0 or fault_every < 0:
        raise ValueError("count must be positive; seeds/fault interval must be nonnegative")
    if output:
        output.mkdir(parents=True, exist_ok=True)
    runs = []
    for seed in range(start_seed, start_seed + count):
        inverted = fault_every > 0 and (seed - start_seed) % fault_every == 0
        result = run_world(generate_world(seed), invert_first=inverted)
        if result.success:
            replay = validate_trace(result.trace)
            if replay != dict(steps=result.steps, forward=result.forward, turns=result.turns):
                raise AssertionError(f"seed {seed}: replay metrics differ")
        full = asdict(result)
        if output:
            trace_path = output / f"seed_{seed:04d}.jsonl"
            with trace_path.open("w", encoding="utf-8") as stream:
                for row in result.trace:
                    stream.write(json.dumps(row, separators=(",", ":"), ensure_ascii=False) + "\n")
        full.pop("trace")
        full["mock_invert_first"] = inverted
        runs.append(full)
    successes = sum(run["success"] for run in runs)
    report = dict(
        mode="P0_TEST_MOCK_RECOGNITION", generator="dfs_tree_or_braided_v1",
        sensor=dict(fov_deg=180, rays=121, min_range_m=0.15, max_range_m=4.5,
                    corner_policy="conservative_stop"),
        start_seed=start_seed, count=count, fault_every=fault_every,
        success_count=successes, failure_count=count - successes,
        success_rate=successes / count,
        failure_reasons=dict(Counter(run["failure"] for run in runs if run["failure"])),
        mean_steps=round(mean(run["steps"] for run in runs), 3),
        min_steps=min(run["steps"] for run in runs), max_steps=max(run["steps"] for run in runs),
        total_rejections=sum(run["rejections"] for run in runs),
        total_switches=sum(run["switches"] for run in runs),
        total_elapsed_s=round(sum(run["elapsed_s"] for run in runs), 3), runs=runs,
    )
    if output:
        (output / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--count", type=int, default=100)
    parser.add_argument("--start-seed", type=int, default=0)
    parser.add_argument("--fault-every", type=int, default=3, help="0 disables intentional first-image misclassification")
    parser.add_argument("--output", type=Path, default=Path("planner_service/artifacts/p0"))
    args = parser.parse_args()
    report = benchmark(args.count, args.start_seed, args.fault_every, args.output)
    print(json.dumps({key: value for key, value in report.items() if key != "runs"}, indent=2))
    raise SystemExit(0 if report["failure_count"] == 0 else 1)
