using Microsoft.Build.Locator;

namespace Ali.Modules.Coding;

/// <summary>
/// Registers the locally installed .NET SDK's MSBuild toolset before any MSBuild API
/// type is loaded. Microsoft.Build assemblies are compile-only dependencies and are
/// resolved from this registered SDK at runtime.
/// </summary>
internal static class AliMsBuildRuntime
{
    private static readonly object RegistrationLock = new();
    private static string? _registeredPath;

    public static string EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                var instance = MSBuildLocator.RegisterDefaults();
                _registeredPath = instance.MSBuildPath;
            }

            return _registeredPath ?? "Registered local .NET SDK";
        }
    }
}
