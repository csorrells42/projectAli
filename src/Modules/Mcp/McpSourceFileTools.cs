using Ali.Modules.Coordinator;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Mcp;

public sealed record McpSourceFileResult(
    bool Success,
    string FileName,
    string Message,
    string? Content = null,
    int ReplacementCount = 0);

/// <summary>
/// Exposes Ali's existing audited workstation file store to MCP clients without
/// requiring them to encode file edits as shell commands.
/// </summary>
internal sealed class McpSourceFileTools(AliWorkstationFileAccess fileAccess)
{
    public async Task<McpSourceFileResult> ReadAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(AliCapabilityCatalog.FileReadName, fileName);
        try
        {
            var content = await fileAccess.Store.ReadAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            return content is null
                ? new McpSourceFileResult(false, normalizedPath, "The file does not exist.")
                : new McpSourceFileResult(true, normalizedPath, "The file was read successfully.", content);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new McpSourceFileResult(false, normalizedPath, ex.Message);
        }
    }

    public async Task<McpSourceFileResult> WriteAsync(
        string fileName,
        string content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(AliCapabilityCatalog.FileWriteName, fileName);
        try
        {
            var exists = await fileAccess.Store.FileExistsAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (exists && !overwrite)
            {
                return new McpSourceFileResult(
                    false,
                    normalizedPath,
                    "The file already exists. Set overwrite=true only after the user approves replacing it.");
            }

            await fileAccess.Store.WriteAsync(normalizedPath, content, cancellationToken)
                .ConfigureAwait(false);
            return new McpSourceFileResult(
                true,
                normalizedPath,
                exists ? "The existing file was overwritten successfully." : "The file was created successfully.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new McpSourceFileResult(false, normalizedPath, ex.Message);
        }
    }

    public async Task<McpSourceFileResult> ReplaceAsync(
        string fileName,
        string oldText,
        string newText,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(AliCapabilityCatalog.FileReplaceName, fileName);
        if (string.IsNullOrEmpty(oldText))
        {
            return new McpSourceFileResult(false, normalizedPath, "oldText must not be empty.");
        }

        try
        {
            var content = await fileAccess.Store.ReadAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                return new McpSourceFileResult(false, normalizedPath, "The file does not exist.");
            }

            var replacementCount = CountOccurrences(content, oldText);
            if (replacementCount == 0)
            {
                return new McpSourceFileResult(false, normalizedPath, "The exact oldText was not found; no file content changed.");
            }

            string updated;
            if (replaceAll)
            {
                updated = content.Replace(oldText, newText, StringComparison.Ordinal);
            }
            else
            {
                var index = content.IndexOf(oldText, StringComparison.Ordinal);
                updated = string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
                replacementCount = 1;
            }

            await fileAccess.Store.WriteAsync(normalizedPath, updated, cancellationToken)
                .ConfigureAwait(false);
            return new McpSourceFileResult(
                true,
                normalizedPath,
                $"Replaced {replacementCount} exact occurrence(s) successfully.",
                ReplacementCount: replacementCount);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new McpSourceFileResult(false, normalizedPath, ex.Message);
        }
    }

    private string NormalizePath(string toolName, string fileName)
    {
        var arguments = fileAccess.NormalizeProviderToolArguments(
            toolName,
            new Dictionary<string, object?> { ["fileName"] = fileName });
        return arguments.TryGetValue("fileName", out var normalized)
            ? normalized?.ToString() ?? fileName
            : fileName;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = content.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static bool IsExpected(Exception ex) =>
        ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException;
}
