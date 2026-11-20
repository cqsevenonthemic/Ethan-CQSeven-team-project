import base64
import csv
from dataclasses import replace
from random import Random
from shutil import copyfile

import pytest

from planner_service.vision.audit_dataset import audit_report
from planner_service.vision.collection_standard import collection_report
from planner_service.vision.dataset import ImageRecord, load_manifest, sha256_file
from planner_service.vision.import_dataset import import_report


@pytest.fixture
def candidates(tmp_path):
    image_module = pytest.importorskip("PIL.Image")
    root = tmp_path / "images"
    root.mkdir()
    rows = []
    random = Random(7)
    for label in ("quyuan", "zhaojun"):
        for index in range(3):
            path = f"{label}-{index}.png"
            pixels = random.randbytes(101 * 121 * 3)
            image_module.frombytes("RGB", (101, 121), pixels).save(root / path)
            rows.append({"path": path, "label": label, "source": f"test-source-{path}",
                         "license": "fixture permission", "group": f"test-group-{path}",
                         "original_filename": path, "kind": "real", "split": ""})
    metadata = tmp_path / "candidates.csv"
    return root, rows, metadata


def write_csv(metadata, rows):
    with metadata.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)


def test_import_keeps_source_and_mirrored_derivatives_together(candidates, tmp_path):
    root, rows, metadata = candidates
    image_module = pytest.importorskip("PIL.Image")
    image_ops = pytest.importorskip("PIL.ImageOps")
    original = rows[0]
    original["split"] = "test"
    with image_module.open(root / original["path"]) as image:
        image_ops.mirror(image).save(root / "derivative.png")
    rows.append({**original, "path": "derivative.png", "source": "mirror-source", "group": "mirror-group", "split": ""})
    rows[1]["source"] = "mirror-source"
    # A new independent group keeps all three splits feasible after the merge.
    random = Random(44)
    image_module.frombytes("RGB", (101, 121), random.randbytes(101 * 121 * 3)).save(root / "extra.png")
    rows.append({**original, "path": "extra.png", "source": "extra-source", "group": "extra-group", "split": ""})
    copyfile(root / original["path"], root / "unlisted.png")
    write_csv(metadata, rows)

    manifest = tmp_path / "manifest.json"
    report = import_report(metadata, root, manifest)
    assert report["manifest_written"] and report["errors"] == []
    assert report["unlisted_images"] == ["unlisted.png"]
    assert report["resolutions"] == {"101x121": 8}
    assert report["warnings"]
    assert len(report["near_duplicates"]) == 1
    assert all(set(labels) == {"quyuan", "zhaojun"} for labels in report["splits"].values())
    records = load_manifest(manifest)
    by_path = {item.path: item for item in records}
    assert all(by_path[path].split == "test" for path in (original["path"], "derivative.png", rows[1]["path"]))
    assert by_path[original["path"]].sha256 == sha256_file(root / original["path"])
    assert by_path[original["path"]].original_filename == original["original_filename"]
    assert audit_report(records, root)["errors"] == []

    tampered = [replace(record, split="val") if record.path == "derivative.png" else record for record in records]
    audit_errors = audit_report(tampered, root)["errors"]
    assert any("near-duplicate images appear across" in error for error in audit_errors)
    assert any("mirror-source" in error for error in audit_errors)

    copied_manifest = tmp_path / "another.json"
    write_csv(metadata, list(reversed(rows)))
    assert import_report(metadata, root, copied_manifest)["manifest_written"]
    before = manifest.read_bytes()
    assert copied_manifest.read_bytes() == before
    assert not import_report(metadata, root, manifest)["manifest_written"]
    assert manifest.read_bytes() == before


def test_import_reports_corrupt_duplicate_and_missing_license(candidates, tmp_path):
    root, rows, metadata = candidates
    original = rows[0]
    copyfile(root / original["path"], root / "copy.png")
    rows.append({**original, "path": "copy.png", "label": "zhaojun"})
    (root / "corrupt.png").write_bytes((root / original["path"]).read_bytes()[:60])
    rows.append({**original, "path": "corrupt.png"})
    rows[1]["license"] = ""
    rows[2]["path"] = "../outside.png"
    write_csv(metadata, rows)
    manifest = tmp_path / "manifest.json"
    report = import_report(metadata, root, manifest)
    for message in ("exact duplicate", "image decode failed", "license", "relative to the data root"):
        assert any(message in error for error in report["errors"])
    assert not manifest.exists()


