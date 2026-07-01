using System.Text;
using System.Text.Json;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Coding;

public sealed class ModelCodingActionPlanner(ILocalModelRuntime runtime) : ICodingActionPlanner
{
    private const int MaxPlannerOutputCharacters = 4096;
    private const string ConversationId = "coding_action_plan";

    public async Task<CodingActionPlan> PlanAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return CodingActionPlan.NoAction;
        }

        var plannerHistory = new List<ChatMessage>
        {
            new(
                "coding_action_planner_system",
                ChatRole.System,
                BuildPlannerInstruction(history),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified)
        };
        var request = new ChatRequest(
            ConversationId,
            "coding_action_plan_user",
            userText,
            plannerHistory);

        try
        {
            var output = new StringBuilder();
            await foreach (var token in runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            {
                output.Append(token.Text);
                if (output.Length > MaxPlannerOutputCharacters)
                {
                    break;
                }
            }

            return TryParsePlan(output.ToString(), userText) ?? CodingActionPlan.NoAction;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return CodingActionPlan.NoAction;
        }
    }

    private static string BuildPlannerInstruction(IReadOnlyList<ChatMessage> history) =>
        CodingPlannerInstructions.Build(history);

    private static CodingActionPlan? TryParsePlan(string text, string userText)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!ReadBool(root, "use_coding_tool"))
        {
            return CodingActionPlan.NoAction;
        }

        var command = NormalizeCommand(ReadString(root, "command", string.Empty), userText);
        if (string.IsNullOrWhiteSpace(command) || !CodingToolRequestParser.TryParse(command, out _))
        {
            return null;
        }

        var summary = ReadString(root, "summary", string.Empty);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "Ali selected the next programming action.";
        }

        return new CodingActionPlan(
            true,
            command,
            TrimForPlanner(summary),
            ReadDouble(root, "confidence"));
    }

    private static string NormalizeCommand(string command, string userText)
    {
        var normalized = command
            .ReplaceLineEndings(" ")
            .Trim()
            .Trim('"', '\'', '`');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var goal = NormalizeGoal(userText);
        normalized = normalized
            .Replace("<goal>", goal, StringComparison.OrdinalIgnoreCase)
            .Replace("<name>", goal, StringComparison.OrdinalIgnoreCase)
            .Replace("<error>", goal, StringComparison.OrdinalIgnoreCase)
            .Replace("<symbol-or-file>", goal, StringComparison.OrdinalIgnoreCase);

        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeGoal(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '`');
        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }

    private static bool ReadBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True;

    private static string ReadString(JsonElement root, string propertyName, string fallback) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static double ReadDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var number)
            ? Math.Clamp(number, 0, 1)
            : 0;
    }

    private static string TrimForPlanner(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}
