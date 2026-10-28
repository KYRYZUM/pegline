# Third-party notices

Pegline, Copyright (c) 2026 Enes Sarı. All rights reserved. See LICENSE.

Pegline includes or downloads the components below. Each stays under its own license, and its
copyright notice is reproduced here as that license requires.

## Runtime dependencies

- Microsoft.Data.Sqlite / SQLitePCLRaw — MIT; SQLite — public domain.
- Velopack (installer and updates), Copyright (c) Velopack Ltd. — MIT License.

## Apple MobileCLIP-S2 (visual search)

Pegline's offline visual search runs Apple's MobileCLIP-S2 image and text encoders, exported to ONNX
from the unmodified [apple/ml-mobileclip](https://github.com/apple/ml-mobileclip) source
(`341ef058802f0e4e5ab13c02f0cb32a3a94e367b`) and the
[apple/MobileCLIP-S2](https://huggingface.co/apple/MobileCLIP-S2) checkpoint
(`6da8f9f1c7fa5ae89385ef86bfdee297d105e6f6`), as pinned in
`src/Pegline.Core/Search/search-model-config.json`. The model files are not stored in this
repository; `scripts/prepare-search-models.ps1` fetches the pinned export and verifies every file.
Apple's licenses (`LICENSE-Apple-code`, `LICENSE-MobileCLIP-S2`) and acknowledgements
(`ACKNOWLEDGEMENTS-MobileCLIP`) ship unchanged next to the models in every build that includes them.
The CLIP tokenizer vocabulary and merges come from OpenAI CLIP (MIT License).

## ONNX Runtime

Microsoft.ML.OnnxRuntime, Copyright (c) Microsoft Corporation, MIT License.
