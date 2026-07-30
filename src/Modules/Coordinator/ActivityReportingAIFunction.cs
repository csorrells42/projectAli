using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

internal sealed class ActivityReportingAIFunction(
    AIFunction innerFunction,
    Func<CoordinatorTurnContext?> turnAccessor) : DelegatingAIFunction(innerFunction)
{
    private const int MaximumVisibleResultCharacters = 520;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var turn = turnAccessor();
        var plan = turn?.CurrentToolPlan;
        var started = Stopwatch.GetTimestamp();
        turn?.Report(
            AgentActivityKind.ToolCall,
            plan is not null && string.Equals(plan.ToolName, Name, StringComparison.Ordinal)
                ? plan.SelectionHeadline
                : $"Running {Humanize(Name)}",
            plan is not null && string.Equals(plan.ToolName, Name, StringComparison.Ordinal)
                ? null
                : $"Selected tool: {Name}");
        try
        {
            var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            turn?.Report(
                AgentActivityKind.ToolResult,
                plan is not null && string.Equals(plan.ToolName, Name, StringComparison.Ordinal)
                    ? plan.ResultHeadline
                    : $"{Humanize(Name)} returned; Ali is evaluating the result.",
                plan is not null && string.Equals(plan.ToolName, Name, StringComparison.Ordinal)
                    ? null
                    : $"{Humanize(Name)} completed; Ali is evaluating the returned evidence.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                executionReceipt: new AgentToolExecutionReceipt(
                    Name,
                    AgentToolExecutionOutcome.Completed,
                    Compact(SerializeResult(result)),
                    DateTimeOffset.UtcNow));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn?.Report(
                AgentActivityKind.Warning,
                $"{Humanize(Name)} was cancelled",
                "The user or application cancelled the in-flight tool call.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                executionReceipt: new AgentToolExecutionReceipt(
                    Name,
                    AgentToolExecutionOutcome.Cancelled,
                    "The in-flight tool call was cancelled before it returned a result.",
                    DateTimeOffset.UtcNow));
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            turn?.Report(
                AgentActivityKind.Error,
                $"{Humanize(Name)} failed",
                ex.Message,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                executionReceipt: new AgentToolExecutionReceipt(
                    Name,
                    AgentToolExecutionOutcome.Failed,
                    Compact(ex.Message),
                    DateTimeOffset.UtcNow));
            throw;
        }
    }

    private static string SerializeResult(object? result) => result switch
    {
        null => "No result",
        string text => text,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };

    private static string Compact(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaximumVisibleResultCharacters
            ? normalized
            : normalized[..MaximumVisibleResultCharacters] + "...";
    }

    private static string Humanize(string value) => value.Replace('_', ' ').Trim();
}