def test_import_requires_independent_groups_and_respects_generated_pins(candidates, tmp_path):
    root, rows, metadata = candidates
    rows[0]["kind"] = "synthetic"
    rows[0]["split"] = "test"
    write_csv(metadata, rows)
    assert any("only eligible for train" in error for error in import_report(metadata, root, None)["errors"])

    rows[0]["split"] = ""
    write_csv(metadata, rows)
    assert any("generation_batch" in error for error in import_report(metadata, root, None)["errors"])
    rows[0].update(generation_batch="fixture-batch", generation_prompt="fixture prompt", generation_tool="fixture tool")
    rows[1]["group"] = rows[0]["group"]
    rows[1]["split"] = "test"
    manifest = tmp_path / "manifest.json"
    write_csv(metadata, rows)
    assert any("conflicting split pins" in error for error in import_report(metadata, root, manifest)["errors"])

    rows[1]["split"] = ""
    write_csv(metadata, rows)
    assert any("lacks an independent" in error for error in import_report(metadata, root, manifest)["errors"])
    assert not manifest.exists()

    rows[1]["group"] = "separate-group"
    write_csv(metadata, rows)
    assert import_report(metadata, root, manifest)["manifest_written"]
    generated = [record for record in load_manifest(manifest) if record.kind == "synthetic"]
    assert len(generated) == 1
    assert generated[0].split == "train"
    assert generated[0].generation_batch == "fixture-batch"

    rows[0]["kind"] = "real"
    write_csv(metadata, rows)
    assert any("cannot have synthetic" in error for error in import_report(metadata, root, None)["errors"])


def test_onnx_evaluation_uses_only_test_rows_and_records_metrics(candidates, tmp_path, monkeypatch):
    from planner_service.vision import evaluate_onnx

    root, rows, metadata = candidates
    for row in rows:
        row.update(view_angle="profile", props="none")
    write_csv(metadata, rows)
    manifest = tmp_path / "manifest.json"
    assert import_report(metadata, root, manifest)["manifest_written"]
    test_rows = [record for record in load_manifest(manifest) if record.split == "test"]
    image_labels = {(root / row.path).read_bytes(): row.label for row in test_rows}
    requests = []

    class Recognizer:
        def __init__(self, path):
            assert path.endswith("model.onnx")

        def predict_base64(self, encoded):
            label = image_labels[base64.b64decode(encoded)]
            requests.append(label)
            return {"pred_role": "quyuan", "confidence": 0.75}

    monkeypatch.setattr(evaluate_onnx, "VisionRecognizer", Recognizer)
    result = evaluate_onnx.evaluate_onnx(manifest, root, tmp_path / "model.onnx")
    assert requests == [test_rows[0].label, *(record.label for record in test_rows)]
    assert result["confusion_matrix"] == [[1, 0], [1, 0]]
    assert result["accuracy"] == 0.5
    assert result["per_class"]["quyuan"] == {"precision": 0.5, "recall": 1.0, "f1": 2 / 3}
    assert result["slices"]["profile"]["confusion_matrix"] == result["confusion_matrix"]
    assert result["slices"]["no_props"]["accuracy"] == result["accuracy"]
    assert set(result["inference_ms"]) == {"provider", "mean", "p50", "p95"}


def test_pilot_requires_reviews_and_keeps_fixed_generation_groups_isolated(candidates, tmp_path):
    root, rows, metadata = candidates
    for index, row in enumerate(rows):
        split = ("train", "val", "test")[index % 3]
        row.update(kind="synthetic", split=split, generation_batch=f"pilot-{split}",
                   generation_prompt="fixture prompt", generation_tool="fixture tool",
                   original_id=row["path"], review_status="approved", reviewer="fixture",
                   identity_evidence="fixture review", attribution="fixture source")
    write_csv(metadata, rows)
    path = tmp_path / "pilot.json"
    assert any("only eligible for train" in error for error in import_report(metadata, root, None)["errors"])
    assert import_report(metadata, root, path, pilot=True)["manifest_written"]
    development = tmp_path / "development.json"
    assert import_report(metadata, root, development, development=True, pilot=True)["manifest_written"]
    assert {row.split for row in load_manifest(development)} == {"train", "val"}
    assert audit_report(load_manifest(path), root, pilot=True)["errors"] == []

    rows[2]["generation_batch"] = rows[0]["generation_batch"]
    write_csv(metadata, rows)
    assert any("generation_batch" in error and "across" in error
               for error in import_report(metadata, root, None, pilot=True)["errors"])

    rows[2].update(generation_batch="pilot-test", review_status="pending")
    write_csv(metadata, rows)
    assert any("test" in error and "both labels" in error
               for error in import_report(metadata, root, None, pilot=True)["errors"])


