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
        var started = Stopwatch.GetTimestamp();
        turn?.Report(
            AgentActivityKind.ToolCall,
            $"Running {Humanize(Name)}",
            Compact(JsonSerializer.Serialize(arguments)));
        try
        {
            var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            turn?.Report(
                AgentActivityKind.ToolResult,
                $"Completed {Humanize(Name)}",
                Compact(SerializeResult(result)),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            turn?.Report(
                AgentActivityKind.Error,
                $"{Humanize(Name)} failed",
                ex.Message,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
