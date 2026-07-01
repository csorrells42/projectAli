using System.Text;
using System.Text.Json;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Coding;

public sealed class ModelCodingPatchPlanner(ILocalModelRuntime runtime) : ICodingPatchPlanner
{
    private const int MaxPlannerOutputCharacters = 18_000;
    private const int MaxPatchEdits = 16;
    private const string ConversationId = "coding_patch_plan";

    public async Task<CodingPatchPlan> PlanPatchAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userText)
            || !contextPack.HasContext
            || string.IsNullOrWhiteSpace(contextPack.Text))
        {
            return CodingPatchPlan.NoPatch;
        }

        var plannerHistory = new List<ChatMessage>
        {
            new(
                "coding_patch_planner_system",
                ChatRole.System,
                BuildPlannerInstruction(contextPack),
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified)
        };
        var request = new ChatRequest(
            ConversationId,
            "coding_patch_plan_user",
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

            return TryParsePlan(output.ToString()) ?? CodingPatchPlan.NoPatch;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return CodingPatchPlan.NoPatch;
        }
    }

    private static string BuildPlannerInstruction(CodingContextPack contextPack) =>
        CodingPatchPlannerInstructions.Build(contextPack);

    private static CodingPatchPlan? TryParsePlan(string text)
    {
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!ReadBool(root, "has_patch", "hasPatch"))
        {
            return CodingPatchPlan.NoPatch with
            {
                Summary = TrimForPlanner(ReadString(root, "summary", string.Empty)),
                Confidence = ReadDouble(root, "confidence"),
                StopReason = TrimForPlanner(ReadString(root, "stop_reason", ReadString(root, "stopReason", string.Empty)))
            };
        }

        if (!root.TryGetProperty("edits", out var editsElement) || editsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var edits = new List<CodingPatchEdit>();
        foreach (var editElement in editsElement.EnumerateArray().Take(MaxPatchEdits))
        {
            if (editElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = NormalizeText(ReadString(editElement, "path", string.Empty));
            var oldText = ReadString(editElement, "oldText", ReadString(editElement, "old_text", string.Empty));
            var newText = ReadString(editElement, "newText", ReadString(editElement, "new_text", string.Empty));
            if (string.IsNullOrWhiteSpace(path) || (oldText.Length == 0 && newText.Length == 0))
            {
                continue;
            }

            edits.Add(new CodingPatchEdit(path, oldText, newText));
        }

        if (edits.Count == 0)
        {
            return null;
        }

        var summary = TrimForPlanner(ReadString(root, "summary", string.Empty));
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "Draft a concrete patch preview.";
        }

        return new CodingPatchPlan(
            true,
            edits,
            summary,
            ReadDouble(root, "confidence"),
            TrimForPlanner(ReadString(root, "stop_reason", ReadString(root, "stopReason", string.Empty))));
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }

    private static bool ReadBool(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }

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

    private static string NormalizeText(string value) =>
        value.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '`');

    private static string TrimForPlanner(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}
