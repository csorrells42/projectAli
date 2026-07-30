# Third-Party Notices

## Node.js

- Version: 24.18.0
- License: MIT
- Source: https://github.com/nodejs/node/tree/v24.18.0
- Used as Ali's portable JavaScript and web-project runtime. The complete Node.js distribution includes its own dependency notices.

## Eclipse Temurin OpenJDK

- Version: 21.0.11+10
- License: GPL-2.0 with Classpath Exception
- Source: https://github.com/adoptium/temurin21-binaries/releases/tag/jdk-21.0.11%2B10
- Used as Ali's portable Java compiler, runtime, debugger, and diagnostic toolchain. The distribution includes its own legal notices.

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
- Aider Chat 0.86.2 is licensed under Apache-2.0. Its pinned Python dependency
  tree is restored from `tools/runtime-assets/requirements-aider.txt`; every
  dependency retains its own upstream license.
- OpenHands CLI 1.16.0 is licensed under MIT and is installed into the user's
  selected WSL distribution rather than copied into the Project Ali bundle.
- TreeSitter.DotNet 1.3.0 and its packaged language grammars retain their
  upstream MIT and grammar-specific licenses.
- Microsoft Roslyn 5.6.0, MSBuild API packages 18.3.3, and MSBuild Locator
  1.11.2 retain their upstream MIT licenses. MSBuild itself is resolved from
  the locally installed .NET SDK and is not redistributed by Project Ali.
- Microsoft.Diagnostics.NETCore.Client 0.2.661903 is MIT licensed and provides
  Ali's managed EventPipe trace capture boundary.
- The bundled Samsung netcoredbg 3.2.0-1092 CLR debugger is licensed under
  MIT and is controlled through its standard Debug Adapter Protocol endpoint.
- The bundled ripgrep 15.2.0 Windows executable is offered under MIT or the
  Unlicense; both upstream license files are included beside `rg.exe`.
- Python, MediaPipe, ONNX Runtime, Sherpa ONNX, native libraries, NuGet
  packages, Python wheels, and other dependencies retain their respective
  upstream licenses.

Anyone redistributing a Project Ali bundle must preserve these notices and
comply with every applicable third-party license. This notice is an engineering
inventory, not legal advice.
