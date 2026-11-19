from copy import deepcopy
import json

import pytest

from planner_service.simulation.replay import validate_trace
from planner_service.simulation.runner import run_world
from planner_service.simulation.world import generate_world
from planner_service.tests.sim_benchmark_100 import benchmark


def test_report_and_jsonl_are_real_replayable_execution(tmp_path):
    report = benchmark(count=2, start_seed=10, fault_every=1, output=tmp_path)
    assert report["success_count"] == 2 and report["failure_count"] == 0
    assert report["total_rejections"] == 2
    assert json.loads((tmp_path / "report.json").read_text(encoding="utf-8")) == report
    for run in report["runs"]:
        trace = [json.loads(line) for line in (tmp_path / f"seed_{run['seed']:04d}.jsonl").read_text().splitlines()]
        assert validate_trace(trace) == {key: run[key] for key in ("steps", "forward", "turns")}
    corrupted = deepcopy(trace)
    row = next(row for row in corrupted if row["executed"])
    row["step_count"] += 1
    with pytest.raises(AssertionError):
        validate_trace(corrupted)


@pytest.mark.parametrize("kwargs", [{"count": 0}, {"start_seed": -1}, {"fault_every": -1}])
def test_invalid_benchmark_parameters(kwargs):
    with pytest.raises(ValueError):
        benchmark(**kwargs)


def test_same_seed_replays_identically_except_timing():
    first = run_world(generate_world(6), invert_first=True)
    second = run_world(generate_world(6), invert_first=True)
    assert first.success and second.success
    assert first.trace == second.trace
    assert first.steps == second.steps
