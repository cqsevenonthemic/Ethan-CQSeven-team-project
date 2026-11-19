"""Train, evaluate, save best weights and export the P1 classifier."""

from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

from planner_service.vision.dataset import ImageRecord, load_manifest, validate_manifest
from planner_service.vision.model import build_model
from planner_service.vision.preprocess import build_transform

LABEL_TO_INDEX = {"quyuan": 0, "zhaojun": 1}


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
                 batch_size: int = 32, learning_rate: float = 1e-3, patience: int = 5) -> dict:
    try:
        import torch
        from torch import nn
        from torch.utils.data import DataLoader
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("PyTorch is required for training") from exc
    seed_everything(seed)
    records = load_manifest(manifest_path)
    errors = validate_manifest(records, data_root)
    if errors:
        raise ValueError("manifest validation failed:\n" + "\n".join(errors))
    train_records = [item for item in records if item.split == "train"]
    val_records = [item for item in records if item.split == "val"]
    test_records = [item for item in records if item.split == "test"]
    train_loader = DataLoader(make_dataset(train_records, data_root, True), batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(make_dataset(val_records, data_root, False), batch_size=batch_size)
    test_loader = DataLoader(make_dataset(test_records, data_root, False), batch_size=batch_size)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = build_model().to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=learning_rate, weight_decay=1e-4)
    criterion = nn.CrossEntropyLoss()
    best_val = float("inf")
    stale = 0
    history = []
    output_dir.mkdir(parents=True, exist_ok=True)
    for epoch in range(1, epochs + 1):
        model.train()
        train_loss = 0.0
        for images, labels in train_loader:
            optimizer.zero_grad()
            loss = criterion(model(images.to(device)), labels.to(device))
            loss.backward()
            optimizer.step()
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
            torch.save({"state_dict": model.state_dict(), "labels": list(LABEL_TO_INDEX), "seed": seed}, output_dir / "best.pt")
        else:
            stale += 1
            if stale >= patience:
                break
    checkpoint = torch.load(output_dir / "best.pt", map_location=device, weights_only=True)
    model.load_state_dict(checkpoint["state_dict"])
    metrics = evaluate(model, test_loader, device)
    onnx_path = output_dir / "classifier.onnx"
    example = torch.zeros(1, 3, 224, 224, device=device)
    torch.onnx.export(model, example, onnx_path, input_names=["image"], output_names=["logits"], opset_version=17)
    check_onnx_consistency(model, onnx_path, example)
    result = {"device": str(device), "epochs": len(history), "history": history, "test": metrics, "labels": list(LABEL_TO_INDEX)}
    (output_dir / "metrics.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def check_onnx_consistency(model, onnx_path: Path, example) -> None:
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
    if not np.allclose(torch_output, onnx_output, rtol=1e-3, atol=1e-4):
        raise RuntimeError("PyTorch and ONNX outputs differ beyond tolerance")


def evaluate(model, loader, device) -> dict:
    import torch
    matrix = [[0, 0], [0, 0]]
    with torch.no_grad():
        for images, labels in loader:
            predictions = model(images.to(device)).argmax(dim=1).cpu()
            for actual, predicted in zip(labels, predictions):
                matrix[int(actual)][int(predicted)] += 1
    per_class = {}
    for index, label in enumerate(LABEL_TO_INDEX):
        tp = matrix[index][index]
        fp = sum(row[index] for row in matrix) - tp
        fn = sum(matrix[index]) - tp
        precision = tp / (tp + fp) if tp + fp else 0.0
        recall = tp / (tp + fn) if tp + fn else 0.0
        per_class[label] = {"precision": precision, "recall": recall, "f1": (2 * precision * recall / (precision + recall)) if precision + recall else 0.0}
    total = sum(map(sum, matrix))
    return {"confusion_matrix": matrix, "accuracy": sum(matrix[i][i] for i in range(2)) / total if total else 0.0, "per_class": per_class}


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
    args = parser.parse_args()
    print(json.dumps(run_training(args.manifest, args.data_root, args.output_dir, args.epochs, args.seed,
                                  args.batch_size, args.learning_rate, args.patience), indent=2))

