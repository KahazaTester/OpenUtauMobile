Third-party notices for code adapted into this repository.

Avalonia Fluent theme source

- Source: https://github.com/AvaloniaUI/Avalonia/tree/12.1.0/src/Avalonia.Themes.Fluent
- Copyright (c) AvaloniaUI OÜ. MIT License.
- License: licenses/Avalonia.Themes.Fluent.MIT.txt
- Adapted XAML resides in OpenUtauMobile/Themes/OpenUtauMobile/{Controls,Accents,Strings,DensityStyles}.
- Changes: local resource URIs/private theme names, complete independent entry point,
  semantic palettes, shared button templates, input/state/cursor handling and metrics.
- The pinned commit, original SHA-256 hashes and template contracts are recorded in
  OpenUtauMobile/Themes/OpenUtauMobile/UPSTREAM.json.

OpenUtau.Core

- Source project: OpenUtau
- License: MIT License
- License file retained in repository: licenses/OpenUtau.MIT.txt
- Scope: the OpenUtau.Core project and the code that depends on it remain subject to the MIT terms from the upstream project.

HifiSampler

- Source project: hifisampler
- Source location used during development: hifisampler-main/hifisampler-main
- License: Apache License 2.0
- Files in this repository containing adapted logic:
  - OpenUtau.Plugin.Builtin/HifiSampler/HifiSamplerDsp.cs
  - OpenUtau.Plugin.Builtin/HifiSampler/HifiSamplerRenderer.cs
- Nature of changes: the original Python-based pipeline was adapted and rewritten for OpenUtauMobile2 in C#, integrated into the renderer architecture, and modified for the project's runtime, dependency, and rendering systems.

The full license text used for this dependency is included in licenses/HifiSampler.Apache-2.0.txt.

VoiSona .tsnvoice engine reference

- The C# inference pipeline, container/package parsing, language frontends, SINGER2 labels,
  HTS duration handling, context compiler, acoustic postprocess and MLSA DSP in
  OpenUtau.Core/TsnVoice/ are a managed port of its native C++ core and C# bridge,
  adapted to OpenUtauMobile's renderer/singer/phonemizer architecture and its
  existing ONNX Runtime distribution (no new native binaries).
- English LTS fallback model data (third_party/flite, CMU Flite) has not been ported;
  out-of-dictionary English words report an explicit unsupported error.
- Bundled runtime data under OpenUtau.Core/TsnVoice/Dictionaries/ (G2P dictionaries),
  OpenUtau.Core/TsnVoice/Voice/catalog.json, list.json and Voice/Singer/ portraits
  are copied from the reference project's resources/ for out-of-the-box use and remain
  subject to their respective upstream terms. Downloadable .tsnvoice packages themselves
  are not included; users must ensure their own licensed voice data.
