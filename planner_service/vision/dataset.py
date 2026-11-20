"""Dataset manifest and file integrity checks for the P1 classifier."""

from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass
from pathlib import Path, PureWindowsPath
from typing import Iterable

from planner_service.vision import LABELS as CLASS_ORDER

LABELS = set(CLASS_ORDER)
SPLITS = ("train", "val", "test")
SYNTHETIC_KINDS = {"synthetic", "generated"}
KINDS = {"real", "unspecified"} | SYNTHETIC_KINDS
GENERATION_FIELDS = ("generation_batch", "generation_prompt", "generation_tool")
IMAGE_SUFFIXES = {".jpg", ".jpeg", ".png", ".webp"}


@dataclass(frozen=True)
class ImageRecord:
    path: str
    label: str
    split: str
    sha256: str
    source: str
    license: str
    group: str = ""
    original_filename: str = ""
    kind: str = "unspecified"
    generation_batch: str = ""
    generation_prompt: str = ""
    generation_tool: str = ""
    identity_evidence: str = ""
    attribution: str = ""
    original_id: str = ""
    reference_group: str = ""
    channel: str = ""
    style: str = ""
    composition: str = ""
    view_angle: str = ""
    background: str = ""
    props: str = ""
    prop_cue: str = ""
    scene_cue: str = ""
    gender_cue: str = ""
    unity_set: str = ""
    unity_asset_group: str = ""
    unity_batch: str = ""
    unity_encoding: str = ""
    review_status: str = ""
    reviewer: str = ""
    review_notes: str = ""

    @property
    def source_group(self) -> str:
        return self.group.strip() or self.source.strip()

    def provenance_keys(self) -> list[tuple[str, str]]:
        return [(kind, value.strip()) for kind, value in (
            ("source", self.source), ("group", self.source_group), ("group", self.reference_group),
            ("original_id", self.original_id),
            ("generation_batch", self.generation_batch), ("unity_asset_group", self.unity_asset_group),
            ("unity_batch", self.unity_batch),
        ) if value.strip()]


def image_path(root: Path, relative: str) -> Path:
    path = Path(relative)
    windows_path = PureWindowsPath(relative)
    if not relative or path.is_absolute() or windows_path.anchor or ".." in windows_path.parts:
        raise ValueError(f"image path must be relative to the data root: {relative!r}")
    root = root.resolve()
    resolved = (root / path).resolve()
    if not resolved.is_relative_to(root):
        raise ValueError(f"image path escapes the data root: {relative!r}")
    return resolved


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_manifest(records: Iterable[ImageRecord], root: Path, *,
                      required_splits: tuple[str, ...] = SPLITS, pilot: bool = False) -> list[str]:
    """Return all integrity errors without silently repairing the manifest."""
    errors: list[str] = []
    seen_hashes: dict[str, ImageRecord] = {}
    split_labels: dict[str, set[str]] = {}
    for index, record in enumerate(records, 1):
        if record.label not in LABELS:
            errors.append(f"row {index}: unsupported label {record.label!r}")
        if record.split not in required_splits:
            errors.append(f"row {index}: split must be one of {', '.join(required_splits)}")
        if not record.source.strip() or not record.license.strip():
            errors.append(f"row {index}: source and license are required")
        if record.original_filename and (PureWindowsPath(record.original_filename).name != record.original_filename
                                         or record.original_filename in {".", ".."}):
            errors.append(f"row {index}: original_filename must be a basename")
        if record.kind not in KINDS or (not pilot and record.kind in SYNTHETIC_KINDS and record.split != "train"):
            errors.append(f"row {index}: synthetic images must be assigned to train and kind must be valid")
        if pilot and (record.review_status != "approved"
                      or not all((record.reviewer.strip(), record.identity_evidence.strip(),
                                  record.original_id.strip(), record.group.strip(), record.attribution.strip()))):
            errors.append(f"row {index}: pilot images require approval, identity evidence and source attribution")
        if pilot and record.kind not in {"synthetic", "real"}:
            errors.append(f"row {index}: pilot images require an explicit real or synthetic source")
        if record.view_angle and record.view_angle not in {"front", "profile", "three_quarter", "other"}:
            errors.append(f"row {index}: view_angle must be front, profile, three_quarter or other")
        if record.kind == "synthetic" and any(not getattr(record, field).strip() for field in GENERATION_FIELDS):
            errors.append(f"row {index}: synthetic images require generation batch, prompt and tool")
        if record.kind == "real" and any(getattr(record, field).strip() for field in GENERATION_FIELDS):
            errors.append(f"row {index}: real images cannot have synthetic generation metadata")
        try:
            path = image_path(root, record.path)
        except (OSError, ValueError) as exc:
            errors.append(f"row {index}: {exc}")
            continue
        if path.suffix.lower() not in IMAGE_SUFFIXES:
            errors.append(f"row {index}: unsupported image suffix {path.suffix!r}")
        if not path.is_file():
            errors.append(f"row {index}: image does not exist: {record.path}")
            continue
        try:
            actual_hash = sha256_file(path)
        except OSError as exc:
            errors.append(f"row {index}: cannot read {record.path}: {exc}")
            continue
        if actual_hash != record.sha256:
            errors.append(f"row {index}: sha256 mismatch for {record.path}")
        previous = seen_hashes.get(actual_hash)
        if previous is not None:
            errors.append(f"row {index}: duplicate image content with {previous.path}")
        seen_hashes[actual_hash] = record
        split_labels.setdefault(record.split, set()).add(record.label)
    for split in required_splits:
        if not split_labels.get(split):
            errors.append(f"split {split!r} is empty")
        elif split_labels[split] != LABELS:
            errors.append(f"split {split!r} must contain both labels")
    return errors


def load_manifest(path: Path) -> list[ImageRecord]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError("manifest must be a JSON list")
    records = []
    for index, item in enumerate(data, 1):
        if not isinstance(item, dict) or any(not isinstance(value, str) for value in item.values()):
            raise ValueError(f"row {index}: manifest fields must be strings")
        try:
            records.append(ImageRecord(**item))
        except TypeError as exc:
            raise ValueError(f"row {index}: {exc}") from exc
    return records


def write_manifest(path: Path, records: Iterable[ImageRecord], *, overwrite: bool = True) -> None:
    content = json.dumps([asdict(item) for item in records], ensure_ascii=False, indent=2) + "\n"
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w" if overwrite else "x", encoding="utf-8") as stream:
        stream.write(content)


def scan_images(root: Path) -> list[Path]:
    return sorted(path for path in root.rglob("*") if path.is_file() and path.suffix.lower() in IMAGE_SUFFIXES)
