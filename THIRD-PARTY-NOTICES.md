# Third-Party Notices

The root MIT license covers original Project Ali source code contributed to
this repository. It does not replace or alter licenses for third-party source,
binary packages, native libraries, or model assets.

The authoritative runtime inventory is `runtime-assets.json`. It records each
downloaded model or runtime archive, its version, upstream source, license,
destination, size, and checksum. The published bundle includes the same
inventory as `THIRD-PARTY-RUNTIME-ASSETS.json`.

Notable runtime boundaries:

- `piper-tts` 1.6.0 is GPL-3.0-or-later. Its installed package includes the
  upstream `COPYING` file, and the published bundle includes the exact source
  distribution at `third-party-source/piper_tts-1.6.0.tar.gz`.
- The selected Piper voice model is separately licensed under MIT.
- Kitten TTS model assets are Apache-2.0.
- Faster Whisper model assets are MIT.
- The bundled FFmpeg build is the LGPL shared variant and retains its upstream
  license file.
- The bundled Qdrant 1.18.2 Windows service and Qdrant.Client 1.18.1 are
  licensed under Apache-2.0.
- Mem0 2.0.12 and the Qdrant Python client are licensed under Apache-2.0.
  Their pinned Python dependency tree is restored from
  `tools/runtime-assets/requirements-mem0.txt`; every dependency retains its
  own upstream license.
- TreeSitter.DotNet 1.3.0 and its packaged language grammars retain their
  upstream MIT and grammar-specific licenses.
- The bundled ripgrep 15.2.0 Windows executable is offered under MIT or the
  Unlicense; both upstream license files are included beside `rg.exe`.
- Python, MediaPipe, ONNX Runtime, Sherpa ONNX, native libraries, NuGet
  packages, Python wheels, and other dependencies retain their respective
  upstream licenses.

Anyone redistributing a Project Ali bundle must preserve these notices and
comply with every applicable third-party license. This notice is an engineering
inventory, not legal advice.
