using System.Security.Cryptography;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynProviderIdentity(
    string StableIdentity,
    string AssemblyVersion,
    string AssemblyFileSha256)
{
    private const long MaximumProviderAssemblyBytes = 1024L * 1024 * 1024;
    internal static AliRoslynProviderIdentity Create(object provider, string providerKind)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        var type = provider.GetType();
        var assembly = type.Assembly;
        var assemblyName = assembly.GetName();
        var concreteType = type.FullName
            ?? throw new InvalidOperationException("A trusted Roslyn provider has no concrete type identity.");
        var simpleAssemblyName = assemblyName.Name
            ?? throw new InvalidOperationException("A trusted Roslyn provider assembly has no simple name.");
        var assemblyVersion = assemblyName.Version?.ToString()
            ?? throw new InvalidOperationException("A trusted Roslyn provider assembly has no version.");
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidOperationException(
                "A trusted Roslyn provider must come from a physical assembly file.");
        }

        var fullPath = Path.GetFullPath(location);
        using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            writeThrough: false,
            "A trusted Roslyn provider assembly must be a regular no-follow file.");
        if (stream.Length is <= 0 or > MaximumProviderAssemblyBytes)
        {
            throw new InvalidOperationException(
                "A trusted Roslyn provider assembly file is missing or outside the bounded size policy.");
        }

        var assemblySha256 = Convert.ToHexString(SHA256.HashData(stream));
        return new(
            providerKind + ":" + concreteType + "," + simpleAssemblyName,
            assemblyVersion,
            assemblySha256);
    }
}
