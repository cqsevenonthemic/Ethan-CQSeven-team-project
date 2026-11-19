"""Optional P1 real-image classification components.

The production planner does not import this package unless a trained ONNX
model is explicitly configured.  Training dependencies are intentionally
optional because P0 does not require PyTorch or image libraries.
"""

LABELS = ("quyuan", "zhaojun")

