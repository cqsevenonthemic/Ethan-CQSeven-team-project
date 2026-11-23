"""Field validation and readiness checks for the approved P1 collection plan."""

from collections import Counter, defaultdict

from planner_service.vision import LABELS
from planner_service.vision.dataset import SPLITS, ImageRecord


FIELDS = (
    "identity_evidence", "attribution", "original_id", "reference_group", "channel", "style",
    "composition", "view_angle", "background", "props", "prop_cue", "scene_cue", "gender_cue", "unity_set",
    "unity_asset_group", "unity_batch", "unity_encoding", "review_status", "reviewer", "review_notes",
)
CHANNEL_TARGETS = {"historical": 10, "guofeng": 40, "ai_realistic": 35, "clean_portrait": 35}
CHOICES = {
    "channel": CHANNEL_TARGETS,
    "style": {"historical", "guofeng", "realistic"},
    "composition": {"closeup", "half", "full"},
    "background": {"simple", "moderate", "complex"},
    "prop_cue": {"none", "weak", "strong"},
    "scene_cue": {"none", "weak", "strong"},
    "gender_cue": {"weak", "medium", "strong"},
}
PROP_TAGS = {"book", "bamboo_slip", "brush", "pipa", "other_instrument", "horse", "sword", "fan", "other"}
UNITY_SPLITS = {"train": "train", "dev": "val", "final": "test"}
MIN_IMAGES = dict(zip(SPLITS, (45, 15, 20)))
MIN_GROUPS = dict(zip(SPLITS, (5, 3, 4)))


def row_errors(record: ImageRecord, image) -> list[str]:
    if record.review_status != "approved":
        return [] if record.review_status in {"", "pending", "rejected"} else ["invalid review_status"]
    errors = []
    for key in ("identity_evidence", "attribution", "original_id", "props", "reviewer"):
        if not getattr(record, key):
            errors.append(f"{key} is required for approved images")
    for key, values in CHOICES.items():
        if getattr(record, key) not in values:
            errors.append(f"{key} must be one of {', '.join(sorted(values))}")
    props = {item.strip() for item in record.props.split("|")}
    if props != {"none"} and not props <= PROP_TAGS:
        errors.append("props must be none or pipe-separated prop tags")
    if props == {"none"} and record.prop_cue != "none":
        errors.append("props=none requires prop_cue=none")
    if "other" in props and not record.review_notes:
        errors.append("props=other requires review_notes")
    if record.channel == "ai_realistic" and (record.kind != "synthetic" or record.style != "realistic"):
        errors.append("ai_realistic requires kind=synthetic and style=realistic")
    if record.channel == "clean_portrait" and record.background != "simple":
        errors.append("clean_portrait requires background=simple")

    unity_fields = (record.unity_asset_group, record.unity_batch, record.unity_encoding)
    if record.unity_set == "none":
        if any(unity_fields):
            errors.append("non-Unity images must leave Unity provenance fields blank")
    elif record.unity_set in UNITY_SPLITS:
        if record.kind != "real" or not all(unity_fields) or record.split != UNITY_SPLITS[record.unity_set]:
            errors.append(f"Unity {record.unity_set} requires real material, all Unity fields and split={UNITY_SPLITS[record.unity_set]}")
        if image and (image.format not in {"PNG", "JPEG"} or image.size_bytes > 4 * 1024**2
                      or image.width * image.height > 16_000_000):
            errors.append("Unity image exceeds /recognize format, byte or pixel limits")
    else:
        errors.append("unity_set must be none, train, dev or final")
    return errors


def collection_report(records, assigned, groups, images, pairs) -> dict:
    accepted = [record for record in records if record.review_status == "approved"]
    errors = [f"{record.path}: {error}" for record in records for error in row_errors(record, images.get(record.path))]
    originals = defaultdict(list)
    for record in accepted:
        if record.original_id:
            originals[record.original_id].append(record.path)
    for key, paths in originals.items():
        if len(paths) > 1:
            errors.append(f"original_id {key!r} has multiple approved variants: {', '.join(paths)}")
    for left, right in pairs:
        if left.review_status == right.review_status == "approved":
            errors.append(f"near-duplicate approved images require one representative: {left.path}, {right.path}")

    group_by_path = {record.path: index for index, group in enumerate(groups) for record in group}
    diagnostics = {}
    for label in LABELS:
        candidates = [r for r in records if r.label == label and r.path in images]
        approved = [r for r in accepted if r.label == label]
        unique_candidates = {r.original_id: r for r in candidates if r.original_id}
        channels = Counter(r.channel for r in unique_candidates.values() if r.channel)
        info = {"candidates": len(candidates), "identified_originals": len(unique_candidates),
                "approved": len(approved), "channels": dict(channels),
                "channel_target_gaps": {key: size - channels[key] for key, size in CHANNEL_TARGETS.items()
                                        if channels[key] < size},
                "approved_coverage": {key: dict(Counter(getattr(r, key) for r in approved))
                                      for key in (*CHOICES, "props")},
                "unity_sets": dict(Counter(r.unity_set for r in approved)), "splits": {}}
        diagnostics[label] = info
        if len(unique_candidates) < 120:
            errors.append(f"{label}: {len(unique_candidates)}/120 identified candidate originals")
        if len(approved) < 80:
            errors.append(f"{label}: {len(approved)}/80 approved images")
        for field, minimum in (("prop_cue", 35), ("scene_cue", 25)):
            count = sum(getattr(r, field) == "none" for r in approved)
            if approved and count * 100 < len(approved) * minimum:
                errors.append(f"{label}: {field}=none requires at least {minimum}% of approved images")
        for cohort in ("dev", "final"):
            if not info["unity_sets"].get(cohort):
                errors.append(f"{label}: Unity {cohort} samples are required")
        if assigned is None:
            continue
        for split in SPLITS:
            rows = [r for r in assigned if r.label == label and r.split == split]
            group_ids = {group_by_path[r.path] for r in rows}
            no_props = [r for r in rows if r.prop_cue == "none"]
            no_prop_groups = {group_by_path[r.path] for r in no_props}
            info["splits"][split] = {"images": len(rows), "groups": len(group_ids),
                                     "no_prop_images": len(no_props), "no_prop_groups": len(no_prop_groups)}
            if len(rows) < MIN_IMAGES[split] or len(group_ids) < MIN_GROUPS[split]:
                errors.append(f"{label} {split}: {len(rows)}/{MIN_IMAGES[split]} images, {len(group_ids)}/{MIN_GROUPS[split]} independent groups")
            if split == "test" and (len(no_props) < 8 or len(no_prop_groups) < 2):
                errors.append(f"{label} test: at least 8 no-prop images from at least 2 groups are required")
    return {"review_status": dict(Counter(r.review_status or "pending" for r in records)),
            "classes": diagnostics, "errors": errors}
