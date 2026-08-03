using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;

namespace Ali.Modules.WorkstationFiles;

/// <summary>
/// Parses the exact Microsoft Agent Framework file-access mutation schemas and computes the
/// exact text that the provider will pass to <see cref="Microsoft.Agents.AI.AgentFileStore.WriteAsync"/>.
/// This class does not read or write the workstation.
/// </summary>
internal sealed record AliFrameworkFileMutationPlan(
    string ToolName,
    string FileName,
    string PostContent,
    bool RequiresExistingFile,
    bool AllowsExistingFile)
{
    internal static string ReadExactFileName(string toolName, JsonElement arguments)
    {
        if (toolName is not (
                AliCapabilityCatalog.FileWriteName
                or AliCapabilityCatalog.FileReplaceName
                or AliCapabilityCatalog.FileReplaceLinesName))
        {
            throw new InvalidDataException(
                "Only the three registered Agent Framework text-file mutations are supported.");
        }
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The exact file mutation arguments must be an object.");
        }
        return RequireString(arguments, "fileName", allowEmpty: false).Trim();
    }

    internal static AliFrameworkFileMutationPlan Create(
        string toolName,
        JsonElement arguments,
        string? currentContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return toolName switch
        {
            AliCapabilityCatalog.FileWriteName => ParseWrite(arguments),
            AliCapabilityCatalog.FileReplaceName => ParseReplace(arguments, currentContent),
            AliCapabilityCatalog.FileReplaceLinesName => ParseReplaceLines(arguments, currentContent),
            _ => throw new InvalidDataException(
                "Only the three registered Agent Framework text-file mutations are supported.")
        };
    }

    private static AliFrameworkFileMutationPlan ParseWrite(JsonElement arguments)
    {
        RequireExactObject(
            arguments,
            required: ["fileName", "content"],
            optional: ["overwrite"],
            "file_access_write arguments");
        var fileName = RequireString(arguments, "fileName", allowEmpty: false).Trim();
        var content = RequireString(arguments, "content", allowEmpty: true);
        var overwrite = ReadOptionalBoolean(arguments, "overwrite", defaultValue: false);
        return new(
            AliCapabilityCatalog.FileWriteName,
            fileName,
            content,
            RequiresExistingFile: false,
            AllowsExistingFile: overwrite);
    }

    private static AliFrameworkFileMutationPlan ParseReplace(
        JsonElement arguments,
        string? currentContent)
    {
        RequireExactObject(
            arguments,
            required: ["fileName", "oldString", "newString"],
            optional: ["replaceAll"],
            "file_access_replace arguments");
        var fileName = RequireString(arguments, "fileName", allowEmpty: false).Trim();
        var oldString = RequireString(arguments, "oldString", allowEmpty: true);
        var newString = RequireString(arguments, "newString", allowEmpty: true);
        var replaceAll = ReadOptionalBoolean(arguments, "replaceAll", defaultValue: false);
        if (oldString.Length == 0)
        {
            throw new InvalidDataException("The exact 'oldString' value must not be empty.");
        }
        if (currentContent is null)
        {
            throw new FileNotFoundException("The Agent Framework replace target does not exist.", fileName);
        }

        var occurrences = CountOccurrences(currentContent, oldString);
        if (occurrences == 0)
        {
            throw new InvalidDataException("The exact 'oldString' value was not found.");
        }
        if (!replaceAll && occurrences != 1)
        {
            throw new InvalidDataException(
                "The exact 'oldString' value occurs more than once; replaceAll is required.");
        }

        var postContent = replaceAll
            ? currentContent.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceSingle(currentContent, oldString, newString);
        return new(
            AliCapabilityCatalog.FileReplaceName,
            fileName,
            postContent,
            RequiresExistingFile: true,
            AllowsExistingFile: true);
    }

    private static AliFrameworkFileMutationPlan ParseReplaceLines(
        JsonElement arguments,
        string? currentContent)
    {
        RequireExactObject(
            arguments,
            required: ["fileName", "edits"],
            optional: [],
            "file_access_replace_lines arguments");
        var fileName = RequireString(arguments, "fileName", allowEmpty: false).Trim();
        if (currentContent is null)
        {
            throw new FileNotFoundException(
                "The Agent Framework line-replacement target does not exist.",
                fileName);
        }
        if (!arguments.TryGetProperty("edits", out var editsElement)
            || editsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The exact 'edits' argument must be an array.");
        }

        var edits = new List<LineEdit>();
        foreach (var editElement in editsElement.EnumerateArray())
        {
            RequireExactObject(
                editElement,
                required: ["line_number", "new_line"],
                optional: [],
                "file_access_replace_lines edit");
            if (!editElement.TryGetProperty("line_number", out var lineNumberElement)
                || lineNumberElement.ValueKind != JsonValueKind.Number
                || !lineNumberElement.TryGetInt32(out var lineNumber))
            {
                throw new InvalidDataException(
                    "Each exact 'line_number' value must be a 32-bit integer.");
            }
            edits.Add(new LineEdit(
                lineNumber,
                RequireString(editElement, "new_line", allowEmpty: true)));
        }
        if (edits.Count == 0)
        {
            throw new InvalidDataException("At least one exact line edit is required.");
        }

        var lines = SplitLinesKeepEnds(currentContent);
        var replacements = new Dictionary<int, string>();
        foreach (var edit in edits)
        {
            if (edit.LineNumber <= 0 || edit.LineNumber > lines.Count)
            {
                throw new InvalidDataException(
                    $"Line {edit.LineNumber} is outside the exact 1-based file line range.");
            }
            if (!replacements.TryAdd(edit.LineNumber, edit.NewLine))
            {
                throw new InvalidDataException(
                    $"Line {edit.LineNumber} is targeted more than once.");
            }
        }

        var builder = new StringBuilder(currentContent.Length);
        for (var index = 0; index < lines.Count; index++)
        {
            builder.Append(replacements.TryGetValue(index + 1, out var replacement)
                ? replacement
                : lines[index]);
        }
        return new(
            AliCapabilityCatalog.FileReplaceLinesName,
            fileName,
            builder.ToString(),
            RequiresExistingFile: true,
            AllowsExistingFile: true);
    }

    private static IReadOnlyList<string> SplitLinesKeepEnds(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character is not ('\r' or '\n'))
            {
                continue;
            }

            var end = index + 1;
            if (character == '\r' && end < content.Length && content[end] == '\n')
            {
                end++;
            }
            lines.Add(content[start..end]);
            start = end;
            index = end - 1;
        }
        if (start < content.Length)
        {
            lines.Add(content[start..]);
        }
        return lines;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var offset = 0;
        while (offset <= content.Length - value.Length)
        {
            var index = content.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }
            count++;
            offset = index + value.Length;
        }
        return count;
    }

    private static string ReplaceSingle(string content, string oldString, string newString)
    {
        var index = content.IndexOf(oldString, StringComparison.Ordinal);
        return string.Concat(
            content.AsSpan(0, index),
            newString,
            content.AsSpan(index + oldString.Length));
    }

    private static string RequireString(
        JsonElement arguments,
        string propertyName,
        bool allowEmpty)
    {
        if (!arguments.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' argument must be a string"
                + (allowEmpty ? "." : " containing a value."));
        }
        return text;
    }

    private static bool ReadOptionalBoolean(
        JsonElement arguments,
        string propertyName,
        bool defaultValue)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"The exact optional '{propertyName}' argument must be a Boolean.")
        };
    }

    private static void RequireExactObject(
        JsonElement value,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional,
        string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The exact {description} must be an object.");
        }

        var allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"The exact {description} contains unsupported property '{property.Name}'.");
            }
            if (!present.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"The exact {description} duplicates property '{property.Name}'.");
            }
        }
        foreach (var propertyName in required)
        {
            if (!present.Contains(propertyName))
            {
                throw new InvalidDataException(
                    $"The exact {description} is missing required property '{propertyName}'.");
            }
        }
    }

    private sealed record LineEdit(int LineNumber, string NewLine);
}
