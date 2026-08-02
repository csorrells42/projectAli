using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Security;

namespace Ali.Modules.Capabilities;

public sealed record CapabilityInvocationBlockedReason(
    string Code,
    string DependencyId,
    string Message);

public sealed class CapabilityInvocationBlockedResult
{
    internal CapabilityInvocationBlockedResult(
        string toolName,
        string currentResolutionRevision,
        IEnumerable<CapabilityAvailabilityReason> reasons)
    {
        ToolName = toolName;
        CurrentResolutionRevision = currentResolutionRevision;
        Reasons = Array.AsReadOnly(reasons
            .Select(reason => new CapabilityInvocationBlockedReason(
                reason.Code.ToString(),
                reason.DependencyId,
                reason.Message))
            .ToArray());
    }

    public bool Success => false;

    public bool Invoked => false;

    public string Status => "blocked";

    public string ToolName { get; }

    public string CurrentResolutionRevision { get; }

    public IReadOnlyList<CapabilityInvocationBlockedReason> Reasons { get; }
}

public sealed record CapabilityTerminalQuarantineNotice(
    string ToolName,
    string Code,
    string Message);

public sealed class CapabilityTerminalIssueReport
{
    internal CapabilityTerminalIssueReport(
        CapabilityTerminalToolInventory inventory,
        CapabilityPlanningPublication planning,
        IEnumerable<CapabilityTerminalToolIssue>? additionalIssues = null)
    {
        InventoryRevision = inventory.Revision;
        RuntimeRevision = planning.Runtime.RuntimeRevision;
        Issues = Array.AsReadOnly(inventory.Issues
            .Concat(additionalIssues ?? [])
            .OrderBy(issue => issue.ToolIdentity, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code)
            .ToArray());
        QuarantinedCapabilities = Array.AsReadOnly(planning.Resolution.QuarantinedCapabilities
            .Select(item => new CapabilityTerminalQuarantineNotice(
                item.ToolName,
                item.Code.ToString(),
                item.Message))
            .ToArray());
    }

    public string InventoryRevision { get; }

    public string RuntimeRevision { get; }

    public IReadOnlyList<CapabilityTerminalToolIssue> Issues { get; }

    public IReadOnlyList<CapabilityTerminalQuarantineNotice> QuarantinedCapabilities { get; }
}

/// <summary>
/// Terminal Agent Framework context provider that replaces the fully accumulated tool
/// set with the exact effective canonical functions. It does not inspect user text.
/// </summary>
public sealed class TerminalCapabilityEnforcementProvider : AIContextProvider
{
    private readonly CapabilitySettingsSnapshotOwner _owner;
    private readonly Func<CapabilityRuntimeStateSnapshot> _runtimeStateAccessor;
    private readonly Func<AIFunctionArguments, string?>? _targetBindingIdAccessor;
    private readonly Action<CapabilityTerminalIssueReport>? _issueCallback;
    private readonly Func<AIFunction, CapabilityResolutionSnapshot, AIFunction>? _functionProjector;
    private readonly Func<string>? _activeUserIdAccessor;
    private readonly Func<string>? _permissionRevisionAccessor;
    private readonly Func<string>? _activeUserRevisionAccessor;
    private readonly Func<string>? _invocationBoundaryRevisionAccessor;
    private readonly string _invocationBoundaryDependencyId;
    private readonly string _invocationBoundaryChangedMessage;
    private readonly string _invocationBoundaryUnavailableMessage;
    private readonly CapabilityResolver _resolver = new();

