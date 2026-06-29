# Ali 1.0.8 Cumulative Patch

Ali 1.0.8 is the cumulative customer repair patch for systems currently at 1.0.4 or newer. It includes the 1.0.5 delivery fixes, the 1.0.6 voice hotfix, the 1.0.7 source catalog expansion, and the latest programming and voice-output updates.

## Included Since 1.0.4

- Bumps Ali app, installer payload, and Visual Studio companion metadata to `1.0.8`.
- Keeps current President and Vice President answers deterministic only when the current user message explicitly asks for those officeholders.
- Prevents officeholder guards from hijacking unrelated follow-up questions.
- Improves weather location handling for explicit city/state requests and saved current-location memories.
- Keeps multi-day forecast requests limited to current-day weather while multi-day support is reworked.
- Restores KittenTTS voice sample playback with the installed local KittenTTS API and bundled voice resources.
- Expands the bundled curated source seed to `2000` entries across weather, sports, local/regional/national/international news, science, history, National Geographic-style knowledge, and military history.
- Adds project index, project dependency map, ownership map, coding context ownership, project-impact review, patch validation hints, safer edit planning, targeted test recommendations, and richer commit/release readiness.
- Adds a visible Voice / Mic speaker selector in the upper voice settings strip so the customer can choose where Ali talks from without changing voice selection.

## Customer Patch Contents

- `Ali.Setup.exe`
- `Ali.VoicePatch.zip`
- `Run Ali 1.0.8 Cumulative Patch.cmd`
- `README.txt`

The patch preserves user chats, memories, app settings, installed voice models, and user-added sources. It repairs the app payload, voice Python runtime/bridge scripts, bundled source catalog, Visual Studio companion payload, and current voice settings support.

## Verification

- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- `dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-build -p:UseSharedCompilation=false -nr:false`
- `tools\installer\build-ali-setup.ps1 -Configuration Release -Runtime win-x64 -BuildVoicePatch`

## Field Notes

Use this patch when the customer needs the post-1.0.4 fixes without a full voice-pack reinstall. After patching, restart Ali and test current weather with an explicit city/state, a KittenTTS voice sample, the Sources catalog, the Maintenance dashboard, and the new speaker selector.
