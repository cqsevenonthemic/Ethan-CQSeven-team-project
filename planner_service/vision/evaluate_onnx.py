"""Evaluate the exported ONNX model on the manifest's independent test split."""

from __future__ import annotations

import argparse
import base64
import json
import math
from collections import Counter
from pathlib import Path
from statistics import mean, median
from time import perf_counter_ns

from planner_service.vision import LABELS
from planner_service.vision.audit_dataset import audit
from planner_service.vision.dataset import image_path, load_manifest
from planner_service.vision.inference import VisionRecognizer
from planner_service.vision.metrics import classification_metrics


def evaluate_onnx(manifest: Path, root: Path, model: Path, *, pilot: bool = False) -> dict:
    records = load_manifest(manifest)
    errors = audit(records, root, pilot=pilot)
    if errors:
        raise ValueError("manifest audit failed:\n" + "\n".join(errors))
    test_records = [record for record in records if record.split == "test"]
    recognizer = VisionRecognizer(str(model))
    label_index = {label: index for index, label in enumerate(LABELS)}
    matrix = [[0 for _ in LABELS] for _ in LABELS]
    slices = {}
    samples = []
    durations = []
    for record in test_records:
        raw = image_path(root, record.path).read_bytes()
        encoded = base64.b64encode(raw).decode("ascii")
        if not durations:
            recognizer.predict_base64(encoded)
        started = perf_counter_ns()
        prediction = recognizer.predict_base64(encoded)
        durations.append((perf_counter_ns() - started) / 1_000_000)
        actual, predicted = label_index[record.label], label_index[prediction["pred_role"]]
        matrix[actual][predicted] += 1
        groups = ["no_props" if record.props == "none" else "with_props"] if record.props else []
        if record.view_angle:
            groups.append(record.view_angle)
        for group in groups:
            group_matrix = slices.setdefault(group, [[0 for _ in LABELS] for _ in LABELS])
            group_matrix[actual][predicted] += 1
        samples.append({"path": record.path, "label": record.label, "predicted": prediction["pred_role"],
                        "confidence": prediction["confidence"], "group": record.source_group,
                        "props": record.props, "view_angle": record.view_angle})
    p95 = sorted(durations)[math.ceil(0.95 * len(durations)) - 1]
    return {"labels": list(LABELS), "split": "test", "images": len(test_records),
            "dataset_mode": "pilot_illustrations" if pilot else "standard",
            "kinds": dict(Counter(record.kind for record in test_records)),
            **classification_metrics(matrix),
            "slices": {name: classification_metrics(group) for name, group in slices.items()},
            "samples": samples,
            "inference_ms": {"provider": "CPUExecutionProvider", "mean": mean(durations),
                             "p50": median(durations), "p95": p95}}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--pilot", action="store_true", help="evaluate a held-out illustration pilot")
    args = parser.parse_args()
    try:
        result = evaluate_onnx(args.manifest, args.data_root, args.model, pilot=args.pilot)
    except (OSError, ValueError, RuntimeError) as exc:
        print(json.dumps({"errors": [str(exc)]}, indent=2))
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
