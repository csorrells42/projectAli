using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.Observation;

internal interface IShadowToolObserver
{
    bool TryObserveReturned(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        object? result,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission);

    bool TryObserveDenied(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        string? failureCode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission);

    bool TryObserveThrew(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        Exception exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission);

    bool TryObserveCancelled(
        TurnIdentity? identity,
        string callId,
        string toolName,
        object? arguments,
        OperationCanceledException exception,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission);

    ShadowObservationHealthSnapshot Health { get; }
}

internal sealed record ShadowObservationHealthSnapshot(
    long Enqueued,
    long Pending,
    long QueueFullDrops,
    long MissingIdentityDrops,
    long InvalidObservationDrops,
    long StoppedDrops,
    long Persisted,
    long DuplicateTerminals,
    long PersistenceFailures,
    long DedupeEvictions,
    long ShutdownTimeouts,
    long ShutdownPendingAtTimeout,
    long ShutdownAbandoned,
    bool IsAccepting,
    bool IsReaderCompleted);

internal sealed record ShadowToolObservation
{
    internal const int MaximumIdentityComponentCharacters = 512;
    internal const int MaximumCallIdCharacters = 256;
    internal const int MaximumToolNameCharacters = 256;
    internal const int MaximumFailureCodeCharacters = 128;
    internal const int MaximumExceptionTypeCharacters = 512;
    internal const int MaximumPermissionValueCharacters = 64;

    private ShadowToolObservation(
        TurnIdentity identity,
        string callId,
        string toolName,
        InvocationStatus invocationStatus,
        bool? reportedSuccess,
        string? exceptionType,
        string? failureCode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        Identity = identity;
        CallId = callId;
        ToolName = toolName;
        InvocationStatus = invocationStatus;
        ReportedSuccess = reportedSuccess;
        ExceptionType = exceptionType;
        FailureCode = failureCode;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Permission = permission with { };
    }

    public TurnIdentity Identity { get; }

    public string CallId { get; }

    public string ToolName { get; }

    public InvocationStatus InvocationStatus { get; }

    /// <summary>
    /// A typed domain-success value supplied directly by a trusted adapter. Generic observation
    /// deliberately leaves this unreported instead of inspecting an arbitrary result object.
    /// </summary>
    public bool? ReportedSuccess { get; }

    /// <summary>
    /// The bounded runtime type name captured for a thrown terminal. The exception object and its
    /// message, data, inner exceptions, and stack are never retained by the observer.
    /// </summary>
    public string? ExceptionType { get; }

    public string? FailureCode { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public EvidencePermissionMetadata Permission { get; }

    public static ShadowToolObservation Returned(
        TurnIdentity identity,
        string callId,
        string toolName,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission,
        bool? reportedSuccess = null) =>
        Create(
            identity,
            callId,
            toolName,
            InvocationStatus.Returned,
            reportedSuccess,
            null,
            null,
            startedAtUtc,
            completedAtUtc,
            permission);

    public static ShadowToolObservation Denied(
        TurnIdentity identity,
        string callId,
        string toolName,
        string? failureCode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission) =>
        Create(
            identity,
            callId,
            toolName,
            InvocationStatus.Denied,
            null,
            null,
            failureCode,
            startedAtUtc,
            completedAtUtc,
            permission);

    public static ShadowToolObservation Threw(
        TurnIdentity identity,
        string callId,
        string toolName,
        string exceptionType,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        return Create(
            identity,
            callId,
            toolName,
            InvocationStatus.Threw,
            null,
            exceptionType,
            null,
            startedAtUtc,
            completedAtUtc,
            permission);
    }

    public static ShadowToolObservation Cancelled(
        TurnIdentity identity,
        string callId,
        string toolName,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        return Create(
            identity,
            callId,
            toolName,
            InvocationStatus.Cancelled,
            null,
            null,
            null,
            startedAtUtc,
            completedAtUtc,
            permission);
    }

    private static ShadowToolObservation Create(
        TurnIdentity identity,
        string callId,
        string toolName,
        InvocationStatus invocationStatus,
        bool? reportedSuccess,
        string? exceptionType,
        string? failureCode,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        EvidencePermissionMetadata permission)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireBounded(identity.UserId, MaximumIdentityComponentCharacters, nameof(identity));
        RequireBounded(identity.ConversationId, MaximumIdentityComponentCharacters, nameof(identity));
        RequireBounded(identity.AssistantMessageId, MaximumIdentityComponentCharacters, nameof(identity));
        RequireBounded(callId, MaximumCallIdCharacters, nameof(callId));
        RequireBounded(toolName, MaximumToolNameCharacters, nameof(toolName));
        ArgumentNullException.ThrowIfNull(permission);
        RequireBounded(
            permission.Decision,
            MaximumPermissionValueCharacters,
            nameof(permission));
        RequireBounded(
            permission.Scope,
            MaximumPermissionValueCharacters,
            nameof(permission));
        RequireOptionalBounded(failureCode, MaximumFailureCodeCharacters, nameof(failureCode));
        RequireOptionalBounded(exceptionType, MaximumExceptionTypeCharacters, nameof(exceptionType));
        if (completedAtUtc < startedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAtUtc),
                "Observation completion cannot precede its start.");
        }

        if (invocationStatus != InvocationStatus.Returned && reportedSuccess is not null)
        {
            throw new ArgumentException(
                "Only a returned observation can carry a reported success value.",
                nameof(reportedSuccess));
        }

        if ((invocationStatus == InvocationStatus.Threw) != (exceptionType is not null))
        {
            throw new ArgumentException(
                "Exactly a thrown observation must carry an exception type.",
                nameof(exceptionType));
        }

        if (invocationStatus != InvocationStatus.Denied && failureCode is not null)
        {
            throw new ArgumentException(
                "Only a denied observation can carry a failure code.",
                nameof(failureCode));
        }

        return new ShadowToolObservation(
            identity,
            callId,
            toolName,
            invocationStatus,
            reportedSuccess,
            exceptionType,
            failureCode,
            startedAtUtc,
            completedAtUtc,
            permission);
    }

    private static void RequireBounded(string? value, int maximumCharacters, string parameterName)
    {
        if (value is null
            || value.Length == 0
            || value.Length > maximumCharacters
            || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"Observation metadata must contain 1-{maximumCharacters} non-whitespace characters.",
                parameterName);
        }
    }

    private static void RequireOptionalBounded(
        string? value,
        int maximumCharacters,
        string parameterName)
    {
        if (value is null)
        {
            return;
        }

        RequireBounded(value, maximumCharacters, parameterName);
    }
}
