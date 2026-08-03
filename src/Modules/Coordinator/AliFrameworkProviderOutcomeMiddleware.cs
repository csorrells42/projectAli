using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Establishes the exact framework invocation scope used by provider-owned outcome
/// reporters and verifies mode results against the actual Agent Framework session state.
/// </summary>
internal static class AliFrameworkProviderOutcomeMiddleware
{
    private static readonly IReadOnlySet<string> ProviderBoundaryToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.GetAgentModeName,
            AliCapabilityCatalog.SetAgentModeName,
            AliCapabilityCatalog.LoadAgentSkillName,
            AliCapabilityCatalog.ReadAgentSkillResourceName,
            AliCapabilityCatalog.RunAgentSkillScriptName
        };

    internal static AIAgent WithOutcomeReporting(
        AIAgent agent,
        AliFrameworkToolOutcomeSidecar outcomes,
        Func<CoordinatorTurnContext?> turnAccessor,
        AliOutcomeReportingAgentSkillsSource? skillsSource = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(turnAccessor);

        var activeSession = new AsyncLocal<SessionFrame?>();
        skillsSource?.ConfigureInventoryReporting((session, skillNames) =>
        {
            var frame = activeSession.Value;
            if (frame?.IsActive == true
                && ReferenceEquals(frame.Session, session))
            {
                frame.SetSkillNames(skillNames);
            }
        });
        var functionBuilder = new AIAgentBuilder(agent);
        functionBuilder.Use(async (
            AIAgent _,
            FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken cancellationToken) =>
        {
            var toolName = context.Function.Name;
            var callId = context.CallContent.CallId;
            if (string.IsNullOrWhiteSpace(toolName)
                || string.IsNullOrWhiteSpace(callId)
                || !string.Equals(
                    context.CallContent.Name,
                    toolName,
                    StringComparison.Ordinal)
                || !outcomes.TryEnterInvocation(
                    turnAccessor(),
                    callId,
                    toolName,
                    out var invocationScope)
                || invocationScope is null)
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            using (invocationScope)
            {
                object? result;
                try
                {
                    result = await next(context, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    ReportInvocationFailure(toolName, outcomes);

                    throw;
                }

                if (toolName is AliCapabilityCatalog.GetAgentModeName
                    or AliCapabilityCatalog.SetAgentModeName)
                {
                    try
                    {
                        var sessionFrame = activeSession.Value;
                        await VerifyModeReturnAsync(
                                agent.GetService<AgentModeProvider>(),
                                sessionFrame?.IsActive == true ? sessionFrame.Session : null,
                                toolName,
                                context.Arguments,
                                outcomes)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Outcome verification is observational. A missing signal is
                        // fail-closed and cannot change a successful provider return.
                    }
                }
                else if (result is not ToolApprovalRequestContent
                         && toolName is (AliCapabilityCatalog.LoadAgentSkillName
                             or AliCapabilityCatalog.ReadAgentSkillResourceName
                             or AliCapabilityCatalog.RunAgentSkillScriptName))
                {
                    var sessionFrame = activeSession.Value;
                    VerifySkillReturn(
                        toolName,
                        context.Arguments,
                        sessionFrame?.IsActive == true
                            ? sessionFrame.SkillNames
                            : null,
                        outcomes);
                }

                return result;
            }
        });

        var sessionBuilder = new AIAgentBuilder(functionBuilder.Build());
        sessionBuilder.Use(async (
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task> next,
            CancellationToken cancellationToken) =>
        {
            var frame = new SessionFrame(session, activeSession.Value);
            activeSession.Value = frame;
            try
            {
                await next(messages, session, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                frame.Deactivate();
                if (ReferenceEquals(activeSession.Value, frame))
                {
                    activeSession.Value = frame.Previous;
                }
            }
        });
        return sessionBuilder.Build();
    }

    internal static void ReportInvocationFailure(
        string toolName,
        AliFrameworkToolOutcomeSidecar outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (!string.IsNullOrWhiteSpace(toolName)
            && ProviderBoundaryToolNames.Contains(toolName))
        {
            TryReport(
                outcomes,
                [toolName],
                AliFrameworkToolOutcomeSignal.Failed);
        }
    }

    internal static async ValueTask VerifyModeReturnAsync(
        AgentModeProvider? provider,
        AgentSession? session,
        string toolName,
        AIFunctionArguments? arguments,
        AliFrameworkToolOutcomeSidecar outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (provider is null
            || session is null
            || toolName is not (AliCapabilityCatalog.GetAgentModeName
                or AliCapabilityCatalog.SetAgentModeName))
        {
            return;
        }

        string? currentMode;
        try
        {
            currentMode = await provider.GetModeAsync(session, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Verification itself was unavailable. Do not infer failure from a missing
            // observation and never inspect the provider's model-facing return text.
            return;
        }

        if (toolName == AliCapabilityCatalog.GetAgentModeName)
        {
            TryReport(
                outcomes,
                [toolName],
                string.IsNullOrWhiteSpace(currentMode)
                    ? AliFrameworkToolOutcomeSignal.Failed
                    : AliFrameworkToolOutcomeSignal.Found);
            return;
        }

        if (!TryGetExactStringArgument(arguments, "mode", out var requestedMode))
        {
            return;
        }

        TryReport(
            outcomes,
            [toolName],
            string.Equals(currentMode, requestedMode, StringComparison.Ordinal)
                ? AliFrameworkToolOutcomeSignal.Completed
                : AliFrameworkToolOutcomeSignal.Rejected);
    }

    internal static void VerifySkillReturn(
        string toolName,
        AIFunctionArguments? arguments,
        IReadOnlySet<string>? skillNames,
        AliFrameworkToolOutcomeSidecar outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (toolName is not (AliCapabilityCatalog.LoadAgentSkillName
            or AliCapabilityCatalog.ReadAgentSkillResourceName
            or AliCapabilityCatalog.RunAgentSkillScriptName)
            || !TryGetExactStringArgument(arguments, "skillName", out var skillName))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(skillName))
        {
            TryReport(
                outcomes,
                [toolName],
                AliFrameworkToolOutcomeSignal.Rejected);
            return;
        }

        if (skillNames is null)
        {
            return;
        }

        if (!skillNames.Contains(skillName))
        {
            TryReport(
                outcomes,
                [toolName],
                AliFrameworkToolOutcomeSignal.NotFound);
            return;
        }

        var componentArgumentName = toolName switch
        {
            AliCapabilityCatalog.ReadAgentSkillResourceName => "resourceName",
            AliCapabilityCatalog.RunAgentSkillScriptName => "scriptName",
            _ => null
        };
        if (componentArgumentName is not null
            && TryGetExactStringArgument(arguments, componentArgumentName, out var componentName)
            && string.IsNullOrWhiteSpace(componentName))
        {
            TryReport(
                outcomes,
                [toolName],
                AliFrameworkToolOutcomeSignal.Rejected);
        }
    }

    private static bool TryGetExactStringArgument(
        AIFunctionArguments? arguments,
        string argumentName,
        out string value)
    {
        value = string.Empty;
        if (arguments is null
            || !arguments.TryGetValue(argumentName, out var raw))
        {
            return false;
        }

        switch (raw)
        {
            case string text:
                value = text;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                value = element.GetString() ?? string.Empty;
                return true;
            default:
                return false;
        }
    }

    private static void TryReport(
        AliFrameworkToolOutcomeSidecar outcomes,
        IReadOnlyList<string> toolNames,
        AliFrameworkToolOutcomeSignal signal)
    {
        try
        {
            outcomes.TryRecordActive(toolNames, signal);
        }
        catch
        {
            // Provider outcome observation cannot change invocation behavior.
        }
    }

    private sealed class SessionFrame(
        AgentSession? session,
        SessionFrame? previous)
    {
        private int _active = 1;

        public AgentSession? Session { get; } = session;

        public SessionFrame? Previous { get; } = previous;

        public IReadOnlySet<string>? SkillNames { get; private set; }

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Deactivate() => Interlocked.Exchange(ref _active, 0);

        public void SetSkillNames(IReadOnlySet<string>? skillNames)
        {
            if (IsActive)
            {
                SkillNames = skillNames;
            }
        }
    }
}
