# Real image dataset intake

The repository currently contains **0** licensed Qu Yuan or Wang Zhaojun training images and therefore has no train/validation/test counts and no trained model. Do not create placeholder images or report accuracy until the intake is complete.

Use this layout outside Git history when possible:

```text
data/
  quyuan/
  zhaojun/
  manifest.json
```

Each manifest row must include:

```json
{
  "path": "quyuan/example.jpg",
  "label": "quyuan",
  "split": "train",
  "sha256": "...",
  "source": "stable source URL or collection identifier",
  "license": "license or permission record"
}
```

The audit rejects missing/corrupt files, unsupported labels, hash mismatches, exact duplicates, perceptual near-duplicates, and a source collection appearing in more than one split. Keep all images from one source or near-duplicate group in a single split. Run it before training:

```bash
python -m planner_service.vision.audit_dataset \
  --manifest data/manifest.json --data-root data
```

Only the training split receives random augmentation. Validation and test images use deterministic resize and normalization.

