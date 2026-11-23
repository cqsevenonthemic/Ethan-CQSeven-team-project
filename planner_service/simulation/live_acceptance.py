"""Exercise all three live endpoints on a seeded maze and held-out portraits."""

import argparse
import base64
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path

import httpx

from planner_service.schemas import Pose, RecognizeResponse, SummaryRequest, SummaryResponse
from planner_service.simulation.delivery import DeliveryEnvironment, Move
from planner_service.simulation.replay import validate_trace
from planner_service.simulation.rl_experiment import observed_request
from planner_service.simulation.runner import run_world
from planner_service.simulation.world import generate_world
from planner_service.vision import LABELS
from planner_service.vision.dataset import image_path, load_manifest


def write_json(path: Path, data) -> None:
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def heldout_portraits(manifest: Path, root: Path) -> dict:
    records = load_manifest(manifest)
    portraits = {}
    for label in LABELS:
        record = next((item for item in records
                       if item.split == "test" and item.label == label and item.review_status == "approved"), None)
        if record is None:
            raise ValueError(f"No approved held-out portrait for {label}")
        raw = image_path(root, record.path).read_bytes()
        if sha256(raw).hexdigest() != record.sha256:
            raise ValueError(f"Portrait digest differs from manifest: {record.path}")
        portraits[label] = record, base64.b64encode(raw).decode("ascii")
    return portraits


def verify_summary(request: SummaryRequest, summary: SummaryResponse) -> None:
    env = DeliveryEnvironment(request)
    best = None
    numbers = [row.episode for row in summary.episode_records]
    assert len(numbers) == 40 and numbers == sorted(set(numbers))
    for path, steps, row in zip(summary.explored_trajectories, summary.episode_steps,
                                summary.episode_records, strict=True):
        actions = row.trajectory.actions
        switches = set(row.switch_steps)
        assert all(1 <= step <= steps for step in switches)
        moves = [Move(action, step in switches) for step, action in enumerate(actions, 1)]
        states = env.replay(moves)
        poses = [state.pose for state in states]
        assert path == [pose[:2] for pose in poses]
        assert [Pose(x=x, z=z, theta=theta) for x, z, theta in poses] == row.trajectory.poses
        assert steps == row.trajectory.steps == len(moves)
        assert row.turns == sum(action != "forward" for action in actions)
        assert states[-1].completed == row.completed
        if row.episode == summary.optimal_episode:
            best = row
            assert row.completed and path == summary.optimal_path and actions == summary.optimal_actions
    assert best is not None and best.trajectory.steps == summary.optimal_steps.total
    assert best.turns == summary.optimal_steps.turn
    assert summary.optimal_steps.total == summary.optimal_steps.forward + summary.optimal_steps.turn
    assert summary.optimality_proven == (summary.exact_steps == summary.optimal_steps.total)
    full_map = all(mask == 15 for column in request.seen_walls for mask in column)
    assert summary.map_scope == ("complete_map" if full_map else "confirmed_subgraph")


def run_live(url: str, manifest: Path, root: Path, output: Path, seed: int) -> dict:
    portraits = heldout_portraits(manifest, root)
    output.mkdir(parents=True, exist_ok=True)
    calls: Counter = Counter()
    recognitions = {}
    with httpx.Client(base_url=url, timeout=20.0, trust_env=False) as client:
        def post(route, payload):
            response = client.post(route, json=payload)
            response.raise_for_status()
            calls[route] += 1
            return response.json()

        def recognize_figure(role):
            record, encoded = portraits[role]
            request = {"image_base64": encoded}
            body = post("/recognize", request)
            prediction = RecognizeResponse.model_validate(body)
            assert prediction.recognition_source == "onnx" and prediction.image_sha256 == record.sha256
            recognitions[prediction.recognition_id] = {"expected": role, "portrait": record.path, **body}
            write_json(output / f"recognize-{role}-request.json", request)
            write_json(output / f"recognize-{role}-response.json", body)
            return {field: body[field] for field in ("pred_role", "confidence", "recognition_id",
                                                     "recognition_source")}

        def decide(payload):
            # Only IDs already returned by /recognize may enter an online snapshot.
            assert "initial_targets" not in payload and "figures" not in payload
            assert all(slot.get("recognition_id") in recognitions for slot in payload["slots"].values())
            return post("/next_action", payload)

        run = run_world(generate_world(seed), decision_fn=decide, recognize_figure=recognize_figure)
        trace = output / "execution.jsonl"
        with trace.open("w", encoding="utf-8") as stream:
            for row in run.trace:
                stream.write(json.dumps(row, separators=(",", ":")) + "\n")
        if not run.success:
            raise RuntimeError(f"Live delivery failed: {run.failure}. Replay: {trace}")
        assert len(recognitions) == len(LABELS) and calls["/recognize"] == len(LABELS)
        assert all(item["pred_role"] == item["expected"] for item in recognitions.values())
        counts = validate_trace(run.trace)
        recognition_steps = [row["step_count"] - int(row["executed"]) for row in run.trace
                             if row["event"] and row["event"]["type"] == "trigger_discovered"]
        delivery_steps = [row["step_count"] for row in run.trace if row.get("delivery_accepted")]
        request = observed_request(trace)
        payload = request.model_dump(mode="json")
        write_json(output / "plan-summary-request.json", payload)
        body = post("/plan_summary", payload)
        write_json(output / "plan-summary-response.json", body)
        summary = SummaryResponse.model_validate(body)
        verify_summary(request, summary)
    report = {"seed": seed, "http_200_calls": dict(calls), "motion": counts,
              "recognition_steps": recognition_steps, "delivery_steps": delivery_steps,
              "recognitions": list(recognitions.values()), "switches": run.switches,
              "rejections": run.rejections, "episodes": len(summary.episode_records),
              "optimal_episode": summary.optimal_episode, "best_steps": summary.optimal_steps.total,
              "exact_steps": summary.exact_steps, "map_scope": summary.map_scope,
              "optimality_proven": summary.optimality_proven}
    write_json(output / "report.json", report)
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8000")
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--seed", type=int, default=74)
    args = parser.parse_args()
    print(json.dumps(run_live(args.url, args.manifest, args.data_root, args.output, args.seed), indent=2))


if __name__ == "__main__":
    main()
