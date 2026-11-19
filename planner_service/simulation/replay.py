"""Independent validator for saved execution traces, without hidden map inputs."""


def validate_trace(trace: list[dict], *, require_complete: bool = True) -> dict:
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    expected_pose = None
    steps = forward = turns = 0
    previous_seq = -1
    for row in trace:
        assert row["observation_seq"] > previous_seq
        previous_seq = row["observation_seq"]
        before = row["pose_before"]
        if expected_pose is not None:
            assert before == expected_pose, "discontinuous execution trace"
        assert row["visited_cell"] == [before["x"], before["z"]]
        for x, z, observed, walls in row["wall_updates"]:
            assert walls & observed == walls
            assert seen[x][z] & observed == seen[x][z], "observations regressed"
            assert known[x][z] & walls == known[x][z], "walls changed"
            seen[x][z], known[x][z] = observed, walls
        carried = [key for key, slot in row["slots"].items() if slot["state"] == "carried"]
        assert carried == ([] if row["carry_slot"] is None else [row["carry_slot"]])
        after = dict(before)
        if row["executed"]:
            action = row["action"]
            if action == "forward":
                dx, dz, bit, opposite = {0: (0, 1, 1, 4), 90: (1, 0, 2, 8),
                                          180: (0, -1, 4, 1), 270: (-1, 0, 8, 2)}[before["theta"]]
                x, z = before["x"], before["z"]
                nx, nz = x + dx, z + dz
                assert 0 <= nx < 16 and 0 <= nz < 16
                assert seen[x][z] & bit and seen[nx][nz] & opposite
                assert not known[x][z] & bit and not known[nx][nz] & opposite
                after.update(x=nx, z=nz)
                forward += 1
            else:
                assert action in ("turn_left", "turn_right")
                after["theta"] = (before["theta"] + (-90 if action == "turn_left" else 90)) % 360
                turns += 1
            steps += 1
        assert row["pose_after"] == after
        assert row["step_count"] == steps
        expected_pose = after
    if require_complete:
        assert trace and trace[-1]["status"] == "completed"
        assert trace[-1]["action"] is None
        slots = trace[-1]["slots"]
        assert len(slots) == 2
        assert {slot["pred_role"] for slot in slots.values()} == {"quyuan", "zhaojun"}
        assert all(slot["state"] == "delivered" and slot["verified"] for slot in slots.values())
    return dict(steps=steps, forward=forward, turns=turns)


if __name__ == "__main__":
    import argparse
    import json
    from pathlib import Path

    parser = argparse.ArgumentParser(description="Validate saved P0 execution JSONL without hidden truth")
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    report = json.loads((args.directory / "report.json").read_text(encoding="utf-8"))
    for run in report["runs"]:
        path = args.directory / f"seed_{run['seed']:04d}.jsonl"
        rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
        metrics = validate_trace(rows, require_complete=run["success"])
        assert metrics == {key: run[key] for key in ("steps", "forward", "turns")}
    print(f"Validated {len(report['runs'])} saved execution traces; pose, observed edges, capacity and step counts agree")
