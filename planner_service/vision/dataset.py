"""Dataset manifest and image integrity checks for the P1 classifier.

Images are never copied or synthesized by this module.  A manifest must
contain one row per source image and records a content hash so near-duplicate
files can be rejected before splitting.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

LABELS = {"quyuan", "zhaojun"}
IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".webp"}


@dataclass(frozen=True)
class ImageRecord:
    path: str
    label: str
    split: str
    sha256: str
    source: str
    license: str


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_manifest(records: Iterable[ImageRecord], root: Path) -> list[str]:
    """Return all integrity errors without silently repairing the manifest."""
    errors: list[str] = []
    seen_hashes: dict[str, ImageRecord] = {}
    split_labels: dict[str, set[str]] = {}
    for index, record in enumerate(records, 1):
        path = root / record.path
        if record.label not in LABELS:
            errors.append(f"row {index}: unsupported label {record.label!r}")
        if record.split not in {"train", "val", "test"}:
            errors.append(f"row {index}: split must be train, val or test")
        if not record.source.strip() or not record.license.strip():
            errors.append(f"row {index}: source and license are required")
        if path.suffix.lower() not in IMAGE_SUFFIXES:
            errors.append(f"row {index}: unsupported image suffix {path.suffix!r}")
        if not path.is_file():
            errors.append(f"row {index}: image does not exist: {record.path}")
            continue
        actual_hash = sha256_file(path)
        if actual_hash != record.sha256:
            errors.append(f"row {index}: sha256 mismatch for {record.path}")
        previous = seen_hashes.get(actual_hash)
        if previous is not None:
            errors.append(f"row {index}: duplicate image content with {previous.path}")
        seen_hashes[actual_hash] = record
        split_labels.setdefault(record.split, set()).add(record.label)
    for split in ("train", "val", "test"):
        if not split_labels.get(split):
            errors.append(f"split {split!r} is empty")
        elif split_labels[split] != LABELS:
            errors.append(f"split {split!r} must contain both labels")
    return errors


def load_manifest(path: Path) -> list[ImageRecord]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError("manifest must be a JSON list")
    return [ImageRecord(**item) for item in data]


def write_manifest(path: Path, records: Iterable[ImageRecord]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps([asdict(item) for item in records], indent=2) + "\n", encoding="utf-8")


def scan_images(root: Path) -> list[Path]:
    return sorted(path for path in root.rglob("*") if path.is_file() and path.suffix.lower() in IMAGE_SUFFIXES)

