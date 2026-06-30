using System.Text;
using System.Text.Json;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Coding;

public sealed class ModelCodingPatchPlanner(ILocalModelRuntime runtime) : ICodingPatchPlanner
{
    private const int MaxPlannerOutputCharacters = 18_000;
    private const int MaxPatchEdits = 8;
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
        string.Join(
            Environment.NewLine,
            "You are Ali's programming patch planner.",
            "Return exactly one JSON object and no other text.",
            "Do not answer the user. Do not explain. Do not include markdown.",
            "Use the current user message as the only requested goal. Ignore stale goals from previous turns unless the user explicitly says continue, next, or keep going.",
            "Generate a guarded patch preview only when the provided context contains enough exact file text to make a safe edit.",
            "For an existing file, oldText must be copied exactly from an editable file excerpt below. Preserve line endings exactly when possible.",
            "For a new file, oldText must be an empty string and path must be a concrete file path inside the selected workspace.",
            "For console apps, prefer a complete Program.cs with clear prompts, input validation, visible output, and an optional Console.ReadKey when the user asks the app to wait before closing.",
            "For WPF apps, prefer small MVVM-friendly slices: XAML binds to public view-model properties/commands, code-behind stays minimal, property changes raise notifications, and commands keep UI work on the dispatcher-safe path.",
            "For data structures, SQL/database access, services, caches, queues, and APIs, prefer small seams: keep pure data-structure logic testable, keep SQL parameterized, preserve transactions and connection lifetimes, avoid hidden global state, and do not add packages or external services unless context or owner approval supports it.",
            "Do not invent tool results, builds, tests, files, or hidden project facts.",
            "If the request cannot be patched safely from the provided excerpts, return has_patch false with a short stop_reason.",
            "JSON shape:",
            "{\"has_patch\":true,\"summary\":\"Update Program.cs to read an integer and print its factorial.\",\"confidence\":0.86,\"edits\":[{\"path\":\"C:\\\\Workspace\\\\Demo\\\\Program.cs\",\"oldText\":\"exact current text\",\"newText\":\"replacement text\"}]}",
            "No-patch shape:",
            "{\"has_patch\":false,\"summary\":\"Need an exact target file first.\",\"confidence\":0.2,\"stop_reason\":\"No editable file excerpt matched the requested change.\",\"edits\":[]}",
            "Approved read-only context:",
            contextPack.Text);

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
