# Ali 1.0.4 Patch

Ali 1.0.4 is the final pre-delivery hardening patch. It keeps the 1.0.3 maintenance/source catalog work, then tightens high-visibility answers and removes unfinished screenshot UI from the delivered surface.

## Fixes

- Bumps Ali app, installer payload, and Visual Studio companion metadata to `1.0.4`.
- Weather and forecast wording route through approved weather sources and return current-day weather.
- Explicit multi-day forecast requests return current-day weather with a note that multi-day forecasts are being reworked for a later release.
- Current President and Vice President questions use deterministic White House-backed answers and avoid unrelated state-government source bleed-through.
- The main window disclaimer now says `This assistant can make mistakes. Check important info.`
- The unfinished screenshot attachment `+` button is hidden for this delivery.
- The Computer Maintenance and Programming dashboards remain split so source/library repair stays under maintenance and coding tools stay under programming.

## Verification

- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- `dotnet test .\Ali.sln --no-build -p:UseSharedCompilation=false -nr:false`
- Setup and repair patch exported to the local delivery folders.

## Field Notes

Weather delivery is intentionally conservative: Ali can answer the current local weather from approved weather sources, but multi-day forecast output is held back until it can be certified without freezing client machines.

Screenshot/image plumbing remains in the codebase for future work, but the unfinished add-screenshot button is not exposed in 1.0.4.
