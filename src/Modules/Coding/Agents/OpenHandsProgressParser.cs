using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ali.Modules.Coding.Agents;

internal sealed partial class OpenHandsProgressParser(Action<ExternalCodingAgentProgress>? publish)
{
    private const int MaximumDetailLength = 240;

    public void Observe(string line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line) || publish is null)
        {
            return;
        }

        if (TryParseEvent(line, out var progress))
        {
            publish(progress);
            return;
        }

        var trimmed = line.TrimStart();
        if (isError
            && (trimmed.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("fatal", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("traceback", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("exception", StringComparison.OrdinalIgnoreCase)))
        {
            publish(new ExternalCodingAgentProgress(
                "OpenHands",
                ExternalCodingAgentProgressKind.Warning,
                "OpenHands reported a runtime warning",
                Sanitize(line)));
        }
    }

    internal static bool TryParseEvent(string line, out ExternalCodingAgentProgress progress)
    {
        progress = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var kind = ReadString(root, "kind") ?? string.Empty;
            if (kind.Length == 0)
            {
                return false;
            }

            if (kind.Equals("FinishObservation", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("FinishObservationEvent", StringComparison.OrdinalIgnoreCase))
            {
                progress = new(
                    "OpenHands",
                    ExternalCodingAgentProgressKind.Completed,
                    "OpenHands finished its implementation loop",
                    "The agent reported that it reached its final result.");
                return true;
            }

            if (kind.EndsWith("ErrorEvent", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("ConversationErrorEvent", StringComparison.OrdinalIgnoreCase))
            {
                progress = new(
                    "OpenHands",
                    ExternalCodingAgentProgressKind.Error,
                    "OpenHands encountered an error",
                    ReadSafeDetail(root) ?? "The agent reported an error without a readable explanation.");
                return true;
            }

            if (kind.EndsWith("ActionEvent", StringComparison.OrdinalIgnoreCase))
            {
                var action = ReadActionName(root) ?? "a project tool";
                progress = new(
                    "OpenHands",
                    ExternalCodingAgentProgressKind.Working,
                    $"OpenHands chose {Humanize(action)}",
                    DescribeAction(root, action));
                return true;
            }

            if (kind.EndsWith("ObservationEvent", StringComparison.OrdinalIgnoreCase))
            {
                var observation = ReadActionName(root) ?? "the last project step";
                progress = new(
                    "OpenHands",
                    ExternalCodingAgentProgressKind.Working,
                    $"OpenHands completed {Humanize(observation)}",
                    ReadSafeDetail(root) ?? "The result is available and OpenHands is deciding what to do next.");
                return true;
            }

            if (kind.EndsWith("MessageEvent", StringComparison.OrdinalIgnoreCase)
                || kind.EndsWith("AgentEvent", StringComparison.OrdinalIgnoreCase))
            {
                progress = new(
                    "OpenHands",
                    ExternalCodingAgentProgressKind.Working,
                    "OpenHands is choosing its next step",
                    "The agent is evaluating the project state and its available actions.");
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static bool ContainsFinishEvent(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseEvent(line.Trim(), out var progress)
                && progress.Kind == ExternalCodingAgentProgressKind.Completed)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeAction(JsonElement root, string action)
    {
        var path = ReadNestedString(root, "args", "path")
            ?? ReadNestedString(root, "args", "file_path")
            ?? ReadString(root, "path")
            ?? ReadString(root, "file_path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return $"Working on {Sanitize(path)}.";
        }

        var command = ReadNestedString(root, "args", "command") ?? ReadString(root, "command");
        if (!string.IsNullOrWhiteSpace(command))
        {
            return $"Running {Sanitize(command)}.";
        }

        return $"Using {Humanize(action)} to advance the requested project.";
    }

    private static string? ReadSafeDetail(JsonElement root)
    {
        foreach (var name in new[] { "detail", "message", "content", "observation", "output" })
        {
            var value = ReadString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return Sanitize(value);
            }
        }

        return null;
    }

    private static string? ReadActionName(JsonElement root)
    {
        foreach (var name in new[] { "action", "observation", "tool_name", "tool", "name" })
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                return ReadString(value, "kind")
                    ?? ReadString(value, "name")
                    ?? ReadString(value, "tool_name");
            }
        }

        return null;
    }

    private static string? ReadNestedString(JsonElement root, string parent, string name) =>
        root.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object
            ? ReadString(child, name)
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Humanize(string value)
    {
        var withoutSuffix = value
            .Replace("ActionEvent", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ObservationEvent", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Action", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Observation", string.Empty, StringComparison.OrdinalIgnoreCase);
        return WordBoundaryRegex().Replace(withoutSuffix, "$1 $2").Trim().ToLowerInvariant() switch
        {
            "" => "a project tool",
            var text => text
        };
    }

    private static string Sanitize(string value)
    {
        var singleLine = WhitespaceRegex().Replace(value, " ").Trim();
        var redacted = SecretRegex().Replace(singleLine, "$1=[redacted]");
        return redacted.Length <= MaximumDetailLength
            ? redacted
            : $"{redacted[..MaximumDetailLength]}…";
    }

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundaryRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)\b(api[_-]?key|token|password|secret)\s*=\s*[^\s,;]+")]
    private static partial Regex SecretRegex();
}
