using System.Text;
using Ali.Modules.Evidence;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Ali's release-candidate execution path.
///
/// One request enters. The model may call an already-loaded tool, receives that
/// exact result, and continues until it returns an answer. No recovery journal,
/// receipt replay, memory mutation, semantic reload, critic, or durable pause is
/// permitted in this class. Attachments are already part of <paramref name="input"/>.
/// Workspace enforcement remains inside the tool implementations.
/// </summary>
internal sealed class AliMinimumMessage
{
    private const int MaximumToolResults = 64;
    // The gate's unconditional post-mutation run check would force a launch after
    // every single source edit, even when nobody asked for one. A model-chosen run
    // that then fails still blocks via run-failed, and an explicit stop still blocks
    // via the separate run-stopped code; only this specific proactive-run-after-edit
    // demand is skipped here to avoid manufacturing busywork on a personal desktop.
    private const string SkippedBlockerCode = "run-missing-or-stale";

    internal async Task<AgentHarnessRunResult> RunAsync(
        CoordinatorTurnContext turn,
        AIAgent agent,
        IReadOnlyList<ChatMessage> input,
        Func<FinalAnswerPublication, CancellationToken,
            ValueTask<FinalAnswerPublicationAcknowledgment>> publishFinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(publishFinal);

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var gate = new CoreAssistantCompletionGate();
        var answer = new StringBuilder();
        var completedToolResults = 0;
        var separateNextModelMessage = false;
        string? finishReason = null;
        var nextInput = input;

        while (completedToolResults < MaximumToolResults)
        {
            var toolResultsBeforeBurst = completedToolResults;
            await foreach (var update in agent.RunStreamingAsync(
                               nextInput,
                               session,
                               options: null,
                               cancellationToken).ConfigureAwait(false))
            {
                finishReason = update.FinishReason?.ToString() ?? finishReason;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent call when !call.InformationalOnly:
                            gate.Track(call);
                            turn.Report(
                                AgentActivityKind.ToolCall,
                                DescribeToolAction(call),
                                "Ali selected the next Serena action.");
                            break;

                        case FunctionResultContent result:
                            completedToolResults++;
                            separateNextModelMessage = answer.Length > 0;
                            gate.Observe(result);

                            if (result.Exception is not null)
                            {
                                turn.Report(
                                    AgentActivityKind.Error,
                                    "Tool returned an error",
                                    result.Exception.GetBaseException().Message);
                            }
                            break;

                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            if (separateNextModelMessage)
                            {
                                // A single line break reads as a continuation, not a new
                                // block, in the chat display; a blank line is what actually
                                // separates this fresh segment from what came before it.
                                answer.Append(Environment.NewLine + Environment.NewLine);
                                turn.PublishResponseText(Environment.NewLine + Environment.NewLine);
                                separateNextModelMessage = false;
                            }
                            answer.Append(text.Text);
                            turn.PublishResponseText(text.Text);
                            break;
                    }
                }
            }

            if (answer.Length > 0)
            {
                if (gate.TryGetBlocker(out var blocker)
                    && !string.Equals(blocker.Code, SkippedBlockerCode, StringComparison.Ordinal))
                {
                    turn.Report(
                        AgentActivityKind.Warning,
                        "Completion not yet verified",
                        $"The model ended with an unverified condition: {blocker.Code}. Ali did not invent a user message to continue it.");
                }

                break;
            }

            if (completedToolResults == toolResultsBeforeBurst)
            {
                throw new InvalidOperationException(
                    "The model returned neither a final answer nor a tool result. Ali did not invent a user message to force another response.");
            }

