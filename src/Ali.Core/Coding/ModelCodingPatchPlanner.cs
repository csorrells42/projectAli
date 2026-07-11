using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ali.Core.Evidence;
using Ali.Core.Runtime;

namespace Ali.Core.Coding;

public sealed class ModelCodingPatchPlanner(ILocalModelRuntime runtime) : ICodingPatchPlanner
{
    private const int MaxPlannerOutputCharacters = 64_000;
    private const int MaxPatchEdits = 16;
    private const int MaxPlannerRepairAttempts = 2;
    private const string ConversationId = "coding_patch_plan";

    public async Task<CodingPatchPlan> PlanPatchAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken,
        CodingActionPlan? actionPlan = null)
    {
        if (string.IsNullOrWhiteSpace(userText)
            || !contextPack.HasContext
            || string.IsNullOrWhiteSpace(contextPack.Text))
        {
            return BuildNoPatch("No approved coding context pack was available for patch authoring.");
        }

        try
        {
            var plannerInstruction = BuildPlannerInstruction(contextPack, actionPlan);
            var pathEvidence = PatchPlannerPathEvidence.From(contextPack);
            var plannerOutput = await RunPatchPlannerAsync(
                BuildPatchPlannerRequest(userText, plannerInstruction),
                cancellationToken).ConfigureAwait(false);
            if (TryParsePlan(plannerOutput, actionPlan, pathEvidence, out var validationError) is { } plan)
            {
                return plan;
            }

            var lastOutput = plannerOutput;
            var lastValidationError = validationError;
            for (var attempt = 1; attempt <= MaxPlannerRepairAttempts; attempt++)
            {
                var repairOutput = await RunPatchPlannerAsync(
                    BuildPatchPlannerRepairRequest(userText, plannerInstruction, lastValidationError, lastOutput, attempt),
                    cancellationToken).ConfigureAwait(false);
                if (TryParsePlan(repairOutput, actionPlan, pathEvidence, out validationError) is { } repairedPlan)
                {
                    return repairedPlan;
                }

                lastOutput = repairOutput;
                lastValidationError = validationError;
            }

            return BuildNoPatch(lastValidationError, lastOutput);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            return BuildNoPatch($"Coding patch planner failed: {ex.Message}");
        }
    }

    private static string BuildPlannerInstruction(CodingContextPack contextPack, CodingActionPlan? actionPlan) =>
        CodingPatchPlannerInstructions.Build(contextPack, actionPlan);

    private async Task<string> RunPatchPlannerAsync(ChatRequest request, CancellationToken cancellationToken)
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

        return output.ToString();
    }

    private static ChatRequest BuildPatchPlannerRequest(string userText, string plannerInstruction) =>
        new(
            ConversationId,
            "coding_patch_plan_user",
            userText,
            [
                new(
                    "coding_patch_planner_system",
                    ChatRole.System,
                    plannerInstruction,
                    DateTimeOffset.UtcNow,
                    EvidenceStatus.Verified)
            ]);

    private static ChatRequest BuildPatchPlannerRepairRequest(
        string userText,
        string plannerInstruction,
        string validationError,
        string previousOutput,
        int attempt)
    {
        var repairInstruction = string.Join(
            Environment.NewLine,
            "You are Ali's programming patch planner JSON repairer. Return exactly one JSON object and no other text.",
            "Do not answer the user. Do not explain. Do not include markdown fences or hidden reasoning.",
            "Use the current user message as the requested goal.",
            "Valid shape examples:",
            "{\"has_patch\":true,\"edits\":[{\"p\":\"MainWindow.xaml.cs\",\"o\":\"using System.Windows;\",\"n\":\"using System.Windows;\\nusing System.Windows.Controls;\"}],\"summary\":\"Add missing WPF controls namespace.\",\"confidence\":0.86}",
            "{\"has_patch\":true,\"edits\":[{\"p\":\"MainWindow.xaml\",\"o\":\"<TextBox x:Name=\\\"textBox\\\" Grid.Row=\\\"1\\\" Margin=\\\"10,4\\\" />\",\"n\":\"<TextBox x:Name=\\\"textBox\\\" Grid.Row=\\\"1\\\" Margin=\\\"10,4\\\" />\\n<Label x:Name=\\\"statusLabel\\\" Content=\\\"Ready\\\" Grid.Row=\\\"2\\\" Margin=\\\"10,4\\\" />\"}],\"summary\":\"Insert one WPF label with an exact anchor edit.\",\"confidence\":0.78}",
            "{\"has_patch\":false,\"summary\":\"Need a shorter safe patch.\",\"confidence\":0.2,\"stop_reason\":\"Patch JSON was truncated or too long to serialize safely.\",\"edits\":[]}",
            $"Patch planner JSON repair attempt {attempt}:",
            "The previous patch planner output was rejected before preview. Return a fresh, complete, valid JSON object only.",
            "Do not continue from the previous text. Do not use markdown fences. Do not explain.",
            "Use FILE relative paths where possible. Escape JSON string quotes, newlines, and backslashes correctly.",
            "Never use literal placeholder strings such as exact current text, replacement text, complete file text, TODO, or ellipses in oldText/newText.",
            "If the validation error requires replace_file, use mode=\"replace_file\" on the affected file and put the complete final file text in n/newText.",
            "For mode=\"replace_file\", set o/oldText to an empty string. Do not copy the old full file into o/oldText.",
            "Avoid n_lines for ordinary feature edits. Use compact o/n strings with escaped \\n when needed; reserve n_lines for structural whole-file repair only.",
            "If the selected path is Existing feature or bug fix and no structural validation failure is present, repair with compact exact oldText/newText edits only: no replace_file, no n_lines, no full XAML serialization.",
            "If the previous output used markdown fences, n_lines, replace_file, or full Window/Grid XAML for a small existing-feature change, discard it and return a smaller exact-anchor patch.",
            "If the patch would be too long to serialize safely, return has_patch=false with a concise stop_reason.",
            "Approved patch context and constraints for this fresh repair:",
            TrimForPatchRepairPrompt(plannerInstruction),
            $"Current user request: {TrimForRepairPrompt(userText)}",
            "Previous validation error:",
            TrimForRepairPrompt(validationError),
            "Previous invalid patch planner output:",
            TrimPreviousPatchOutputForRepair(previousOutput));

        return BuildPatchPlannerRequest(userText, repairInstruction) with
        {
            UserMessageId = $"coding_patch_plan_repair_{attempt}"
        };
    }

    private static CodingPatchPlan? TryParsePlan(
        string text,
        CodingActionPlan? actionPlan,
        PatchPlannerPathEvidence pathEvidence,
        out string validationError)
    {
        validationError = string.Empty;
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            validationError = "Patch planner output did not contain a JSON object.";
            return null;
        }

        if (!TryParseJsonDocument(json, out var document, out var jsonError))
        {
            validationError = $"Patch planner output contained invalid JSON: {jsonError}";
            return null;
        }

        using var documentScope = document;
        var root = document.RootElement;
        if (!ReadBool(root, "has_patch", "hasPatch"))
        {
            var noPatchSummary = TrimForPlanner(ReadString(root, "summary", string.Empty));
            var stopReason = TrimForPlanner(ReadString(root, "stop_reason", ReadString(root, "stopReason", string.Empty)));
            if (string.IsNullOrWhiteSpace(stopReason))
            {
                stopReason = string.IsNullOrWhiteSpace(noPatchSummary)
                    ? "Patch planner returned has_patch false without a stop_reason."
                    : noPatchSummary;
            }

            return CodingPatchPlan.NoPatch with
            {
                Summary = noPatchSummary,
                Confidence = ReadDouble(root, "confidence"),
                StopReason = stopReason,
                SelectedPath = TrimForPlanner(ReadString(root, "selected_path", ReadString(root, "selectedPath", string.Empty)))
            };
        }

        if (!TryReadArray(root, out var editsElement, "edits", "patches"))
        {
            validationError = "Patch planner returned has_patch true without an edits array.";
            return null;
        }

        var editCount = editsElement.GetArrayLength();
        if (editCount > MaxPatchEdits)
        {
            return CodingPatchPlan.NoPatch with
            {
                Summary = $"Model patch response contained {editCount} edits, which exceeds the safe patch preview limit of {MaxPatchEdits}.",
                Confidence = ReadDouble(root, "confidence"),
                StopReason = "Too many edits for one guarded patch preview; choose a smaller coherent slice or fewer coordinated files.",
                SelectedPath = TrimForPlanner(ReadString(root, "selected_path", ReadString(root, "selectedPath", string.Empty)))
            };
        }

        var edits = new List<CodingPatchEdit>();
        foreach (var editElement in editsElement.EnumerateArray())
        {
            if (editElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = NormalizeText(ReadStringAny(editElement, string.Empty, "path", "p", "file"));
            var oldText = ReadStringAny(editElement, string.Empty, "oldText", "old_text", "old", "o", "find", "before");
            var newText = ReadStringAny(editElement, string.Empty, "newText", "new_text", "new", "n", "replace", "after", "content");
            if (newText.Length == 0)
            {
                newText = ReadJoinedStringArrayAny(editElement, "newLines", "new_lines", "n_lines", "content_lines", "lines");
            }

            var replaceEntireFile = ReadBool(
                    editElement,
                    "replace_entire_file",
                    "replaceEntireFile",
                    "whole_file",
                    "wholeFile",
                    "full_file",
                    "fullFile")
                || IsReplaceEntireFileMode(ReadStringAny(editElement, string.Empty, "mode", "op", "operation"));
            if (string.IsNullOrWhiteSpace(path) || (oldText.Length == 0 && newText.Length == 0))
            {
                continue;
            }

            if (LooksLikePlaceholderPatchText(oldText) || LooksLikePlaceholderPatchText(newText))
            {
                validationError = "Patch planner edit used placeholder text instead of exact current source or real replacement source.";
                return null;
            }

            edits.Add(new CodingPatchEdit(path, oldText, newText, replaceEntireFile));
        }

        if (edits.Count == 0)
        {
            validationError = "Patch planner edits array did not contain any usable path/oldText/newText edit objects.";
            return null;
        }

        if (!pathEvidence.TryValidate(edits, out var pathValidationError))
        {
            validationError = pathValidationError;
            return null;
        }

        if (RequiresCompleteWpfXamlReplacement(actionPlan)
            && !ValidateCompleteWpfXamlReplacement(edits, out var wpfValidationError))
        {
            validationError = wpfValidationError;
            return null;
        }

        var criteriaCoverage = ReadStringArray(root, "criteria_coverage", "criteriaCoverage")
            .Select(TrimForPlanner)
            .Where(coverage => !string.IsNullOrWhiteSpace(coverage))
            .Take(8)
            .ToArray();
        var selectedPath = TrimForPlanner(ReadString(root, "selected_path", ReadString(root, "selectedPath", string.Empty)));
        var actionSelectedPath = TrimForPlanner(actionPlan?.SelectedPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(actionSelectedPath))
        {
            if (!string.IsNullOrWhiteSpace(selectedPath)
                && !selectedPath.Equals(actionSelectedPath, StringComparison.OrdinalIgnoreCase))
            {
                validationError = $"Patch planner selected_path '{selectedPath}' did not match action selected_path '{actionSelectedPath}'.";
                return null;
            }

            selectedPath = actionSelectedPath;
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
            TrimForPlanner(ReadString(root, "stop_reason", ReadString(root, "stopReason", string.Empty))),
            selectedPath,
            criteriaCoverage);
    }

    private static CodingPatchPlan BuildNoPatch(string diagnostic, string rawOutput = "")
    {
        var reason = string.IsNullOrWhiteSpace(diagnostic)
            ? "Patch planner did not return a safe patch preview."
            : TrimForPlanner(diagnostic);
        var rawExcerpt = TrimForPlanner(rawOutput);
        if (!string.IsNullOrWhiteSpace(rawExcerpt))
        {
            reason = TrimForPlanner($"{reason} Raw patch planner output excerpt: {rawExcerpt}");
        }

        return CodingPatchPlan.NoPatch with
        {
            Summary = reason,
            StopReason = reason
        };
    }

    private sealed class PatchPlannerPathEvidence
    {
        private static readonly Regex AbsoluteDiagnosticPathPattern = new(
            @"(?<path>[A-Za-z]:[\\/][^\r\n:]+?\.(?:xaml\.cs|csproj|xaml|cs|vb|fs|razor|props|targets|json|xml|config))(?=\(|:|\s|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RelativeDiagnosticPathPattern = new(
            @"(?<path>(?:[\w .-]+[\\/])*[\w .-]+\.(?:xaml\.cs|csproj|xaml|cs|vb|fs|razor|props|targets|json|xml|config))\(\d+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string workspaceRoot;
        private readonly HashSet<string> diagnosticTargets;
        private readonly HashSet<string> approvedFilePaths;
        private readonly bool requireDiagnosticTargetsOnly;

        private PatchPlannerPathEvidence(
            string workspaceRoot,
            HashSet<string> diagnosticTargets,
            HashSet<string> approvedFilePaths,
            bool requireDiagnosticTargetsOnly)
        {
            this.workspaceRoot = workspaceRoot;
            this.diagnosticTargets = diagnosticTargets;
            this.approvedFilePaths = approvedFilePaths;
            this.requireDiagnosticTargetsOnly = requireDiagnosticTargetsOnly;
        }

        public static PatchPlannerPathEvidence From(CodingContextPack contextPack)
        {
            var text = contextPack.Text ?? string.Empty;
            var workspaceRoot = ExtractWorkspaceRoot(text);
            var diagnosticTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var approvedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
                {
                    AddPathVariants(approvedFilePaths, rawLine["FILE:".Length..], workspaceRoot);
                }
                else if (rawLine.StartsWith("ABSOLUTE PATH:", StringComparison.OrdinalIgnoreCase))
                {
                    AddPathVariants(approvedFilePaths, rawLine["ABSOLUTE PATH:".Length..], workspaceRoot);
                }

                foreach (Match match in AbsoluteDiagnosticPathPattern.Matches(rawLine))
                {
                    AddPathVariants(diagnosticTargets, match.Groups["path"].Value, workspaceRoot);
                }

                if (rawLine.Contains(" error ", StringComparison.OrdinalIgnoreCase)
                    || rawLine.Contains(" warning ", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match match in RelativeDiagnosticPathPattern.Matches(rawLine))
                    {
                        AddPathVariants(diagnosticTargets, match.Groups["path"].Value, workspaceRoot);
                    }
                }
            }

            var effectiveDiagnosticTargets = contextPack.IncludesLastFailure
                ? diagnosticTargets
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new PatchPlannerPathEvidence(
                workspaceRoot,
                effectiveDiagnosticTargets,
                approvedFilePaths,
                contextPack.IncludesLastFailure && RequiresWpfCs0246DiagnosticTargetOnly(text, effectiveDiagnosticTargets));
        }

        public bool TryValidate(IReadOnlyList<CodingPatchEdit> edits, out string validationError)
        {
            validationError = string.Empty;
            if (diagnosticTargets.Count == 0)
            {
                return true;
            }

            var allowedPaths = requireDiagnosticTargetsOnly
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(approvedFilePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var target in diagnosticTargets)
            {
                allowedPaths.Add(target);
                foreach (var paired in GetDirectlyPairedWpfPaths(target))
                {
                    allowedPaths.Add(paired);
                }
            }

            foreach (var edit in edits)
            {
                var editPathVariants = BuildPathVariants(edit.Path, workspaceRoot);
                if (!editPathVariants.Any(allowedPaths.Contains))
                {
                    validationError = string.Join(
                        " ",
                        $"Patch planner edit path '{TrimForPlanner(edit.Path)}' was not present in the latest compiler diagnostic targets or approved FILE excerpts.",
                        requireDiagnosticTargetsOnly
                            ? "Latest WPF CS0246 diagnostics require editing the diagnostic target file or its direct XAML/code-behind pair only."
                            : string.Empty,
                        $"Latest diagnostic targets: {FormatPathList(diagnosticTargets)}.",
                        $"Approved FILE excerpts: {FormatPathList(approvedFilePaths)}.",
                        "Retry with one of those target paths, or return has_patch=false.");
                    return false;
                }

                if (requireDiagnosticTargetsOnly
                    && editPathVariants.Any(IsWpfCodeBehindPath)
                    && TryGetUnsafeWpfUsingRepairReason(edit, out var unsafeUsingRepairReason))
                {
                    validationError = unsafeUsingRepairReason;
                    return false;
                }
            }

            return true;
        }

        private static string ExtractWorkspaceRoot(string text)
        {
            foreach (var line in text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Workspace root:", StringComparison.OrdinalIgnoreCase))
                {
                    return line["Workspace root:".Length..].Trim();
                }
            }

            return string.Empty;
        }

        private static void AddPathVariants(HashSet<string> paths, string path, string workspaceRoot)
        {
            foreach (var variant in BuildPathVariants(path, workspaceRoot))
            {
                paths.Add(variant);
            }
        }

        private static IReadOnlyList<string> BuildPathVariants(string path, string workspaceRoot)
        {
            var normalized = NormalizePath(path, workspaceRoot);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return Array.Empty<string>();
            }

            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                normalized
            };

            var fileName = Path.GetFileName(normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                variants.Add(fileName);
            }

            return variants.ToArray();
        }

        private static string NormalizePath(string path, string workspaceRoot)
        {
            var normalized = path.Trim().Trim('"', '\'', '`').Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            var normalizedRoot = workspaceRoot.Trim().Trim('"', '\'', '`').Replace('\\', '/').TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(normalizedRoot)
                && normalized.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[(normalizedRoot.Length + 1)..];
            }

            return normalized.TrimStart('/').Trim();
        }

        private static IEnumerable<string> GetDirectlyPairedWpfPaths(string target)
        {
            if (target.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            {
                yield return target[..^3];
            }
            else if (target.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{target}.cs";
            }
        }

        private static bool IsWpfCodeBehindPath(string path) =>
            path.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase);

        private static bool TryGetUnsafeWpfUsingRepairReason(CodingPatchEdit edit, out string reason)
        {
            reason = string.Empty;
            if (edit.ReplaceEntireFile && !edit.NewText.Contains("partial class", StringComparison.Ordinal))
            {
                reason = "WPF CS0246 using repair rejected: replace_file for .xaml.cs must include the complete valid source with namespace, partial class, constructor, handlers, and using directives.";
                return true;
            }

            if (LooksLikeUsingDirectiveOnlyText(edit.NewText))
            {
                reason = "WPF CS0246 using repair rejected: newText contains only using directives. Preserve the existing namespace/class by replacing the exact namespace line with using directives followed by that same namespace line, or use replace_file with complete valid source.";
                return true;
            }

            if (edit.OldText.Contains("namespace ", StringComparison.Ordinal)
                && edit.NewText.Contains("using System.Windows", StringComparison.Ordinal)
                && !edit.NewText.Contains("namespace ", StringComparison.Ordinal))
            {
                reason = "WPF CS0246 using repair rejected: the edit replaces a namespace line without preserving it. newText must include the added using directives followed by the exact same namespace line.";
                return true;
            }

            return false;
        }

        private static bool LooksLikeUsingDirectiveOnlyText(string text)
        {
            var lines = text.ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0
                && lines.All(line => line.StartsWith("using ", StringComparison.Ordinal)
                    && line.EndsWith(";", StringComparison.Ordinal));
        }

        private static bool RequiresWpfCs0246DiagnosticTargetOnly(string text, HashSet<string> diagnosticTargets) =>
            diagnosticTargets.Any(target =>
                target.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                || target.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            && text.Contains("CS0246", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("Window", StringComparison.Ordinal)
                || text.Contains("RoutedEventArgs", StringComparison.Ordinal)
                || text.Contains("TextBox", StringComparison.Ordinal)
                || text.Contains("Button", StringComparison.Ordinal)
                || text.Contains("CheckBox", StringComparison.Ordinal)
                || text.Contains("Label", StringComparison.Ordinal)
                || text.Contains("Grid", StringComparison.Ordinal));

        private static string FormatPathList(IEnumerable<string> paths)
        {
            var formatted = paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
            return formatted.Length == 0 ? "none" : string.Join(", ", formatted);
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : null;
    }

    private static bool TryParseJsonDocument(string json, out JsonDocument document, out string error)
    {
        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        try
        {
            document = JsonDocument.Parse(json, options);
            error = string.Empty;
            return true;
        }
        catch (JsonException first)
        {
            var firstError = first.Message;
            var repaired = json;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var nextRepair = RepairInvalidJsonStringCharacters(repaired);
                if (nextRepair.Equals(repaired, StringComparison.Ordinal))
                {
                    break;
                }

                repaired = nextRepair;
                try
                {
                    document = JsonDocument.Parse(repaired, options);
                    error = string.Empty;
                    return true;
                }
                catch (JsonException)
                {
                }
            }

            var looseBackslashRepair = RepairInvalidJsonBackslashesLoosely(repaired);
            if (!looseBackslashRepair.Equals(repaired, StringComparison.Ordinal))
            {
                try
                {
                    document = JsonDocument.Parse(looseBackslashRepair, options);
                    error = string.Empty;
                    return true;
                }
                catch (JsonException second)
                {
                    document = null!;
                    error = $"{second.Message} First parse error before string-character repair: {firstError}";
                    return false;
                }
            }

            document = null!;
            error = firstError;
            return false;
        }
    }

    private static string RepairInvalidJsonStringCharacters(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        var repaired = new StringBuilder(json.Length + 32);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var ch = json[index];
            if (ch == '"')
            {
                var precedingBackslashes = CountPrecedingBackslashes(json, index);
                if (precedingBackslashes % 2 == 0)
                {
                    inString = !inString;
                }

                repaired.Append(ch);
                continue;
            }

            if (!inString)
            {
                repaired.Append(ch);
                continue;
            }

            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch))
            {
                AppendEscapedJsonControlCharacter(repaired, ch);
                continue;
            }

            if (ch != '\\')
            {
                repaired.Append(ch);
                continue;
            }

            if (index + 1 >= json.Length)
            {
                repaired.Append(@"\\");
                continue;
            }

            var next = json[index + 1];
            if (IsValidJsonEscape(next))
            {
                repaired.Append(ch);
                continue;
            }

            if (next == 'u' && index + 5 < json.Length && IsHexQuad(json, index + 2))
            {
                repaired.Append(ch);
                continue;
            }

            repaired.Append(@"\\");
        }

        return repaired.ToString();
    }

    private static string RepairInvalidJsonBackslashesLoosely(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        var repaired = new StringBuilder(json.Length + 32);
        for (var index = 0; index < json.Length; index++)
        {
            var ch = json[index];
            if (ch != '\\')
            {
                repaired.Append(ch);
                continue;
            }

            if (index + 1 >= json.Length)
            {
                repaired.Append(@"\\");
                continue;
            }

            var next = json[index + 1];
            if (IsValidJsonEscape(next)
                || next == 'u' && index + 5 < json.Length && IsHexQuad(json, index + 2))
            {
                repaired.Append(ch);
                continue;
            }

            repaired.Append(@"\\");
        }

        return repaired.ToString();
    }

    private static void AppendEscapedJsonControlCharacter(StringBuilder builder, char ch)
    {
        switch (ch)
        {
            case '\r':
                builder.Append(@"\r");
                break;
            case '\n':
                builder.Append(@"\n");
                break;
            case '\t':
                builder.Append(@"\t");
                break;
            default:
                builder.Append(@"\u");
                builder.Append(((int)ch).ToString("x4"));
                break;
        }
    }

    private static bool IsValidJsonEscape(char ch) =>
        ch is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't';

    private static bool IsHexQuad(string value, int start)
    {
        for (var index = start; index < start + 4; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountPrecedingBackslashes(string value, int quoteIndex)
    {
        var count = 0;
        for (var index = quoteIndex - 1; index >= 0 && value[index] == '\\'; index--)
        {
            count++;
        }

        return count;
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

    private static string ReadJoinedStringArrayAny(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            var lines = value
                .EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
            if (lines.Length > 0)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }

        return string.Empty;
    }

    private static bool TryReadArray(JsonElement root, out JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out value) && value.ValueKind is JsonValueKind.Array)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsReplaceEntireFileMode(string value)
    {
        var normalized = value.Replace('-', '_').Trim();
        return normalized.Equals("replace_file", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("replace_entire_file", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("write_file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePlaceholderPatchText(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '`');
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized.Equals("exact current text", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("replacement text", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("complete file text", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("complete file t", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("YourNamespace.", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ExampleNamespace.", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("compacted patch-planner context", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("middle omitted for local runtime context budget", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("TODO", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("...", StringComparison.Ordinal);
    }

    private static bool RequiresCompleteWpfXamlReplacement(CodingActionPlan? actionPlan)
    {
        if (actionPlan is null)
        {
            return false;
        }

        var evidence = string.Join(
            ' ',
            actionPlan.CommandGoal,
            actionPlan.UnderstoodGoal,
            actionPlan.Summary,
            string.Join(' ', actionPlan.AcceptanceCriteria ?? Array.Empty<string>()),
            string.Join(' ', actionPlan.InfoUsed ?? Array.Empty<string>()));
        return evidence.Contains("structural XAML", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("XAML structural", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("layout overlap", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("simple Grid layout overlap", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("non-overlapping WPF XAML", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("duplicate x:Name", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("XML parse", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateCompleteWpfXamlReplacement(
        IReadOnlyList<CodingPatchEdit> edits,
        out string validationError)
    {
        validationError = string.Empty;
        var xamlEdits = edits
            .Where(edit => edit.Path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (xamlEdits.Length == 0)
        {
            validationError = "WPF structural repair requires a complete replace_file edit for the affected .xaml file.";
            return false;
        }

        foreach (var edit in xamlEdits)
        {
            if (!edit.ReplaceEntireFile)
            {
                validationError = "WPF structural repair must use mode=\"replace_file\" for .xaml edits, not additive snippets or partial replacements.";
                return false;
            }

            if (!LooksLikeCompleteWpfXamlDocument(edit.NewText))
            {
                validationError = "WPF structural replace_file edit must provide complete valid-looking XAML with a Window/UserControl/Page root and matching closing tag.";
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeCompleteWpfXamlDocument(string text)
    {
        var normalized = text.ReplaceLineEndings("\n").Trim();
        return (normalized.Contains("<Window", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("</Window>", StringComparison.OrdinalIgnoreCase))
            || (normalized.Contains("<UserControl", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("</UserControl>", StringComparison.OrdinalIgnoreCase))
            || (normalized.Contains("<Page", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("</Page>", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName, string camelCasePropertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            && !root.TryGetProperty(camelCasePropertyName, out value))
        {
            return Array.Empty<string>();
        }

        return value.ValueKind is JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind is JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : Array.Empty<string>();
    }

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
        return normalized.Length <= 800 ? normalized : normalized[..800];
    }

    private static string TrimForRepairPrompt(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 4_000 ? normalized : normalized[..4_000];
    }

    private static string TrimForPatchRepairPrompt(string value)
    {
        var normalized = string.Join(
                Environment.NewLine,
                value.ReplaceLineEndings(Environment.NewLine)
                    .Split(Environment.NewLine, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.Contains("Raw patch planner output excerpt", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.Contains("Response reached the configured output limit", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.Contains("```json", StringComparison.OrdinalIgnoreCase)))
            .Trim();
        return normalized.Length <= 12_000 ? normalized : normalized[^12_000..];
    }

    private static string TrimPreviousPatchOutputForRepair(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        if (value.Contains("Response reached the configured output limit", StringComparison.OrdinalIgnoreCase)
            || value.Length > 24_000)
        {
            return "Previous output was truncated or overlong. Do not continue it, repeat it, or copy it; return a fresh compact JSON object.";
        }

        return TrimForRepairPrompt(value);
    }
}
