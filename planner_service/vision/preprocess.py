"""The single preprocessing definition shared by training and inference."""

from __future__ import annotations

IMAGE_SIZE = (224, 224)
MEAN = (0.485, 0.456, 0.406)
STD = (0.229, 0.224, 0.225)


def build_transform(training: bool = False):
    try:
        from torchvision import transforms
    except ImportError as exc:  # pragma: no cover - depends on optional P1 env
        raise RuntimeError("torchvision is required for image preprocessing") from exc
    operations = [transforms.Resize(IMAGE_SIZE)]
    if training:
        operations.extend([transforms.RandomHorizontalFlip(), transforms.RandomRotation(8)])
    operations.extend([transforms.ToTensor(), transforms.Normalize(MEAN, STD)])
    return transforms.Compose(operations)

