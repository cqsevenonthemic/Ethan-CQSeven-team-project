import base64
import hashlib
import importlib
from pathlib import Path
from urllib.parse import urlsplit

import pytest
from fastapi.testclient import TestClient

from planner_service.vision import LABELS
from planner_service.vision.dataset import ImageRecord, sha256_file, write_manifest
from planner_service.vision.model import build_model
from planner_service.vision.train import run_training


def encode_image(image):
    return base64.b64encode(image).decode("ascii")


def test_recognize_loads_onnx_and_handles_bad_inputs(monkeypatch, tmp_path):
    onnx = pytest.importorskip("onnx")
    pytest.importorskip("onnxruntime")
    image_module = pytest.importorskip("PIL.Image")
    module = importlib.import_module("planner_service.app")
    path = tmp_path / "classifier.onnx"
    helper = onnx.helper
    float_type = onnx.TensorProto.FLOAT
    logits = helper.make_tensor("scores", float_type, [1, 2], [0.0, 2.0])
    graph = helper.make_graph(
        [helper.make_node("Constant", [], ["logits"], value=logits)],
        "recognize_fixture",
        [helper.make_tensor_value_info("image", float_type, [1, 3, 224, 224])],
        [helper.make_tensor_value_info("logits", float_type, [1, 2])],
    )
    image_path = tmp_path / "sample.png"
    image_module.new("RGB", (31, 45)).save(image_path)
    raw = image_path.read_bytes()
    body = {"image_base64": encode_image(raw)}
    monkeypatch.setenv("PLANNER_ONNX_MODEL", str(path))
    with TestClient(module.app) as client:
        missing = client.post("/recognize", json=body)
        assert missing.status_code == 501
        assert missing.json()["error"]["code"] == "VISION_NOT_IMPLEMENTED"

        path.write_bytes(b"broken ONNX")
        broken = client.post("/recognize", json=body)
        assert broken.status_code == 501
        assert broken.json()["error"]["code"] == "VISION_INFERENCE_FAILED"
        assert str(path) not in broken.text

        onnx.save_model(helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)]), path)
        response = client.post("/recognize", json=body)
        assert response.status_code == 200
        payload = response.json()
        assert payload.pop("recognition_id").startswith("onnx-")
        assert payload == {
            "pred_role": "zhaojun", "confidence": pytest.approx(0.880797),
            "recognition_source": "onnx",
            "image_sha256": hashlib.sha256(raw).hexdigest(), "status": "recognized",
        }

        invalid = client.post("/recognize", json={"image_base64": encode_image(raw[:8])})
        assert invalid.status_code == 422
        assert invalid.json()["error"]["code"] == "INVALID_INPUT"


@pytest.mark.parametrize("pretrained", [False, True], ids=["random-init", "imagenet-cache"])
def test_mobilenet_cuda_training_and_export(monkeypatch, tmp_path, pretrained):
    torch = pytest.importorskip("torch")
    torchvision = pytest.importorskip("torchvision")
    onnx = pytest.importorskip("onnx")
    pytest.importorskip("onnxruntime")
    image_module = pytest.importorskip("PIL.Image")
    np = pytest.importorskip("numpy")
    if not torch.cuda.is_available():
        pytest.skip("CUDA is required for the P1 GPU smoke test")
    if pretrained:
        weights = torchvision.models.MobileNet_V3_Small_Weights.DEFAULT
        weight_file = Path(urlsplit(weights.url).path).name
        if not (Path(torch.hub.get_dir()) / "checkpoints" / weight_file).is_file():
            pytest.skip("Official ImageNet weights must be cached for the offline smoke test")

    # Track the real training entry point across both phases, including gradient flow.
    module = importlib.import_module("planner_service.vision.train")
    build = module.build_mobilenet_v3_small
    phases = []
    gradients = []
    batch_norm_updates = []

    def batch_norm_counters(model):
        return tuple(model.features[index][1].num_batches_tracked.item() for index in (0, -1))

    def track_model(**kwargs):
        model = build(**kwargs).to("cuda")
        model.features[-1][0].weight.register_hook(lambda grad: gradients.append("backbone"))
        model.classifier[-1].weight.register_hook(lambda grad: gradients.append("head"))
        initial_counters = batch_norm_counters(model)

        def check_forward(current, _inputs, logits):
            if current.training:
                phases.append((tuple(block.training for block in current.features), logits.dtype))
                batch_norm_updates.append(tuple(
                    value - initial for value, initial in zip(batch_norm_counters(current), initial_counters)
                ))

        model.register_forward_hook(check_forward)
        return model

    monkeypatch.setattr(module, "build_mobilenet_v3_small", track_model)
    rng = np.random.default_rng(42)
    records = []
    for split in ("train", "val", "test"):
        for label in LABELS:
            image_path = tmp_path / f"{split}-{label}.png"
            image_module.fromarray(rng.integers(0, 256, (37, 51, 3), dtype=np.uint8)).save(image_path)
            records.append(ImageRecord(image_path.name, label, split, sha256_file(image_path),
                                       f"synthetic-{split}", "test fixture"))
    manifest = tmp_path / "manifest.json"
    write_manifest(manifest, records)
    output = tmp_path / "models"
    result = run_training(manifest, tmp_path, output, epochs=2, seed=42,
                          batch_size=2, pretrained=pretrained, freeze_epochs=1)
    assert (result["architecture"], result["device"], result["amp"]) == ("mobilenet_v3_small", "cuda", True)
    assert result["labels"] == ["quyuan", "zhaojun"]
    assert result["epochs"] == 2
    assert all(np.isfinite(row["train_loss"]) for row in result["history"])
    assert len(phases) == 2
    assert not any(phases[0][0])
    assert not any(phases[1][0][:-2]) and all(phases[1][0][-2:])
    assert [dtype for _, dtype in phases] == [torch.float16, torch.float16]
    assert batch_norm_updates == [(0, 0), (0, 1)]
    assert gradients.count("head") == 2 and gradients.count("backbone") == 1
    assert result["onnx_max_abs_error"] < 1e-4
    model_path = output / "classifier.onnx"
    onnx.checker.check_model(str(model_path))

    api = importlib.import_module("planner_service.app")
    monkeypatch.setenv("PLANNER_ONNX_MODEL", str(model_path))
    with TestClient(api.app) as client:
        response = client.post("/recognize", json={"image_base64": encode_image(image_path.read_bytes())})
    assert response.status_code == 200
    assert response.json()["pred_role"] in LABELS
    assert response.json()["recognition_source"] == "onnx"
    print(f"CUDA synthetic training passed. ONNX max absolute error {result['onnx_max_abs_error']:.8g}")


def test_cnn_baseline_retains_three_convolutions_and_two_logits():
    torch = pytest.importorskip("torch")
    model = build_model().eval()
    assert sum(isinstance(layer, torch.nn.Conv2d) for layer in model) == 3
    with torch.no_grad():
        assert model(torch.zeros(1, 3, 32, 32)).shape == (1, 2)
