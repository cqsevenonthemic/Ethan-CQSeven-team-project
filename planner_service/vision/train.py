"""Train, evaluate, save best weights and export the P1 classifier."""

from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

from planner_service.vision import LABELS
from planner_service.vision.audit_dataset import audit
from planner_service.vision.dataset import ImageRecord, SPLITS, load_manifest
from planner_service.vision.metrics import classification_metrics
from planner_service.vision.model import (
    build_mobilenet_v3_small, build_model, freeze_backbone,
    set_mobilenet_training_mode, unfreeze_last_blocks,
)
from planner_service.vision.preprocess import build_transform

LABEL_TO_INDEX = {label: index for index, label in enumerate(LABELS)}


def seed_everything(seed: int) -> None:
    random.seed(seed)
    try:
        import numpy as np
        import torch
        np.random.seed(seed)
        torch.manual_seed(seed)
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("PyTorch and NumPy are required for training") from exc


def make_dataset(records: list[ImageRecord], root: Path, training: bool):
    try:
        from PIL import Image
        from torch.utils.data import Dataset
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("Pillow and PyTorch are required for training") from exc
    transform = build_transform(training)

    class ManifestDataset(Dataset):
        def __len__(self):
            return len(records)

        def __getitem__(self, index):
            record = records[index]
            with Image.open(root / record.path) as image:
                image = image.convert("RGB")
                return transform(image), LABEL_TO_INDEX[record.label]

    return ManifestDataset()


