# Ali 1.0.3 Patch

Ali 1.0.3 finishes the computer-management dashboard work and fixes the installed source/weather lookup gap before the next public install package.

## Fixes

- Embeds a 1000-entry curated source seed catalog in `Ali.Infrastructure`.
- Fresh installs and repair patches now merge that bundled seed into `%LOCALAPPDATA%\Ali\BootstrapData\Sources\curated_sources.json`.
- Existing valid user-added sources are preserved during repair.
- The seed includes the prior 311 public sources plus expanded weather, news, science, health, reliable reference, and official state/territory government sources.
- Chris's local Ali source catalog was synced to the same 1000-entry seed, with the previous local catalog backed up.
- Weather questions now route through approved source lookup so Ali can answer local weather requests instead of falling back to the bootstrap unknown response.
- Adds the Computer Maintenance dashboard with one-click buttons for status, repair, assistant setup, maintenance planning, diagnostics, and receipts.
- Adds guarded maintenance receipts under `%LOCALAPPDATA%\Ali\Receipts` instead of writing them into the main Ali data folder.
- Replaces duplicate dashboard shortcuts with process, port, Windows startup/service, disk cleanup, suspicious activity, install, and peripheral troubleshooting checks.
- Simplifies dashboard output so button results show concise user-facing rows such as `PID 1234 - Name dotnet`, component status, and clear next steps instead of command/tool capability text.
- Treats Visual Studio as neutral when no primary solution has been selected; that state no longer flags Visual Studio as bad.
- Recognizes dashboard-triggered tool integration checks so tool availability no longer reports as bad when the local integration is actually installed.

## Verification

- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- `dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-restore -p:UseSharedCompilation=false -nr:false`
- DevRun published to `%LOCALAPPDATA%\Ali\DevRun` and smoke-tested through the maintenance dashboard.

## Field Notes

The previous 1.0.2 repair logic worked, but it merged only the small built-in starter catalog. The larger source library existed in Chris's local Ali data folder and was intentionally not copied as personal BootstrapData during install. This patch promotes that public source library into a bundled seed resource so installs and repairs can reproduce it.

The maintenance dashboard is intentionally conservative: the buttons gather status, produce plans, or launch approved repair flows. They should not stop processes, delete files, alter services, install drivers, or make Windows changes without explicit owner approval.
