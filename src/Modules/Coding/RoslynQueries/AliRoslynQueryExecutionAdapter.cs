using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.RoslynQueries;

/// <summary>
/// Exact broker preparation and crash reconciliation for one read-only Roslyn semantic query.
/// Query execution can safely repeat after interruption because these adapters never publish
/// source or durable target-domain state.
/// </summary>
internal sealed class AliRoslynQueryExecutionAdapter : IAliExecutionEffectAdapter
{
    private readonly AliRoslynQueryTargetStateAdapter _targetStates;

    private AliRoslynQueryExecutionAdapter(
        string toolName,
        AliRoslynQueryTargetStateAdapter targetStates)
    {
        ToolName = toolName;
        CapabilityId = CapabilityIdFor(toolName);
        ReconcilerId = ReconcilerIdFor(toolName);
        _targetStates = targetStates ?? throw new ArgumentNullException(nameof(targetStates));
    }

    public string ToolName { get; }

    public string CapabilityId { get; }

    public string ReconcilerId { get; }

    internal static IReadOnlyList<IAliExecutionEffectAdapter> CreateAll(
        AliRoslynQueryTargetStateAdapter targetStates)
    {
        ArgumentNullException.ThrowIfNull(targetStates);
        return targetStates.ToolNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => (IAliExecutionEffectAdapter)new AliRoslynQueryExecutionAdapter(
                name,
                targetStates))
            .ToArray();
    }

    public ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.ToolName, ToolName, StringComparison.Ordinal)
            || !string.Equals(request.CapabilityId, CapabilityId, StringComparison.Ordinal)
            || !string.Equals(request.ReconcilerId, ReconcilerId, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The Roslyn semantic-query adapter received a mismatched execution identity.");
        }

        var current = _targetStates.Capture(ToolName, request.Arguments);
        var currentDigest = WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            current.TargetVersions);
        if (!string.Equals(
                currentDigest,
                request.TargetVersionDigest,
                StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The Roslyn semantic-query target changed after the accepted decision.");
        }

        var target = _targetStates.ResolveTarget(ToolName, request.Arguments);
        return ValueTask.FromResult(new AliExecutionPreparation(
            Guid.NewGuid().ToString("N"),
            AliRoslynActionExecutionAdapter.RootBinding(target.RootDirectory),
            request.TargetVersionDigest));
    }

    public ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(intent.ToolName, ToolName, StringComparison.Ordinal)
            || !string.Equals(intent.CapabilityId, CapabilityId, StringComparison.Ordinal)
            || !string.Equals(intent.ReconcilerId, ReconcilerId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
        {
            return ValueTask.FromResult(
                ActionReconciliationResult.Unknown(
                    "roslyn-query-adapter-identity-mismatch"));
        }

        return ValueTask.FromResult(
            ActionReconciliationResult.Absent("roslyn-query-safe-to-repeat"));
    }

    internal static string CapabilityIdFor(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return "ali.tool." + toolName;
    }

    internal static string ReconcilerIdFor(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return "ali.reconcile." + toolName;
    }
}
