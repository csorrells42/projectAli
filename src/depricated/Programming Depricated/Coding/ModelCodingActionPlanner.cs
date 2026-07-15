using System.Text;
using System.Text.Json;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Coding;

public sealed class ModelCodingActionPlanner(ILocalModelRuntime runtime) : ICodingActionPlanner
{
    private const int MaxPlannerOutputCharacters = 4096;
    private const int MaxPlannerAttempts = 2;
    private const string ConversationId = "coding_action_plan";

    public async Task<CodingActionPlan> PlanAsync(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken,
        CodingContextPack? contextPack = null)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return BuildNoAction("No programming request text was provided.");
        }

        try
        {
            var validationError = string.Empty;
            var previousOutput = string.Empty;
            var mustUseLocalRepairBeforePatch = HasRecentPatchPreviewBlockEvidence(userText, history, contextPack ?? CodingContextPack.Empty);
            for (var attempt = 0; attempt < MaxPlannerAttempts; attempt++)
            {
                var plannerHistory = BuildPlannerHistory(
                    history,
                    contextPack ?? CodingContextPack.Empty,
                    validationError,
                    previousOutput);
                var request = new ChatRequest(
                    ConversationId,
                    "coding_action_plan_user",
                    userText,
                    plannerHistory);

                var output = new StringBuilder();
                await foreach (var token in runtime.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    output.Append(token.Text);
                    if (output.Length > MaxPlannerOutputCharacters)
                    {
                        break;
                    }
                }

                previousOutput = output.ToString();
                var plan = TryParsePlan(previousOutput, userText, mustUseLocalRepairBeforePatch, out validationError);
                if (plan is not null)
                {
                    return plan;
                }
            }

            if (mustUseLocalRepairBeforePatch)
            {
                return BuildLocalFailureRepairPlan(userText, validationError, previousOutput);
            }

            return BuildNoAction(validationError, previousOutput);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return BuildNoAction($"Coding action planner failed: {ex.Message}");
        }
    }

    private static List<ChatMessage> BuildPlannerHistory(
        IReadOnlyList<ChatMessage> history,
        CodingContextPack contextPack,
        string validationError,
        string previousOutput)
    {
        var plannerInstruction = BuildPlannerInstruction(history, contextPack);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            plannerInstruction = string.Join(
                Environment.NewLine,
                plannerInstruction,
                "Previous planner output validation error:",
                TrimForPlanner(validationError),
                "Previous invalid action planner output:",
                TrimForRepairPrompt(previousOutput),
                "Repair the decision by returning one valid JSON object that uses the same tool contract. Do not explain the repair.");
        }

        return
        [
            new(
                "coding_action_planner_system",
                ChatRole.System,
                plannerInstruction,
                DateTimeOffset.UtcNow,
                EvidenceStatus.Verified)
        ];
    }

    private static string BuildPlannerInstruction(IReadOnlyList<ChatMessage> history, CodingContextPack contextPack) =>
        CodingPlannerInstructions.Build(history, contextPack);

    private static CodingActionPlan? TryParsePlan(
        string text,
        string userText,
        bool mustUseLocalRepairBeforePatch,
        out string validationError)
    {
        validationError = string.Empty;
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            validationError = "Planner output did not contain a JSON object.";
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            validationError = $"Planner output contained invalid JSON: {ex.Message}";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var selectedPath = TrimForPlanner(ReadStringAny(root, string.Empty, "selected_path", "selectedPath", "path", "sp"));
            var understoodGoal = TrimForPlanner(ReadStringAny(root, string.Empty, "understood_goal", "understoodGoal", "goal", "g"));
            var acceptanceCriteria = ReadStringArray(root, "acceptance_criteria", "acceptanceCriteria", "criteria", "c")
                .Select(TrimForPlanner)
                .Where(criterion => !string.IsNullOrWhiteSpace(criterion))
                .Take(6)
                .ToArray();
            var selectedTool = ResolveSelectedToolTemplate(TrimForPlanner(ReadStringAny(root, string.Empty, "selected_tool", "selectedTool", "tool", "t")));
            var rawCommandGoal = ReadStringAny(root, understoodGoal, "command_goal", "commandGoal", "cg");
            var commandGoal = TrimForPlanner(StripPlannerMarkup(rawCommandGoal));
            if (string.IsNullOrWhiteSpace(selectedPath) || !IsKnownCapabilityPath(selectedPath))
            {
                var inferredPath = InferCapabilityPathFromSelectedTool(selectedTool);
                if (!string.IsNullOrWhiteSpace(inferredPath))
                {
                    selectedPath = inferredPath;
                }
                else if (IsPatchPreviewSelectedTool(selectedTool))
                {
                    selectedPath = "Existing feature or bug fix";
                }
            }

            var infoUsed = ReadStringArray(root, "info_used", "infoUsed", "info", "i")
                .Select(TrimForPlanner)
                .Where(info => !string.IsNullOrWhiteSpace(info))
                .Take(5)
                .ToArray();
            var groundingText = string.Join(' ', userText, understoodGoal, string.Join(' ', infoUsed), string.Join(' ', acceptanceCriteria));
            if (SelectedToolRequiresCommandGoal(selectedTool)
                && ShouldRepairPlannerCommandGoal(rawCommandGoal, commandGoal, groundingText)
                && TryChooseGroundedCommandGoal(userText, understoodGoal, infoUsed, acceptanceCriteria, out var repairedCommandGoal))
            {
                commandGoal = repairedCommandGoal;
            }

            if (ReadExplicitFalse(root, "use_coding_tool", "useCodingTool", "use", "u"))
            {
                return BuildNoAction("Planner returned use_coding_tool false.", text);
            }

            if (!ReadBool(root, "use_coding_tool", "useCodingTool", "use", "u")
                && string.IsNullOrWhiteSpace(selectedTool))
            {
                return BuildNoAction("Planner omitted use_coding_tool and selected_tool.", text);
            }

            if (string.IsNullOrWhiteSpace(selectedTool) || !TryBuildCommandFromSelectedTool(selectedTool, commandGoal, out var command))
            {
                validationError = string.IsNullOrWhiteSpace(selectedTool)
                    ? "selected_tool is required and must be one exact known tool template."
                    : $"selected_tool '{selectedTool}' is not an exact known tool template or is missing a required command_goal.";
                return null;
            }

            if (SelectedToolRequiresCommandGoal(selectedTool)
                && !IsCommandGoalGrounded(groundingText, commandGoal))
            {
                validationError = "command_goal must be grounded in the current user request, understood_goal, info_used, or acceptance_criteria, not copied from an example or stale context.";
                return null;
            }

            var executionMode = NormalizeExecutionMode(ReadStringAny(root, string.Empty, "execution_mode", "executionMode", "mode", "m"));
            if (string.IsNullOrWhiteSpace(executionMode)
                && IsPatchPreviewSelectedTool(selectedTool))
            {
                executionMode = "model_patch_preview";
            }

            if (executionMode.Equals("local_tool", StringComparison.OrdinalIgnoreCase)
                && IsPatchPreviewSelectedTool(selectedTool))
            {
                executionMode = "model_patch_preview";
            }

            if ((executionMode.Equals("local_tool", StringComparison.OrdinalIgnoreCase)
                 || executionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase))
                && !IsPatchPreviewSelectedTool(selectedTool)
                && TryResolvePatchPreviewToolForGuidePath(selectedPath, selectedTool, out var patchPreviewTool))
            {
                selectedTool = patchPreviewTool;
                executionMode = "model_patch_preview";
                if (!TryBuildCommandFromSelectedTool(selectedTool, commandGoal, out command))
                {
                    validationError = $"selected_tool '{selectedTool}' and command_goal did not produce a runnable coding command.";
                    return null;
                }
            }

            if (string.IsNullOrWhiteSpace(executionMode))
            {
                validationError = "execution_mode is required and must be either model_patch_preview or local_tool.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(command) || !CodingToolRequestParser.TryParse(command, out _))
            {
                validationError = $"selected_tool '{selectedTool}' and command_goal did not produce a runnable coding command.";
                return null;
            }

            if (executionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase)
                && !IsKnownCapabilityPath(selectedPath))
            {
                validationError = "model_patch_preview requires selected_path to be one exact Programming capability path name.";
                return null;
            }

            if (executionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase))
            {
                if (acceptanceCriteria.Length == 0 && !string.IsNullOrWhiteSpace(understoodGoal))
                {
                    acceptanceCriteria = [understoodGoal];
                }

                if (infoUsed.Length == 0)
                {
                    infoUsed = ["current project context available"];
                }
            }

            if (mustUseLocalRepairBeforePatch
                && executionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase))
            {
                validationError = "Recent evidence says model patch preview is blocked or unsafe. Choose selected_tool \"suggest patch from last failure\" with execution_mode local_tool before another model_patch_preview.";
                return null;
            }

            var summary = ReadStringAny(root, string.Empty, "summary", "s");
            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = "Ali selected the next programming action.";
            }

            return new CodingActionPlan(
                true,
                command,
                TrimForPlanner(summary),
                ReadDouble(root, "confidence", "conf"),
                selectedPath,
                understoodGoal,
                executionMode,
                selectedTool,
                commandGoal,
                acceptanceCriteria,
                infoUsed);
        }
    }

    private static CodingActionPlan BuildNoAction(string diagnostic, string rawOutput = "")
    {
        var normalizedDiagnostic = string.IsNullOrWhiteSpace(diagnostic)
            ? "Coding action planner did not return a runnable decision."
            : TrimForPlanner(diagnostic);
        return CodingActionPlan.NoAction with
        {
            Summary = normalizedDiagnostic,
            Diagnostic = normalizedDiagnostic,
            RawOutputExcerpt = TrimForRepairPrompt(rawOutput)
        };
    }

    private static CodingActionPlan BuildLocalFailureRepairPlan(string userText, string diagnostic, string rawOutput)
    {
        var goal = NormalizeGoal(userText);
        var summary = string.IsNullOrWhiteSpace(diagnostic)
            ? "Use the local failure repair tool before another model patch preview."
            : TrimForPlanner(diagnostic);
        return new CodingActionPlan(
            true,
            "suggest patch from last failure",
            summary,
            0.7,
            "Build/test repair loop",
            goal,
            "local_tool",
            "suggest patch from last failure",
            goal,
            ["Repair the latest failed build or validation result."],
            ["Recent evidence shows model patch preview is blocked or unsafe."],
            summary,
            TrimForRepairPrompt(rawOutput));
    }

    private static bool HasRecentPatchPreviewBlockEvidence(
        string userText,
        IReadOnlyList<ChatMessage> history,
        CodingContextPack contextPack)
    {
        if (!LooksLikeRepairFollowUp(userText))
        {
            return false;
        }

        var recentAssistantText = string.Join(
            Environment.NewLine,
            history
                .Where(message => message.Role is ChatRole.Assistant)
                .TakeLast(4)
                .Select(message => message.Text));
        if (!HasFreshBuildFailureEvidence(recentAssistantText))
        {
            return false;
        }

        var evidence = string.Join(Environment.NewLine, recentAssistantText, contextPack.Text);
        return evidence.Contains("patch preview would leave invalid WPF/XAML structure", StringComparison.OrdinalIgnoreCase)
               || evidence.Contains("Patch planner output contained invalid JSON", StringComparison.OrdinalIgnoreCase)
               || evidence.Contains("Repeat guard:", StringComparison.OrdinalIgnoreCase)
               || evidence.Contains("expected exactly one match", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFreshBuildFailureEvidence(string recentAssistantText) =>
        recentAssistantText.Contains("Build failed", StringComparison.OrdinalIgnoreCase)
        || recentAssistantText.Contains("Test failed", StringComparison.OrdinalIgnoreCase)
        || recentAssistantText.Contains("Restore failed", StringComparison.OrdinalIgnoreCase)
        || recentAssistantText.Contains("Run failed", StringComparison.OrdinalIgnoreCase)
        || recentAssistantText.Contains("failed confirmed dotnet", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRepairFollowUp(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var tokens = normalized
            .Split([' ', '\t', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tokens.Overlaps(["continue", "next", "again", "fix", "repair", "failure", "failed", "failing", "build", "validation", "xaml", "wpf"]);
    }

    private static bool TryBuildCommandFromSelectedTool(string selectedTool, string commandGoal, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(selectedTool))
        {
            return false;
        }

        var template = CodingPlannerInstructions.BuildPlannerCommandList()
            .FirstOrDefault(candidate => string.Equals(candidate, selectedTool, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var requiresGoal = template.Contains('<') && template.Contains('>');
        if (requiresGoal && string.IsNullOrWhiteSpace(commandGoal))
        {
            return false;
        }

        command = requiresGoal
            ? NormalizeCommand(template, commandGoal)
            : NormalizeCommand(template, commandGoal);
        return !string.IsNullOrWhiteSpace(command);
    }

    private static string ResolveSelectedToolTemplate(string selectedTool)
    {
        if (string.IsNullOrWhiteSpace(selectedTool))
        {
            return string.Empty;
        }

        var normalized = selectedTool.Trim();
        var commands = CodingPlannerInstructions.BuildPlannerCommandList();
        var exact = commands.FirstOrDefault(command => string.Equals(command, normalized, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }

        var shorthand = commands.FirstOrDefault(command =>
            command.Contains('<', StringComparison.Ordinal)
            && command.StartsWith(normalized + " <", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(shorthand))
        {
            return shorthand;
        }

        var prefix = commands.FirstOrDefault(command =>
        {
            var marker = command.IndexOf(" <", StringComparison.Ordinal);
            if (marker <= 0)
            {
                return false;
            }

            var baseName = command[..marker];
            return normalized.StartsWith(baseName + " ", StringComparison.OrdinalIgnoreCase);
        });

        return prefix ?? normalized;
    }

    private static bool IsPatchPreviewSelectedTool(string selectedTool) =>
        CodingAbilityCatalog.PatchPreviewToolTemplates.Any(template =>
            string.Equals(template, selectedTool, StringComparison.OrdinalIgnoreCase));

    private static bool TryResolvePatchPreviewToolForGuidePath(
        string selectedPath,
        string selectedTool,
        out string patchPreviewTool)
    {
        patchPreviewTool = string.Empty;
        if (string.IsNullOrWhiteSpace(selectedPath)
            || string.IsNullOrWhiteSpace(selectedTool)
            || !selectedTool.Contains(" guide", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = CodingAbilityCatalog.ProgrammingCapabilityPaths
            .FirstOrDefault(candidate => string.Equals(candidate.Name, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            return false;
        }

        var candidates = path.CommandSequence
            .Concat(path.BuildingBlocks)
            .Where(command => CodingAbilityCatalog.PatchPreviewToolTemplates.Contains(command))
            .Concat(CodingAbilityCatalog.PatchPreviewToolTemplates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        patchPreviewTool = candidates.FirstOrDefault(command => string.Equals(command, "concrete patch authoring <goal>", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(command => string.Equals(command, "preview synthesized feature patch <goal>", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(command => !string.Equals(command, "build this for me <goal>", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(patchPreviewTool);
    }

    private static bool SelectedToolRequiresCommandGoal(string selectedTool) =>
        !string.IsNullOrWhiteSpace(selectedTool)
        && selectedTool.Contains('<')
        && selectedTool.Contains('>');

    private static bool ShouldRepairPlannerCommandGoal(
        string rawCommandGoal,
        string commandGoal,
        string groundingText)
    {
        if (string.IsNullOrWhiteSpace(commandGoal))
        {
            return true;
        }

        return ContainsPlannerPlaceholderMarkup(rawCommandGoal)
               || ContainsPlannerPlaceholderMarkup(commandGoal)
               || !IsCommandGoalGrounded(groundingText, commandGoal);
    }

    private static bool ContainsPlannerPlaceholderMarkup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Contains('<', StringComparison.Ordinal)
               || normalized.Contains('>', StringComparison.Ordinal)
               || normalized.Equals("goal", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("name", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("error", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("symbol-or-file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryChooseGroundedCommandGoal(
        string userText,
        string understoodGoal,
        IReadOnlyList<string> infoUsed,
        IReadOnlyList<string> acceptanceCriteria,
        out string commandGoal)
    {
        commandGoal = string.Empty;
        var groundingText = string.Join(' ', userText, understoodGoal, string.Join(' ', infoUsed), string.Join(' ', acceptanceCriteria));
        foreach (var candidate in new[] { understoodGoal, userText }
                     .Concat(acceptanceCriteria)
                     .Concat(infoUsed)
                     .Select(TrimForPlanner)
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            if (IsCommandGoalGrounded(groundingText, candidate))
            {
                commandGoal = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandGoalGrounded(
        string groundingText,
        string commandGoal)
    {
        var goalTokens = MeaningfulGoalTokens(commandGoal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (goalTokens.Length == 0)
        {
            return false;
        }

        var sourceTokens = MeaningfulGoalTokens(groundingText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sourceTokens.Count == 0)
        {
            return false;
        }

        var overlap = goalTokens.Count(sourceTokens.Contains);
        var required = goalTokens.Length <= 2
            ? goalTokens.Length
            : 2;
        return overlap >= Math.Max(1, required);
    }

    private static IEnumerable<string> MeaningfulGoalTokens(string value)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "be", "by", "could", "for", "from", "i", "in", "is", "it", "me",
            "of", "on", "or", "please", "that", "the", "this", "to", "with", "you",
            "add", "build", "change", "create", "do", "make", "need", "requested", "same", "short", "software",
            "button", "buttons", "click", "current", "feature", "goal", "thing"
        };

        return value
            .ReplaceLineEndings(" ")
            .Split([' ', '\t', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '"', '\'', '`', '<', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .Where(token => token.Length > 1 && !stopWords.Contains(token));
    }

    private static string StripPlannerMarkup(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('<', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var insideTag = false;
        foreach (var ch in value)
        {
            if (ch == '<')
            {
                insideTag = true;
                builder.Append(' ');
                continue;
            }

            if (ch == '>')
            {
                insideTag = false;
                builder.Append(' ');
                continue;
            }

            if (!insideTag)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
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

    private static string TrimForRepairPrompt(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 1_000 ? normalized : normalized[..1_000];
    }

    private static string NormalizeExecutionMode(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '`')
            .Replace('-', '_')
            .ToLowerInvariant();
        return normalized switch
        {
            "model_patch_preview" or "patch" or "preview" or "model" => "model_patch_preview",
            "local_tool" or "tool" or "local" => "local_tool",
            _ => string.Empty
        };
    }

    private static bool IsKnownCapabilityPath(string selectedPath) =>
        !string.IsNullOrWhiteSpace(selectedPath)
        && CodingAbilityCatalog.ProgrammingCapabilityPaths.Any(path =>
            string.Equals(path.Name, selectedPath, StringComparison.OrdinalIgnoreCase));

    private static string InferCapabilityPathFromSelectedTool(string selectedTool)
    {
        if (string.IsNullOrWhiteSpace(selectedTool))
        {
            return string.Empty;
        }

        return CodingAbilityCatalog.ProgrammingCapabilityPaths
            .FirstOrDefault(path => path.BuildingBlocks
                .Concat(path.CommandSequence)
                .Any(block => string.Equals(block, selectedTool, StringComparison.OrdinalIgnoreCase)))
            ?.Name ?? string.Empty;
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
        var found = false;
        JsonElement value = default;
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out value))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static bool ReadExplicitFalse(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.False => true,
                JsonValueKind.Number => value.TryGetInt32(out var number) && number == 0,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && !parsed,
                _ => false
            };
        }

        return false;
    }

    private static string ReadString(JsonElement root, string propertyName, string fallback) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string ReadStringAny(JsonElement root, string fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String)
            {
                return value.GetString() ?? fallback;
            }
        }

        return fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, params string[] propertyNames)
    {
        var found = false;
        JsonElement value = default;
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out value))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return Array.Empty<string>();
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray(),
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString())
                ? Array.Empty<string>()
                : [value.GetString()!],
            _ => Array.Empty<string>()
        };
    }

    private static double ReadDouble(JsonElement root, params string[] propertyNames)
    {
        var found = false;
        JsonElement value = default;
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out value))
            {
                found = true;
                break;
            }
        }

        if (!found)
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
