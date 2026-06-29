# Ali 1.0.6 Patch

Ali 1.0.6 is a small voice hotfix on top of the 1.0.5 delivery patch.

## Fixes

- Bumps Ali app, installer payload, and Visual Studio companion metadata to `1.0.6`.
- Fixes the local KittenTTS bridge for the installed `KittenTTS(model_name, cache_dir)` API.
- Keeps compatibility with older direct-ONNX KittenTTS constructor styles.
- Restores the `Hear Sample` flow for KittenTTS voices such as Luna.

## Verification

- Generated a WAV through `tools\voice\local_kitten_tts.py` using the installed DevRun local voice Python runtime and bundled KittenTTS cache.
- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- `dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-build -p:UseSharedCompilation=false -nr:false`
- Setup and repair patch exported to the local delivery folders.
