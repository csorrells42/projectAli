# Ali 1.0.3 Patch

Ali 1.0.3 fixes Sources & Topics transfer for installed machines by bundling the expanded public source catalog with the app instead of relying on Chris's local BootstrapData catalog.

## Fixes

- Embeds a 1000-entry curated source seed catalog in `Ali.Infrastructure`.
- Fresh installs and repair patches now merge that bundled seed into `%LOCALAPPDATA%\Ali\BootstrapData\Sources\curated_sources.json`.
- Existing valid user-added sources are preserved during repair.
- The seed includes the prior 311 public sources plus expanded weather, news, science, health, reliable reference, and official state/territory government sources.
- Chris's local Ali source catalog was synced to the same 1000-entry seed, with the previous local catalog backed up.

## Verification

- `dotnet build .\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false`
- `dotnet run --project .\tests\Ali.Tests\Ali.Tests.csproj --no-restore -p:UseSharedCompilation=false -nr:false`

## Field Notes

The previous 1.0.2 repair logic worked, but it merged only the small built-in starter catalog. The larger source library existed in Chris's local Ali data folder and was intentionally not copied as personal BootstrapData during install. This patch promotes that public source library into a bundled seed resource so installs and repairs can reproduce it.
