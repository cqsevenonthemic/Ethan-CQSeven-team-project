"""MobileNetV3-Small classifier and a lightweight CNN baseline."""

from __future__ import annotations

from planner_service.vision import LABELS


def build_model():
    try:
        import torch.nn as nn
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("PyTorch is required for model construction") from exc

    return nn.Sequential(
        nn.Conv2d(3, 24, kernel_size=3, padding=1), nn.ReLU(), nn.MaxPool2d(2),
        nn.Conv2d(24, 48, kernel_size=3, padding=1), nn.ReLU(), nn.MaxPool2d(2),
        nn.Conv2d(48, 96, kernel_size=3, padding=1), nn.ReLU(), nn.AdaptiveAvgPool2d((1, 1)),
        nn.Flatten(), nn.Linear(96, len(LABELS)),
    )


def build_mobilenet_v3_small(pretrained: bool = True, num_classes: int = len(LABELS)):
    """Build ImageNet-pretrained MobileNetV3-Small with a two-class head."""
    try:
        from torch import nn
        from torchvision.models import MobileNet_V3_Small_Weights, mobilenet_v3_small
    except ImportError as exc:  # pragma: no cover - optional P1 environment
        raise RuntimeError("torchvision is required for MobileNetV3-Small") from exc
    weights = MobileNet_V3_Small_Weights.DEFAULT if pretrained else None
    model = mobilenet_v3_small(weights=weights)
    in_features = model.classifier[-1].in_features
    model.classifier[-1] = nn.Linear(in_features, num_classes)
    return model


def freeze_backbone(model) -> None:
    """Freeze all MobileNet parameters except its classifier head."""
    model.features.requires_grad_(False)
    model.classifier.requires_grad_(True)


def unfreeze_last_blocks(model, blocks: int = 2) -> None:
    """Unfreeze the final feature blocks for a controlled fine-tuning phase."""
    if not 0 <= blocks <= len(model.features):
        raise ValueError("blocks must be within the backbone depth")
    for block in model.features[len(model.features) - blocks:]:
        block.requires_grad_(True)


def set_mobilenet_training_mode(model) -> None:
    """Train the unfrozen blocks while keeping frozen BatchNorm statistics fixed."""
    model.train()
    for block in model.features:
        if not any(parameter.requires_grad for parameter in block.parameters()):
            block.eval()
