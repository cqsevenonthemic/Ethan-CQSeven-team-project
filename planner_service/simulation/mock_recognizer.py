"""Explicit test substitute. Not a learned classifier and never used by the API."""

from hashlib import sha256


# Opaque mock image payloads. Only this test recognizer maps pixels to labels.
MOCK_IMAGES = {"quyuan": b"P0_TEST_IMAGE_A", "zhaojun": b"P0_TEST_IMAGE_B"}


def recognize_mock(image: bytes, *, invert: bool = False) -> dict:
    labels = {sha256(value).hexdigest(): key for key, value in MOCK_IMAGES.items()}
    image_id = sha256(image).hexdigest()
    role = labels[image_id]
    if invert:
        role = "zhaojun" if role == "quyuan" else "quyuan"
    return {"pred_role": role, "confidence": 0.7 if invert else 0.99,
            "recognition_id": "mock-" + image_id[:16], "recognition_source": "mock"}
