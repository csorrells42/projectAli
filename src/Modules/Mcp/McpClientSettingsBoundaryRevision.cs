using System.Globalization;
using Ali.Modules.Capabilities;

namespace Ali.Modules.Mcp;

/// <summary>
/// Captures the exact persisted incoming-MCP configuration used to create a
/// turn-scoped client session. Missing settings are a stable disabled boundary;
/// unreadable settings fail closed through the caller.
/// </summary>
internal static class McpClientSettingsBoundaryRevision
{
    internal static string Capture(
        string path,
        long maximumBytes = McpClientSettingsStore.MaximumSettingsFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.GetFullPath(path);
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-incoming-mcp-settings-boundary-v1");
        revision.Add(normalizedPath);
        try
        {
            using var stream = new FileStream(
                normalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var beforeLength = stream.Length;
            if (beforeLength > maximumBytes)
            {
                throw new IOException(
                    $"Incoming MCP settings file '{Path.GetFileName(normalizedPath)}' exceeds Ali's bounded settings size.");
            }
            var beforeLastWriteUtc = File.GetLastWriteTimeUtc(normalizedPath).Ticks;
            var contentHash = McpBoundedFileHash.CalculateExactExtent(
                stream,
                beforeLength,
                maximumBytes,
                Path.GetFileName(normalizedPath),
                "Incoming MCP settings file");
            var afterLength = stream.Length;
            var afterLastWriteUtc = File.GetLastWriteTimeUtc(normalizedPath).Ticks;
            if (beforeLength != afterLength || beforeLastWriteUtc != afterLastWriteUtc)
            {
                throw new IOException(
                    $"Incoming MCP settings file '{Path.GetFileName(normalizedPath)}' changed while it was being inspected.");
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

        return revision.Finish();
    }
}
