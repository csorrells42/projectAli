using System.Diagnostics;
using System.Text.Json;
using Ali.Modules.Mcp;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;
using Ali.Modules.Orchestration.Planning;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

internal sealed class ActivityReportingAIFunction(
    AIFunction innerFunction,
    Func<CoordinatorTurnContext?> turnAccessor,
    IShadowToolObserver? shadowObserver = null,
    bool requiresApproval = false,
    string? userFacingDisplayName = null) : DelegatingAIFunction(innerFunction)
{
    private readonly bool _hasUserFacingDisplayName = !string.IsNullOrWhiteSpace(userFacingDisplayName);
    private readonly string _activityDisplayName = ResolveActivityDisplayName(
        userFacingDisplayName,
        innerFunction.Name);

    internal string? UserFacingDisplayName =>
        _hasUserFacingDisplayName ? _activityDisplayName : null;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (AliCoreAssistantExecutionContext.IsActive)
        {
            // CORE PATH BYPASS: keep the existing capability/workspace permission
            // boundary, but do not synchronously create activity receipts, shadow
            // observations, evidence journals, or durable recovery records here.
            // The actual typed tool result returns directly to the model in RAM.
            return await InnerFunction.InvokeAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
        }

        var turn = TryGetTurn(turnAccessor);
        var plan = TryResolveActivePlan(turn, Name);
        var started = Stopwatch.GetTimestamp();
        var startedAtUtc = DateTimeOffset.UtcNow;
        var callId = plan?.CallId ?? TryResolveCallId(turn, Name);
        var permission = ResolvePermission(turn, callId, Name, requiresApproval);
        var usePlanHeadline = !_hasUserFacingDisplayName
            && plan is not null;
        // The planning client publishes an accepted model-authored transition once it
        // has validated and journaled the call. Do not repeat that same start line at
        // the invocation boundary. Tools invoked outside a registered plan retain the
        // generic lifecycle notification.
        if (!usePlanHeadline)
        {
            TryReport(
                turn,
                AgentActivityKind.ToolCall,
                $"Running {_activityDisplayName}",
                $"Selected tool: {_activityDisplayName}");
        }
        try
        {
            var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            TryObserveReturned(
                shadowObserver,
                turn,
                callId,
                Name,
                arguments,
                result,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                permission);
            if (result is IMcpPostDispatchFailureResult mcpFailure)
            {
                var failureSummary = DescribeMcpFailure(mcpFailure);
                TryReport(
                    turn,
                    AgentActivityKind.Warning,
                    $"{_activityDisplayName} returned an uncertain external result",
                    failureSummary,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    executionReceipt: new AgentToolExecutionReceipt(
                        Name,
                        AgentToolExecutionOutcome.Failed,
                        failureSummary,
                        DateTimeOffset.UtcNow)
                    {
                        DisplayName = UserFacingDisplayName
                    });
                return result;
            }

            if (AliProductionToolOutcomeRegistry.ClassifyTypedReturn(Name, result)
                == PlanningToolDomainOutcome.Failed)
            {
                var failureSummary = DescribeResult(result);
                TryReport(
                    turn,
                    AgentActivityKind.Warning,
                    $"{_activityDisplayName} returned a failed result",
                    failureSummary,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    executionReceipt: new AgentToolExecutionReceipt(
                        Name,
                        AgentToolExecutionOutcome.Failed,
                        failureSummary,
                        DateTimeOffset.UtcNow)
                    {
                        DisplayName = UserFacingDisplayName
                    });
                return result;
            }

            TryReport(
                turn,
                AgentActivityKind.ToolResult,
                usePlanHeadline
                    ? plan!.ResultHeadline
                    : $"{_activityDisplayName} returned; Ali is evaluating the result.",
                usePlanHeadline
                    ? null
                    : $"{_activityDisplayName} completed; Ali is evaluating the returned evidence.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                executionReceipt: new AgentToolExecutionReceipt(
                    Name,
                    AgentToolExecutionOutcome.Completed,
                    DescribeResult(result),
                    DateTimeOffset.UtcNow)
                {
                    DisplayName = UserFacingDisplayName
                });
            return result;
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryObserveCancelled(
                    shadowObserver,
                    turn,
                    callId,
                    Name,
                    arguments,
                    ex,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    permission);
                TryReport(
                    turn,
                    AgentActivityKind.Warning,
                    $"{_activityDisplayName} was cancelled",
                    "The user or application cancelled the in-flight tool call.",
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    executionReceipt: new AgentToolExecutionReceipt(
                        Name,
                        AgentToolExecutionOutcome.Cancelled,
                        "The in-flight tool call was cancelled before it returned a result.",
                        DateTimeOffset.UtcNow)
                    {
                        DisplayName = UserFacingDisplayName
                    });
            }
            else
            {
                TryObserveThrew(
                    shadowObserver,
                    turn,
                    callId,
                    Name,
                    arguments,
                    ex,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    permission);
                var failureDetail = DescribeException(ex);
                TryReport(
                    turn,
                    AgentActivityKind.Error,
                    $"{_activityDisplayName} failed",
                    failureDetail,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    executionReceipt: new AgentToolExecutionReceipt(
                        Name,
                        AgentToolExecutionOutcome.Failed,
                        failureDetail,
                        DateTimeOffset.UtcNow)
                    {
                        DisplayName = UserFacingDisplayName
                    });
            }
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryObserveThrew(
                shadowObserver,
                turn,
                callId,
                Name,
                arguments,
                ex,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                permission);
            var failureDetail = DescribeException(ex);
            TryReport(
                turn,
                AgentActivityKind.Error,
                $"{_activityDisplayName} failed",
                failureDetail,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                executionReceipt: new AgentToolExecutionReceipt(
                    Name,
                    AgentToolExecutionOutcome.Failed,
                    failureDetail,
                    DateTimeOffset.UtcNow)
                {
                    DisplayName = UserFacingDisplayName
                });
            if (AliCoreAssistantExecutionContext.IsActive)
            {
                return new
                {
                    success = false,
                    recoverable = true,
                    tool = Name,
                    error = ex.Message,
                    instruction = "Correct the tool arguments from this error and continue the same user request. Do not end the turn while a useful corrected call remains possible."
                };
            }
            throw;
        }
    }

    private static string? TryResolveCallId(CoordinatorTurnContext? turn, string toolName)
    {
        try
        {
            if (turn?.TryGetActiveToolCallId(toolName, out var callId) == true
                && !string.IsNullOrWhiteSpace(callId))
            {
                return callId;
            }
        }
        catch
        {
            // Shadow correlation must never affect the actual tool call.
        }

        return null;
    }

    private static CoordinatorToolPlan? TryResolveActivePlan(
        CoordinatorTurnContext? turn,
        string toolName)
    {
        try
        {
            return turn?.TryGetActiveToolPlan(toolName, out var plan) == true
                ? plan
                : null;
        }
        catch
        {
            // Ambient activity context is supplementary and cannot affect the actual tool call.
            return null;
        }
    }

    private static CoordinatorTurnContext? TryGetTurn(
        Func<CoordinatorTurnContext?> accessor)
    {
        try
        {
            return accessor();
        }
        catch
        {
            // Ambient activity context is optional and cannot prevent the real invocation.
            return null;
        }
    }

    private static EvidencePermissionMetadata ResolvePermission(
        CoordinatorTurnContext? turn,
        string? callId,
        string toolName,
        bool requiresApproval)
    {
        if (!requiresApproval)
        {
            return new EvidencePermissionMetadata("not-required", "none");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(callId)
                && turn?.TryGetShadowPermission(callId, out var permission) == true
                && permission is not null)
            {
                return permission;
            }

            if (turn?.TryGetShadowStandingPermission(toolName, out var standingPermission) == true
                && standingPermission is not null)
            {
                return standingPermission;
            }
        }
        catch
        {
            // Permission enrichment is receipt-only and cannot affect the invocation.
        }

        return new EvidencePermissionMetadata("unknown", "unknown");
    }

    private static void TryObserveReturned(
        IShadowToolObserver? observer,
        CoordinatorTurnContext? turn,
        string? callId,
        string toolName,
        object? arguments,
        object? result,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        if (observer is null || string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        try
        {
            if (observer.TryObserveReturned(
                turn?.ObservationIdentity,
                callId,
                toolName,
                arguments,
                result,
                startedAtUtc,
                completedAtUtc,
                permission))
            {
                turn?.MarkShadowObserved(callId);
            }
        }
        catch
        {
            // The shadow observer is failure-isolated from the live invocation path.
        }
    }

    private static void TryObserveThrew(
        IShadowToolObserver? observer,
        CoordinatorTurnContext? turn,
        string? callId,
        string toolName,
        object? arguments,
        Exception exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        if (observer is null || string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        try
        {
            if (observer.TryObserveThrew(
                turn?.ObservationIdentity,
                callId,
                toolName,
                arguments,
                exception,
                startedAtUtc,
                completedAtUtc,
                permission))
            {
                turn?.MarkShadowObserved(callId);
            }
        }
        catch
        {
            // The original exception and stack remain authoritative.
        }
    }

    private static void TryObserveCancelled(
        IShadowToolObserver? observer,
        CoordinatorTurnContext? turn,
        string? callId,
        string toolName,
        object? arguments,
        OperationCanceledException exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        if (observer is null || string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        try
        {
            if (observer.TryObserveCancelled(
                turn?.ObservationIdentity,
                callId,
                toolName,
                arguments,
                exception,
                startedAtUtc,
                completedAtUtc,
                permission))
            {
                turn?.MarkShadowObserved(callId);
            }
        }
        catch
        {
            // The original cancellation and stack remain authoritative.
        }
    }

    internal static string DescribeResult(object? result)
    {
        try
        {
            return result switch
            {
                null => "No result was returned.",
                string text => $"Returned text ({text.Length} characters).",
                bool value => $"Returned {value.ToString().ToLowerInvariant()}.",
                JsonElement element => $"Returned JSON {element.ValueKind.ToString().ToLowerInvariant()}.",
                _ => $"{BoundedTypeName(result.GetType())} returned."
            };
        }
        catch
        {
            return "Tool returned a result.";
        }
    }

    private static string DescribeException(Exception exception)
    {
        try
        {
            return $"{BoundedTypeName(exception.GetType())} occurred while the tool was running.";
        }
        catch
        {
            // A hostile exception type cannot replace the exception being rethrown.
            return "The tool failed with an exception.";
        }
    }

    private static string DescribeMcpFailure(IMcpPostDispatchFailureResult failure)
    {
        const int maximumSummaryCharacters = 480;
        try
        {
            var message = failure.Message.ReplaceLineEndings(" ").Trim();
            if (message.Length > maximumSummaryCharacters)
            {
                message = message[..maximumSummaryCharacters];
            }

            return string.IsNullOrWhiteSpace(message)
                ? "The external MCP call was dispatched, but its result is uncertain and must not be retried automatically."
                : message;
        }
        catch
        {
            return "The external MCP call was dispatched, but its result is uncertain and must not be retried automatically.";
        }
    }

    private static string BoundedTypeName(Type type)
    {
        const int maximumTypeNameCharacters = 120;
        var name = type.Name;
        return name.Length <= maximumTypeNameCharacters
            ? name
            : name[..maximumTypeNameCharacters];
    }

    private static string ResolveActivityDisplayName(
        string? userFacingDisplayName,
        string internalToolName)
    {
        const int maximumDisplayNameCharacters = 160;
        var candidate = string.IsNullOrWhiteSpace(userFacingDisplayName)
            ? Humanize(internalToolName)
            : userFacingDisplayName;
        var normalized = string.Join(
            " ",
            candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return "tool";
        }

        return normalized.Length <= maximumDisplayNameCharacters
            ? normalized
            : normalized[..maximumDisplayNameCharacters];
    }

    private static void TryReport(
        CoordinatorTurnContext? turn,
        AgentActivityKind kind,
        string title,
        string? detail = null,
        double? elapsedMilliseconds = null,
        AgentToolApprovalPrompt? approvalPrompt = null,
        string? activityKey = null,
        AgentToolExecutionReceipt? executionReceipt = null)
    {
        try
        {
            turn?.Report(
                kind,
                title,
                detail,
                elapsedMilliseconds,
                approvalPrompt,
                activityKey,
                executionReceipt);
        }
        catch
        {
            // Activity presentation must never alter a tool's return or exception semantics.
        }
    }

    private static string Humanize(string value) => value.Replace('_', ' ').Trim();
}