def test_formal_import_blocks_unapproved_material_and_unity_leakage(candidates, tmp_path):
    root, rows, metadata = candidates
    for index, row in enumerate(rows):
        row.update(original_id=f"original-{index}", unity_set="none", unity_asset_group="", unity_batch="",
                   unity_encoding="", review_status="pending", reviewer="", identity_evidence="", attribution="",
                   channel="", style="", composition="", background="", props="", prop_cue="", scene_cue="",
                   gender_cue="")
    write_csv(metadata, rows)
    manifest = tmp_path / "formal.json"
    report = import_report(metadata, root, manifest, standard=True)
    assert report["collection_standard"]["review_status"] == {"pending": 6}
    assert not report["collection_standard"]["passed"] and not manifest.exists()

    for row, cohort, split in zip(rows, ("dev", "final"), ("val", "test")):
        row.update(unity_set=cohort, split=split, unity_asset_group="shared-character", unity_batch=cohort,
                   unity_encoding="PNG", review_status="approved", reviewer="fixture reviewer",
                   identity_evidence="fixture", attribution="fixture", channel="clean_portrait", style="realistic",
                   composition="half", background="simple", props="none", prop_cue="none", scene_cue="none",
                   gender_cue="weak")
    write_csv(metadata, rows)
    report = import_report(metadata, root, manifest, standard=True)
    assert any("conflicting split pins" in error for error in report["errors"])
    assert not manifest.exists()


def test_collection_readiness_counts_independent_groups_and_no_prop_images():
    candidates, assigned, groups = [], [], []
    for label in ("quyuan", "zhaojun"):
        for split, count, group_count in (("train", 45, 5), ("val", 15, 3), ("test", 20, 4)):
            by_group = [[] for _ in range(group_count)]
            for index in range(count):
                key = f"{label}-{split}-{index}"
                row = ImageRecord(f"{key}.png", label, split, key, key, "fixture", original_id=key,
                                  review_status="approved", reviewer="fixture", identity_evidence="fixture",
                                  attribution="fixture", channel="clean_portrait", style="realistic",
                                  composition="half", background="simple", props="none", prop_cue="none",
                                  scene_cue="none", gender_cue="weak", unity_set="none")
                if index == 0 and split in {"val", "test"}:
                    row = replace(row, unity_set="dev" if split == "val" else "final",
                                  unity_asset_group=key, unity_batch=key, unity_encoding="PNG", kind="real")
                by_group[index % group_count].append(row)
                assigned.append(row)
            groups.extend(by_group)
        for index in range(40):
            key = f"{label}-pending-{index}"
            candidates.append(ImageRecord(f"{key}.png", label, "", key, key, "fixture", original_id=key))
    records = assigned + candidates
    images = dict.fromkeys(record.path for record in records)
    assert collection_report(records, assigned, groups, images, [])["errors"] == []
    less_independent = [list(row) for row in groups]
    less_independent[8].extend(less_independent.pop(9))
    report = collection_report(records, assigned, less_independent, images, [])
    assert any("quyuan test" in error and "3/4" in error for error in report["errors"])


def test_development_manifest_omits_all_test_rows(candidates, tmp_path, monkeypatch):
    from planner_service.vision import import_dataset

    root, rows, metadata = candidates
    for row in rows:
        row["review_status"] = "approved"
    write_csv(metadata, rows)
    monkeypatch.setattr(import_dataset, "collection_report", lambda *_: {"errors": []})
    target = tmp_path / "development.json"
    report = import_report(metadata, root, target, development=True)
    assert report["manifest_written"]
    exported = load_manifest(target)
    assert {row.split for row in exported} == {"train", "val"}
    assert audit_report(exported, root, required_splits=("train", "val"))["errors"] == []
