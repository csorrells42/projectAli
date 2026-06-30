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

    private static string BuildPlannerInstruction(IReadOnlyList<ChatMessage> history)
    {
        var lines = new List<string>
        {
            "You are the app's programming action planner.",
            "Return exactly one JSON object and no other text.",
            "Do not answer the user.",
            "The user is in Programming mode. They should be able to describe a coding goal in normal language.",
            "Choose one internal coding command that Ali should run next, replacing placeholders like <goal>, <name>, or <error> with concise text from the user's request.",
            "Operate the tools as a lane, not as a static command catalog. Pick the next command that advances the current build, repair, validation, or closeout step.",
            "If recent assistant text contains `Next:` or `Next command:` and the user says next, continue, go, keep going, do it, or similar, choose that exact command when it is parseable.",
            "Use existing commands only. Do not invent commands, files, project names, test names, package names, or tool results.",
            "Prefer read-only planning and inspection commands unless the user explicitly confirms a gated action.",
            "For a request to create, add, implement, fix, or change code, prefer `build this for me <goal>`.",
            "For a request to keep going or continue, prefer `continue current task`.",
            "For a request about the next move, prefer `show next coding action`.",
            "For readiness, progress, or score questions, prefer `mini codex status`.",
            "For build or test failures, prefer `diagnose last build failure`, `suggest patch from last failure`, or `validation repair runner <goal>`.",
            "Do not choose capability-card, command-index, or status commands for a build request unless the user asks what Ali can do.",
            "If the request is not about programming, return use_coding_tool false.",
            "JSON shape:",
            "{\"use_coding_tool\":true,\"command\":\"build this for me add export button\",\"summary\":\"Start the guided build lane.\",\"confidence\":0.8}",
            "Programming lane map:",
            "- New build/change request: build this for me <goal>.",
            "- Clarify target/project: active workspace project, feature work context <goal>, or feature intake <goal>.",
            "- Plan and target edits: autonomous feature orchestrator <goal>, roslyn edit planner <goal>, multi-file patch synthesis <goal>.",
            "- Draft/preview patch: feature patch draft <goal>, exact patch synthesis <goal>, preview synthesized feature patch <goal>, preview guided feature bundle <goal>.",
            "- Apply only after owner confirmation: confirm apply last patch preview.",
            "- Validate after edits: post patch validation <goal>, validation command minimizer <goal>, validation chain planner <goal>.",
            "- Repair failures: diagnose last build failure, validation repair runner <goal>, first diagnostic repair route <goal>, failure to patch v3 <goal>.",
            "- Close out: semantic diff summary <goal>, semantic change receipt <goal>, review current changes, can i safely commit.",
            "High-value commands Ali can use:"
        };

        foreach (var command in BuildPlannerCommandList())
        {
            lines.Add($"- {command}");
        }

        lines.Add("Recent conversation context:");
        foreach (var message in history
                     .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
                     .TakeLast(6))
        {
            lines.Add($"{message.Role}: {TrimForPlanner(message.Text)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> BuildPlannerCommandList()
    {
        var commands = CodingAbilityCatalog.FastBuilderPath
            .Concat(CodingAbilityCatalog.BuilderGroups.SelectMany(group => group.Commands).Select(command => command.Command))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(120)
            .ToList();

        if (!commands.Contains("continue current task", StringComparer.OrdinalIgnoreCase))
        {
            commands.Add("continue current task");
        }

        return commands;
    }

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
