"""Build a training manifest from licensed image files and local CSV metadata."""

from __future__ import annotations

import argparse
import csv
import json
from collections import Counter, defaultdict
from dataclasses import replace
from pathlib import Path, PureWindowsPath

from planner_service.vision import LABELS as CLASS_ORDER
from planner_service.vision.audit_dataset import audit_report, duplicate_report, inspect_images, inventory, near_duplicates
from planner_service.vision.collection_standard import FIELDS, collection_report
from planner_service.vision.dataset import (
    GENERATION_FIELDS, IMAGE_SUFFIXES, LABELS, SPLITS, SYNTHETIC_KINDS,
    ImageRecord, image_path, scan_images, sha256_file, write_manifest,
)

REQUIRED_COLUMNS = {"path", "label", "source", "license", "group", "original_filename", "kind"}
TARGET_FRACTIONS = dict(zip(SPLITS, (0.70, 0.15, 0.15)))


def read_candidates(metadata: Path, root: Path, *, pilot: bool = False) -> tuple[list[ImageRecord], list[str]]:
    records = []
    errors = []
    seen_paths = set()
    seen_hashes = {}
    with metadata.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream, strict=True)
        headers = reader.fieldnames or []
        missing = REQUIRED_COLUMNS - set(headers)
        if missing:
            raise ValueError(f"CSV is missing columns: {', '.join(sorted(missing))}")
        if len(headers) != len(set(headers)):
            raise ValueError("CSV headers must be unique")
        for row in reader:
            try:
                if None in row or any(value is None for value in row.values()):
                    raise ValueError("CSV row does not match the headers")
                fields = {key: row[key].strip() for key in REQUIRED_COLUMNS}
                if empty := sorted(key for key, value in fields.items() if not value):
                    raise ValueError(f"required values are missing: {', '.join(empty)}")
                split = row.get("split", "").strip()
                if fields["label"] not in LABELS or (split and split not in SPLITS):
                    raise ValueError("label must be quyuan or zhaojun and split must be blank, train, val or test")
                generation = {name: row.get(name, "").strip() for name in GENERATION_FIELDS}
                if fields["kind"] in SYNTHETIC_KINDS:
                    if not pilot and split not in {"", "train"}:
                        raise ValueError("synthetic images are only eligible for train")
                    fields["kind"] = "synthetic"
                    if not all(generation.values()):
                        raise ValueError("synthetic images require generation_batch, generation_prompt and generation_tool")
                    fields.update(generation)
                elif fields["kind"] != "real":
                    raise ValueError("kind must be real or synthetic")
                elif any(generation.values()):
                    raise ValueError("real images cannot have synthetic generation metadata")
                fields.update({name: row.get(name, "").strip() for name in FIELDS})
                original = fields["original_filename"]
                if PureWindowsPath(original).name != original or original in {".", ".."}:
                    raise ValueError("original_filename must be a basename")
                fields["path"] = fields["path"].replace("\\", "/")
                path = image_path(root, fields["path"])
                if path.suffix.lower() not in IMAGE_SUFFIXES or not path.is_file():
                    raise ValueError(f"expected an existing JPEG, PNG or WebP file at {fields['path']}")
                if path in seen_paths:
                    raise ValueError(f"the same image path is listed more than once: {fields['path']}")
                digest = sha256_file(path)
                seen_paths.add(path)
                if digest in seen_hashes:
                    errors.append(f"row {reader.line_num}: exact duplicate of {seen_hashes[digest]}: {fields['path']}")
                else:
                    seen_hashes[digest] = fields["path"]
                records.append(ImageRecord(**fields, split=split, sha256=digest))
            except (OSError, ValueError) as exc:
                errors.append(f"row {reader.line_num}: {exc}")
    return records, errors


def connected_groups(records: list[ImageRecord], pairs: list[tuple[ImageRecord, ImageRecord]]) -> list[list[ImageRecord]]:
    parents = {record.path: record.path for record in records}

    def find(path):
        while path != parents[path]:
            parents[path] = parents[parents[path]]
            path = parents[path]
        return path

    def join(left, right):
        parents[find(left)] = find(right)

    seen = {}
    for record in records:
        for key in record.provenance_keys():
            join(record.path, seen.setdefault(key, record.path))
    for left, right in pairs:
        join(left.path, right.path)
    groups = defaultdict(list)
    for record in records:
        groups[find(record.path)].append(record)
    return sorted(groups.values(), key=lambda group: (len(group), min(record.path for record in group)))


