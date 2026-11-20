"""ONNX inference with the same decode and normalization contract as training."""

from __future__ import annotations

import base64
import hashlib
import io
import os
from functools import lru_cache
import uuid

from planner_service.vision import LABELS
from planner_service.vision.preprocess import IMAGE_SIZE, MEAN, STD


class ModelUnavailable(RuntimeError):
    pass


class VisionRecognizer:
    def __init__(self, model_path: str | None = None):
        self.model_path = model_path or os.getenv("PLANNER_ONNX_MODEL")
        if not self.model_path or not os.path.isfile(self.model_path):
            raise ModelUnavailable("A trained ONNX model is not configured")
        try:
            import onnxruntime as ort
        except ImportError as exc:
            raise ModelUnavailable("onnxruntime is not installed") from exc
        try:
            self.session = ort.InferenceSession(self.model_path, providers=["CPUExecutionProvider"])
            inputs, outputs = self.session.get_inputs(), self.session.get_outputs()
            if len(inputs) != 1 or len(outputs) != 1:
                raise ValueError("Expected one input and one output")
            self.input_name = inputs[0].name
        except Exception as exc:
            raise ModelUnavailable("The configured ONNX model could not be loaded") from exc

    def predict_base64(self, image_base64: str) -> dict:
        try:
            from PIL import Image
            import numpy as np
        except ImportError as exc:
            raise ModelUnavailable("Pillow and NumPy are required for inference") from exc
        try:
            raw = base64.b64decode(image_base64, validate=True)
            with Image.open(io.BytesIO(raw)) as image:
                if image.format not in ("PNG", "JPEG"):
                    raise ValueError("image must decode as PNG or JPEG")
                if image.width * image.height > 16_000_000:
                    raise ValueError("image dimensions exceed the safe limit")
                image = image.convert("RGB").resize(IMAGE_SIZE, Image.Resampling.BILINEAR)
                array = np.asarray(image, dtype=np.float32) / 255.0
        except Exception as exc:
            raise ValueError("image could not be decoded") from exc
        array = (array - np.asarray(MEAN, dtype=np.float32)) / np.asarray(STD, dtype=np.float32)
        batch = np.transpose(array, (2, 0, 1))[None, ...]
        logits = self.session.run(None, {self.input_name: batch})[0]
        if logits.shape != (1, len(LABELS)) or not np.isfinite(logits).all():
            raise ModelUnavailable("The configured model did not return finite two-class logits")
        logits = logits[0]
        probabilities = np.exp(logits - np.max(logits))
        probabilities /= probabilities.sum()
        index = int(np.argmax(probabilities))
        return {"pred_role": LABELS[index], "confidence": float(probabilities[index]),
                "recognition_id": f"onnx-{uuid.uuid4().hex}", "recognition_source": "onnx",
                "image_sha256": hashlib.sha256(raw).hexdigest()}


@lru_cache(maxsize=1)
def recognizer_for(model_path: str) -> VisionRecognizer:
    return VisionRecognizer(model_path)
