using System.Security.Cryptography;
using System.Text.Json;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coordinator;

internal static class AliProductionTargetStateAdapters
{
    internal static TargetStateRegistry Create(AliWorkstationFileAccess fileAccess) => new(
    [
        new WorkstationFileTargetStateAdapter(fileAccess)
    ]);

    private sealed class WorkstationFileTargetStateAdapter(
        AliWorkstationFileAccess fileAccess) : IActionTargetStateAdapter
    {
        private readonly AliWorkstationFileAccess _fileAccess = fileAccess
            ?? throw new ArgumentNullException(nameof(fileAccess));

        public IReadOnlyCollection<string> ToolNames { get; } =
        [
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReadName,
            AliCapabilityCatalog.FileDeleteName,
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
            if (!File.Exists(path))
            {
                return "absent";
            }

            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
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
