using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// One framework-native lifecycle boundary shared by Ali and every private specialist.
/// It emits operational activity only; hidden reasoning is never inspected or exposed.
/// </summary>
internal static class AliAgentFrameworkMiddleware
{
    public static AIAgent WithVisibleLifecycle(
        AIAgent agent,
        Func<CoordinatorTurnContext?> turnAccessor,
        string role)
    {
        var builder = new AIAgentBuilder(agent);
        builder.Use(async (
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            Func<IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, CancellationToken, Task> next,
            CancellationToken cancellationToken) =>
        {
            var turn = turnAccessor();
            var timer = Stopwatch.StartNew();
            turn?.Report(
                AgentActivityKind.Status,
                $"{role} agent started",
                "Agent Framework middleware accepted the task.");
            try
            {
                await next(messages, session, options, cancellationToken).ConfigureAwait(false);
                turn?.Report(
                    AgentActivityKind.Status,
                    $"{role} agent finished",
                    $"Framework run completed in {timer.Elapsed.TotalSeconds:0.00} s.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                turn?.Report(
                    AgentActivityKind.Warning,
                    $"{role} agent stopped",
                    "The active framework run was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                turn?.Report(
                    AgentActivityKind.Error,
                    $"{role} agent failed safely",
                    ex.Message);
                throw;
            }
        });
        return builder.Build();
    }
}
