using System.Globalization;
using Ali.Modules.Capabilities;

namespace Ali.Modules.Mcp;

/// <summary>
/// Captures the persisted security inputs that a long-running stdio MCP host cannot
/// safely hot-reload. Any change invalidates its already-published invocation leases
/// and requires the host process to be restarted.
/// </summary>
internal static class McpPersistedSecurityBoundaryRevision
{
    internal const long MaximumInspectedFileBytes = 4 * 1024 * 1024;

    internal static string Capture(string dataRoot, string? hostConfigurationPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var normalizedRoot = Path.GetFullPath(dataRoot);
        var paths = new List<string>
        {
            CapabilityAvailabilitySettingsStore.GetSettingsPath(normalizedRoot),
            Path.Combine(normalizedRoot, "active-user-session.json"),
            McpServerSettingsStore.GetSettingsPath(normalizedRoot),
            Path.Combine(normalizedRoot, "Permissions", "agent-tool-permissions.json")
        };
        if (!string.IsNullOrWhiteSpace(hostConfigurationPath))
        {
            paths.Add(Path.GetFullPath(hostConfigurationPath));
        }

        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-mcp-persisted-security-boundary-v1");
        foreach (var path in paths
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            revision.Add(path);
            AddFileState(revision, path);
        }

        return revision.Finish();
    }

    private static void AddFileState(CapabilityRevisionBuilder revision, string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var beforeLength = stream.Length;
            if (beforeLength > MaximumInspectedFileBytes)
            {
                throw new IOException(
                    $"Security settings file '{Path.GetFileName(path)}' exceeds Ali's bounded inspection size.");
            }
            var beforeLastWriteUtc = File.GetLastWriteTimeUtc(path).Ticks;
            var contentHash = McpBoundedFileHash.CalculateExactExtent(
                stream,
                beforeLength,
                MaximumInspectedFileBytes,
                Path.GetFileName(path),
                "Security settings file");
            var afterLength = stream.Length;
            var afterLastWriteUtc = File.GetLastWriteTimeUtc(path).Ticks;
            if (beforeLength != afterLength || beforeLastWriteUtc != afterLastWriteUtc)
            {
                throw new IOException(
                    $"Security settings file '{Path.GetFileName(path)}' changed while it was being inspected.");
            }

            revision.Add("present");
            revision.Add(beforeLength.ToString(CultureInfo.InvariantCulture));
            revision.Add(beforeLastWriteUtc.ToString(CultureInfo.InvariantCulture));
            revision.Add(contentHash);
        }
        catch (FileNotFoundException)
        {
            revision.Add("missing");
        }
        catch (DirectoryNotFoundException)
        {
            revision.Add("missing");
        }
    }
}
