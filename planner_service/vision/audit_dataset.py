"""Audit real image files before a manifest can be used for training."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from planner_service.vision.dataset import ImageRecord, load_manifest, sha256_file, validate_manifest


def average_hash(path: Path) -> int:
    try:
        from PIL import Image, ImageOps
    except ImportError as exc:  # pragma: no cover - optional P1 environment
        raise RuntimeError("Pillow is required for corruption and near-duplicate checks") from exc
    with Image.open(path) as image:
        gray = ImageOps.grayscale(image).resize((8, 8))
        pixels = list(gray.getdata())
    average = sum(pixels) / len(pixels)
    return sum((1 << index) for index, pixel in enumerate(pixels) if pixel >= average)


def audit(records: list[ImageRecord], root: Path) -> list[str]:
    errors = validate_manifest(records, root)
    source_splits: dict[str, set[str]] = {}
    fingerprints: dict[int, ImageRecord] = {}
    for record in records:
        source_splits.setdefault(record.source, set()).add(record.split)
        path = root / record.path
        if not path.is_file():
            continue
        try:
            fingerprint = average_hash(path)
        except Exception as exc:
            errors.append(f"{record.path}: image decode failed: {exc}")
            continue
        for previous_fingerprint, previous in fingerprints.items():
            if (fingerprint ^ previous_fingerprint).bit_count() <= 4:
                errors.append(f"{record.path}: near-duplicate of {previous.path}")
        fingerprints[fingerprint] = record
    for source, splits in source_splits.items():
        if len(splits) > 1:
            errors.append(f"source {source!r} appears across dataset splits: {sorted(splits)}")
    return sorted(set(errors))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    args = parser.parse_args()
    records = load_manifest(args.manifest)
    errors = audit(records, args.data_root)
    print(json.dumps({"images": len(records), "errors": errors}, indent=2))
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())

