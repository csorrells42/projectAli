# Ali 1.0.5 Patch

Ali 1.0.5 is the final delivery patch after the 1.0.4 hardening pass.

## Fixes

- Bumps Ali app, installer payload, and Visual Studio companion metadata to `1.0.5`.
- Prevents current President/Vice President deterministic answers from hijacking unrelated follow-up questions.
- Keeps official President/Vice President answers deterministic only when the current user message explicitly asks for that officeholder.
- Improves weather location handling so explicit city/state wording and saved current-location memories feed source lookup.
- Adds an approved NWS point forecast source for Tullahoma, TN without making Tullahoma a hard-coded default location.
- Rewrites `what can you do` / abilities help in plain user-facing terms.

## Verification

- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- `dotnet test .\Ali.sln --no-build -p:UseSharedCompilation=false -nr:false`
- Setup and repair patch exported to the local delivery folders.

## Field Notes

For weather, Ali should answer when the user names a city/state or has saved a current-location memory. If no usable location is available, Ali should not pretend she knows where the user is.