            // The session already contains the exact tool result. Continue without
            // fabricating another user-role message.
            nextInput = Array.Empty<ChatMessage>();
        }

        if (answer.Length == 0)
        {
            throw new InvalidOperationException(
                $"The model used {MaximumToolResults} tool results without returning a final answer. Ali stopped instead of running indefinitely.");
        }

        var exactAnswer = FinalAnswerRenderer.Compose(answer.ToString(), turn.WebSources);
        finishReason ??= ChatFinishReason.Stop.ToString();
        var publication = new FinalAnswerPublication(
            turn.ConversationId,
            turn.UserMessageId,
            turn.AssistantMessageId,
            "publication_" + turn.AssistantMessageId,
            exactAnswer,
            TurnStateIntegrity.Digest(exactAnswer),
            turn.UsedEvidenceTool ? EvidenceStatus.Verified : EvidenceStatus.Unverified,
            finishReason);
        var acknowledgment = await publishFinal(publication, cancellationToken)
            .ConfigureAwait(false);
        FinalAnswerPublicationBoundary.RequireExactInMemoryAcknowledgment(
            publication,
            acknowledgment);

        return new AgentHarnessRunResult(
            WroteAnswer: true,
            FinishReason: finishReason,
            Paused: false,
            ResumeIdentity: null,
            CompletedSuccessfully: true);
    }

    private static string DescribeToolAction(FunctionCallContent call)
    {
        var file = FileName(ReadArgument(call.Arguments, "relative_path", "path", "file_path"));
        var symbol = ReadArgument(call.Arguments, "name_path", "name_path_pattern", "symbol_name");
        var project = FileName(ReadArgument(call.Arguments, "project", "project_path"));
        var command = Shorten(ReadArgument(call.Arguments, "command"), 90);
        var searchPattern = Shorten(
            ReadArgument(call.Arguments, "substring_pattern", "pattern", "query")
                .ReplaceLineEndings(" "),
            56);

        return call.Name switch
        {
            "activate_project" => string.IsNullOrWhiteSpace(project)
                ? "Activating the workspace project"
                : $"Activating {project}",
            "initial_instructions" => "Loading Serena project instructions",
            "list_dir" => string.IsNullOrWhiteSpace(file)
                ? "Listing workspace files"
                : $"Listing {file}",
            "get_symbols_overview" => string.IsNullOrWhiteSpace(file)
                ? "Inspecting source symbols"
                : $"Inspecting symbols in {file}",
            "get_diagnostics_for_file" => string.IsNullOrWhiteSpace(file)
                ? "Checking source diagnostics"
                : $"Checking diagnostics in {file}",
            "read_file" => string.IsNullOrWhiteSpace(file)
                ? "Reading a source file"
                : $"Reading {file}",
            "find_symbol" => string.IsNullOrWhiteSpace(symbol)
                ? "Finding a source symbol"
                : $"Finding symbol {symbol}",
            "find_referencing_symbols" => string.IsNullOrWhiteSpace(symbol)
                ? "Finding symbol references"
                : $"Finding references to {symbol}",
            "find_implementations" => string.IsNullOrWhiteSpace(symbol)
                ? "Finding implementations"
                : $"Finding implementations of {symbol}",
            "replace_symbol_body" => DescribeEdit("Editing", file, symbol),
            "insert_before_symbol" => DescribeEdit("Inserting before", file, symbol),
            "insert_after_symbol" => DescribeEdit("Inserting after", file, symbol),
            "rename_symbol" => DescribeEdit("Renaming", file, symbol),
            "safe_delete_symbol" => DescribeEdit("Removing", file, symbol),
            "replace_content" or "replace_in_files" or "replace_lines" =>
                string.IsNullOrWhiteSpace(file) ? "Applying targeted source edits" : $"Editing {file}",
            "create_text_file" => string.IsNullOrWhiteSpace(file)
                ? "Creating a project file"
                : $"Creating {file}",
            "search_for_pattern" => DescribeSearch(file, searchPattern),
            "execute_shell_command" => string.IsNullOrWhiteSpace(command)
                ? "Running a workspace command"
                : $"Running {command}",
            "write_memory" => "Saving Serena project guidance",
            "read_memory" => "Reading Serena project guidance",
            _ => $"Using {call.Name}"
        };
    }

    private static string DescribeEdit(string verb, string file, string symbol)
    {
        if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(symbol))
        {
            return $"{verb} {symbol} in {file}";
        }
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            return $"{verb} {symbol}";
        }
        return string.IsNullOrWhiteSpace(file) ? "Editing source" : $"Editing {file}";
    }

    private static string DescribeSearch(string file, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return string.IsNullOrWhiteSpace(file) ? "Searching the source" : $"Searching {file}";
        }

        return string.IsNullOrWhiteSpace(file)
            ? $"Searching the source for '{pattern}'"
            : $"Searching {file} for '{pattern}'";
    }

    private static string ReadArgument(
        IDictionary<string, object?>? arguments,
        params string[] names)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (!arguments.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            var text = value switch
            {
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element =>
                    element.GetString(),
                string direct => direct,
                _ => value.ToString()
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return string.Empty;
    }

    private static string FileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is ".")
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static string Shorten(string text, int maximumLength) =>
        string.IsNullOrWhiteSpace(text) || text.Length <= maximumLength
            ? text
            : text[..maximumLength] + "…";
}
