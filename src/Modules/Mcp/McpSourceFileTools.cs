using System.Text;
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
    internal const string AppendToolName = "file_access_append";
    internal const string LocateSolutionToolName = "coding_locate_solution";

    public async Task<McpSourceFileResult> ReadAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var normalizedPath = fileName;
        try
        {
            normalizedPath = NormalizePath(AliCapabilityCatalog.FileReadName, fileName);
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
        var normalizedPath = fileName;
        try
        {
            normalizedPath = NormalizePath(AliCapabilityCatalog.FileWriteName, fileName);
            var exists = await fileAccess.Store.FileExistsAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (exists && !overwrite)
            {
                return new McpSourceFileResult(
                    false,
                    normalizedPath,
                    "The file already exists. Modify it with file_access_replace_lines or file_access_append instead of rewriting the whole file.");
            }

            if (exists
                && normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var existingContent = await fileAccess.Store.ReadAsync(
                        normalizedPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (WouldDowngradeModernDotNet(existingContent, content, out var current, out var proposed))
                {
                    return new McpSourceFileResult(
                        false,
                        normalizedPath,
                        $"Refused to downgrade TargetFramework from '{current}' to '{proposed}'. Use the newest installed .NET SDK unless the user explicitly requests an older compatibility target.");
                }
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

    public Task<McpSourceFileResult> WriteCoreAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken) =>
        WriteAsync(fileName, content, overwrite: false, cancellationToken);

    public async Task<McpSourceFileResult> ReplaceAsync(
        string fileName,
        string oldString,
        string newString,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        var normalizedPath = fileName;
        try
        {
            normalizedPath = NormalizePath(AliCapabilityCatalog.FileReplaceName, fileName);
            if (string.IsNullOrEmpty(oldString))
            {
                return new McpSourceFileResult(false, normalizedPath, "oldString must not be empty.");
            }

            var content = await fileAccess.Store.ReadAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                return new McpSourceFileResult(false, normalizedPath, "The file does not exist.");
            }

            var replacementCount = CountOccurrences(content, oldString);
            if (replacementCount == 0)
            {
                return new McpSourceFileResult(false, normalizedPath, "The exact oldText was not found; no file content changed.");
            }

            string updated;
            if (replaceAll)
            {
                updated = content.Replace(oldString, newString, StringComparison.Ordinal);
            }
            else
            {
                var index = content.IndexOf(oldString, StringComparison.Ordinal);
                updated = string.Concat(content.AsSpan(0, index), newString, content.AsSpan(index + oldString.Length));
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

    public async Task<McpSourceFileResult> ReplaceLinesAsync(
        string fileName,
        int startLine,
        int endLine,
        string newContent,
        CancellationToken cancellationToken)
    {
        var normalizedPath = fileName;
        try
        {
            normalizedPath = NormalizePath(AliCapabilityCatalog.FileReplaceLinesName, fileName);
            var content = await fileAccess.Store.ReadAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (content is null)
            {
                return new McpSourceFileResult(false, normalizedPath, "The file does not exist.");
            }

            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalizedContent = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var hadTrailingNewline = normalizedContent.EndsWith('\n');
            var lines = normalizedContent.Split('\n').ToList();
            if (hadTrailingNewline)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (startLine < 1 || endLine < startLine || endLine > lines.Count)
            {
                return new McpSourceFileResult(
                    false,
                    normalizedPath,
                    $"Line range {startLine}-{endLine} is outside the file's 1-{lines.Count} line range.");
            }

            var replacement = (newContent ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var replacementLines = replacement.Length == 0
                ? []
                : replacement.Split('\n').ToList();
            if (replacementLines.Count > 0 && replacement.EndsWith('\n'))
            {
                replacementLines.RemoveAt(replacementLines.Count - 1);
            }

            var replacedLineCount = endLine - startLine + 1;
            lines.RemoveRange(startLine - 1, replacedLineCount);
            lines.InsertRange(startLine - 1, replacementLines);
            var updated = string.Join(newline, lines);
            if (hadTrailingNewline)
            {
                updated += newline;
            }

            await fileAccess.Store.WriteAsync(normalizedPath, updated, cancellationToken)
                .ConfigureAwait(false);
            return new McpSourceFileResult(
                true,
                normalizedPath,
                $"Replaced lines {startLine}-{endLine} successfully.",
                ReplacementCount: replacedLineCount);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new McpSourceFileResult(false, normalizedPath, ex.Message);
        }
    }

    public async Task<McpSourceFileResult> AppendAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken)
    {
        var normalizedPath = fileName;
        try
        {
            normalizedPath = NormalizePath(AliCapabilityCatalog.FileWriteName, fileName);
            var resolved = fileAccess.ResolvePhysicalFilePath(normalizedPath);
            if (!File.Exists(resolved.PhysicalPath))
            {
                return new McpSourceFileResult(false, normalizedPath, "The file does not exist.");
            }

            if ((File.GetAttributes(resolved.PhysicalPath) & FileAttributes.ReparsePoint) != 0)
            {
                return new McpSourceFileResult(false, normalizedPath, "Refused to append through a reparse point.");
            }

            await using var stream = new FileStream(
                resolved.PhysicalPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync((content ?? string.Empty).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new McpSourceFileResult(
                true,
                normalizedPath,
                $"Appended {(content ?? string.Empty).Length} character(s) successfully.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new McpSourceFileResult(false, normalizedPath, ex.Message);
        }
    }

    public Task<McpSourceFileResult> LocateSolutionAsync(
        string? nameContains,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = new List<string>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };
            foreach (var mount in fileAccess.Mounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var physicalPath in Directory.EnumerateFiles(mount.RootPath, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extension = Path.GetExtension(physicalPath);
                    if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(mount.RootPath, physicalPath)
                        .Replace('\\', '/');
                    var workspacePath = $"{mount.Name}/{relative}";
                    if (string.IsNullOrWhiteSpace(nameContains)
                        || workspacePath.Contains(nameContains.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(workspacePath);
                    }
                }
            }

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(matches.Count == 0
                ? new McpSourceFileResult(false, string.Empty, "No matching solution or C# project was found in the configured workspace.")
                : new McpSourceFileResult(
                    true,
                    matches[0],
                    $"Found {matches.Count} workspace-formatted solution/project path(s).",
                    string.Join(Environment.NewLine, matches)));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Task.FromResult(new McpSourceFileResult(false, string.Empty, ex.Message));
        }
    }

    private string NormalizePath(string toolName, string fileName)
    {
        var modelPath = (fileName ?? string.Empty)
            .Trim()
            .Trim('"', '\'', '`')
            .Trim();
        if (AliCoreAssistantExecutionContext.IsActive)
        {
            modelPath = AliCoreAssistantExecutionContext.RebaseToActiveProject(modelPath);
        }
        var arguments = fileAccess.NormalizeProviderToolArguments(
            toolName,
            new Dictionary<string, object?> { ["fileName"] = modelPath });
        return arguments.TryGetValue("fileName", out var normalized)
            ? normalized?.ToString() ?? modelPath
            : modelPath;
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

    private static bool WouldDowngradeModernDotNet(
        string? existingContent,
        string proposedContent,
        out string current,
        out string proposed)
    {
        current = ReadTargetFramework(existingContent);
        proposed = ReadTargetFramework(proposedContent);
        return TryReadModernDotNetMajor(current, out var currentMajor)
               && TryReadModernDotNetMajor(proposed, out var proposedMajor)
               && proposedMajor < currentMajor;
    }

    private static string ReadTargetFramework(string? projectContent)
    {
        if (string.IsNullOrWhiteSpace(projectContent))
        {
            return string.Empty;
        }

        try
        {
            return System.Xml.Linq.XDocument.Parse(projectContent)
                       .Descendants()
                       .FirstOrDefault(element =>
                           element.Name.LocalName == "TargetFramework")
                       ?.Value.Trim()
                   ?? string.Empty;
        }
        catch (System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadModernDotNetMajor(string targetFramework, out int major)
    {
        major = 0;
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dot = targetFramework.IndexOf('.', 3);
        return dot > 3
               && int.TryParse(
                   targetFramework.AsSpan(3, dot - 3),
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out major);
    }

    private static bool IsExpected(Exception ex) =>
        ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException;
}
