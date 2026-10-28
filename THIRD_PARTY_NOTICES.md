# Third-party notices

## ClipBoardLine / Tendedero behavior contracts

The JSON files in `tests/Pegline.Core.Tests/Contracts/` are copied from
[orkunisitmak/clipboardline-desktop](https://github.com/orkunisitmak/clipboardline-desktop)
(`contracts/`). Pegline uses them as test fixtures to keep its shelf navigation, Library keyboard
behavior, text deduplication, clipboard admission and history cleanup equivalent. Pegline's
source code is an independent implementation; it uses neither the Tendedero/ClipBoardLine
name nor their icon.

```
MIT License

Copyright (c) 2026 Alejandro Buján

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

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
