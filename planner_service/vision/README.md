# P1 real-image classifier

This package is optional for the planner service. The [dataset intake guide](DATASET.md) covers the external dataset layout, licensing metadata, grouped splits, audit, training and independent ONNX evaluation.

From the repository root, create `planner_service/.venv-vision` with Python 3.12 if it does not already exist. On Windows x64, install the pinned vision dependencies with the SHA-256-verified CPython 3.12 wheels for `torch==2.6.0+cu124` and `torchvision==0.21.0+cu124` from the official [PyTorch CUDA 12.4 torch index](https://download.pytorch.org/whl/cu124/torch/) and [torchvision index](https://download.pytorch.org/whl/cu124/torchvision/). In PowerShell, point `$torchWheel` and `$visionWheel` at the verified local wheel files.

```powershell
$visionPython = '.\planner_service\.venv-vision\Scripts\python.exe'
& $visionPython -m pip install -r planner_service/requirements-vision.txt $torchWheel $visionWheel
& $visionPython -m pip check
```

Run training from the repository root using this interpreter. The checkout is importable directly, so the training environment does not need an editable install of `maze-planner-service`. The service environment can load a configured model with NumPy, Pillow and ONNX Runtime. PyTorch, TorchVision and the `onnx` exporter package are used for training and export.

MobileNetV3-Small uses ImageNet weights by default. It trains the classifier head for two epochs by default, then unfreezes the final two feature blocks. Frozen blocks keep their BatchNorm statistics fixed. CUDA AMP is enabled automatically on CUDA. `--architecture baseline_cnn` selects the original three-layer CNN. Use `--no-pretrained` for a smoke test that does not fetch weights.

The training command refuses incomplete or duplicated data and writes `best.pt`, `classifier.onnx`, and `metrics.json`. Metrics include the maximum CPU FP32 PyTorch/ONNX Runtime logit difference on a validation sample, test accuracy, a confusion matrix, and per-class precision, recall, and F1. Classification metrics are meaningful only on the held-out real images. Set `PLANNER_ONNX_MODEL` to the exported ONNX path when running the service with the inference dependencies installed.

The [illustration pilot](DATASET.md#国风插画首轮试验) uses separately grouped character designs and records per-angle and prop-free results. Its metrics cover the pilot illustration domain. Use independent source material and Unity inputs for formal acceptance.
