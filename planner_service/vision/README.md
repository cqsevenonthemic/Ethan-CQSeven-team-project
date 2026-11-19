# P1 real-image classifier

This package is deliberately optional until the team supplies licensed images and a training environment. `dataset.py` requires a JSON manifest with `path`, `label`, `split`, `sha256`, `source`, and `license` for every image. Every split must contain both `quyuan` and `zhaojun`; duplicate content and hash mismatches fail validation.

Expected layout:

```text
data/quyuan/*.jpg
data/zhaojun/*.jpg
data/manifest.json
models/best.pt
models/classifier.onnx
```

After installing a separately reviewed, pinned vision environment (`torch`, `torchvision`, `Pillow`, `numpy`, `onnx`, and `onnxruntime`), run:

```bash
python -m planner_service.vision.train \
  --manifest data/manifest.json --data-root data \
  --output-dir models --epochs 30 --seed 20261119
```

The training command refuses incomplete or duplicated data and writes `best.pt`, `classifier.onnx`, and `metrics.json` containing test accuracy, a confusion matrix, and per-class precision/recall/F1. No result is valid until it has been run against a real manifest.