def assign_splits(groups: list[list[ImageRecord]]) -> list[ImageRecord]:
    totals = Counter(record.label for group in groups for record in group)
    counts = {split: Counter() for split in SPLITS}
    assigned = []
    pending = []

    def assign(group, split):
        counts[split].update(record.label for record in group)
        assigned.extend(replace(record, split=split) for record in group)

    for group in groups:
        fixed = {record.split for record in group if record.split}
        if any(record.kind in SYNTHETIC_KINDS for record in group):
            fixed.add("train")
        if len(fixed) > 1:
            raise ValueError(f"conflicting split pins for connected images starting at {group[0].path}: {sorted(fixed)}")
        if fixed:
            assign(group, fixed.pop())
        else:
            pending.append((group, frozenset(record.label for record in group)))

    def take(labels, split):
        index = next((i for i, (_, available) in enumerate(pending) if available == labels), None)
        if index is None:
            return False
        group, _ = pending.pop(index)
        assign(group, split)
        return True

    # A mixed group can fill both classes in an empty split. Reserve single-class
    # groups for any gaps left after that, before optimizing counts.
    for split in reversed(SPLITS):
        if not counts[split]:
            take(LABELS, split)
    for split in reversed(SPLITS):
        for label in CLASS_ORDER:
            if not counts[split][label] and not (take({label}, split) or take(LABELS, split)):
                raise ValueError(f"{split} lacks an independent {label} group. Add distinct labeled sources or review split pins.")

    def placement_cost(split, added):
        score = 0.0
        for label, count in added.items():
            target = totals[label] * TARGET_FRACTIONS[split]
            before = counts[split][label] - target
            score += ((before + count) ** 2 - before ** 2) / totals[label] ** 2
        return score

    for group, _ in reversed(pending):
        added = Counter(record.label for record in group)
        assign(group, min(SPLITS, key=lambda split: placement_cost(split, added)))
    return sorted(assigned, key=lambda record: record.path)


def import_report(metadata: Path, root: Path, manifest: Path | None, *, standard: bool = False,
                  development: bool = False, pilot: bool = False) -> dict:
    if pilot and standard:
        raise ValueError("--pilot and --standard select different dataset cohorts")
    if not root.is_dir():
        raise ValueError(f"data root does not exist or is not a directory: {root}")
    records, errors = read_candidates(metadata, root, pilot=pilot)
    images, decode_errors = inspect_images(records, root)
    errors.extend(decode_errors)
    pairs = near_duplicates(records, images)
    for left, right in pairs:
        if left.label != right.label and not pilot:
            errors.append(f"{left.path} and {right.path}: near-duplicate images have conflicting labels")
    listed_paths = {image_path(root, record.path) for record in records}
    unlisted = [path.relative_to(root).as_posix() for path in scan_images(root) if path.resolve() not in listed_paths]
    report = {**inventory(records, images), "near_duplicates": duplicate_report(pairs), "unlisted_images": unlisted,
              "errors": errors, "manifest_written": False}
    if pilot:
        selected = [record for record in records if record.review_status == "approved"]
        approved_report = audit_report(selected, root, pilot=True)
        errors.extend(approved_report["errors"])
        report.update(dataset_mode="pilot_illustrations", approved=approved_report)
        if manifest is not None and not errors:
            try:
                write_manifest(manifest, (record for record in selected
                                         if not development or record.split != "test"), overwrite=False)
                report["manifest_written"] = True
            except OSError as exc:
                errors.append(str(exc))
        return report
    if standard or development:
        groups = connected_groups(records, pairs)
        assigned = None
        try:
            approved_groups = []
            for group in groups:
                approved = [record for record in group if record.review_status == "approved"]
                if not approved:
                    continue
                pins = {record.split for record in group if record.split}
                if len(pins) > 1:
                    raise ValueError(f"conflicting split pins for connected images starting at {group[0].path}: {sorted(pins)}")
                split = next(iter(pins), "")
                approved_groups.append([replace(record, split=split) for record in approved])
            assigned = assign_splits(approved_groups)
        except ValueError as exc:
            errors.append(str(exc))
        checks = collection_report(records, assigned, groups, images, pairs)
        errors.extend(checks.pop("errors"))
        report["collection_standard"] = checks
        if assigned is not None and not errors:
            errors.extend(audit_report(assigned, root)["errors"])
            if manifest is not None and not errors:
                try:
                    output_records = (record for record in assigned if not development or record.split != "test")
                    write_manifest(manifest, output_records, overwrite=False)
                    report["manifest_written"] = True
                except OSError as exc:
                    errors.append(str(exc))
        checks["passed"] = not errors
        return report
    if manifest is None or errors:
        return report
    try:
        assigned = assign_splits(connected_groups(records, pairs))
        report.update(audit_report(assigned, root))
        if not report["errors"]:
            write_manifest(manifest, assigned, overwrite=False)
            report["manifest_written"] = True
    except (OSError, ValueError) as exc:
        report["errors"].append(str(exc))
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metadata", type=Path, required=True, help="UTF-8 CSV with one row per candidate image")
    parser.add_argument("--data-root", type=Path, required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--inspect", action="store_true", help="audit candidates without assigning splits")
    mode.add_argument("--manifest", type=Path, help="path to a new manifest file")
    mode.add_argument("--development-manifest", type=Path, help="export only train/val after the full collection audit")
    cohort = parser.add_mutually_exclusive_group()
    cohort.add_argument("--standard", action="store_true", help="enforce the approved P1 collection and Unity split rules")
    cohort.add_argument("--pilot", action="store_true", help="accept reviewed illustrations with explicit group splits")
    args = parser.parse_args()
    try:
        report = import_report(args.metadata, args.data_root, args.manifest or args.development_manifest,
                               standard=args.standard, development=args.development_manifest is not None,
                               pilot=args.pilot)
    except (OSError, ValueError, csv.Error) as exc:
        report = {"errors": [str(exc)], "manifest_written": False}
    print(json.dumps(report, indent=2))
    return 1 if report["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
