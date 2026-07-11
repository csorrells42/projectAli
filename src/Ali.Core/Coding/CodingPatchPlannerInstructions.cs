namespace Ali.Core.Coding;

internal static class CodingPatchPlannerInstructions
{
    private const int MaxPatchPlannerContextCharacters = 3_200;

    public static string Build(CodingContextPack contextPack, CodingActionPlan? actionPlan = null)
    {
        if (ShouldUseCompactExistingFeaturePrompt(contextPack, actionPlan))
        {
            return BuildCompactExistingFeaturePrompt(contextPack, actionPlan);
        }

        var lines = new List<string>
        {
            "You are Ali's programming patch planner. Return exactly one JSON object and no other text.",
            "Do not answer the user. Do not explain. Do not include markdown or hidden reasoning.",
            "Use the current user message as the only requested goal. Ignore stale goals from previous turns unless the user explicitly says continue, next, or keep going.",
            "Design the solution dynamically from the user's goal, the approved evidence, and the model-selected path. Do not route by canned phrase tables.",
            "Loop: infer the Question, use the Info below, make the Decision, Execute one guarded patch preview or a no-patch reason, then expect validation evidence on the next turn.",
            "Patch only from approved context. Existing-file edits need exact old text, or mode=\"replace_file\" with complete replacement text. New files use empty old text.",
            "oldText is always current source before the requested change. Never put requested new labels, content, names, handlers, or future-state text in oldText unless that exact text already appears in the approved current file evidence.",
            "For edit p/path values, prefer the FILE relative path shown in the context. If using a Windows absolute path, write it with forward slashes or escape each backslash as \\\\ so the JSON remains valid.",
            "When the context includes compiler diagnostics, every edit path must be a diagnostic target file, a directly paired XAML/code-behind file, or a FILE path shown in the approved excerpts.",
            "replace_file is only for replacing a text-like existing file; Ali will still preview the diff and require confirmation before writing.",
            "A replace_file edit's n value must be the complete final file text, not a snippet or partial method.",
            "For mode=\"replace_file\", set o/oldText to an empty string. Do not copy the old full file into o/oldText; it wastes output budget and can make the JSON unparseable.",
            "Avoid n_lines for ordinary feature edits. Use compact o/n strings with escaped \\n when needed; reserve n_lines for structural whole-file repair only.",
            "If old text would be short, generic, or likely repeated, use mode=\"replace_file\" with complete final file text instead of an ambiguous substring replacement.",
            "For WPF, keep project SDK/UseWPF/target framework, x:Class, namespace, partial class, .xaml, and .xaml.cs aligned. Include required source/project files and validate through build/XAML/command checks.",
            "For existing WPF surfaces, preserve existing x:Name/Name values and exact XAML event handler names unless the user explicitly asks to rename/remove them.",
            "When the selected path is Existing feature or bug fix and there is no structural validation failure, return compact exact edits only: no mode=\"replace_file\", no n_lines, no full Window/Grid serialization.",
            "When the request says to keep existing button/label behavior, do not rename existing Click handlers; update the existing .xaml.cs method bodies instead.",
            "For existing WPF Window/UserControl edits, prefer exact unique oldText replacements for the smallest XAML region and C# method(s). Use mode=\"replace_file\" for whole-window rewrites, non-unique snippets, or structural retry after validation failure.",
            "For small existing WPF feature edits without structural validation failure, do not use replace_file for .xaml. Use the WPF exact patch anchors from the context as oldText, even when the XAML file is compacted onto one long line.",
            "If adding one visible WPF control, patch the nearest exact XAML element/row anchor and any affected Grid.Row attributes as small edits instead of serializing the whole Window.",
            "When inserting a control between existing Grid rows, update RowDefinitions and shift Grid.Row values for later sibling controls so simple visible controls do not share a row/cell unless the layout explicitly spans or nests them.",
            "Before choosing Grid.Row for a new visible WPF child, compare against the WPF Grid row map. If that row is occupied in a simple sibling Grid, include edits that increment the occupied child and every later sibling row, or choose a genuinely empty row.",
            "If a compact WPF edit would require many row shifts, emit several small oldText/newText edits using exact anchor strings; do not switch to full-file XAML unless structural validation has failed.",
            "For WPF structural validation failures such as duplicate x:Name/Name, XML parse failures, or simple Grid layout overlap, use mode=\"replace_file\" for the affected .xaml file with complete valid XAML. Do not repair structural layout by inserting duplicate controls or partial snippets.",
            "For WPF layout-overlap repair, preserve the actual x:Class value, xmlns declarations, named controls, Click handlers, bindings, and code-behind contract from the excerpts. Use Grid RowDefinitions/ColumnDefinitions, StackPanel, DockPanel, or another valid layout container so visible controls no longer overlap.",
            "Never invent WPF placeholder namespaces/classes such as YourNamespace.MainWindow or Example.MainWindow. If the existing XAML says x:Class=\"AliProjects.MainWindow\", the replacement must keep that exact value unless the matching .xaml.cs namespace/class is also deliberately changed.",
            "For WPF CheckBox state that affects an existing button/command, prefer a named CheckBox plus reading IsChecked inside the existing handler; do not add Checked/Unchecked XAML events unless immediate toggle behavior is required.",
            "For XAML binding validation failures shaped like File.xaml -> PropertyName, repair the binding directly: remove the binding attribute when code-behind already reads the named control state, or add a real DataContext/property only when the requested design needs binding.",
            "When removing a single unnecessary XAML binding attribute, replace the exact element/attribute text only. Do not rewrite the whole Window, remove controls, or omit closing XAML tags.",
            "For WPF CS0103/name build repairs, patch the symbol named in the diagnostic: add the matching x:Name/Name in XAML, use an existing XAML name from the excerpt, add a missing class member only when the code intentionally uses state, or remove obsolete Checked/Unchecked handlers when direct IsChecked reads replace them.",
            "If any XAML event attribute is added or preserved, the same patch must include the matching method in the .xaml.cs partial class, or remove the event attribute if it is unnecessary.",
            "For WPF member removal in .xaml.cs, remove complete method/property declarations only. If removing multiple members, changing brace balance, or repairing a previous exact-match failure, you MUST use mode=\"replace_file\" with the complete valid .xaml.cs file text.",
            "For existing C# code-behind behavior changes inside an existing handler, prefer replacing the exact complete method body or complete method text from the excerpt. Do not replace the whole .xaml.cs file unless class/namespace/usings/handlers must change.",
            "If repairing multiple WPF handlers or restoring regressed code-behind behavior, use mode=\"replace_file\" for MainWindow.xaml.cs with complete valid C# including using directives, namespace, partial class, constructor, and every referenced event handler. Never return only method snippets as replacement text for a whole .xaml.cs file.",
            "For WPF build repairs, make the smallest diagnostic-specific repair. For CS0246 missing WPF control types such as TextBox, CheckBox, Label, Button, Grid, or RoutedEventArgs in .xaml.cs, add the missing using only: System.Windows.Controls for controls, System.Windows for RoutedEventArgs/Window. Do not edit XAML for this diagnostic unless the diagnostic names XAML.",
            "When adding WPF using directives to a file-scoped namespace file, preserve the namespace line: replace the exact namespace line with using directives followed by the same namespace line, or replace the whole file with complete valid source.",
            "Patch the project file and XAML together only when the diagnostic proves project/XAML alignment is broken: use a Windows target framework, UseWPF=true, System.Windows usings, and a complete public partial Window class containing InitializeComponent and event handlers.",
            "Never put WPF event handlers as top-level partial methods; they belong inside the matching partial Window/UserControl class.",
            "Never emit placeholders, XML tags, markdown fences, or angle-bracket notes inside C# or XAML replacement text; replacement text must be compilable source.",
            "Never use literal placeholder strings such as exact current text, replacement text, complete file text, TODO, or ellipses in o/oldText or n/newText.",
            "Keep patches small: ordinary patches <=10 edits; WPF/window bundles <=16 coordinated edits.",
            "Do not invent tool results, builds, tests, files, or hidden project facts.",
            "If the request cannot be patched safely from the provided excerpts, return has_patch false with a short stop_reason.",
            "Use compact JSON. Preferred edit fields: p=path, o=oldText, n=newText.",
            "For p/path, use an actual FILE relative path from the evidence below. Never use placeholder, tutorial, demo, temp, or invented paths.",
            "Patch responses must include: has_patch=true, edits with real p/o/n values copied from evidence and new source, summary, confidence. Do not copy schema labels into o or n.",
            "{\"has_patch\":false,\"summary\":\"Need an exact target file first.\",\"confidence\":0.2,\"stop_reason\":\"No editable file excerpt matched the requested change.\",\"edits\":[]}",
            BuildPatchCapabilityGuide(actionPlan),
            BuildCompactWpfPatchGuide(),
            "Approved read-only patch evidence, compacted for the local runtime:",
            TrimForPatchPlannerContext(contextPack.Text)
        };

        if (actionPlan is { UseCodingTool: true })
        {
            lines.Add("Model-selected action decision for this Execute step:");
            lines.Add(string.IsNullOrWhiteSpace(actionPlan.SelectedPath)
                ? "Selected path: not provided."
                : $"Selected path: {actionPlan.SelectedPath}");
            lines.Add("Do not change the selected path in a has_patch true response; omit selected_path or repeat this exact path.");
            lines.Add(string.IsNullOrWhiteSpace(actionPlan.Command)
                ? "Action command evidence: not provided."
                : $"Action command evidence, not an execution instruction: {actionPlan.Command}");
            lines.Add(string.IsNullOrWhiteSpace(actionPlan.SelectedTool)
                ? "Selected tool evidence: not provided."
                : $"Selected tool evidence, not a patch-authoring constraint: {actionPlan.SelectedTool}");
            lines.Add(string.IsNullOrWhiteSpace(actionPlan.CommandGoal)
                ? "Command goal: not provided."
                : $"Command goal: {actionPlan.CommandGoal}");
            lines.Add(string.IsNullOrWhiteSpace(actionPlan.Summary)
                ? "Decision summary: not provided."
                : $"Decision summary: {actionPlan.Summary}");
            lines.Add(string.IsNullOrWhiteSpace(actionPlan.UnderstoodGoal)
                ? "Understood goal: not provided."
                : $"Understood goal: {actionPlan.UnderstoodGoal}");
            var infoUsed = actionPlan.InfoUsed ?? Array.Empty<string>();
            if (infoUsed.Count > 0)
            {
                lines.Add("Info used for decision:");
                foreach (var info in infoUsed.Take(5))
                {
                    lines.Add($"- {info}");
                }
            }
            else
            {
                lines.Add("Info used for decision: not provided.");
            }

            var acceptanceCriteria = actionPlan.AcceptanceCriteria ?? Array.Empty<string>();
            if (acceptanceCriteria.Count > 0)
            {
                lines.Add("Acceptance criteria:");
                foreach (var criterion in acceptanceCriteria.Take(6))
                {
                    lines.Add($"- {criterion}");
                }
            }
            else
            {
                lines.Add("Acceptance criteria: not provided.");
            }

            lines.Add(string.IsNullOrWhiteSpace(actionPlan.ExecutionMode)
                ? "Execution mode: not provided."
                : $"Execution mode: {actionPlan.ExecutionMode}");
            lines.Add($"Decision confidence: {actionPlan.Confidence:0.###}");
            lines.Add("Use this decision as the chosen path for the current step, unless the approved context proves it cannot be patched safely.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool ShouldUseCompactExistingFeaturePrompt(CodingContextPack contextPack, CodingActionPlan? actionPlan)
    {
        if (actionPlan is not { UseCodingTool: true }
            || !actionPlan.ExecutionMode.Equals("model_patch_preview", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (actionPlan.SelectedPath.Equals("Existing feature or bug fix", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var context = contextPack.Text ?? string.Empty;
        return context.Contains("WPF exact patch anchors for oldText:", StringComparison.Ordinal)
            && context.Contains("WPF Grid row map for patch planning:", StringComparison.Ordinal);
    }

    private static string BuildCompactExistingFeaturePrompt(CodingContextPack contextPack, CodingActionPlan? actionPlan)
    {
        var lines = new List<string>
        {
            "You are Ali's compact patch planner for an existing feature edit.",
            "Return exactly one valid JSON object and no other text. No markdown fences.",
            "Use only FILE paths and exact oldText anchors from the approved context.",
            "Patch preview only; files are not written until the owner confirms apply.",
            "Use compact edits with full keys: path, oldText, newText. Do not use n_lines or replace_file.",
            "oldText must be copied exactly from an approved anchor, including every attribute, space, and quote.",
            "For additions, oldText is an existing neighboring anchor from the current file; newText is that same anchor plus the added source. Do not put the requested new content in oldText.",
            "For WPF, preserve existing x:Name values and Click handler names. Edit existing handler bodies when behavior changes.",
            "Use the WPF event handler map to pair existing visible controls with their .xaml.cs methods before editing behavior.",
            "For WPF Grid insertions, update RowDefinitions and shift later Grid.Row values when adding a new row between existing controls.",
            "Use the WPF Grid row map: do not put a new visible sibling into an occupied Grid.Row/Grid.Column unless the patch also moves the existing occupant and all later siblings.",
            "If the latest failure says behavior coverage is missing, include a .xaml.cs/code-behind edit using a WPF code-behind method anchor.",
            "For a WPF request that both adds a visible control and changes what happens on click/send/selection, return coordinated XAML and .xaml.cs edits in the same patch.",
            "If an exact safe patch cannot be made from the anchors, return has_patch=false with stop_reason.",
            "Schema only: {\"has_patch\":true,\"edits\":[{\"path\":\"FILE relative path\",\"oldText\":\"exact current text copied from approved context\",\"newText\":\"replacement text\"}],\"summary\":\"short summary\",\"confidence\":0.78}",
            "Current decision:",
            string.IsNullOrWhiteSpace(actionPlan?.CommandGoal)
                ? "Command goal: not provided."
                : $"Command goal: {actionPlan.CommandGoal}",
            string.IsNullOrWhiteSpace(actionPlan?.UnderstoodGoal)
                ? "Understood goal: not provided."
                : $"Understood goal: {actionPlan.UnderstoodGoal}",
            "Approved context:",
            TrimForCompactExistingFeatureContext(contextPack.Text)
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPatchCapabilityGuide(CodingActionPlan? actionPlan)
    {
        var selectedPath = actionPlan?.SelectedPath ?? string.Empty;
        var selected = CodingAbilityCatalog.ProgrammingCapabilityPaths.FirstOrDefault(path =>
            path.Name.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            var lines = new List<string>
            {
                $"Selected programming capability path: {selected.Name}",
                $"When to use: {selected.WhenToUse}",
                "Path building blocks:"
            };
            lines.AddRange(selected.BuildingBlocks.Take(5).Select(block => $"- {block}"));
            return string.Join(Environment.NewLine, lines);
        }

        return string.Join(
            Environment.NewLine,
            "Programming capability paths available:",
            string.Join(
                Environment.NewLine,
                CodingAbilityCatalog.ProgrammingCapabilityPaths.Select(path => $"- {path.Name}: {path.WhenToUse}")));
    }

    private static string BuildCompactWpfPatchGuide()
    {
        var lines = new[]
        {
            "Compact WPF patch guide:",
            "- Align x:Class, namespace, partial class, .xaml, and .xaml.cs names.",
            "- Preserve existing x:Name/Name and Click handler names for existing controls unless the user explicitly asks to rename/remove them.",
            "- For kept behavior, edit existing handler bodies; do not invent replacement handler names for Send/Clear/Exit/etc.",
            "- For existing Window/UserControl changes, patch the smallest unique XAML region and C# method; use complete .xaml/.xaml.cs replacements only for whole-window rewrites or structural retry.",
            "- For compact one-line XAML, use the WPF exact patch anchors as oldText and make small element/attribute replacements.",
            "- Existing feature or bug fix path means compact exact edits: avoid n_lines and replace_file unless the context says structural validation failed.",
            "- For CheckBox state, name the CheckBox and read IsChecked from the existing action handler unless Checked/Unchecked events are truly needed.",
            "- For unknown XAML bindings on a named CheckBox whose IsChecked is read in code-behind, remove the unnecessary IsChecked binding attribute rather than adding unused state.",
            "- For CS0103 in WPF code-behind, fix the exact missing name by aligning XAML names and class members; unrelated visual attributes do not repair the build.",
            "- For CS0246 missing WPF control names in .xaml.cs, add the missing namespace using; do not rewrite XAML or handler bodies.",
            "- Every XAML event handler referenced by Click/Checked/Unchecked/etc. must exist inside the matching partial class in the same patch.",
            "- When removing multiple WPF code-behind members, use complete .xaml.cs replacement; snippet removals are too fragile.",
            "- If the repair touches multiple handlers, write a complete .xaml.cs replacement; method-only snippets outside the class are invalid.",
            "- For existing handler behavior changes, patch inside the existing class/method and preserve namespace, base class, constructor, and other handlers.",
            "- Prefer Grid/DockPanel layout and MVVM properties/commands for visible behavior.",
            "- Define resources, converters, selectors, and templates before binding to them.",
            "- Keep code-behind minimal for shell events and services.",
            "- Include every new file needed by the requested window/workflow."
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string TrimForPatchPlannerContext(string value)
    {
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        if (normalized.Length <= MaxPatchPlannerContextCharacters)
        {
            return normalized;
        }

        var sections = new List<string>();
        AddMatchingLines(
            sections,
            normalized,
            "Workspace root:",
            "Current solution/project:",
            "Current user request:",
            "Current coding task goal:",
            "- Latest receipt:",
            "- Latest validation:",
            "- Latest receipt detail:",
            "- Latest validation detail:",
            "- Changed files:");
        AddSection(sections, normalized, "WPF event handler map for behavior edits:", 700);
        AddSection(sections, normalized, "WPF code-behind method anchors for behavior edits:", 1_200);
        AddSection(sections, normalized, "WPF Grid row map for patch planning:", 900);
        AddSection(sections, normalized, "Editable file excerpts for patch planning:", 1_800);
        AddSection(sections, normalized, "Last failed dotnet command", 800);
        AddSection(sections, normalized, "Diagnostic file excerpts:", 1_300);
        AddSection(sections, normalized, "Latest patch preview failure:", 1_400);
        AddSection(sections, normalized, "Retry patch contract:", 1_400);

        var focused = string.Join(Environment.NewLine, sections.Where(section => !string.IsNullOrWhiteSpace(section))).Trim();
        if (!string.IsNullOrWhiteSpace(focused))
        {
            return TrimMiddle(focused, MaxPatchPlannerContextCharacters);
        }

        return TrimMiddle(normalized, MaxPatchPlannerContextCharacters);
    }

    private static string TrimForCompactExistingFeatureContext(string value)
    {
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        var sections = new List<string>();
        AddMatchingLines(
            sections,
            normalized,
            "Workspace root:",
            "Current solution/project:",
            "Current user request:");
        AddSection(sections, normalized, "WPF event handler map for behavior edits:", 700);
        AddSection(sections, normalized, "WPF code-behind method anchors for behavior edits:", 1_000);
        AddSection(sections, normalized, "WPF Grid row map for patch planning:", 450);
        AddSection(sections, normalized, "WPF exact patch anchors for oldText:", 800);
        if (sections.Count == 0)
        {
            AddSection(sections, normalized, "Editable file excerpts for patch planning:", 1_700);
        }

        var focused = string.Join(Environment.NewLine, sections.Where(section => !string.IsNullOrWhiteSpace(section))).Trim();
        return string.IsNullOrWhiteSpace(focused)
            ? TrimAtLineBoundary(normalized, 1_700)
            : TrimAtLineBoundary(focused, 3_000);
    }

    private static void AddMatchingLines(List<string> sections, string text, params string[] prefixes)
    {
        var lines = text.Split(Environment.NewLine, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Take(8)
            .ToArray();
        if (lines.Length > 0)
        {
            sections.Add(string.Join(Environment.NewLine, lines));
        }
    }

    private static void AddSection(List<string> sections, string text, string header, int maxCharacters)
    {
        var start = text.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return;
        }

        var end = FindNextSectionStart(text, start + header.Length);
        var section = end > start ? text[start..end] : text[start..];
        sections.Add(TrimAtLineBoundary(section.Trim(), maxCharacters));
    }

    private static int FindNextSectionStart(string text, int start)
    {
        var headers = new[]
        {
            "Workspace map",
            "Package references",
            "Current coding state",
            "Targeted validation",
            "Relevant source/config files",
            "WPF event handler map for behavior edits",
            "WPF code-behind method anchors for behavior edits",
            "WPF Grid row map for patch planning",
            "WPF exact patch anchors for oldText",
            "Editable file excerpts for patch planning",
            "Last failed dotnet command",
            "Diagnostic file excerpts",
            "Latest patch preview failure",
            "Retry patch contract",
            "Relevant workspace matches"
        };

        var best = -1;
        foreach (var header in headers)
        {
            var index = text.IndexOf(Environment.NewLine + header, start, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static string TrimAtLineBoundary(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        var clipped = value[..maxCharacters].TrimEnd();
        var lastNewLine = clipped.LastIndexOf(Environment.NewLine, StringComparison.Ordinal);
        if (lastNewLine > maxCharacters / 2)
        {
            clipped = clipped[..lastNewLine].TrimEnd();
        }

        return clipped;
    }

    private static string TrimMiddle(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        var headLength = maxCharacters / 2;
        var tailLength = maxCharacters - headLength;
        var head = value[..headLength].TrimEnd();
        var tail = value[^tailLength..].TrimStart();
        return string.Join(
            Environment.NewLine,
            head,
            "... compacted patch-planner context: middle omitted for local runtime context budget ...",
            tail);
    }
}
