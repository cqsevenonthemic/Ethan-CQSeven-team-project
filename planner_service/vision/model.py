"""Small two-class CNN used for reproducible CPU-friendly training."""

from __future__ import annotations


def build_model():
    try:
        import torch.nn as nn
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("PyTorch is required for model construction") from exc

    return nn.Sequential(
        nn.Conv2d(3, 24, kernel_size=3, padding=1), nn.ReLU(), nn.MaxPool2d(2),
        nn.Conv2d(24, 48, kernel_size=3, padding=1), nn.ReLU(), nn.MaxPool2d(2),
        nn.Conv2d(48, 96, kernel_size=3, padding=1), nn.ReLU(), nn.AdaptiveAvgPool2d((1, 1)),
        nn.Flatten(), nn.Linear(96, 2),
    )