    public TerminalCapabilityEnforcementProvider(
        CapabilitySettingsSnapshotOwner owner,
        Func<CapabilityRuntimeStateSnapshot> runtimeStateAccessor,
        Func<AIFunctionArguments, string?>? targetBindingIdAccessor = null,
        Action<CapabilityTerminalIssueReport>? issueCallback = null,
        Func<AIFunction, CapabilityResolutionSnapshot, AIFunction>? functionProjector = null,
        Func<string>? activeUserIdAccessor = null,
        Func<string>? permissionRevisionAccessor = null,
        Func<string>? activeUserRevisionAccessor = null,
        Func<string>? invocationBoundaryRevisionAccessor = null,
        string invocationBoundaryDependencyId = "external-security-boundary",
        string invocationBoundaryChangedMessage = "An external security boundary changed after this tool was planned.",
        string invocationBoundaryUnavailableMessage = "An external security boundary could not be verified")
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _runtimeStateAccessor = runtimeStateAccessor
            ?? throw new ArgumentNullException(nameof(runtimeStateAccessor));
        _targetBindingIdAccessor = targetBindingIdAccessor;
        _issueCallback = issueCallback;
        _functionProjector = functionProjector;
        _activeUserIdAccessor = activeUserIdAccessor;
        _permissionRevisionAccessor = permissionRevisionAccessor;
        _activeUserRevisionAccessor = activeUserRevisionAccessor;
        _invocationBoundaryRevisionAccessor = invocationBoundaryRevisionAccessor;
        _invocationBoundaryDependencyId = string.IsNullOrWhiteSpace(invocationBoundaryDependencyId)
            ? throw new ArgumentException(
                "An invocation-boundary dependency ID is required.",
                nameof(invocationBoundaryDependencyId))
            : invocationBoundaryDependencyId;
        _invocationBoundaryChangedMessage = string.IsNullOrWhiteSpace(invocationBoundaryChangedMessage)
            ? throw new ArgumentException(
                "An invocation-boundary change message is required.",
                nameof(invocationBoundaryChangedMessage))
            : invocationBoundaryChangedMessage;
        _invocationBoundaryUnavailableMessage = string.IsNullOrWhiteSpace(invocationBoundaryUnavailableMessage)
            ? throw new ArgumentException(
                "An invocation-boundary unavailable message is required.",
                nameof(invocationBoundaryUnavailableMessage))
            : invocationBoundaryUnavailableMessage;
    }

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken) =>
        ApplyTerminalContextAsync(context.AIContext, cancellationToken);

    internal ValueTask<AIContext> ApplyTerminalContextAsync(
        AIContext accumulatedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accumulatedContext);
        cancellationToken.ThrowIfCancellationRequested();
        var finalTools = accumulatedContext.Tools?.ToArray() ?? [];
        var registry = _owner.CapturePlanning().Registry;
        var inventory = CapabilityTerminalToolInventory.Create(finalTools, registry);
        var duplicateNames = inventory.Issues
            .Where(issue => issue.Code == CapabilityTerminalToolIssueCode.DuplicateToolName)
            .Select(issue => issue.ToolIdentity)
            .ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var planning = PublishExactRuntime(inventory, cancellationToken);
            var plannedActiveUserRevision = _activeUserRevisionAccessor?.Invoke();
            var plannedInvocationBoundary = CaptureInvocationBoundary();
            if (!PermissionBoundaryMatches(planning)
                || !ActiveUserRevisionMatches(plannedActiveUserRevision))
            {
                continue;
            }

            var enforcedTools = new List<AITool>();
            var projectionIssues = new List<CapabilityTerminalToolIssue>();
            foreach (var function in finalTools.OfType<AIFunction>())
            {
                if (duplicateNames.Contains(function.Name)
                    || !planning.Resolution.TryGetTool(function.Name, out var descriptor))
                {
                    continue;
                }

                var projectedFunction = _functionProjector?.Invoke(function, planning.Resolution)
                    ?? function;
                if (!string.Equals(projectedFunction.Name, descriptor.ToolName, StringComparison.Ordinal)
                    || !string.Equals(
                        CapabilitySchemaIdentity.Calculate(projectedFunction),
                        descriptor.SchemaFingerprint,
                        StringComparison.Ordinal))
                {
                    projectionIssues.Add(new CapabilityTerminalToolIssue(
                        function.Name,
                        CapabilityTerminalToolIssueCode.SchemaIdentityMismatch,
                        $"Terminal projection for '{function.Name}' did not preserve its canonical schema and was withheld."));
                    continue;
                }

                var lease = planning.CreateInvocationLease(function.Name);
                AIFunction guarded = new CapabilityInvocationLeaseAIFunction(
                    projectedFunction,
                    _owner,
                    _resolver,
                    lease,
                    _targetBindingIdAccessor,
                    _activeUserIdAccessor,
                    _permissionRevisionAccessor,
                    invocationBoundaryRevisionAccessor: _invocationBoundaryRevisionAccessor,
                    plannedInvocationBoundaryRevision: plannedInvocationBoundary.Revision,
                    invocationBoundaryDependencyId: _invocationBoundaryDependencyId,
                    invocationBoundaryChangedMessage: _invocationBoundaryChangedMessage,
                    invocationBoundaryUnavailableMessage: _invocationBoundaryUnavailableMessage,
                    activeUserRevisionAccessor: _activeUserRevisionAccessor,
                    plannedActiveUserRevision: plannedActiveUserRevision,
                    livePlanningAccessor: token => PublishExactRuntime(inventory, token));
                var requiresApproval = ContainsApprovalMarker(projectedFunction)
                    || descriptor.Permission.RequiresApproval;
                if (requiresApproval && !ContainsApprovalMarker(guarded))
                {
                    guarded = new ApprovalRequiredAIFunction(guarded);
                }
                enforcedTools.Add(guarded);
            }

            if (!PermissionBoundaryMatches(planning)
                || !ActiveUserRevisionMatches(plannedActiveUserRevision)
                || !InvocationBoundaryMatches(plannedInvocationBoundary)
                || !string.Equals(
                    _owner.CapturePlanning().PublicationRevision,
                    planning.PublicationRevision,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (inventory.Issues.Count > 0
                || planning.Resolution.QuarantinedCapabilities.Count > 0
                || projectionIssues.Count > 0)
            {
                _issueCallback?.Invoke(
                    new CapabilityTerminalIssueReport(inventory, planning, projectionIssues));
            }

            return ValueTask.FromResult(new AIContext
            {
                Instructions = accumulatedContext.Instructions,
                Messages = accumulatedContext.Messages,
                Tools = Array.AsReadOnly(enforcedTools.ToArray())
            });
        }
    }

    private bool PermissionBoundaryMatches(CapabilityPlanningPublication planning) =>
        _permissionRevisionAccessor is null
        || string.Equals(
            _permissionRevisionAccessor(),
            planning.Resolution.PermissionRevision,
            StringComparison.Ordinal);

    private bool ActiveUserRevisionMatches(string? plannedActiveUserRevision) =>
        _activeUserRevisionAccessor is null
        || (!string.IsNullOrWhiteSpace(plannedActiveUserRevision)
            && string.Equals(
                _activeUserRevisionAccessor(),
                plannedActiveUserRevision,
                StringComparison.Ordinal));

    private bool InvocationBoundaryMatches(InvocationBoundaryCapture planned)
    {
        if (_invocationBoundaryRevisionAccessor is null || !planned.Available)
        {
            return true;
        }

        var current = CaptureInvocationBoundary();
        return !current.Available
            || string.Equals(
                current.Revision,
                planned.Revision,
                StringComparison.Ordinal);
    }

    private InvocationBoundaryCapture CaptureInvocationBoundary()
    {
        if (_invocationBoundaryRevisionAccessor is null)
        {
            return new InvocationBoundaryCapture(null, Available: true);
        }

        try
        {
            var revision = _invocationBoundaryRevisionAccessor();
            return string.IsNullOrWhiteSpace(revision)
                ? new InvocationBoundaryCapture(null, Available: false)
                : new InvocationBoundaryCapture(revision, Available: true);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or SecurityException)
        {
            return new InvocationBoundaryCapture(null, Available: false);
        }
    }

    private CapabilityPlanningPublication PublishExactRuntime(
        CapabilityTerminalToolInventory inventory,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = _owner.CaptureSettings().Stamp;
            var state = _runtimeStateAccessor()
                ?? throw new InvalidOperationException(
                    "The capability runtime state accessor returned no state.");
            var runtime = CapabilityRuntimeAvailabilityFactory.Create(inventory, state);
            var current = _owner.CapturePlanning();
            if (string.Equals(
                    current.Runtime.RuntimeRevision,
                    runtime.RuntimeRevision,
                    StringComparison.Ordinal))
            {
                return current;
            }

            if (!_owner.TryPublishRuntime(expected, runtime, out _))
            {
                continue;
            }

            var published = _owner.CapturePlanning();
            if (string.Equals(
                    published.Runtime.RuntimeRevision,
                    runtime.RuntimeRevision,
                    StringComparison.Ordinal))
            {
                return published;
            }
        }
    }

    private static bool ContainsApprovalMarker(AIFunction function) =>
        function.GetService<ApprovalRequiredAIFunction>() is not null;

    private sealed record InvocationBoundaryCapture(string? Revision, bool Available);
}

