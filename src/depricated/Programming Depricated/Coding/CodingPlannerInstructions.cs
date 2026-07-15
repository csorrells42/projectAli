using Ali.Core.Runtime;

namespace Ali.Core.Coding;

internal static class CodingPlannerInstructions
{
    private const int MaxActionPlannerContextCharacters = 1_600;

    public static string Build(IReadOnlyList<ChatMessage> history, CodingContextPack contextPack)
    {
        var lines = new List<string>
        {
            "You are the app's programming action planner.",
            "Return exactly one JSON object and no other text.",
            "No markdown fences. Keep the whole JSON under 350 characters.",
            "Do not answer the user.",
            "The user is in Programming mode. They should be able to describe a coding goal in normal language.",
            "Choose one internal coding tool template that Ali should run next, replacing placeholders through command_goal instead of writing an ad-hoc command.",
            "Operate the tools with the loop: Question, Info, Decision, Execute, Repeat.",
            "- Question: infer the user's actual programming problem from the current message and recent context.",
            "- Info: inspect the available tool map, current project state, and recent evidence before selecting a tool template.",
            "- Decision: choose the next tool/path dynamically from the problem and evidence; never route by a canned phrase table.",
            "- Execute: return exactly one existing selected_tool template plus command_goal when needed.",
            "- Repeat: expect Ali to run the derived command, collect evidence, and make the next decision from that evidence.",
            "Return understood_goal or compact alias g: one short restatement of the user's intended software outcome.",
            "Return info_used or compact alias i only when useful: max 1 short note.",
            "Return acceptance_criteria or compact alias c only when useful: max 1 short criterion.",
            "For execution_mode model_patch_preview, selected_path must be one exact Programming capability path name from the list below.",
            "For execution_mode model_patch_preview, selected_tool is supporting decision evidence; the patch planner authors from selected_path, understood_goal, command_goal, info_used, and acceptance_criteria.",
            "Prefer patch-authoring templates for model_patch_preview, but do not switch to a guide-only local_tool when the user's intended outcome is code creation and the project target is clear.",
            "Return selected_tool or compact alias t: the exact tool template from the Programming tool map or High-value commands list.",
            "Return command_goal or compact alias cg when selected_tool contains <goal>, <name>, <error>, or <symbol-or-file>.",
            "Return execution_mode or compact alias m: use patch for model_patch_preview or tool for local_tool.",
            "First choose the best programming capability path internally, then return the next selected_tool for that path. Do not add a widget or starter recipe just because a keyword appears.",
            "Prefer the path whose building blocks match the user's goal and the current project state; if the target project is unclear, choose an active-workspace/context command before edit commands.",
            "A guarded patch preview is a safe execution step because it does not change files until the owner confirms apply.",
            "When the user's desired end state requires creating or changing code and the context has editable file excerpts or a clear target project, prefer an authoring/action preview path over a guide-only path.",
            "When recent Info contains a failed confirmed dotnet build/test/run/restore result and the user's current request is to fix, repair, continue, or address that failure, treat the failure evidence as the dominant target: choose the Build/test repair loop path.",
            "If the failure output already names the file, line, diagnostic code, or target project, consider selected_tool \"suggest patch from last failure\" with execution_mode local_tool before model_patch_preview; otherwise choose a repair/patch authoring selected_tool for model_patch_preview.",
            "Do not select context packet, guide, status, or command-index tools for a build/test repair when the failure output already names the file, line, diagnostic code, or target project.",
            "When recent Info says a model patch preview was blocked while repairing a confirmed build/test/run/restore failure, choose selected_tool \"suggest patch from last failure\" with execution_mode local_tool before attempting another model_patch_preview.",
            "When recent Info says a feature-authoring patch preview was blocked by WPF/XAML or C# structural validation, retry the same feature authoring path with whole-file replace_file edits for affected structural files, or choose a context/inspection tool if the full files are missing.",
            "Use design guide tools when the user asks to compare, explain, or plan design without code, or when the Info step shows more context is needed before a safe preview.",
            "If recent assistant text contains `Next:` or `Next command:` and the user says next, continue, go, keep going, do it, or similar, choose the matching selected_tool template and command_goal for that queued step.",
            "If recent repeat-guard evidence says a selected path/tool/mode already stopped on a continuation, do not choose that same combination again without new project evidence; choose an Info/context/validation tool instead.",
            "Use existing selected_tool templates only. Do not invent commands, files, project names, test names, package names, or tool results.",
            "Do not return a raw command instead of selected_tool; Ali derives the executable command from selected_tool and command_goal.",
            "Prefer read-only planning and inspection commands for unclear targets, destructive operations, dependency installs, builds, applies, git operations, and other gated actions until the owner explicitly confirms.",
            "If the request is not about programming, return use_coding_tool false.",
            "Compact JSON keys: u boolean, path capability path, g understood goal, t exact selected_tool template, cg grounded command_goal, m execution mode, conf confidence. Optional short keys: i, c, s.",
            BuildCompactProgrammingCapabilityPathGuide(),
            BuildCompactWpfObjectLayoutDecisionMap(),
            "Programming tool map for dynamic selection:",
            "- Authoring/action tools include build this for me <goal>, feature patch draft <goal>, exact patch synthesis <goal>, preview synthesized feature patch <goal>, preview guided feature bundle <goal>, multi-file patch synthesis <goal>, concrete patch authoring <goal>, patch body generator <goal>, and confirm apply last patch preview.",
            "- Context/target tools include active workspace project, feature work context <goal>, feature intake <goal>, autonomous feature orchestrator <goal>, and roslyn edit planner <goal>.",
            "- Design tools include console app guide <goal>, wpf app guide <goal>, wpf layout guide <goal>, wpf controls guide <goal>, wpf styling guide <goal>, wpf complex window guide <goal>, data structure chooser <goal>, data systems guide <goal>, sql performance guide <goal>, service architecture guide <goal>, and cache queue guide <goal>.",
            "- Validation/repair tools include post patch validation <goal>, validation command minimizer <goal>, validation chain planner <goal>, diagnose last build failure, validation repair runner <goal>, first diagnostic repair route <goal>, and failure to patch v3 <goal>.",
            "- Closeout tools include semantic diff summary <goal>, semantic change receipt <goal>, review current changes, and can i safely commit.",
            "Choose among these tools only after the Question and Info steps; the list is a tool map, not a routing table.",
            "Patch-authoring selected_tool templates that fit model_patch_preview well:"
        };

        foreach (var command in CodingAbilityCatalog.PatchPreviewToolTemplates)
        {
            lines.Add($"- {command}");
        }

        lines.Add("High-value commands Ali can use:");

        foreach (var command in BuildPlannerPromptCommandList())
        {
            lines.Add($"- {command}");
        }

        if (contextPack.HasContext && !string.IsNullOrWhiteSpace(contextPack.Text))
        {
            lines.Add("Approved project context for the Info step:");
            lines.Add("Bounded for action selection; patch authoring receives the fuller context later.");
            lines.Add(TrimForActionPlannerContext(contextPack.Text));
        }

        var queuedStep = TryFindQueuedStep(history);
        if (queuedStep is not null)
        {
            lines.Add("Queued step evidence for continue/next requests:");
            lines.Add($"Next command: {queuedStep.Command}");
            lines.Add($"Matching selected_tool: {queuedStep.SelectedTool}");
            if (!string.IsNullOrWhiteSpace(queuedStep.CommandGoal))
            {
                lines.Add($"Matching command_goal: {queuedStep.CommandGoal}");
            }

            lines.Add("This is Info, not an automatic route; use it only when it matches the current user request and project evidence.");
        }

        var repeatGuardEvidence = TryFindRecentRepeatGuardEvidence(history);
        if (repeatGuardEvidence.Count > 0)
        {
            lines.Add("Recent programming repeat guard evidence:");
            foreach (var evidence in repeatGuardEvidence)
            {
                lines.Add(evidence);
            }

            lines.Add("Treat repeat guard evidence as Info: choose a different tool/path or gather context before another patch preview.");
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

    private sealed record QueuedStep(string Command, string SelectedTool, string CommandGoal);

    private static QueuedStep? TryFindQueuedStep(IReadOnlyList<ChatMessage> history)
    {
        foreach (var message in history
                     .Where(message => message.Role is ChatRole.Assistant)
                     .Reverse())
        {
            foreach (var line in message.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                var command = ExtractLineValue(line, "Next:")
                              ?? ExtractLineValue(line, "Next command:");
                if (string.IsNullOrWhiteSpace(command)
                    || command.Equals("no queued step yet.", StringComparison.OrdinalIgnoreCase)
                    || !CodingToolRequestParser.TryParse(command, out _))
                {
                    continue;
                }

                var selectedTool = TryMatchSelectedTool(command, out var commandGoal);
                if (!string.IsNullOrWhiteSpace(selectedTool))
                {
                    return new QueuedStep(command, selectedTool, commandGoal);
                }
            }
        }

        return null;
    }

    private static string? ExtractLineValue(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].Trim()
            : null;

    private static IReadOnlyList<string> TryFindRecentRepeatGuardEvidence(IReadOnlyList<ChatMessage> history) =>
        history
            .Where(message => message.Role is ChatRole.Assistant)
            .Reverse()
            .SelectMany(message => message.Text.Split('\n'))
            .Select(line => line.Trim())
            .Select(line =>
            {
                var markerIndex = line.IndexOf("Repeat guard:", StringComparison.OrdinalIgnoreCase);
                return markerIndex >= 0 ? line[markerIndex..] : string.Empty;
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(3)
            .Reverse()
            .ToArray();

    private static string TryMatchSelectedTool(string command, out string commandGoal)
    {
        commandGoal = string.Empty;
        var normalizedCommand = NormalizeCommandTemplate(command);
        foreach (var template in BuildPlannerCommandList())
        {
            var normalizedTemplate = NormalizeCommandTemplate(template);
            var placeholderStart = normalizedTemplate.IndexOf('<');
            var placeholderEnd = placeholderStart >= 0
                ? normalizedTemplate.IndexOf('>', placeholderStart)
                : -1;
            if (placeholderStart < 0 || placeholderEnd < placeholderStart)
            {
                if (string.Equals(normalizedCommand, normalizedTemplate, StringComparison.OrdinalIgnoreCase))
                {
                    return template;
                }

                continue;
            }

            var prefix = normalizedTemplate[..placeholderStart].Trim();
            var suffix = normalizedTemplate[(placeholderEnd + 1)..].Trim();
            if (!normalizedCommand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(suffix) && !normalizedCommand.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var goalStart = prefix.Length;
            var goalLength = normalizedCommand.Length - goalStart - suffix.Length;
            if (goalLength <= 0)
            {
                continue;
            }

            commandGoal = normalizedCommand.Substring(goalStart, goalLength).Trim();
            return template;
        }

        return string.Empty;
    }

    private static string NormalizeCommandTemplate(string value) =>
        string.Join(' ', value.ReplaceLineEndings(" ").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string BuildCompactWpfObjectLayoutDecisionMap()
    {
        var lines = new[]
        {
            "Advanced WPF object/layout decision map:",
            "- Select the shell and region pattern before authoring: Window shell, UserControl workflow regions, ContentControl active region, dialog/window service when needed.",
            "- Layout: prefer Grid/DockPanel for complex windows; use GridSplitter, SharedSizeGroup, ScrollViewer only around scrolling regions, and VirtualizingPanel for larger lists/grids.",
            "- Data controls: choose ItemsControl/ListBox/ListView/DataGrid/TreeView/TabControl by data shape; use CollectionViewSource for sort/filter/group/current item state.",
            "- Templates/resources: use DataTemplate, DataTemplateSelector, Style, ControlTemplate, ResourceDictionary Source paths, converters, and merged dictionaries deliberately.",
            "- Binding/state: use MVVM properties, commands, observable collections, INotifyPropertyChanged, INotifyDataErrorInfo, validation rules, ErrorTemplate/Adorner, async busy/error state, and explicit selection state.",
            "- Advanced binding: use RelativeSource, ElementName, x:Reference, Freezable BindingProxy, attached behaviors, RoutedCommand/InputBindings, and dependency properties only when the surface requires them.",
            "- Integrity checks: keep x:Class, namespaces, partial classes, code-behind files, resources, converters, selectors, and bindings aligned across XAML and C#.",
            "Dynamic WPF construction route:",
            "1. Interpret the requested window/workflow, data, commands, and validation.",
            "2. Choose shell, regions, controls, bindings, resources, and view-model state from the current project evidence.",
            "3. Author a coherent multi-file preview bundle, then queue validation before apply."
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCompactProgrammingCapabilityPathGuide()
    {
        var lines = new List<string> { "Programming capability paths:" };
        foreach (var path in CodingAbilityCatalog.ProgrammingCapabilityPaths)
        {
            lines.Add($"{path.Name}:");
            lines.Add($"- When to use: {path.WhenToUse}");
            lines.Add("- Command sequence:");
            foreach (var command in path.CommandSequence.Take(6))
            {
                lines.Add($"- {command}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> BuildPlannerPromptCommandList()
    {
        var commands = new List<string>
        {
            "active workspace project",
            "coding context packet <goal>",
            "build this for me <goal>",
            "feature intake <goal>",
            "feature work context <goal>",
            "autonomous feature orchestrator <goal>",
            "roslyn edit planner <goal>",
            "feature patch draft <goal>",
            "exact patch synthesis <goal>",
            "multi-file patch synthesis <goal>",
            "preview synthesized feature patch <goal>",
            "preview guided feature bundle <goal>",
            "concrete patch authoring <goal>",
            "patch body generator <goal>",
            "console app guide <goal>",
            "wpf app guide <goal>",
            "wpf layout guide <goal>",
            "wpf controls guide <goal>",
            "wpf styling guide <goal>",
            "wpf complex window guide <goal>",
            "data structure chooser <goal>",
            "data systems guide <goal>",
            "sql performance guide <goal>",
            "service architecture guide <goal>",
            "cache queue guide <goal>",
            "diagnose last build failure",
            "suggest patch from last failure",
            "validation repair runner <goal>",
            "first diagnostic repair route <goal>",
            "validation command minimizer <goal>",
            "validation chain planner <goal>",
            "failure to patch v3 <goal>",
            "confirm apply last patch preview",
            "post patch validation <goal>",
            "semantic diff summary <goal>",
            "semantic change receipt <goal>",
            "review current changes",
            "can i safely commit",
            "continue current task"
        };

        foreach (var command in CodingAbilityCatalog.PatchPreviewToolTemplates)
        {
            if (!commands.Contains(command, StringComparer.OrdinalIgnoreCase))
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    internal static IReadOnlyList<string> BuildPlannerCommandList()
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

        if (!commands.Contains("suggest patch from last failure", StringComparer.OrdinalIgnoreCase))
        {
            commands.Add("suggest patch from last failure");
        }

        return commands;
    }

    private static string TrimForPlanner(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    private static string TrimForActionPlannerContext(string value)
    {
        var normalized = value.ReplaceLineEndings(Environment.NewLine).Trim();
        if (normalized.Length <= MaxActionPlannerContextCharacters)
        {
            return normalized;
        }

        var headLength = MaxActionPlannerContextCharacters * 2 / 3;
        var tailLength = MaxActionPlannerContextCharacters - headLength;
        var head = normalized[..headLength].TrimEnd();
        var tail = normalized[^tailLength..].TrimStart();
        return string.Join(
            Environment.NewLine,
            head,
            "... bounded action-planner context: middle omitted; full context is retained for patch authoring ...",
            tail);
    }
}