def run_training(manifest_path: Path, data_root: Path, output_dir: Path, epochs: int, seed: int,
                 batch_size: int = 32, learning_rate: float = 1e-3, patience: int = 5,
                 architecture: str = "mobilenet_v3_small", pretrained: bool = True,
                 freeze_epochs: int = 2, defer_test: bool = False, pilot: bool = False) -> dict:
    if architecture not in {"mobilenet_v3_small", "baseline_cnn"}:
        raise ValueError(f"unsupported architecture: {architecture}")
    if freeze_epochs < 0 or epochs < 1:
        raise ValueError("epochs must be positive and freeze_epochs must be nonnegative")
    try:
        import torch
        from torch import nn
        from torch.utils.data import DataLoader
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("PyTorch is required for training") from exc
    seed_everything(seed)
    records = load_manifest(manifest_path)
    if defer_test and any(record.split == "test" for record in records):
        raise ValueError("--defer-test requires a development manifest containing only train and val")
    errors = audit(records, data_root, required_splits=("train", "val") if defer_test else SPLITS,
                   pilot=pilot)
    if errors:
        raise ValueError("manifest validation failed:\n" + "\n".join(errors))
    train_records = [item for item in records if item.split == "train"]
    val_records = [item for item in records if item.split == "val"]
    test_records = [item for item in records if item.split == "test"]
    train_loader = DataLoader(make_dataset(train_records, data_root, True), batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(make_dataset(val_records, data_root, False), batch_size=batch_size)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    use_amp = device.type == "cuda"
    use_mobilenet = architecture == "mobilenet_v3_small"
    if use_mobilenet:
        model = build_mobilenet_v3_small(pretrained=pretrained).to(device)
        freeze_backbone(model)
    else:
        model = build_model().to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=learning_rate, weight_decay=1e-4)
    criterion = nn.CrossEntropyLoss()
    scaler = torch.amp.GradScaler("cuda", enabled=use_amp)
    best_val = float("inf")
    best_epoch = 0
    stale = 0
    history = []
    output_dir.mkdir(parents=True, exist_ok=True)
    for epoch in range(1, epochs + 1):
        if use_mobilenet:
            if epoch == freeze_epochs + 1:
                unfreeze_last_blocks(model)
                stale = 0
            set_mobilenet_training_mode(model)
        else:
            model.train()
        train_loss = 0.0
        for images, labels in train_loader:
            optimizer.zero_grad()
            with torch.autocast(device_type=device.type, dtype=torch.float16, enabled=use_amp):
                loss = criterion(model(images.to(device)), labels.to(device))
            scaler.scale(loss).backward()
            scaler.step(optimizer)
            scaler.update()
            train_loss += loss.item() * len(labels)
        train_loss /= len(train_loader.dataset)
        model.eval()
        val_loss = 0.0
        with torch.no_grad():
            for images, labels in val_loader:
                val_loss += criterion(model(images.to(device)), labels.to(device)).item() * len(labels)
        val_loss /= len(val_loader.dataset)
        history.append({"epoch": epoch, "train_loss": train_loss, "val_loss": val_loss})
        if val_loss < best_val:
            best_val, stale = val_loss, 0
            best_epoch = epoch
            torch.save({"state_dict": model.state_dict(), "labels": list(LABEL_TO_INDEX), "seed": seed}, output_dir / "best.pt")
        else:
            stale += 1
            waiting_for_fine_tuning = use_mobilenet and epoch <= freeze_epochs < epochs
            if stale >= patience and not waiting_for_fine_tuning:
                break
    checkpoint = torch.load(output_dir / "best.pt", map_location=device, weights_only=True)
    model.load_state_dict(checkpoint["state_dict"])
    validation = evaluate(model, val_loader, device)
    metrics = None
    if not defer_test:
        test_loader = DataLoader(make_dataset(test_records, data_root, False), batch_size=batch_size)
        metrics = evaluate(model, test_loader, device)
    onnx_path = output_dir / "classifier.onnx"
    # Use the serving precision for export and parity checks. CUDA convolution
    # may use TF32 even when AMP is disabled.
    example = next(iter(val_loader))[0][:1]
    model.cpu().eval()
    torch.onnx.export(model, example, onnx_path, input_names=["image"],
                      output_names=["logits"], opset_version=17, dynamo=False)
    max_abs_error = check_onnx_consistency(model, onnx_path, example)
    result = {"device": str(device), "amp": use_amp, "architecture": architecture,
              "dataset_mode": "pilot_illustrations" if pilot else "standard",
              "pretrained": pretrained and use_mobilenet, "seed": seed, "freeze_epochs": freeze_epochs,
              "epochs": len(history), "selected_epoch": best_epoch, "selected_val_loss": best_val,
              "history": history, "validation": validation, "test": metrics,
              "labels": list(LABELS), "onnx_max_abs_error": max_abs_error}
    (output_dir / "metrics.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def check_onnx_consistency(model, onnx_path: Path, example) -> float:
    """Compare one deterministic PyTorch/ONNX forward pass before delivery."""
    try:
        import numpy as np
        import onnxruntime as ort
        import torch
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("onnxruntime and NumPy are required for ONNX consistency validation") from exc
    model.eval()
    with torch.no_grad():
        torch_output = model(example).detach().cpu().numpy()
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    onnx_output = session.run(None, {session.get_inputs()[0].name: example.detach().cpu().numpy()})[0]
    max_abs_error = float(np.max(np.abs(torch_output - onnx_output)))
    if not np.allclose(torch_output, onnx_output, rtol=1e-3, atol=1e-4):
        raise RuntimeError(f"PyTorch and ONNX outputs differ beyond tolerance (max absolute error {max_abs_error:.6g})")
    return max_abs_error


def evaluate(model, loader, device) -> dict:
    import torch
    matrix = [[0 for _ in LABELS] for _ in LABELS]
    with torch.no_grad():
        for images, labels in loader:
            predictions = model(images.to(device)).argmax(dim=1).cpu()
            for actual, predicted in zip(labels, predictions):
                matrix[int(actual)][int(predicted)] += 1
    return classification_metrics(matrix)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--epochs", type=int, default=30)
    parser.add_argument("--seed", type=int, default=20261119)
    parser.add_argument("--batch-size", type=int, default=32)
    parser.add_argument("--learning-rate", type=float, default=1e-3)
    parser.add_argument("--patience", type=int, default=5)
    parser.add_argument("--architecture", choices=["mobilenet_v3_small", "baseline_cnn"], default="mobilenet_v3_small")
    parser.add_argument("--no-pretrained", action="store_true")
    parser.add_argument("--freeze-epochs", type=int, default=2)
    parser.add_argument("--defer-test", action="store_true", help="fit using a train/val-only development manifest")
    parser.add_argument("--pilot", action="store_true", help="train on a reviewed illustration pilot manifest")
    args = parser.parse_args()
    print(json.dumps(run_training(args.manifest, args.data_root, args.output_dir, args.epochs, args.seed,
                                  args.batch_size, args.learning_rate, args.patience,
                                  args.architecture, not args.no_pretrained, args.freeze_epochs,
                                  args.defer_test, args.pilot), indent=2))