internal sealed class CapabilityInvocationLeaseAIFunction(
    AIFunction innerFunction,
    CapabilitySettingsSnapshotOwner owner,
    CapabilityResolver resolver,
    CapabilityInvocationLease lease,
    Func<AIFunctionArguments, string?>? targetBindingIdAccessor,
    Func<string>? activeUserIdAccessor = null,
    Func<string>? permissionRevisionAccessor = null,
    Func<string>? invocationBoundaryRevisionAccessor = null,
    string? plannedInvocationBoundaryRevision = null,
    string invocationBoundaryDependencyId = "persisted-security-boundary",
    string invocationBoundaryChangedMessage = "Persisted MCP security settings changed after this tool was published; restart the MCP host.",
    string invocationBoundaryUnavailableMessage = "Persisted MCP security settings could not be verified",
    Func<string>? activeUserRevisionAccessor = null,
    string? plannedActiveUserRevision = null,
    Func<CancellationToken, CapabilityPlanningPublication>? livePlanningAccessor = null) :
    DelegatingAIFunction(innerFunction)
{
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var planning = livePlanningAccessor?.Invoke(cancellationToken)
            ?? owner.CapturePlanning();
        var liveBoundaryReasons = new List<CapabilityAvailabilityReason>();
        if (!string.Equals(
                planning.PublicationRevision,
                lease.PlanningPublicationRevision,
                StringComparison.Ordinal))
        {
            liveBoundaryReasons.Add(new CapabilityAvailabilityReason(
                CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                "planning-publication",
                "The capability security boundary changed after this tool was planned."));
        }
        if (activeUserIdAccessor?.Invoke() is { } activeUserId
            && !string.Equals(activeUserId, lease.ActiveUserId, StringComparison.Ordinal))
        {
            liveBoundaryReasons.Add(new CapabilityAvailabilityReason(
                CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                "active-user",
                "The active user changed after this tool was planned."));
        }
        if (activeUserRevisionAccessor is not null)
        {
            var activeUserRevision = activeUserRevisionAccessor();
            if (string.IsNullOrWhiteSpace(plannedActiveUserRevision)
                || !string.Equals(
                    activeUserRevision,
                    plannedActiveUserRevision,
                    StringComparison.Ordinal))
            {
                liveBoundaryReasons.Add(new CapabilityAvailabilityReason(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    "active-user-selection",
                    "The active-user selection generation changed after this tool was planned."));
            }
        }
        if (permissionRevisionAccessor?.Invoke() is { } permissionRevision
            && !string.Equals(permissionRevision, lease.PermissionRevision, StringComparison.Ordinal))
        {
            liveBoundaryReasons.Add(new CapabilityAvailabilityReason(
                CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                "permission-profile",
                "The permission profile changed after this tool was planned."));
        }
        if (invocationBoundaryRevisionAccessor is not null)
        {
            try
            {
                var currentBoundaryRevision = invocationBoundaryRevisionAccessor();
                if (string.IsNullOrWhiteSpace(plannedInvocationBoundaryRevision)
                    || !string.Equals(
                        currentBoundaryRevision,
                        plannedInvocationBoundaryRevision,
                        StringComparison.Ordinal))
                {
                    liveBoundaryReasons.Add(new CapabilityAvailabilityReason(
                        CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                        invocationBoundaryDependencyId,
                        invocationBoundaryChangedMessage));
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or SecurityException)
            {
                liveBoundaryReasons.Add(new CapabilityAvailabilityReason(
                    CapabilityAvailabilityReasonCode.InvocationLeaseStale,
                    invocationBoundaryDependencyId,
                    $"{invocationBoundaryUnavailableMessage} ({ex.GetType().Name})."));
            }
        }
        var boundTargetBindingId = lease.RequiresTargetResolution
            ? targetBindingIdAccessor?.Invoke(arguments)
            : null;
        var validation = resolver.ValidateInvocation(
            planning.Registry,
            planning.Runtime,
            lease,
            boundTargetBindingId);
        if (liveBoundaryReasons.Count > 0 || !validation.Success)
        {
            var reasons = liveBoundaryReasons
                .Concat(validation.Reasons)
                .DistinctBy(reason => (reason.Code, reason.DependencyId, reason.Message))
                .ToArray();
            return ValueTask.FromResult<object?>(new CapabilityInvocationBlockedResult(
                lease.ToolName,
                validation.CurrentResolutionRevision,
                reasons));
        }

        return InnerFunction.InvokeAsync(arguments, cancellationToken);
    }
}
