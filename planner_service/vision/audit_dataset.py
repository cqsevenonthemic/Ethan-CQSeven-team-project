"""Audit image integrity, dataset coverage and cross-split leakage."""

from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from dataclasses import dataclass
from itertools import combinations
from pathlib import Path

from planner_service.vision.dataset import ImageRecord, SPLITS, image_path, load_manifest, validate_manifest


@dataclass(frozen=True)
class ImageInfo:
    width: int
    height: int
    format: str
    size_bytes: int
    fingerprint: tuple[int, int]
    mirrored: tuple[int, int]


def perceptual_hashes(gray) -> tuple[int, int]:
    pixels = list(gray.resize((8, 8)).getdata())
    average = sum(pixels) / len(pixels)
    average_bits = sum(1 << i for i, pixel in enumerate(pixels) if pixel >= average)
    pixels = list(gray.resize((9, 8)).getdata())
    difference_bits = sum(1 << i for i in range(72) if i % 9 < 8 and pixels[i] > pixels[i + 1])
    return average_bits, difference_bits


def inspect_images(records: list[ImageRecord], root: Path) -> tuple[dict[str, ImageInfo], list[str]]:
    from PIL import Image, ImageOps

    images = {}
    errors = []
    for record in records:
        try:
            path = image_path(root, record.path)
            size_bytes = path.stat().st_size
            with Image.open(path) as image:
                image_format = image.format
                if image_format not in {"JPEG", "PNG", "WEBP"} or getattr(image, "n_frames", 1) != 1:
                    raise ValueError("expected a single-frame JPEG, PNG or WebP")
                image.verify()
            with Image.open(path) as image:
                image.load()
                width, height = image.size
                gray = ImageOps.grayscale(image)
                images[record.path] = ImageInfo(width, height, image_format, size_bytes,
                                                perceptual_hashes(gray), perceptual_hashes(ImageOps.mirror(gray)))
        except (OSError, ValueError, SyntaxError, EOFError, Image.DecompressionBombError) as exc:
            errors.append(f"{record.path}: image decode failed: {exc}")
    return images, errors


def near_duplicates(records: list[ImageRecord], images: dict[str, ImageInfo]) -> list[tuple[ImageRecord, ImageRecord]]:
    pairs = []
    for left, right in combinations(records, 2):
        if left.sha256 == right.sha256 or left.path not in images or right.path not in images:
            continue
        a = images[left.path].fingerprint
        b = images[right.path]
        if any((a[0] ^ match[0]).bit_count() <= 4 and (a[1] ^ match[1]).bit_count() <= 6
               for match in (b.fingerprint, b.mirrored)):
            pairs.append((left, right))
    return pairs


def inventory(records: list[ImageRecord], images: dict[str, ImageInfo]) -> dict:
    splits = defaultdict(Counter)
    sources = defaultdict(Counter)
    source_groups = defaultdict(Counter)
    group_splits = defaultdict(set)
    for record in records:
        splits[record.split or "unassigned"][record.label] += 1
        sources[record.source][record.label] += 1
        source_groups[record.source_group][record.label] += 1
        group_splits[record.source_group].add(record.split or "unassigned")
    warnings = []
    for path, info in images.items():
        if min(info.width, info.height) < 224:
            warnings.append(f"{path}: short edge below 224 pixels ({info.width}x{info.height})")
        if info.format not in {"JPEG", "PNG"} or info.size_bytes > 4 * 1024**2 or info.width * info.height > 16_000_000:
            warnings.append(f"{path}: original exceeds /recognize format, byte or pixel limits")
    resolutions = Counter(f"{info.width}x{info.height}" for info in images.values())
    files = {record.path: {"sha256": record.sha256, "width": images[record.path].width,
                           "height": images[record.path].height, "format": images[record.path].format,
                           "size_bytes": images[record.path].size_bytes}
             for record in records if record.path in images}
    return {"images": len(records), "decoded": len(images), "labels": dict(Counter(r.label for r in records)),
            "kinds": dict(Counter(r.kind for r in records)), "splits": dict(splits), "sources": dict(sources),
            "groups": dict(source_groups), "group_splits": {group: sorted(items) for group, items in group_splits.items()},
            "resolutions": dict(resolutions), "formats": dict(Counter(info.format for info in images.values())),
            "files": files, "warnings": warnings}


def duplicate_report(pairs: list[tuple[ImageRecord, ImageRecord]]) -> list[dict]:
    return [{"left": left.path, "right": right.path, "labels": [left.label, right.label],
             "splits": [left.split, right.split]} for left, right in pairs]


def audit_report(records: list[ImageRecord], root: Path, *,
                 required_splits: tuple[str, ...] = SPLITS, pilot: bool = False) -> dict:
    errors = validate_manifest(records, root, required_splits=required_splits, pilot=pilot)
    images, decode_errors = inspect_images(records, root)
    errors.extend(decode_errors)
    pairs = near_duplicates(records, images)
    for left, right in pairs:
        if left.label != right.label:
            errors.append(f"{left.path} and {right.path}: near-duplicate images have conflicting labels")
        if left.split != right.split:
            errors.append(f"{left.path} and {right.path}: near-duplicate images appear across dataset splits")
    split_sets = defaultdict(set)
    for record in records:
        for key in record.provenance_keys():
            split_sets[key].add(record.split)
    for (field, key), splits in split_sets.items():
        if len(splits) > 1:
            errors.append(f"{field} {key!r} appears across dataset splits: {sorted(splits)}")
    return {**inventory(records, images), "near_duplicates": duplicate_report(pairs), "errors": sorted(set(errors))}


def audit(records: list[ImageRecord], root: Path, *,
          required_splits: tuple[str, ...] = SPLITS, pilot: bool = False) -> list[str]:
    return audit_report(records, root, required_splits=required_splits, pilot=pilot)["errors"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--pilot", action="store_true", help="audit explicitly grouped pilot illustrations")
    args = parser.parse_args()
    try:
        report = audit_report(load_manifest(args.manifest), args.data_root, pilot=args.pilot)
    except (OSError, ValueError) as exc:
        report = {"errors": [str(exc)]}
    print(json.dumps(report, indent=2))
    return 1 if report["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
