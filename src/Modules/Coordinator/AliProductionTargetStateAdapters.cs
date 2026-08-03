using System.Security.Cryptography;
using System.Text.Json;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coordinator;

internal static class AliProductionTargetStateAdapters
{
    internal static TargetStateRegistry Create(
        AliWorkstationFileAccess fileAccess,
        IEnumerable<IActionTargetStateAdapter>? additionalAdapters = null)
    {
        ArgumentNullException.ThrowIfNull(fileAccess);
        var adapters = new List<IActionTargetStateAdapter>
        {
            new WorkstationFileTargetStateAdapter(fileAccess)
        };
        if (additionalAdapters is not null)
        {
            adapters.AddRange(additionalAdapters);
        }

        return new TargetStateRegistry(adapters);
    }

    private sealed class WorkstationFileTargetStateAdapter(
        AliWorkstationFileAccess fileAccess) : IActionTargetStateAdapter
    {
        private readonly AliWorkstationFileAccess _fileAccess = fileAccess
            ?? throw new ArgumentNullException(nameof(fileAccess));

        public IReadOnlyCollection<string> ToolNames { get; } =
        [
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReadName,
            AliCapabilityCatalog.FileReplaceName,
            AliCapabilityCatalog.FileReplaceLinesName
        ];

        public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
        {
            var virtualPath = RequireString(arguments, "fileName");
            var resolved = _fileAccess.ResolvePhysicalFilePath(virtualPath);
            var version = CaptureExactFileVersion(resolved.PhysicalPath);
            var versions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["file:" + virtualPath.Replace('\\', '/')] = version
            };
            return new TargetStateSnapshot(
                versions,
                versions,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        private static string CaptureExactFileVersion(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                var parent = Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidDataException(
                        "The workstation target has no parent directory.");
                WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
                    parent,
                    "The workstation target parent is not a regular local directory.");
                try
                {
                    var attributes = File.GetAttributes(fullPath);
                    if ((attributes & (FileAttributes.ReparsePoint
                                       | FileAttributes.Directory
                                       | FileAttributes.Device)) != 0)
                    {
                        throw new InvalidDataException(
                            "The workstation target is a reparse point or non-regular entry.");
                    }
                }
                catch (FileNotFoundException)
                {
                }
                catch (DirectoryNotFoundException)
                {
                }
                if (!File.Exists(fullPath))
                {
                    return "absent";
                }

                using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    writeThrough: false,
                    "The workstation target is not a regular local file.");
                var hash = SHA256.HashData(stream);
                try
                {
                    return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "unavailable:" + ex.GetType().Name;
            }
        }

        private static string RequireString(JsonElement arguments, string propertyName)
        {
            if (arguments.ValueKind != JsonValueKind.Object
                || !arguments.TryGetProperty(propertyName, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InvalidDataException(
                    $"The exact '{propertyName}' target is unavailable for target-state capture.");
            }

            return value.GetString()!.Trim();
        }
    }
}
