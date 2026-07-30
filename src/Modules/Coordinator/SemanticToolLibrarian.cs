using System.Text;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Uses the configured model to choose the small live-tool subset that is relevant to the
/// current objective. This class validates protocol shape only; it never interprets English,
/// assigns tool categories, or routes a request with keywords.
/// </summary>
internal sealed class SemanticToolLibrarian(IChatClient model, string assistantName)
{
    internal const int MaximumSelectedTools = 12;
    private const int MaximumRequestCharacters = 4000;
    private const int MaximumEvidenceCharacters = 5000;
    private const int MaximumDescriptionCharacters = 220;
    private readonly string _assistantName = string.IsNullOrWhiteSpace(assistantName)
        ? "Ali"
        : assistantName.Trim();

    public async Task<IReadOnlyList<AIFunctionDeclaration>> SelectAsync(
        IReadOnlyList<AIChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> registeredTools,
        string? currentUserRequest,
        ChatOptions? sourceOptions,
        CoordinatorTurnContext? turn,
        CancellationToken cancellationToken)
    {
        if (registeredTools.Count == 0)
        {
            return [];
        }

        var byName = registeredTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var selectionMessages = BuildSelectionMessages(
            messages,
            registeredTools,
            currentUserRequest);
        var selectionOptions = sourceOptions?.Clone() ?? new ChatOptions();
        selectionOptions.Tools = null;
        selectionOptions.ToolMode = ChatToolMode.None;
        selectionOptions.AllowMultipleToolCalls = false;
        selectionOptions.ResponseFormat = null;
        selectionOptions.MaxOutputTokens = Math.Min(sourceOptions?.MaxOutputTokens ?? 512, 512);
        selectionOptions.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["ali.internalRouting"] = true,
            ["ali.semanticToolDiscovery"] = true
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            turn?.Report(
                AgentActivityKind.Planning,
                $"{_assistantName} is matching the objective to her live tools",
                $"The model is reviewing {registeredTools.Count} registered capabilities by meaning before choosing the next action.",
                activityKey: "semantic-tool-discovery");
            var response = await model.GetResponseAsync(
                selectionMessages,
                selectionOptions,
                cancellationToken).ConfigureAwait(false);
            if (TryResolveSelection(response.Text, byName, out var selected, out var error))
            {
                turn?.Report(
                    AgentActivityKind.Status,
                    selected.Count == 0
                        ? $"{_assistantName} found no tool necessary for this step"
                        : $"{_assistantName} selected {selected.Count} relevant live tool(s)",
                    selected.Count == 0
                        ? "The model will answer directly; no data source or action tool improves this step."
                        : "Available for the next decision: " + string.Join(", ", selected.Select(tool => tool.Name)),
                    activityKey: "semantic-tool-discovery");
                return selected;
            }

            selectionMessages.Add(new AIChatMessage(
                AIChatRole.Assistant,
                response.Text ?? string.Empty));
            selectionMessages.Add(new AIChatMessage(
                AIChatRole.User,
                "The selection could not be matched to the live registry: " + error + Environment.NewLine
                + "Return DIRECT, or one exact registered tool name per line. Return no explanation."));
        }
    }

    private static List<AIChatMessage> BuildSelectionMessages(
        IReadOnlyList<AIChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> registeredTools,
        string? currentUserRequest)
    {
        var evidence = new StringBuilder();
        foreach (var message in messages.Where(message => message.Role != AIChatRole.System).TakeLast(8))
        {
            var text = message.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                evidence.Append(message.Role).Append(": ").AppendLine(text);
            }

            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                evidence.Append("Prior tool call: ").AppendLine(call.Name);
            }

            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                evidence.Append("Prior tool result: ")
                    .AppendLine(LemonadeToolCallingChatClient.SerializeToolResultForModel(result.Result));
            }
        }

        var toolInventory = string.Join(
            Environment.NewLine,
            registeredTools.Select(tool =>
                tool.Name + " | " + Compact(tool.Description ?? string.Empty, MaximumDescriptionCharacters)));
        return
        [
            new AIChatMessage(
                AIChatRole.System,
                string.Join(
                    Environment.NewLine,
                    "You are the semantic tool librarian inside a local agent.",
                    "Understand the current human objective and the evidence from this same turn.",
                    "Choose only tools whose exact schemas the planner should receive for its next decision.",
                    "Selection is semantic: use the meaning of the request and tool descriptions. Do not use keyword matching or surface-word rules.",
                    "Return DIRECT when no tool would improve correctness or perform a requested action.",
                    $"Otherwise return one exact registered tool name per line, with no explanation. Select at most {MaximumSelectedTools} tools.",
                    "Include complementary tools when the next step may require inspection, mutation, verification, or recovery, but do not dump unrelated capabilities.",
                    "REGISTERED LIVE TOOL INDEX:",
                    toolInventory)),
            new AIChatMessage(
                AIChatRole.User,
                "CURRENT HUMAN OBJECTIVE:\n"
                + Compact(currentUserRequest?.Trim() ?? string.Empty, MaximumRequestCharacters)
                + "\n\nCURRENT TURN EVIDENCE:\n"
                + Compact(evidence.ToString(), MaximumEvidenceCharacters))
        ];
    }

    private static bool TryResolveSelection(
        string? text,
        IReadOnlyDictionary<string, AIFunctionDeclaration> registeredTools,
        out IReadOnlyList<AIFunctionDeclaration> selected,
        out string error)
    {
        selected = [];
        error = string.Empty;
        var lines = (text ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 1 && string.Equals(lines[0], "DIRECT", StringComparison.Ordinal))
        {
            return true;
        }

        if (lines.Length == 0)
        {
            error = "No selection was returned.";
            return false;
        }

        if (lines.Length > MaximumSelectedTools)
        {
            error = $"The selection contained {lines.Length} names; the maximum is {MaximumSelectedTools}.";
            return false;
        }

        var resolved = new List<AIFunctionDeclaration>(lines.Length);
        foreach (var line in lines.Distinct(StringComparer.Ordinal))
        {
            if (!registeredTools.TryGetValue(line, out var tool))
            {
                error = $"'{line}' is not an exact registered tool name.";
                return false;
            }

            resolved.Add(tool);
        }

        selected = resolved;
        return true;
    }

    private static string Compact(string value, int maximumCharacters)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "...";
    }
}
