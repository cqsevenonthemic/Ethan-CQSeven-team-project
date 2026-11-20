from pathlib import Path

from planner_service.vision.dataset import ImageRecord, sha256_file, validate_manifest
from planner_service.vision.inference import ModelUnavailable, VisionRecognizer


def record(path: str, label: str, split: str, root: Path, source: str = "course") -> ImageRecord:
    file_path = root / path
    return ImageRecord(path, label, split, sha256_file(file_path), source, "CC BY 4.0")


def test_manifest_requires_both_labels_in_each_split(tmp_path):
    for name in ("train-q.jpg", "train-z.jpg", "val-q.jpg", "val-z.jpg", "test-q.jpg", "test-z.jpg"):
        (tmp_path / name).write_bytes(name.encode())
    records = [
        record("train-q.jpg", "quyuan", "train", tmp_path), record("train-z.jpg", "zhaojun", "train", tmp_path),
        record("val-q.jpg", "quyuan", "val", tmp_path), record("val-z.jpg", "zhaojun", "val", tmp_path),
        record("test-q.jpg", "quyuan", "test", tmp_path), record("test-z.jpg", "zhaojun", "test", tmp_path),
    ]
    assert validate_manifest(records, tmp_path) == []


def test_manifest_rejects_duplicate_content_and_hash_mismatch(tmp_path):
    (tmp_path / "a.jpg").write_bytes(b"same")
    (tmp_path / "b.jpg").write_bytes(b"same")
    records = [
        record("a.jpg", "quyuan", "train", tmp_path),
        ImageRecord("b.jpg", "zhaojun", "train", "0" * 64, "course", "CC BY 4.0"),
    ]
    errors = validate_manifest(records, tmp_path)
    assert any("duplicate image content" in item for item in errors)
    assert any("sha256 mismatch" in item for item in errors)


def test_inference_refuses_missing_model_without_a_test_substitute(monkeypatch):
    monkeypatch.delenv("PLANNER_ONNX_MODEL", raising=False)
    try:
        VisionRecognizer()
    except ModelUnavailable:
        return
    raise AssertionError("inference must not proceed without a trained model")
