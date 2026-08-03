using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ali.Modules.UserMemory;

public sealed class Mem0UserMemoryService :
    IUserMemoryService,
    IParticipantMemoryDesktopReviewService,
    IParticipantMemoryService,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions WorkerJsonOptions = CreateWorkerJsonOptions();
    private readonly IParticipantMemoryTransport _client;
    private readonly Func<UserMemorySettings> _settings;
    private readonly ParticipantMemoryReceiptAuthority _receiptAuthority;
    private readonly IParticipantRosterAuthority? _rosterAuthority;
    private readonly IActiveUserSession? _activeUsers;
    private readonly IParticipantMemoryAuthenticationProvider? _authenticationProvider;
    private readonly Dictionary<string, ParticipantMemoryRecord> _desktopRecordCache =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _desktopRecordCacheOrder = new();
    private readonly object _desktopRecordCacheSync = new();

    internal Mem0UserMemoryService(
        IParticipantMemoryTransport client,
        Func<UserMemorySettings> settings,
        ParticipantMemoryReceiptAuthority? receiptAuthority = null,
        IParticipantRosterAuthority? rosterAuthority = null,
        IActiveUserSession? activeUsers = null,
        IParticipantMemoryAuthenticationProvider? authenticationProvider = null)
    {
        _client = client;
        _settings = settings;
        _receiptAuthority = receiptAuthority ?? new ParticipantMemoryReceiptAuthority();
        _rosterAuthority = rosterAuthority;
        _activeUsers = activeUsers;
        _authenticationProvider = authenticationProvider;
    }

    internal ParticipantMemoryReceiptAuthority ReceiptAuthority => _receiptAuthority;

    private static JsonSerializerOptions CreateWorkerJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public Task<IReadOnlyList<UserMemory>> RecallAsync(
        ActiveUser user,
        string query,
        int maximumResults,
        CancellationToken cancellationToken) =>
        RecallDesktopParticipantsAsync(
            user,
            query,
            maximumResults,
            includeSensitive: false,
            cancellationToken);

    public async Task<IReadOnlyList<UserMemory>> RecallDesktopParticipantsAsync(
        ActiveUser user,
        string query,
        int maximumResults,
        bool includeSensitive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)
            || !TryCreateDesktopContext(user, "Read", out var context, out _))
        {
            return [];
        }
        var health = await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken)
            .ConfigureAwait(false);
        if (!ParticipantMemoryReady(health))
        {
            return [];
        }
        var result = await RecallParticipantsAsync(
            new ParticipantMemoryRecallRequest(
                context.RequestId,
                context.Roster,
                context.Authority,
                query.Trim(),
                Math.Clamp(maximumResults, 1, ParticipantMemoryLimits.MaximumRecallResults),
                health.EmbeddingSpaceId,
                IncludeSensitive: includeSensitive),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return [];
        }
        CacheDesktopRecords(result.Records);
        return result.Records.Select(ToUserMemory).ToArray();
    }

    public Task<MemoryOperationResult> RememberAsync(
        ActiveUser user,
        string conversation,
        string source,
        string? category,
        CancellationToken cancellationToken) =>
        Task.FromResult(MemoryOperationResult.Failed(
            "Legacy active-user memory writes are retired from the participant collection.",
            "participant_memory_required"));

    public async Task<MemoryOperationResult> CorrectAsync(
        ActiveUser user,
        string memoryId,
        string correction,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(memoryId) || string.IsNullOrWhiteSpace(correction))
        {
            return MemoryOperationResult.Failed(
                "Choose one exact participant memory and enter a correction.",
                "invalid_request");
        }
        if (!TryGetDesktopRecord(memoryId.Trim(), out var target))
        {
            _ = await ListAsync(user, null, cancellationToken).ConfigureAwait(false);
            if (!TryGetDesktopRecord(memoryId.Trim(), out target))
            {
                return MemoryOperationResult.Failed(
                    "The exact participant memory is not in the current authorized review set.",
                    "not_found");
            }
        }
        return await MutateDesktopRecordAsync(
            user,
            target!,
            ParticipantMemoryMutationKind.Correct,
            correction.Trim(),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<UserMemory>> ListAsync(
        ActiveUser user,
        string? category,
        CancellationToken cancellationToken) =>
        ListDesktopParticipantsAsync(
            user,
            category,
            includeSensitive: false,
            cancellationToken);

    public async Task<IReadOnlyList<UserMemory>> ListDesktopParticipantsAsync(
        ActiveUser user,
        string? category,
        bool includeSensitive,
        CancellationToken cancellationToken) =>
        (await ReviewDesktopParticipantsAsync(
            user,
            category,
            includeSensitive,
            cancellationToken).ConfigureAwait(false)).Memories;

    public async Task<ParticipantMemoryDesktopReviewResult> ReviewDesktopParticipantsAsync(
        ActiveUser user,
        string? category,
        bool includeSensitive,
        CancellationToken cancellationToken)
    {
        if (!TryCreateDesktopContext(user, "Read", out var context, out var admissionFailure))
        {
            return ParticipantMemoryDesktopReviewResult.Failed(
                admissionFailure,
                ParticipantMemoryFailureCode.PermissionDenied);
        }
        var health = await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken)
            .ConfigureAwait(false);
        if (!ParticipantMemoryReady(health))
        {
            return ParticipantMemoryDesktopReviewResult.Failed(
                health.Failure?.SafeMessage
                    ?? "Participant memory is unavailable; no memories were loaded.",
                health.Failure?.Code ?? ParticipantMemoryFailureCode.Unavailable);
        }
        var result = await ListParticipantsAsync(
            new ParticipantMemoryListRequest(
                context.RequestId,
                context.Roster,
                context.Authority,
                ParticipantMemoryLimits.MaximumRecallResults,
                health.EmbeddingSpaceId,
                IncludeSensitive: includeSensitive),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return ParticipantMemoryDesktopReviewResult.Failed(
                result.Failure?.SafeMessage
                    ?? "Participant-memory review failed safely; no memories were loaded.",
                result.Failure?.Code ?? ParticipantMemoryFailureCode.ProtocolFailure);
        }
        CacheDesktopRecords(result.Records);
        var memories = result.Records
            .Where(record => string.IsNullOrWhiteSpace(category)
                || string.Equals(record.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(ToUserMemory)
            .ToArray();
        return new(
            true,
            memories,
            includeSensitive
                ? "Sensitive review completed through independent Windows credential verification."
                : "Authorized low-sensitivity review completed.");
    }

    public async Task<MemoryOperationResult> DeleteAsync(
        ActiveUser user,
        string memoryId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            return MemoryOperationResult.Failed(
                "Choose one exact participant memory to delete.",
                "invalid_request");
        }
        if (!TryGetDesktopRecord(memoryId.Trim(), out var target))
        {
            _ = await ListAsync(user, null, cancellationToken).ConfigureAwait(false);
            if (!TryGetDesktopRecord(memoryId.Trim(), out target))
            {
                return MemoryOperationResult.Failed(
                    "The exact participant memory is not in the current authorized review set.",
                    "not_found");
            }
        }
        return await MutateDesktopRecordAsync(
            user,
            target!,
            ParticipantMemoryMutationKind.Delete,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMemoryStatus> TestAsync(ActiveUser user, CancellationToken cancellationToken)
    {
        if (!TryCreateDesktopContext(user, "Read", out var context, out var failure))
        {
            return new(
                _settings().Normalize().Enabled,
                false,
                false,
                "SelectionRequired",
                failure);
        }
        var health = await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken)
            .ConfigureAwait(false);
        var ready = ParticipantMemoryReady(health);
        var count = 0;
        if (ready)
        {
            var list = await ListParticipantsAsync(
                new ParticipantMemoryListRequest(
                    context.RequestId,
                    context.Roster,
                    context.Authority,
                    ParticipantMemoryLimits.MaximumRecallResults,
                    health.EmbeddingSpaceId,
                    IncludeSensitive: false),
                cancellationToken).ConfigureAwait(false);
            if (list.Success)
            {
                count = list.Records.Count;
                CacheDesktopRecords(list.Records);
            }
        }
        return new(
            health.Enabled,
            ready,
            health.QdrantAvailable,
            ready ? "Ready" : health.Failure?.Code.ToString() ?? "Unavailable",
            ready
                ? $"Participant memory is ready in {health.CollectionName}."
                : health.Failure?.SafeMessage ?? "Participant memory is unavailable.",
            count);
    }

    internal async Task<ParticipantMemoryHealthResult> CheckDesktopParticipantHealthAsync(
        ActiveUser user,
        CancellationToken cancellationToken)
    {
        if (!TryCreateDesktopContext(user, "Read", out var context, out var failure))
        {
            return new(
                _settings().Normalize().Enabled,
                false,
                false,
                string.Empty,
                string.Empty,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.PermissionDenied,
                    "health",
                    "desktop-health",
                    failure));
        }
        return await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<ParticipantMemoryRepairResult> RepairDesktopParticipantPointsAsync(
        ActiveUser user,
        IReadOnlyList<string> pointIds,
        CancellationToken cancellationToken)
    {
        if (!TryCreateDesktopContext(user, "Repair", out var context, out var failure))
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                "Repair",
                "desktop-repair",
                failure));
        }
        var authentication = await TryAuthenticateAsync(
            context.Authority.RequestingParticipantReference,
            "Repair",
            "Confirm access to sensitive participant-memory repair candidates.",
            cancellationToken).ConfigureAwait(false);
        if (authentication is null)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.AuthenticationRequired,
                "Repair",
                context.RequestId,
                "Participant-memory repair discovery requires the selected user's Windows credential."));
        }
        context = context with
        {
            Authority = context.Authority with { Authentication = authentication }
        };
        var health = await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken)
            .ConfigureAwait(false);
        if (!health.Enabled || !health.EmbeddingAvailable)
        {
            return RepairFailed(health.Failure ?? ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Unavailable,
                "Repair",
                context.RequestId,
                "Participant-memory embedding verification is unavailable."));
        }
        return await RepairParticipantEmbeddingSpaceAsync(
            new ParticipantMemoryRepairRequest(
                context.RequestId,
                context.Roster,
                context.Authority,
                health.EmbeddingSpaceId,
                pointIds),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParticipantMemoryRecallResult> RecallParticipantsAsync(
        ParticipantMemoryRecallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = _settings().Normalize();
        var rosterRevision = SafeRosterRevision(request.Roster);
        if (!settings.Enabled)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Disabled,
                    "recall",
                    request.RequestId,
                    "Participant memory is disabled."));
        }
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new(true, [], rosterRevision, request.ExpectedEmbeddingSpaceId, null);
        }
        if (request.Query.Trim().Length > ParticipantMemoryLimits.MaximumRecallQueryLength)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    "recall",
                    request.RequestId,
                    $"Participant-memory recall queries may contain at most {ParticipantMemoryLimits.MaximumRecallQueryLength} characters."));
        }

        ParticipantRosterSnapshot roster;
        try
        {
            roster = request.Roster.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    "recall",
                    request.RequestId,
                    $"The participant roster is invalid: {ex.Message}"));
        }
        var authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Read",
            request.RequestId,
            DateTimeOffset.UtcNow,
            _receiptAuthority);
        if (authorityFailure is not null)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                request.ExpectedEmbeddingSpaceId,
                authorityFailure);
        }
        var staleFailure = ValidateCurrentRoster(roster, "recall", request.RequestId);
        if (staleFailure is not null)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                request.ExpectedEmbeddingSpaceId,
                staleFailure);
        }
        if (request.IncludeSensitive && request.Authority.Authentication is null)
        {
            var authentication = await TryAuthenticateAsync(
                request.Authority.RequestingParticipantReference,
                "Read",
                "Confirm access to sensitive participant memory.",
                cancellationToken).ConfigureAwait(false);
            if (authentication is null)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    request.ExpectedEmbeddingSpaceId,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.AuthenticationRequired,
                        "recall",
                        request.RequestId,
                        "Sensitive participant-memory recall requires the selected profile owner's Windows credential."));
            }
            request = request with
            {
                Authority = request.Authority with { Authentication = authentication }
            };
            authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                roster,
                request.Authority,
                "Read",
                request.RequestId,
                DateTimeOffset.UtcNow,
                _receiptAuthority);
            staleFailure = ValidateCurrentRoster(roster, "recall", request.RequestId);
            if (authorityFailure is not null || staleFailure is not null)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    request.ExpectedEmbeddingSpaceId,
                    authorityFailure ?? staleFailure!);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.RecallTimeoutMilliseconds));
        Mem0EmbeddingSpaceConfiguration space;
        try
        {
            space = await _client.ResolveCurrentEmbeddingSpaceAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.TimedOut,
                    "recall",
                    request.RequestId,
                    "Participant-memory embedding verification reached the recall deadline.",
                    retryable: true));
        }
        catch (OperationCanceledException)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    "recall",
                    request.RequestId,
                    "Participant-memory recall was cancelled.",
                    retryable: true));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                FailureFromException("recall", request.RequestId, ex));
        }
        if (!string.Equals(
                request.ExpectedEmbeddingSpaceId,
                space.Id,
                StringComparison.Ordinal))
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                space.Id,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                    "recall",
                    request.RequestId,
                "The recall request targets a different embedding space."));
        }

        var dispatchUtc = DateTimeOffset.UtcNow;
        authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Read",
            request.RequestId,
            dispatchUtc,
            _receiptAuthority);
        authorityFailure ??= ValidateCurrentAuthenticationBinding(
            request.Authority,
            "Read",
            request.RequestId,
            dispatchUtc);
        staleFailure = ValidateCurrentRoster(roster, "recall", request.RequestId);
        if (authorityFailure is not null || staleFailure is not null)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                authorityFailure ?? staleFailure!);
        }

        var maximumResults = Math.Clamp(
            request.MaximumResults,
            1,
            Math.Min(settings.RecallMaximumResults, ParticipantMemoryLimits.MaximumRecallResults));
        var accessKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            request.Authority,
            DateTimeOffset.UtcNow,
            _receiptAuthority,
            "Read");
        try
        {
            var response = await SendAsync(new
            {
                operation = "participant_recall",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                accessKeys,
                query = request.Query.Trim(),
                maximumResults
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    FailureFromResponse("recall", request.RequestId, response));
            }
            if (!string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.StaleResult,
                        "recall",
                        request.RequestId,
                        "A stale participant-memory result was rejected.",
                        retryable: true));
            }
            staleFailure = ValidateCurrentRoster(roster, "recall", request.RequestId);
            if (staleFailure is not null)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    staleFailure);
            }

            var records = response.ParticipantMemories ?? [];
            if (records.Count > maximumResults)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        "recall",
                        request.RequestId,
                    "Participant memory returned more than the requested bounded result count."));
            }
            if (records.Any(record => !ParticipantRecordHasBoundedShape(record)))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        "recall",
                        request.RequestId,
                        "Participant memory returned a malformed or unbounded recall record."));
            }
            if (records.Any(record => !string.Equals(
                    record.EmbeddingSpaceId,
                    space.Id,
                    StringComparison.Ordinal)))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                        "recall",
                        request.RequestId,
                        "A vector from another embedding space was rejected."));
            }

            if (records.Any(record =>
                    !string.Equals(record.TenantId, roster.TenantId, StringComparison.Ordinal)
                    || record.State != ParticipantMemoryState.Confirmed))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        "recall",
                        request.RequestId,
                        "Participant memory returned an ineligible tenant or lifecycle state."));
            }
            if (records.Any(record => !ParticipantMemoryPolicy.BuildRecordAccessKeys(record)
                    .Intersect(accessKeys, StringComparer.Ordinal)
                    .Any()))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.PermissionDenied,
                        "recall",
                        request.RequestId,
                        "Participant memory returned a record outside the authorized audience."));
            }

            var acceptedIds = FilterRecallMatches(
                    records.Select(ToUserMemory).ToArray(),
                    settings,
                    maximumResults)
                .Select(memory => memory.MemoryId)
                .ToHashSet(StringComparer.Ordinal);
            var acceptedRecords = records
                .Where(record => acceptedIds.Contains(record.MemoryId))
                .ToArray();
            return new(true, acceptedRecords, roster.Revision, space.Id, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.TimedOut,
                    "recall",
                    request.RequestId,
                    "Participant-memory recall reached its configured deadline.",
                    retryable: true));
        }
        catch (OperationCanceledException)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    "recall",
                    request.RequestId,
                    "Participant-memory recall was cancelled.",
                    retryable: true));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                FailureFromException("recall", request.RequestId, ex));
        }
    }

    public async Task<ParticipantMemoryRecallResult> ListParticipantsAsync(
        ParticipantMemoryListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = _settings().Normalize();
        var rosterRevision = SafeRosterRevision(request.Roster);
        if (!settings.Enabled)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Disabled,
                    "List",
                    request.RequestId,
                    "Participant memory is disabled."));
        }

        ParticipantRosterSnapshot roster;
        try
        {
            roster = request.Roster.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                FailureFromException("List", request.RequestId, ex));
        }
        var authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Read",
            request.RequestId,
            DateTimeOffset.UtcNow,
            _receiptAuthority);
        if (authorityFailure is not null)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                request.ExpectedEmbeddingSpaceId,
                authorityFailure);
        }
        var staleFailure = ValidateCurrentRoster(roster, "List", request.RequestId);
        if (staleFailure is not null)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                request.ExpectedEmbeddingSpaceId,
                staleFailure);
        }
        if (request.IncludeSensitive && request.Authority.Authentication is null)
        {
            var authentication = await TryAuthenticateAsync(
                request.Authority.RequestingParticipantReference,
                "Read",
                "Confirm access to the sensitive participant-memory inventory.",
                cancellationToken).ConfigureAwait(false);
            if (authentication is null)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    request.ExpectedEmbeddingSpaceId,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.AuthenticationRequired,
                        "List",
                        request.RequestId,
                        "Sensitive participant-memory listing requires the selected profile owner's Windows credential."));
            }
            request = request with
            {
                Authority = request.Authority with { Authentication = authentication }
            };
            authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                roster,
                request.Authority,
                "Read",
                request.RequestId,
                DateTimeOffset.UtcNow,
                _receiptAuthority);
            staleFailure = ValidateCurrentRoster(roster, "List", request.RequestId);
            if (authorityFailure is not null || staleFailure is not null)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    request.ExpectedEmbeddingSpaceId,
                    authorityFailure ?? staleFailure!);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.RecallTimeoutMilliseconds));
        Mem0EmbeddingSpaceConfiguration space;
        try
        {
            space = await _client.ResolveCurrentEmbeddingSpaceAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.TimedOut,
                    "List",
                    request.RequestId,
                    "Participant-memory embedding verification reached the list deadline.",
                    retryable: true));
        }
        catch (OperationCanceledException)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    "List",
                    request.RequestId,
                    "Participant-memory list was cancelled.",
                    retryable: true));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                request.ExpectedEmbeddingSpaceId,
                FailureFromException("List", request.RequestId, ex));
        }
        if (!string.Equals(request.ExpectedEmbeddingSpaceId, space.Id, StringComparison.Ordinal))
        {
            return ParticipantMemoryRecallResult.Failed(
                rosterRevision,
                space.Id,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                    "List",
                    request.RequestId,
                "The list request targets a different embedding space."));
        }

        var dispatchUtc = DateTimeOffset.UtcNow;
        authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Read",
            request.RequestId,
            dispatchUtc,
            _receiptAuthority);
        authorityFailure ??= ValidateCurrentAuthenticationBinding(
            request.Authority,
            "Read",
            request.RequestId,
            dispatchUtc);
        staleFailure = ValidateCurrentRoster(roster, "List", request.RequestId);
        if (authorityFailure is not null || staleFailure is not null)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                authorityFailure ?? staleFailure!);
        }

        var maximumResults = Math.Clamp(
            request.MaximumResults,
            1,
            Math.Min(settings.RecallMaximumResults, ParticipantMemoryLimits.MaximumRecallResults));
        var accessKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            request.Authority,
            DateTimeOffset.UtcNow,
            _receiptAuthority,
            "Read");
        try
        {
            var response = await SendAsync(new
            {
                operation = "participant_list",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                authorizedAccessKeys = accessKeys,
                maximumResults
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    FailureFromResponse("List", request.RequestId, response));
            }
            if (!string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.StaleResult,
                        "List",
                        request.RequestId,
                        "A stale participant-memory list was rejected.",
                        retryable: true));
            }
            var liveFailure = ValidateCurrentRoster(roster, "List", request.RequestId);
            if (liveFailure is not null)
            {
                return ParticipantMemoryRecallResult.Failed(roster.Revision, space.Id, liveFailure);
            }

            var records = response.ParticipantMemories ?? [];
            if (records.Count > maximumResults
                || records.Any(record => !ParticipantRecordHasBoundedShape(record))
                || records.Any(record =>
                    !string.Equals(record.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                    || !string.Equals(record.TenantId, roster.TenantId, StringComparison.Ordinal)
                    || record.State != ParticipantMemoryState.Confirmed
                    || !ParticipantMemoryPolicy.BuildRecordAccessKeys(record)
                        .Intersect(accessKeys, StringComparer.Ordinal)
                        .Any()))
            {
                return ParticipantMemoryRecallResult.Failed(
                    roster.Revision,
                    space.Id,
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        "List",
                        request.RequestId,
                        "Participant memory returned an ineligible list result."));
            }
            return new(true, records, roster.Revision, space.Id, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.TimedOut,
                    "List",
                    request.RequestId,
                    "Participant-memory list reached its configured deadline.",
                    retryable: true));
        }
        catch (OperationCanceledException)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    "List",
                    request.RequestId,
                    "Participant-memory list was cancelled.",
                    retryable: true));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return ParticipantMemoryRecallResult.Failed(
                roster.Revision,
                space.Id,
                FailureFromException("List", request.RequestId, ex));
        }
    }

    public async Task<ParticipantMemoryMutationResult> MutateParticipantsAsync(
        ParticipantMemoryMutationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        static ParticipantMemoryMutationResult ConfirmedNoEffect(
            ParticipantMemoryFailureReceipt failure) =>
            ParticipantMemoryMutationResult.Failed(
                failure,
                noEffectConfirmed: true,
                mutationStatus: "rolled_back");
        var settings = _settings().Normalize();
        if (!settings.Enabled)
        {
            return ConfirmedNoEffect(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Disabled,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Participant memory is disabled."));
        }

        var requiresAuthentication = request.Proposal.Sensitivity == ParticipantMemorySensitivity.Sensitive
            || request.Proposal.Operation is ParticipantMemoryMutationKind.Correct
                or ParticipantMemoryMutationKind.Dispute
                or ParticipantMemoryMutationKind.Revoke
                or ParticipantMemoryMutationKind.Archive
                or ParticipantMemoryMutationKind.Delete;
        var validation = ParticipantMemoryPolicy.ValidateMutation(
            request,
            DateTimeOffset.UtcNow,
            _receiptAuthority);
        if (!validation.Valid
            && validation.Failure?.Code == ParticipantMemoryFailureCode.AuthenticationRequired
            && requiresAuthentication
            && request.Authority.Authentication is null)
        {
            ParticipantMemoryAuthenticationReceipt? issuedAuthentication;
            try
            {
                issuedAuthentication = await TryAuthenticateAsync(
                    request.Authority.RequestingParticipantReference,
                    request.Proposal.Operation.ToString(),
                    $"Confirm {request.Proposal.Operation.ToString().ToLowerInvariant()} for participant memory.",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ConfirmedNoEffect(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    request.Proposal.Operation.ToString(),
                    request.RequestId,
                    "Participant-memory authentication was cancelled."));
            }
            if (issuedAuthentication is not null)
            {
                request = request with
                {
                    Authority = request.Authority with { Authentication = issuedAuthentication }
                };
                validation = ParticipantMemoryPolicy.ValidateMutation(
                    request,
                    DateTimeOffset.UtcNow,
                    _receiptAuthority);
            }
        }
        if (!validation.Valid)
        {
            return ConfirmedNoEffect(validation.Failure!);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
        Mem0EmbeddingSpaceConfiguration space;
        try
        {
            space = await _client.ResolveCurrentEmbeddingSpaceAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConfirmedNoEffect(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.TimedOut,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Participant-memory embedding verification reached the mutation deadline.",
                retryable: true));
        }
        catch (OperationCanceledException)
        {
            return ConfirmedNoEffect(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Cancelled,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "Participant-memory mutation was cancelled.",
                retryable: true));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ConfirmedNoEffect(FailureFromException(
                request.Proposal.Operation.ToString(),
                request.RequestId,
                ex));
        }
        if (!string.Equals(
                request.ExpectedEmbeddingSpaceId,
                space.Id,
                StringComparison.Ordinal))
        {
            return ConfirmedNoEffect(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                request.Proposal.Operation.ToString(),
                request.RequestId,
                "The mutation request targets a different embedding space."));
        }

        // Embedding-space verification can consume most of the mutation deadline. Revalidate
        // every process-local receipt at dispatch time so an expired permission, consent, or
        // authentication grant never crosses the state-changing worker boundary.
        var dispatchUtc = DateTimeOffset.UtcNow;
        validation = ParticipantMemoryPolicy.ValidateMutation(
            request,
            dispatchUtc,
            _receiptAuthority);
        if (!validation.Valid)
        {
            return ConfirmedNoEffect(validation.Failure!);
        }
        var authenticationBindingFailure = ValidateCurrentAuthenticationBinding(
            request.Authority,
            request.Proposal.Operation.ToString(),
            request.RequestId,
            dispatchUtc);
        if (authenticationBindingFailure is not null)
        {
            return ConfirmedNoEffect(authenticationBindingFailure);
        }

        var roster = validation.Roster!;
        var proposal = validation.Proposal!;
        var staleFailure = ValidateCurrentRoster(
            roster,
            proposal.Operation.ToString(),
            request.RequestId);
        if (staleFailure is not null)
        {
            return ConfirmedNoEffect(staleFailure);
        }
        var authorizedAccessKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            request.Authority,
            dispatchUtc,
            _receiptAuthority,
            proposal.Operation.ToString());
        var proposedAccessKeys = ParticipantMemoryPolicy.BuildAccessKeys(
            proposal.Visibility,
            proposal.Sensitivity,
            proposal.AudienceParticipantReferences);
        if (proposal.Operation is ParticipantMemoryMutationKind.Add
                or ParticipantMemoryMutationKind.Correct
                or ParticipantMemoryMutationKind.Dispute
            && !proposedAccessKeys.Intersect(
                    authorizedAccessKeys,
                    StringComparer.Ordinal).Any())
        {
            return ConfirmedNoEffect(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                proposal.Operation.ToString(),
                request.RequestId,
                "The requesting participant is outside the proposed memory audience."));
        }
        var requestingParticipantReference =
            request.Authority.RequestingParticipantReference?.Trim();
        var authentication = request.Authority.Authentication;
        var requestingParticipantAuthenticated = authentication is not null
            && _receiptAuthority.IsIssued(authentication)
            && authentication.IsCurrent(dispatchUtc)
            && authentication.UsesIndependentTrustedFactor
            && string.Equals(
                authentication.PrincipalParticipantReference,
                requestingParticipantReference,
                StringComparison.Ordinal)
            && authentication.GrantedOperations.Any(value => string.Equals(
                value,
                proposal.Operation.ToString(),
                StringComparison.OrdinalIgnoreCase));
        try
        {
            var response = await SendAsync(new
            {
                operation = "participant_mutate",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                mutationRequestId = request.RequestId,
                proposal,
                provenance = validation.Provenance,
                consentReceipts = validation.ConsentReceipts,
                requestingParticipantReference,
                requestingParticipantAuthenticated,
                accessKeys = proposedAccessKeys,
                authorizedAccessKeys
            }, timeout.Token).ConfigureAwait(false);
            var reconciliationAttempted = false;
            if (!response.Success)
            {
                reconciliationAttempted = true;
                var reconciled = await TryReconcileMutationAsync(
                    request,
                    validation,
                    roster,
                    proposal,
                    space,
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated,
                    settings).ConfigureAwait(false);
                if (reconciled is not null)
                {
                    return reconciled;
                }
            }
            if (!response.Success)
            {
                return ParticipantMemoryMutationResult.Failed(FailureFromResponse(
                    proposal.Operation.ToString(),
                    request.RequestId,
                    response),
                    mutationStatus: response.MutationStatus ?? "in_doubt");
            }
            var deletionStaged = proposal.Operation == ParticipantMemoryMutationKind.Delete
                && string.Equals(response.MutationStatus, "delete_staged", StringComparison.Ordinal)
                && response.DeletionFinalized == false;
            var deletionFinalized = proposal.Operation == ParticipantMemoryMutationKind.Delete
                && string.Equals(response.MutationStatus, "committed", StringComparison.Ordinal)
                && response.DeletionFinalized == true;
            var mutationCommitted = string.Equals(
                response.MutationStatus,
                "committed",
                StringComparison.Ordinal);
            if (!string.Equals(response.MutationRequestId, request.RequestId, StringComparison.Ordinal)
                || (!mutationCommitted && !deletionStaged)
                || deletionFinalized
                || !string.Equals(
                    response.MutationOperation,
                    proposal.Operation.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                || (proposal.Operation == ParticipantMemoryMutationKind.Delete
                    && mutationCommitted
                    && !deletionFinalized)
                || (proposal.Operation != ParticipantMemoryMutationKind.Delete
                    && response.DeletionFinalized == true)
                || (reconciliationAttempted && response.Reconciled != true))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "Participant memory returned an invalid durable mutation receipt."));
            }
            if (!string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.StaleResult,
                    proposal.Operation.ToString(),
                    request.RequestId,
                        "A stale participant-memory mutation receipt was rejected."));
            }

            staleFailure = ValidateCurrentRoster(
                roster,
                proposal.Operation.ToString(),
                request.RequestId);
            if (staleFailure is not null)
            {
                if (deletionFinalized)
                {
                    return new ParticipantMemoryMutationResult(true, [], null);
                }
                using var rollbackTimeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
                var rollback = await SendAsync(new
                {
                    operation = "participant_rollback_mutation",
                    embeddingSpaceId = space.Id,
                    tenantId = roster.TenantId,
                    rosterRevision = roster.Revision,
                    mutationRequestId = request.RequestId,
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated
                }, rollbackTimeout.Token).ConfigureAwait(false);
                var rollbackConfirmed = rollback.Success
                    && string.Equals(rollback.MutationRequestId, request.RequestId, StringComparison.Ordinal)
                    && string.Equals(rollback.MutationStatus, "rolled_back", StringComparison.Ordinal)
                    && string.Equals(
                        rollback.MutationOperation,
                        proposal.Operation.ToString(),
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rollback.RosterRevision, roster.Revision, StringComparison.Ordinal)
                    && string.Equals(rollback.EmbeddingSpaceId, space.Id, StringComparison.Ordinal);
                return rollbackConfirmed
                    ? ParticipantMemoryMutationResult.Failed(
                        staleFailure,
                        noEffectConfirmed: true,
                        mutationStatus: "rolled_back")
                    : ParticipantMemoryMutationResult.Failed(
                        ParticipantMemoryPolicy.Failure(
                            ParticipantMemoryFailureCode.Conflict,
                            proposal.Operation.ToString(),
                            request.RequestId,
                            "The roster changed and mutation rollback could not be confirmed; reconciliation is required."),
                        mutationStatus: "in_doubt");
            }

            var records = response.ParticipantMemories ?? [];
            if (deletionFinalized)
            {
                return records.Count == 0
                    ? new ParticipantMemoryMutationResult(true, [], null)
                    : ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        proposal.Operation.ToString(),
                        request.RequestId,
                        "A finalized deletion tombstone unexpectedly returned memory content."));
            }
            if (records.Any(record => !ParticipantRecordHasBoundedShape(record)))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "Participant memory returned a malformed or unbounded mutation record."));
            }
            if (records.Any(record =>
                    !string.Equals(record.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                    || !string.Equals(record.TenantId, roster.TenantId, StringComparison.Ordinal)))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "A mutation receipt referenced another embedding space."));
            }
            var receiptFailure = ValidateMutationReceiptRecords(
                proposal,
                records,
                authorizedAccessKeys,
                request.RequestId,
                validation.Provenance!,
                validation.ConsentReceipts!);
            if (receiptFailure is not null)
            {
                return ParticipantMemoryMutationResult.Failed(receiptFailure);
            }
            if (deletionStaged)
            {
                var finalizationUtc = DateTimeOffset.UtcNow;
                var finalAuthorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                    roster,
                    request.Authority,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    finalizationUtc,
                    _receiptAuthority);
                finalAuthorityFailure ??= ValidateCurrentAuthenticationBinding(
                    request.Authority,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    finalizationUtc);
                staleFailure = finalAuthorityFailure is null
                    ? ValidateCurrentRoster(
                        roster,
                        proposal.Operation.ToString(),
                        request.RequestId)
                    : finalAuthorityFailure;
                if (staleFailure is not null)
                {
                    using var rollbackTimeout = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
                    var rollback = await SendAsync(new
                    {
                        operation = "participant_rollback_mutation",
                        embeddingSpaceId = space.Id,
                        tenantId = roster.TenantId,
                        rosterRevision = roster.Revision,
                        mutationRequestId = request.RequestId,
                        authorizedAccessKeys,
                        requestingParticipantReference,
                        requestingParticipantAuthenticated
                    }, rollbackTimeout.Token).ConfigureAwait(false);
                    var rollbackConfirmed = rollback.Success
                        && string.Equals(rollback.MutationRequestId, request.RequestId, StringComparison.Ordinal)
                        && string.Equals(rollback.MutationStatus, "rolled_back", StringComparison.Ordinal)
                        && string.Equals(rollback.MutationOperation, "delete", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(rollback.RosterRevision, roster.Revision, StringComparison.Ordinal)
                        && string.Equals(rollback.EmbeddingSpaceId, space.Id, StringComparison.Ordinal);
                    return rollbackConfirmed
                        ? ParticipantMemoryMutationResult.Failed(
                            staleFailure,
                            noEffectConfirmed: true,
                            mutationStatus: "rolled_back")
                        : ParticipantMemoryMutationResult.Failed(
                            ParticipantMemoryPolicy.Failure(
                                ParticipantMemoryFailureCode.Conflict,
                                proposal.Operation.ToString(),
                                request.RequestId,
                                "The staged deletion could not be finalized or rolled back safely; reconciliation is required."),
                            mutationStatus: "in_doubt");
                }

                using var finalizeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                finalizeTimeout.CancelAfter(TimeSpan.FromMilliseconds(
                    settings.MutationTimeoutMilliseconds));
                var finalized = await SendAsync(new
                {
                    operation = "participant_reconcile_mutation",
                    embeddingSpaceId = space.Id,
                    tenantId = roster.TenantId,
                    rosterRevision = roster.Revision,
                    mutationRequestId = request.RequestId,
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated,
                    finalizeDelete = true
                }, finalizeTimeout.Token).ConfigureAwait(false);
                var finalReceiptIsExact = finalized.Success
                    && string.Equals(finalized.MutationRequestId, request.RequestId, StringComparison.Ordinal)
                    && string.Equals(finalized.MutationStatus, "committed", StringComparison.Ordinal)
                    && string.Equals(finalized.MutationOperation, "delete", StringComparison.OrdinalIgnoreCase)
                    && finalized.Reconciled == true
                    && finalized.DeletionFinalized == true
                    && string.Equals(finalized.RosterRevision, roster.Revision, StringComparison.Ordinal)
                    && string.Equals(finalized.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                    && (finalized.ParticipantMemories?.Count ?? 0) == 0;
                if (!finalReceiptIsExact)
                {
                    return ParticipantMemoryMutationResult.Failed(
                        finalized.Success
                            ? ParticipantMemoryPolicy.Failure(
                                ParticipantMemoryFailureCode.ProtocolFailure,
                                proposal.Operation.ToString(),
                                request.RequestId,
                                "Participant memory returned an invalid finalized deletion tombstone.")
                            : FailureFromResponse(
                                proposal.Operation.ToString(),
                                request.RequestId,
                                finalized),
                        mutationStatus: finalized.MutationStatus ?? "in_doubt");
                }
                return new(true, [], null);
            }
            return new(true, records, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var reconciled = await TryReconcileMutationAsync(
                request,
                validation,
                roster,
                proposal,
                space,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated,
                settings).ConfigureAwait(false);
            if (reconciled is not null)
            {
                return reconciled;
            }
            return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.TimedOut,
                proposal.Operation.ToString(),
                request.RequestId,
                "Participant-memory mutation reached its configured deadline; retry only with the same request ID for reconciliation.",
                retryable: true),
                mutationStatus: "in_doubt");
        }
        catch (OperationCanceledException)
        {
            var reconciled = await TryReconcileMutationAsync(
                request,
                validation,
                roster,
                proposal,
                space,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated,
                settings).ConfigureAwait(false);
            if (reconciled is not null)
            {
                return reconciled;
            }
            return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Cancelled,
                proposal.Operation.ToString(),
                request.RequestId,
                "Participant-memory mutation was cancelled.",
                retryable: true),
                mutationStatus: "in_doubt");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            var reconciled = await TryReconcileMutationAsync(
                request,
                validation,
                roster,
                proposal,
                space,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated,
                settings).ConfigureAwait(false);
            if (reconciled is not null)
            {
                return reconciled;
            }
            return ParticipantMemoryMutationResult.Failed(FailureFromException(
                proposal.Operation.ToString(),
                request.RequestId,
                ex),
                mutationStatus: "in_doubt");
        }
    }

    public async Task<ParticipantMemoryHealthResult> CheckParticipantHealthAsync(
        ParticipantRosterSnapshot roster,
        ParticipantMemoryAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(authority);
        var settings = _settings().Normalize();
        if (!settings.Enabled)
        {
            return new(false, false, false, string.Empty, settings.CollectionName, null)
            {
                EmbeddingAvailable = false
            };
        }

        ParticipantRosterSnapshot normalized;
        try
        {
            normalized = roster.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return new(true, false, false, string.Empty, settings.CollectionName,
                FailureFromException("health", "health", ex));
        }
        var permissionOperation = authority.Permission?.GrantedOperations?
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?
            .Trim();
        if (permissionOperation is null)
        {
            return new(
                true,
                false,
                false,
                string.Empty,
                settings.CollectionName,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.PermissionDenied,
                    "health",
                    "health",
                    "Participant-memory health requires an exact issued participant permission."))
            {
                EmbeddingAvailable = false
            };
        }
        var authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            normalized,
            authority,
            permissionOperation,
            "health",
            DateTimeOffset.UtcNow,
            _receiptAuthority);
        if (authorityFailure is not null)
        {
            return new(true, false, false, string.Empty, settings.CollectionName, authorityFailure)
            {
                EmbeddingAvailable = false
            };
        }
        var staleFailure = ValidateCurrentRoster(normalized, "health", "health");
        if (staleFailure is not null)
        {
            return new(true, false, false, string.Empty, settings.CollectionName, staleFailure);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.HealthTimeoutMilliseconds));
        Mem0EmbeddingSpaceConfiguration space;
        try
        {
            space = await _client.ResolveCurrentEmbeddingSpaceAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                settings.Enabled,
                false,
                false,
                string.Empty,
                settings.CollectionName,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.TimedOut,
                    "health",
                    "health",
                    "Participant-memory embedding verification reached the health deadline.",
                    retryable: true))
            {
                EmbeddingAvailable = false
            };
        }
        catch (OperationCanceledException)
        {
            return new(
                settings.Enabled,
                false,
                false,
                string.Empty,
                settings.CollectionName,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    "health",
                    "health",
                    "Participant-memory health check was cancelled.",
                    retryable: true))
            {
                EmbeddingAvailable = false
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new(
                settings.Enabled,
                false,
                false,
                string.Empty,
                settings.CollectionName,
                FailureFromException("health", "health", ex))
            {
                EmbeddingAvailable = false
            };
        }

        try
        {
            var dispatchUtc = DateTimeOffset.UtcNow;
            authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                normalized,
                authority,
                permissionOperation,
                "health",
                dispatchUtc,
                _receiptAuthority);
            authorityFailure ??= ValidateCurrentAuthenticationBinding(
                authority,
                permissionOperation,
                "health",
                dispatchUtc);
            if (authorityFailure is not null)
            {
                return new(true, false, false, space.Id, space.CollectionName, authorityFailure)
                {
                    EmbeddingAvailable = true
                };
            }
            staleFailure = ValidateCurrentRoster(normalized, "health", "health");
            if (staleFailure is not null)
            {
                return new(true, false, false, space.Id, space.CollectionName, staleFailure)
                {
                    EmbeddingAvailable = true
                };
            }
            var authorizedAccessKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
                authority,
                dispatchUtc,
                _receiptAuthority,
                permissionOperation);
            var response = await SendAsync(new
            {
                operation = "participant_health",
                embeddingSpaceId = space.Id,
                tenantId = normalized.TenantId,
                rosterRevision = normalized.Revision,
                authorizedAccessKeys
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                return new(
                    true,
                    false,
                    false,
                    space.Id,
                    space.CollectionName,
                    FailureFromResponse("health", "health", response))
                {
                    EmbeddingAvailable = response.EmbeddingAvailable ?? false,
                    DegradedPointCount = Math.Max(0, response.DegradedPointCount),
                    FailedPointIds = SanitizePointIds(response.FailedPointIds),
                    DeliberateRepairAvailable = response.DegradedPointCount > 0
                };
            }
            var liveFailure = ValidateCurrentRoster(normalized, "health", "health");
            if (liveFailure is not null
                || !string.Equals(response.RosterRevision, normalized.Revision, StringComparison.Ordinal)
                || !string.Equals(response.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                || response.EmbeddingAvailable is null
                || response.Mem0Available is null
                || response.QdrantAvailable is null)
            {
                return new(
                    true,
                    false,
                    false,
                    space.Id,
                    space.CollectionName,
                    liveFailure ?? ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        "health",
                        "health",
                        "Participant memory returned an incomplete or stale health receipt."))
                {
                    EmbeddingAvailable = false,
                    DegradedPointCount = Math.Max(0, response.DegradedPointCount),
                    FailedPointIds = SanitizePointIds(response.FailedPointIds),
                    DeliberateRepairAvailable = response.DegradedPointCount > 0
                };
            }
            return new(
                true,
                response.Mem0Available.Value,
                response.QdrantAvailable.Value,
                space.Id,
                space.CollectionName,
                null)
            {
                EmbeddingAvailable = response.EmbeddingAvailable.Value,
                DegradedPointCount = Math.Max(0, response.DegradedPointCount),
                FailedPointIds = SanitizePointIds(response.FailedPointIds),
                DeliberateRepairAvailable = response.DegradedPointCount > 0
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                true,
                false,
                false,
                space.Id,
                space.CollectionName,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.TimedOut,
                    "health",
                    "health",
                    "Participant-memory health check reached its configured deadline.",
                    retryable: true))
            {
                EmbeddingAvailable = true
            };
        }
        catch (OperationCanceledException)
        {
            return new(
                true,
                false,
                false,
                space.Id,
                space.CollectionName,
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Cancelled,
                    "health",
                    "health",
                    "Participant-memory health check was cancelled.",
                    retryable: true))
            {
                EmbeddingAvailable = true
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return new(
                true,
                false,
                false,
                space.Id,
                space.CollectionName,
                FailureFromException("health", "health", ex))
            {
                EmbeddingAvailable = true
            };
        }
    }

    public async Task<ParticipantMemoryRepairResult> RepairParticipantEmbeddingSpaceAsync(
        ParticipantMemoryRepairRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = _settings().Normalize();
        if (!settings.Enabled)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Disabled,
                "Repair",
                request.RequestId,
                "Participant memory is disabled."));
        }
        ParticipantRosterSnapshot roster;
        try
        {
            roster = request.Roster.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return RepairFailed(FailureFromException("Repair", request.RequestId, ex));
        }
        var authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Repair",
            request.RequestId,
            DateTimeOffset.UtcNow,
            _receiptAuthority);
        if (authorityFailure is not null)
        {
            return RepairFailed(authorityFailure);
        }
        var staleFailure = ValidateCurrentRoster(roster, "Repair", request.RequestId);
        if (staleFailure is not null)
        {
            return RepairFailed(staleFailure);
        }
        var repairPointIds = SanitizePointIds(request.PointIds);
        if (request.PointIds is null
            || repairPointIds.Count == 0
            || repairPointIds.Count != request.PointIds.Count
            || repairPointIds.Count > ParticipantMemoryLimits.MaximumRepairPointIds)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.InvalidProposal,
                "Repair",
                request.RequestId,
                $"Participant-memory repair requires 1-{ParticipantMemoryLimits.MaximumRepairPointIds} unique exact point IDs."));
        }
        if (request.Authority.Authentication is null)
        {
            var issuedAuthentication = await TryAuthenticateAsync(
                request.Authority.RequestingParticipantReference,
                "Repair",
                "Confirm deliberate repair of the selected participant-memory points.",
                cancellationToken).ConfigureAwait(false);
            if (issuedAuthentication is null)
            {
                return RepairFailed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.AuthenticationRequired,
                    "Repair",
                    request.RequestId,
                    "Participant-memory repair requires the selected user's Windows credential."));
            }
            request = request with
            {
                Authority = request.Authority with { Authentication = issuedAuthentication }
            };
            authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                roster,
                request.Authority,
                "Repair",
                request.RequestId,
                DateTimeOffset.UtcNow,
                _receiptAuthority);
            if (authorityFailure is not null)
            {
                return RepairFailed(authorityFailure);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.RepairTimeoutMilliseconds));
        Mem0EmbeddingSpaceConfiguration space;
        try
        {
            space = await _client.ResolveCurrentEmbeddingSpaceAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.TimedOut,
                "Repair",
                request.RequestId,
                "Participant-memory embedding verification reached the repair deadline.",
                retryable: true));
        }
        catch (OperationCanceledException)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Cancelled,
                "Repair",
                request.RequestId,
                "Participant-memory repair was cancelled.",
                retryable: true));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return RepairFailed(FailureFromException("Repair", request.RequestId, ex));
        }
        if (!string.Equals(request.ExpectedEmbeddingSpaceId, space.Id, StringComparison.Ordinal))
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                "Repair",
                request.RequestId,
                "The repair request targets another embedding space."));
        }

        var dispatchUtc = DateTimeOffset.UtcNow;
        authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Repair",
            request.RequestId,
            dispatchUtc,
            _receiptAuthority);
        authorityFailure ??= ValidateCurrentAuthenticationBinding(
            request.Authority,
            "Repair",
            request.RequestId,
            dispatchUtc);
        if (authorityFailure is not null)
        {
            return RepairFailed(authorityFailure);
        }
        staleFailure = ValidateCurrentRoster(roster, "Repair", request.RequestId);
        if (staleFailure is not null)
        {
            return RepairFailed(staleFailure);
        }

        try
        {
            var authorizedAccessKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
                request.Authority,
                dispatchUtc,
                _receiptAuthority,
                "Repair");
            var response = await SendAsync(new
            {
                operation = "participant_repair_hybrid",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                repairRequestId = request.RequestId,
                authorizedAccessKeys,
                repairPointIds
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                return RepairFailed(FailureFromResponse("Repair", request.RequestId, response));
            }
            var failedPointIds = SanitizePointIds(response.FailedPointIds);
            var returnedFailedPointCount = response.FailedPointIds?.Count ?? 0;
            var requestedPointIdSet = repairPointIds.ToHashSet(StringComparer.Ordinal);
            var receiptIsExact = string.Equals(
                    response.RepairRequestId,
                    request.RequestId,
                    StringComparison.Ordinal)
                && string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal)
                && string.Equals(response.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                && response.RequestedPointCount == repairPointIds.Count
                && response.UpdatedPointCount >= 0
                && response.UnchangedPointCount >= 0
                && response.DegradedPointCount == failedPointIds.Count
                && returnedFailedPointCount == failedPointIds.Count
                && failedPointIds.All(requestedPointIdSet.Contains)
                && response.UpdatedPointCount
                    + response.UnchangedPointCount
                    + failedPointIds.Count == repairPointIds.Count;
            if (!receiptIsExact)
            {
                return RepairFailed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    "Repair",
                    request.RequestId,
                    "Participant memory returned an invalid exact repair receipt."));
            }
            var liveFailure = ValidateCurrentRoster(roster, "Repair", request.RequestId);
            return liveFailure is null
                ? new(
                    true,
                    response.UpdatedPointCount,
                    failedPointIds.Count,
                    failedPointIds,
                    null)
                : RepairFailed(liveFailure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.TimedOut,
                "Repair",
                request.RequestId,
                "Participant-memory repair reached its configured deadline.",
                retryable: true));
        }
        catch (OperationCanceledException)
        {
            return RepairFailed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Cancelled,
                "Repair",
                request.RequestId,
                "Participant-memory repair was cancelled.",
                retryable: true));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return RepairFailed(FailureFromException("Repair", request.RequestId, ex));
        }
    }

    public async Task<ParticipantMemoryReconciliationResult> ReconcileParticipantMutationAsync(
        ParticipantMemoryReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mutationRequestId = request.MutationRequestId?.Trim() ?? string.Empty;
        ParticipantMemoryReconciliationResult Failed(
            ParticipantMemoryFailureReceipt failure,
            string? mutationOperation = null,
            string? mutationStatus = null) =>
            new(false, mutationRequestId, mutationOperation, mutationStatus, [], failure);

        var settings = _settings().Normalize();
        if (!settings.Enabled)
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Disabled,
                "Reconcile",
                request.RequestId,
                "Participant memory is disabled."));
        }
        if (mutationRequestId.Length is 0 or > 128
            || mutationRequestId.Any(character => char.IsControl(character)))
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.InvalidProposal,
                "Reconcile",
                request.RequestId,
                "A bounded exact mutation request ID is required."));
        }

        ParticipantRosterSnapshot roster;
        try
        {
            roster = request.Roster.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Failed(FailureFromException("Reconcile", request.RequestId, ex));
        }
        var authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Reconcile",
            request.RequestId,
            DateTimeOffset.UtcNow,
            _receiptAuthority);
        if (authorityFailure is not null)
        {
            return Failed(authorityFailure);
        }
        var staleFailure = ValidateCurrentRoster(roster, "Reconcile", request.RequestId);
        if (staleFailure is not null)
        {
            return Failed(staleFailure);
        }
        if (request.Authority.Authentication is null)
        {
            var issuedAuthentication = await TryAuthenticateAsync(
                request.Authority.RequestingParticipantReference,
                "Reconcile",
                "Confirm inspection and reconciliation of this exact participant-memory mutation receipt.",
                cancellationToken).ConfigureAwait(false);
            if (issuedAuthentication is null)
            {
                return Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.AuthenticationRequired,
                    "Reconcile",
                    request.RequestId,
                    "Participant-memory reconciliation requires the selected user's Windows credential."));
            }
            request = request with
            {
                Authority = request.Authority with { Authentication = issuedAuthentication }
            };
            authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                roster,
                request.Authority,
                "Reconcile",
                request.RequestId,
                DateTimeOffset.UtcNow,
                _receiptAuthority);
            if (authorityFailure is not null)
            {
                return Failed(authorityFailure);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
        Mem0EmbeddingSpaceConfiguration space;
        try
        {
            space = await _client.ResolveCurrentEmbeddingSpaceAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.TimedOut,
                "Reconcile",
                request.RequestId,
                "Participant-memory reconciliation reached its configured deadline.",
                retryable: true));
        }
        catch (OperationCanceledException)
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Cancelled,
                "Reconcile",
                request.RequestId,
                "Participant-memory reconciliation was cancelled.",
                retryable: true));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failed(FailureFromException("Reconcile", request.RequestId, ex));
        }
        if (!string.Equals(request.ExpectedEmbeddingSpaceId, space.Id, StringComparison.Ordinal))
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                "Reconcile",
                request.RequestId,
                "The reconciliation request targets another embedding space."));
        }

        var dispatchUtc = DateTimeOffset.UtcNow;
        authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
            roster,
            request.Authority,
            "Reconcile",
            request.RequestId,
            dispatchUtc,
            _receiptAuthority);
        authorityFailure ??= ValidateCurrentAuthenticationBinding(
            request.Authority,
            "Reconcile",
            request.RequestId,
            dispatchUtc);
        if (authorityFailure is not null)
        {
            return Failed(authorityFailure);
        }
        staleFailure = ValidateCurrentRoster(roster, "Reconcile", request.RequestId);
        if (staleFailure is not null)
        {
            return Failed(staleFailure);
        }

        var authorizedAccessKeys = ParticipantMemoryPolicy.BuildAuthorizedRecallKeys(
            request.Authority,
            dispatchUtc,
            _receiptAuthority,
            "Reconcile");
        var requestingParticipantReference =
            request.Authority.RequestingParticipantReference?.Trim();
        var authentication = request.Authority.Authentication;
        var requestingParticipantAuthenticated = authentication is not null
            && _receiptAuthority.IsIssued(authentication)
            && authentication.IsCurrent(dispatchUtc)
            && authentication.UsesIndependentTrustedFactor
            && string.Equals(
                authentication.PrincipalParticipantReference,
                requestingParticipantReference,
                StringComparison.Ordinal)
            && authentication.GrantedOperations.Any(value => string.Equals(
                value,
                "Reconcile",
                StringComparison.OrdinalIgnoreCase));
        try
        {
            var response = await SendAsync(new
            {
                operation = "participant_reconcile_mutation",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                mutationRequestId,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                return Failed(
                    FailureFromResponse("Reconcile", request.RequestId, response),
                    response.MutationOperation,
                    response.MutationStatus);
            }
            var operation = response.MutationOperation?.Trim().ToLowerInvariant();
            var status = response.MutationStatus?.Trim().ToLowerInvariant();
            var records = response.ParticipantMemories ?? [];
            var validOperation = operation is "add" or "correct" or "dispute"
                or "revoke" or "archive" or "delete";
            var deleteStaged = operation == "delete"
                && status == "delete_staged"
                && response.DeletionFinalized == false;
            var deleteFinalized = operation == "delete"
                && status == "committed"
                && response.DeletionFinalized == true;
            var validStatus = status is "committed" or "rolled_back" || deleteStaged;
            var validRecordCount = deleteStaged
                ? records.Count == 1
                : deleteFinalized || status == "rolled_back"
                    ? records.Count == 0
                    : records.Count == 1;
            if (!string.Equals(response.MutationRequestId, mutationRequestId, StringComparison.Ordinal)
                || response.Reconciled != true
                || !string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal)
                || !string.Equals(response.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                || !validOperation
                || !validStatus
                || !validRecordCount
                || (operation == "delete" && status == "committed" && !deleteFinalized)
                || (operation != "delete" && response.DeletionFinalized == true)
                || !ReconciliationRecordsAreExact(
                    operation,
                    status,
                    records,
                    roster.TenantId,
                    space.Id,
                    authorizedAccessKeys))
            {
                return Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    "Reconcile",
                    request.RequestId,
                    "Participant memory returned an invalid exact reconciliation receipt."));
            }
            staleFailure = ValidateCurrentRoster(roster, "Reconcile", request.RequestId);
            if (staleFailure is not null)
            {
                if (deleteFinalized)
                {
                    return new(true, mutationRequestId, "delete", "committed", [], null);
                }
                if (status == "rolled_back")
                {
                    return new(true, mutationRequestId, operation, status, records, null);
                }
                if (!deleteStaged)
                {
                    // Explicit reconciliation can inspect an already-committed
                    // historical receipt. A roster change after that read must
                    // not undo durable state that predates this inspection.
                    return Failed(staleFailure, operation, status);
                }
                var rollbackConfirmed = await TryRollbackExactMutationAsync(
                    roster,
                    space,
                    mutationRequestId,
                    operation!,
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated,
                    settings).ConfigureAwait(false);
                return rollbackConfirmed
                    ? new(true, mutationRequestId, operation, "rolled_back", [], null)
                    : Failed(
                        ParticipantMemoryPolicy.Failure(
                            ParticipantMemoryFailureCode.Conflict,
                            "Reconcile",
                            request.RequestId,
                            "The reconciled mutation became stale and its exact rollback could not be confirmed."),
                        operation,
                        "in_doubt");
            }
            if (deleteStaged)
            {
                var finalizationUtc = DateTimeOffset.UtcNow;
                authorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                    roster,
                    request.Authority,
                    "Reconcile",
                    request.RequestId,
                    finalizationUtc,
                    _receiptAuthority);
                authorityFailure ??= ValidateCurrentAuthenticationBinding(
                    request.Authority,
                    "Reconcile",
                    request.RequestId,
                    finalizationUtc);
                if (authorityFailure is not null)
                {
                    return Failed(authorityFailure, "delete", "delete_staged");
                }
                staleFailure = ValidateCurrentRoster(
                    roster,
                    "Reconcile",
                    request.RequestId);
                if (staleFailure is not null)
                {
                    return Failed(staleFailure, "delete", "delete_staged");
                }
                var finalized = await TryFinalizeStagedDeleteAsync(
                    roster,
                    space,
                    mutationRequestId,
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated,
                    settings).ConfigureAwait(false);
                return IsExactFinalizedDeleteReceipt(
                    finalized,
                    roster,
                    space,
                    mutationRequestId)
                    ? new(true, mutationRequestId, "delete", "committed", [], null)
                    : Failed(
                        finalized is null
                            ? ParticipantMemoryPolicy.Failure(
                                ParticipantMemoryFailureCode.TimedOut,
                                "Reconcile",
                                request.RequestId,
                                "Deletion finalization remains staged; retry reconciliation with the same mutation request ID.",
                                retryable: true)
                            : ParticipantMemoryPolicy.Failure(
                                ParticipantMemoryFailureCode.ProtocolFailure,
                                "Reconcile",
                                request.RequestId,
                                "Participant memory returned an invalid finalized deletion tombstone."),
                        "delete",
                        finalized?.MutationStatus ?? "delete_staged");
            }
            return new(true, mutationRequestId, operation, status, records, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.TimedOut,
                "Reconcile",
                request.RequestId,
                "Participant-memory reconciliation reached its configured deadline.",
                retryable: true));
        }
        catch (OperationCanceledException)
        {
            return Failed(ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.Cancelled,
                "Reconcile",
                request.RequestId,
                "Participant-memory reconciliation was cancelled.",
                retryable: true));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return Failed(FailureFromException("Reconcile", request.RequestId, ex));
        }
    }

    public async Task<ParticipantMemoryReconciliationResult> ReconcileDesktopParticipantMutationAsync(
        ActiveUser user,
        string mutationRequestId,
        CancellationToken cancellationToken)
    {
        var exactRequestId = mutationRequestId?.Trim() ?? string.Empty;
        if (exactRequestId.Length is 0 or > 128 || exactRequestId.Any(char.IsControl))
        {
            return new(
                false,
                exactRequestId,
                null,
                null,
                [],
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.InvalidProposal,
                    "Reconcile",
                    "desktop-reconcile",
                    "A bounded exact mutation request ID is required."));
        }
        if (!TryCreateDesktopContext(user, "Reconcile", out var context, out var failure))
        {
            return new(
                false,
                exactRequestId,
                null,
                null,
                [],
                ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.PermissionDenied,
                    "Reconcile",
                    "desktop-reconcile",
                failure));
        }
        if (context.IsAuthoritativeGeneratedTestProfile)
        {
            context = context with
            {
                Authority = context.Authority with
                {
                    Authentication = _receiptAuthority.IssueTestAuthentication(
                        context.SelectedUser.StableId,
                        ["Reconcile"],
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMinutes(2))
                }
            };
        }
        var health = await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken).ConfigureAwait(false);
        if (!ParticipantMemoryReady(health))
        {
            return new(
                false,
                exactRequestId,
                null,
                null,
                [],
                health.Failure ?? ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.Unavailable,
                    "Reconcile",
                    context.RequestId,
                    "Participant memory is unavailable."));
        }
        return await ReconcileParticipantMutationAsync(
            new ParticipantMemoryReconciliationRequest(
                context.RequestId,
                context.Roster,
                context.Authority,
                exactRequestId,
                health.EmbeddingSpaceId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryOperationResult> MutateDesktopRecordAsync(
        ActiveUser user,
        ParticipantMemoryRecord target,
        ParticipantMemoryMutationKind operation,
        string replacementText,
        CancellationToken cancellationToken)
    {
        if (!TryCreateDesktopContext(user, operation.ToString(), out var context, out var failure))
        {
            return MemoryOperationResult.Failed(failure, "selection_required");
        }
        var selectedParticipant = context.Roster.SelectedParticipantReference;
        var lifecycleActors = new[]
            {
                target.SpeakerParticipantReference,
                target.Provenance.ReportedByParticipantReference
            }
            .Concat(target.SubjectParticipantReferences)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedParticipant is null
            || !lifecycleActors.Contains(selectedParticipant, StringComparer.Ordinal))
        {
            return MemoryOperationResult.Failed(
                "The selected profile is not an exact lifecycle actor for this memory; no mutation was attempted.",
                "permission_denied",
                context.RequestId,
                "rolled_back");
        }
        if (context.IsAuthoritativeGeneratedTestProfile)
        {
            context = context with
            {
                Authority = context.Authority with
                {
                    Authentication = _receiptAuthority.IssueTestAuthentication(
                        context.SelectedUser.StableId,
                        [operation.ToString()],
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMinutes(2))
                }
            };
        }
        if (operation == ParticipantMemoryMutationKind.Correct)
        {
            var requiredConsents = lifecycleActors
                .Concat(target.WitnessParticipantReferences)
                .Concat(target.Visibility is ParticipantMemoryVisibility.Private
                    or ParticipantMemoryVisibility.Shared
                        ? target.AudienceParticipantReferences
                        : [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requiredConsents.Any(reference => !string.Equals(
                    reference,
                    selectedParticipant,
                    StringComparison.Ordinal)))
            {
                return MemoryOperationResult.Failed(
                    "Settings cannot collect every participant's approval for this multi-party correction. Use the conversation consent flow with each registered participant explicitly selected.",
                    "consent_required",
                    context.RequestId,
                    "rolled_back");
            }
        }
        var health = await CheckParticipantHealthAsync(
            context.Roster,
            context.Authority,
            cancellationToken)
            .ConfigureAwait(false);
        if (!ParticipantMemoryReady(health))
        {
            return MemoryOperationResult.Failed(
                health.Failure?.SafeMessage ?? "Participant memory is unavailable.",
                "unavailable");
        }

        var proposal = new ParticipantMemoryProposal(
            operation,
            target.MemoryId,
            operation == ParticipantMemoryMutationKind.Correct ? replacementText : string.Empty,
            operation == ParticipantMemoryMutationKind.Correct ? target.Category : string.Empty,
            target.SpeakerParticipantReference,
            target.SubjectParticipantReferences,
            target.WitnessParticipantReferences,
            target.SharedEventReference,
            target.ClaimKind,
            target.EvidenceKind,
            target.Visibility,
            target.AudienceParticipantReferences,
            target.Sensitivity,
            target.AttributionConfidence,
            target.Provenance.ReportedByParticipantReference);
        IReadOnlyList<ParticipantMemoryConsentReceipt> consents = [];
        if (operation == ParticipantMemoryMutationKind.Correct)
        {
            consents =
            [
                _receiptAuthority.IssueConsent(
                    context.Authority.Permission!,
                    operation.ToString(),
                    ParticipantMemoryProposalFingerprint.Create(
                        proposal,
                        context.Roster.TenantId),
                    $"desktop-consent-session:{Guid.NewGuid():N}",
                    target.Visibility,
                    target.AudienceParticipantReferences,
                    context.Roster.TurnId,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMinutes(2))
            ];
        }
        var result = await MutateParticipantsAsync(
            new ParticipantMemoryMutationRequest(
                context.RequestId,
                context.Roster,
                context.Authority,
                proposal,
                health.EmbeddingSpaceId,
                new ParticipantMemoryProvenance(
                    context.Roster.TurnId,
                    context.RequestId,
                    "desktop-memory-settings",
                    DateTimeOffset.UtcNow,
                    target.Provenance.ReportedByParticipantReference),
                consents),
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return MemoryOperationResult.Failed(
                result.Failure?.SafeMessage ?? "Participant-memory mutation failed safely.",
                result.Failure?.Code.ToString() ?? "failed",
                context.RequestId,
                result.MutationStatus
                    ?? (result.NoEffectConfirmed ? "rolled_back" : "in_doubt"));
        }

        CacheDesktopRecords(result.Records);
        if (operation == ParticipantMemoryMutationKind.Delete)
        {
            RemoveDesktopRecord(target.MemoryId);
        }
        return new(
            true,
            operation == ParticipantMemoryMutationKind.Delete
                ? "Participant memory removed from active recall with an authenticated content-free tombstone."
                : "Participant memory corrected with an authenticated durable receipt.",
            result.Records.Select(ToUserMemory).ToArray(),
            RequestId: context.RequestId,
            MutationStatus: "committed");
    }

    private bool TryCreateDesktopContext(
        ActiveUser user,
        string operation,
        out DesktopParticipantMemoryContext context,
        out string failure)
    {
        context = default!;
        failure = string.Empty;
        if (_activeUsers is null || _rosterAuthority is null)
        {
            failure = "The participant-aware desktop memory boundary is not configured.";
            return false;
        }
        ActiveUser normalized;
        try
        {
            normalized = user.Normalize();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            failure = $"The selected profile is invalid: {ex.Message}";
            return false;
        }
        var selection = _activeUsers.CaptureSelectionSnapshot();
        ActiveUser selectedUser;
        try
        {
            selectedUser = selection.SelectedUser?.Normalize()!;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            failure = $"The authoritative selected profile is invalid: {ex.Message}";
            return false;
        }
        if (!selection.IsResolved
            || !string.Equals(
                selectedUser.StableId,
                normalized.StableId,
                StringComparison.Ordinal))
        {
            failure = "The memory-settings profile must exactly match the current explicit selection.";
            return false;
        }

        var availableUsers = _activeUsers.AvailableUsers
            .Select(candidate => candidate.Normalize())
            .ToArray();
        var isAuthoritativeGeneratedTestProfile = selectedUser.IsTestProfile
            && string.Equals(
                selectedUser.ResolutionMethod,
                "identity-test-profile",
                StringComparison.Ordinal)
            && availableUsers.Length == 1
            && availableUsers[0] == selectedUser;

        var now = DateTimeOffset.UtcNow;
        var requestId = $"participant-desktop:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
        try
        {
            var roster = _rosterAuthority.CaptureAtAdmission(
                requestId,
                "desktop-memory-settings",
                selection,
                _activeUsers.CaptureSelectionRevision(),
                now);
            if (!string.Equals(
                    roster.SelectedParticipantReference,
                    selectedUser.StableId,
                    StringComparison.Ordinal))
            {
                failure = "The participant roster did not preserve the exact selected profile.";
                return false;
            }
            var permission = _receiptAuthority.IssuePermission(
                selectedUser.StableId,
                [operation],
                requestId,
                "desktop-memory-settings",
                now,
                TimeSpan.FromMinutes(2));
            context = new DesktopParticipantMemoryContext(
                requestId,
                roster,
                new ParticipantMemoryAuthorityContext(selectedUser.StableId, null, [])
                {
                    Permission = permission
                },
                selectedUser,
                isAuthoritativeGeneratedTestProfile);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or ArgumentOutOfRangeException
            or InvalidOperationException)
        {
            failure = $"Participant-memory admission failed safely: {ex.Message}";
            return false;
        }
    }

    private static bool ParticipantMemoryReady(ParticipantMemoryHealthResult health) =>
        health.Enabled
        && health.EmbeddingAvailable
        && health.Mem0Available
        && health.QdrantAvailable
        && health.Failure is null;

    private static UserMemory ToUserMemory(ParticipantMemoryRecord record) => new(
        record.MemoryId,
        record.Text,
        record.Category,
        record.CreatedUtc,
        record.CorrectedUtc ?? record.ConfirmedUtc ?? record.CreatedUtc,
        record.Score,
        record.ConsentReceipts.Count != 0,
        record.Provenance.SourceChannel,
        record.SemanticScore,
        record.KeywordScore);

    private void CacheDesktopRecords(IReadOnlyList<ParticipantMemoryRecord> records)
    {
        lock (_desktopRecordCacheSync)
        {
            foreach (var record in records)
            {
                if (!_desktopRecordCache.ContainsKey(record.MemoryId))
                {
                    while (_desktopRecordCache.Count >= 256)
                    {
                        _desktopRecordCache.Remove(_desktopRecordCacheOrder.Dequeue());
                    }
                    _desktopRecordCacheOrder.Enqueue(record.MemoryId);
                }
                _desktopRecordCache[record.MemoryId] = record;
            }
        }
    }

    private bool TryGetDesktopRecord(string memoryId, out ParticipantMemoryRecord? record)
    {
        lock (_desktopRecordCacheSync)
        {
            return _desktopRecordCache.TryGetValue(memoryId, out record);
        }
    }

    private void RemoveDesktopRecord(string memoryId)
    {
        lock (_desktopRecordCacheSync)
        {
            _desktopRecordCache.Remove(memoryId);
        }
    }

    private sealed record DesktopParticipantMemoryContext(
        string RequestId,
        ParticipantRosterSnapshot Roster,
        ParticipantMemoryAuthorityContext Authority,
        ActiveUser SelectedUser,
        bool IsAuthoritativeGeneratedTestProfile);

    private async Task<Mem0Response> SendAsync(object request, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<ParticipantMemoryAuthenticationReceipt?> TryAuthenticateAsync(
        string? principalParticipantReference,
        string operation,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_authenticationProvider is null
            || string.IsNullOrWhiteSpace(principalParticipantReference))
        {
            return null;
        }
        try
        {
            return await _authenticationProvider.AuthenticateAsync(
                principalParticipantReference.Trim(),
                [operation],
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private ParticipantMemoryFailureReceipt? ValidateCurrentAuthenticationBinding(
        ParticipantMemoryAuthorityContext authority,
        string operation,
        string requestId,
        DateTimeOffset now)
    {
        var authentication = authority.Authentication;
        if (authentication is null)
        {
            return null;
        }

        var current = authentication.Kind == ParticipantMemoryAuthenticationKind.TrustedTestFactor
            ? _activeUsers is null || IsCurrentGeneratedTestProfile(authentication)
            : _authenticationProvider?.IsCurrentBinding(authentication, now) == true;
        return current
            ? null
            : ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.AuthenticationRequired,
                operation,
                requestId,
                "The selected profile's independent authentication binding changed before dispatch; authenticate again.");
    }

    private bool IsCurrentGeneratedTestProfile(
        ParticipantMemoryAuthenticationReceipt authentication)
    {
        try
        {
            var selection = _activeUsers!.CaptureSelectionSnapshot();
            if (!selection.IsResolved)
            {
                return false;
            }
            var selected = selection.SelectedUser!.Normalize();
            var available = _activeUsers.AvailableUsers
                .Select(user => user.Normalize())
                .ToArray();
            return selected.IsTestProfile
                && string.Equals(
                    selected.ResolutionMethod,
                    "identity-test-profile",
                    StringComparison.Ordinal)
                && string.Equals(
                    selected.StableId,
                    authentication.PrincipalParticipantReference,
                    StringComparison.Ordinal)
                && available.Length == 1
                && available[0] == selected;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<bool> TryRollbackExactMutationAsync(
        ParticipantRosterSnapshot roster,
        Mem0EmbeddingSpaceConfiguration space,
        string mutationRequestId,
        string mutationOperation,
        IReadOnlyList<string> authorizedAccessKeys,
        string? requestingParticipantReference,
        bool requestingParticipantAuthenticated,
        UserMemorySettings settings)
    {
        using var rollbackTimeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
        try
        {
            var rollback = await SendAsync(new
            {
                operation = "participant_rollback_mutation",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                mutationRequestId,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated
            }, rollbackTimeout.Token).ConfigureAwait(false);
            return rollback.Success
                && string.Equals(rollback.MutationRequestId, mutationRequestId, StringComparison.Ordinal)
                && string.Equals(rollback.MutationStatus, "rolled_back", StringComparison.Ordinal)
                && string.Equals(
                    rollback.MutationOperation,
                    mutationOperation,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(rollback.RosterRevision, roster.Revision, StringComparison.Ordinal)
                && string.Equals(rollback.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                && (rollback.ParticipantMemories?.Count ?? 0) == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or InvalidOperationException
            or TimeoutException)
        {
            return false;
        }
    }

    private async Task<Mem0Response?> TryFinalizeStagedDeleteAsync(
        ParticipantRosterSnapshot roster,
        Mem0EmbeddingSpaceConfiguration space,
        string mutationRequestId,
        IReadOnlyList<string> authorizedAccessKeys,
        string? requestingParticipantReference,
        bool requestingParticipantAuthenticated,
        UserMemorySettings settings)
    {
        using var finalizeTimeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
        try
        {
            return await SendAsync(new
            {
                operation = "participant_reconcile_mutation",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                mutationRequestId,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated,
                finalizeDelete = true
            }, finalizeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or InvalidOperationException
            or TimeoutException)
        {
            return null;
        }
    }

    private static bool IsExactFinalizedDeleteReceipt(
        Mem0Response? response,
        ParticipantRosterSnapshot roster,
        Mem0EmbeddingSpaceConfiguration space,
        string mutationRequestId) =>
        response is not null
        && response.Success
        && string.Equals(response.MutationRequestId, mutationRequestId, StringComparison.Ordinal)
        && string.Equals(response.MutationStatus, "committed", StringComparison.Ordinal)
        && string.Equals(response.MutationOperation, "delete", StringComparison.OrdinalIgnoreCase)
        && response.Reconciled == true
        && response.DeletionFinalized == true
        && string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal)
        && string.Equals(response.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
        && (response.ParticipantMemories?.Count ?? 0) == 0;

    private async Task<ParticipantMemoryMutationResult?> TryReconcileMutationAsync(
        ParticipantMemoryMutationRequest request,
        ParticipantMemoryValidationResult validation,
        ParticipantRosterSnapshot roster,
        ParticipantMemoryProposal proposal,
        Mem0EmbeddingSpaceConfiguration space,
        IReadOnlyList<string> authorizedAccessKeys,
        string? requestingParticipantReference,
        bool requestingParticipantAuthenticated,
        UserMemorySettings settings)
    {
        using var reconciliationTimeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(settings.MutationTimeoutMilliseconds));
        try
        {
            var response = await SendAsync(new
            {
                operation = "participant_reconcile_mutation",
                embeddingSpaceId = space.Id,
                tenantId = roster.TenantId,
                rosterRevision = roster.Revision,
                mutationRequestId = request.RequestId,
                authorizedAccessKeys,
                requestingParticipantReference,
                requestingParticipantAuthenticated
            }, reconciliationTimeout.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                return null;
            }
            if (!string.Equals(response.MutationRequestId, request.RequestId, StringComparison.Ordinal)
                || !string.Equals(
                    response.MutationOperation,
                    proposal.Operation.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                || response.Reconciled != true
                || !string.Equals(response.RosterRevision, roster.Revision, StringComparison.Ordinal)
                || !string.Equals(response.EmbeddingSpaceId, space.Id, StringComparison.Ordinal))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "Participant memory returned an invalid reconciliation receipt."));
            }

            var records = response.ParticipantMemories ?? [];
            if (string.Equals(response.MutationStatus, "rolled_back", StringComparison.Ordinal))
            {
                if (records.Count != 0)
                {
                    return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        proposal.Operation.ToString(),
                        request.RequestId,
                        "A no-effect reconciliation receipt unexpectedly returned a record."));
                }
                return ParticipantMemoryMutationResult.Failed(
                    ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.Conflict,
                        proposal.Operation.ToString(),
                        request.RequestId,
                        "The durable participant-memory receipt confirms that the mutation has no active effect."),
                    noEffectConfirmed: true);
            }
            var deletionStaged = proposal.Operation == ParticipantMemoryMutationKind.Delete
                && string.Equals(response.MutationStatus, "delete_staged", StringComparison.Ordinal)
                && response.DeletionFinalized == false;
            var deletionFinalized = proposal.Operation == ParticipantMemoryMutationKind.Delete
                && string.Equals(response.MutationStatus, "committed", StringComparison.Ordinal)
                && response.DeletionFinalized == true;
            var ordinaryCommit = proposal.Operation != ParticipantMemoryMutationKind.Delete
                && string.Equals(response.MutationStatus, "committed", StringComparison.Ordinal);
            if (!deletionStaged && !deletionFinalized && !ordinaryCommit)
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "Participant memory returned an unsupported reconciliation status."));
            }
            if (records.Any(record => !ParticipantRecordHasBoundedShape(record)))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.ProtocolFailure,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "Participant memory returned a malformed or unbounded reconciliation record."));
            }
            if (records.Any(record =>
                    !string.Equals(record.EmbeddingSpaceId, space.Id, StringComparison.Ordinal)
                    || !string.Equals(record.TenantId, roster.TenantId, StringComparison.Ordinal)))
            {
                return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                    ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    "A reconciled mutation receipt referenced another embedding space."));
            }
            if (deletionFinalized)
            {
                if (records.Count != 0)
                {
                    return ParticipantMemoryMutationResult.Failed(ParticipantMemoryPolicy.Failure(
                        ParticipantMemoryFailureCode.ProtocolFailure,
                        proposal.Operation.ToString(),
                        request.RequestId,
                        "A finalized deletion tombstone unexpectedly returned memory content."));
                }
                var finalizedStaleFailure = ValidateCurrentRoster(
                    roster,
                    proposal.Operation.ToString(),
                    request.RequestId);
                return finalizedStaleFailure is null
                    ? new ParticipantMemoryMutationResult(true, [], null)
                    : new ParticipantMemoryMutationResult(true, [], null);
            }

            var receiptFailure = ValidateMutationReceiptRecords(
                proposal,
                records,
                authorizedAccessKeys,
                request.RequestId,
                validation.Provenance!,
                validation.ConsentReceipts!);
            if (receiptFailure is not null)
            {
                return ParticipantMemoryMutationResult.Failed(receiptFailure);
            }
            var staleFailure = ValidateCurrentRoster(
                roster,
                proposal.Operation.ToString(),
                request.RequestId);
            if (staleFailure is not null)
            {
                var rollbackConfirmed = await TryRollbackExactMutationAsync(
                    roster,
                    space,
                    request.RequestId,
                    proposal.Operation.ToString(),
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated,
                    settings).ConfigureAwait(false);
                return rollbackConfirmed
                    ? ParticipantMemoryMutationResult.Failed(
                        staleFailure,
                        noEffectConfirmed: true,
                        mutationStatus: "rolled_back")
                    : ParticipantMemoryMutationResult.Failed(
                        ParticipantMemoryPolicy.Failure(
                            ParticipantMemoryFailureCode.Conflict,
                            proposal.Operation.ToString(),
                            request.RequestId,
                            "The reconciled mutation became stale and its exact rollback could not be confirmed."),
                        mutationStatus: "in_doubt");
            }
            if (deletionStaged)
            {
                var finalizationUtc = DateTimeOffset.UtcNow;
                var finalAuthorityFailure = ParticipantMemoryPolicy.ValidateAuthorityContext(
                    roster,
                    request.Authority,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    finalizationUtc,
                    _receiptAuthority);
                finalAuthorityFailure ??= ValidateCurrentAuthenticationBinding(
                    request.Authority,
                    proposal.Operation.ToString(),
                    request.RequestId,
                    finalizationUtc);
                if (finalAuthorityFailure is not null)
                {
                    var rollbackConfirmed = await TryRollbackExactMutationAsync(
                        roster,
                        space,
                        request.RequestId,
                        proposal.Operation.ToString(),
                        authorizedAccessKeys,
                        requestingParticipantReference,
                        requestingParticipantAuthenticated,
                        settings).ConfigureAwait(false);
                    return rollbackConfirmed
                        ? ParticipantMemoryMutationResult.Failed(
                            finalAuthorityFailure,
                            noEffectConfirmed: true,
                            mutationStatus: "rolled_back")
                        : ParticipantMemoryMutationResult.Failed(
                            ParticipantMemoryPolicy.Failure(
                                ParticipantMemoryFailureCode.Conflict,
                                proposal.Operation.ToString(),
                                request.RequestId,
                                "Delete finalization authority expired and exact rollback could not be confirmed."),
                            mutationStatus: "in_doubt");
                }
                var finalized = await TryFinalizeStagedDeleteAsync(
                    roster,
                    space,
                    request.RequestId,
                    authorizedAccessKeys,
                    requestingParticipantReference,
                    requestingParticipantAuthenticated,
                    settings).ConfigureAwait(false);
                if (IsExactFinalizedDeleteReceipt(
                    finalized,
                    roster,
                    space,
                    request.RequestId))
                {
                    return new ParticipantMemoryMutationResult(true, [], null);
                }
                return finalized is null
                    ? null
                    : ParticipantMemoryMutationResult.Failed(
                        ParticipantMemoryPolicy.Failure(
                            ParticipantMemoryFailureCode.ProtocolFailure,
                            proposal.Operation.ToString(),
                            request.RequestId,
                            "Participant memory returned an invalid finalized deletion tombstone."),
                        mutationStatus: finalized.MutationStatus ?? "in_doubt");
            }
            return new ParticipantMemoryMutationResult(true, records, null);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or InvalidOperationException
            or TimeoutException)
        {
            return null;
        }
    }

    private static string SafeRosterRevision(ParticipantRosterSnapshot roster)
    {
        try { return roster.Normalize().Revision; }
        catch { return "invalid-roster"; }
    }

    private static ParticipantMemoryFailureReceipt FailureFromException(
        string operation,
        string requestId,
        Exception exception) =>
        ParticipantMemoryPolicy.Failure(
            ParticipantMemoryFailureCode.Unavailable,
            operation,
            requestId,
            $"Participant memory failed safely at the {exception.GetType().Name} boundary.",
            retryable: true);

    private static ParticipantMemoryFailureReceipt FailureFromResponse(
        string operation,
        string requestId,
        Mem0Response response)
    {
        var code = response.ErrorCode switch
        {
            "permission_denied" => ParticipantMemoryFailureCode.PermissionDenied,
            "not_found" => ParticipantMemoryFailureCode.NotFound,
            "embedding_space_mismatch" => ParticipantMemoryFailureCode.EmbeddingSpaceMismatch,
            "stale_roster" => ParticipantMemoryFailureCode.StaleRoster,
            "conflict" => ParticipantMemoryFailureCode.Conflict,
            "mutation_in_doubt" => ParticipantMemoryFailureCode.Conflict,
            "legacy_operation_rejected" => ParticipantMemoryFailureCode.Conflict,
            "invalid_request" => ParticipantMemoryFailureCode.InvalidProposal,
            _ => ParticipantMemoryFailureCode.Unavailable
        };
        return ParticipantMemoryPolicy.Failure(
            code,
            operation,
            requestId,
            code switch
            {
                ParticipantMemoryFailureCode.PermissionDenied =>
                    "Participant memory denied the requested authority boundary.",
                ParticipantMemoryFailureCode.NotFound =>
                    "The exact participant memory is unavailable.",
                ParticipantMemoryFailureCode.EmbeddingSpaceMismatch =>
                    "Participant memory rejected a different embedding space.",
                ParticipantMemoryFailureCode.StaleRoster =>
                    "Participant memory rejected a stale roster.",
                ParticipantMemoryFailureCode.Conflict =>
                    "Participant memory requires explicit reconciliation.",
                _ => "Participant memory failed safely at the worker boundary."
            },
            retryable: code == ParticipantMemoryFailureCode.Unavailable);
    }

    private ParticipantMemoryFailureReceipt? ValidateCurrentRoster(
        ParticipantRosterSnapshot roster,
        string operation,
        string requestId)
    {
        if (_rosterAuthority is null)
        {
            return ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.StaleRoster,
                operation,
                requestId,
                "No authoritative live participant-generation source is configured.");
        }
        var freshness = _rosterAuthority.CheckCurrent(roster);
        return freshness.IsCurrent
            ? null
            : ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.StaleRoster,
                operation,
                requestId,
                "The admitted participant roster no longer matches live authoritative generations.",
                retryable: true);
    }

    private static ParticipantMemoryRepairResult RepairFailed(
        ParticipantMemoryFailureReceipt failure) =>
        new(false, 0, 0, [], failure);

    private static bool ParticipantRecordHasBoundedShape(ParticipantMemoryRecord? record)
    {
        static bool Required(string? value, int maximum = 128) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum
            && !value.Any(char.IsControl);
        static bool Optional(string? value, int maximum = 128) =>
            value is null || Required(value, maximum);
        static bool References(IReadOnlyList<string>? values, int maximum) =>
            values is not null
            && values.Count <= maximum
            && values.All(value => Required(value))
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

        if (record is null
            || !Required(record.MemoryId)
            || !Required(record.TenantId)
            || !Required(record.EmbeddingSpaceId, 256)
            || !Required(record.Text, ParticipantMemoryLimits.MaximumMemoryTextLength)
            || !Required(record.Category, ParticipantMemoryLimits.MaximumCategoryLength)
            || !Optional(record.SpeakerParticipantReference)
            || !Optional(record.SharedEventReference)
            || !Optional(record.CorrectsMemoryId)
            || !Optional(record.SupersedesMemoryId)
            || !Optional(record.DisputesMemoryId)
            || !References(
                record.SubjectParticipantReferences,
                ParticipantMemoryLimits.MaximumReferencesPerRole)
            || !References(
                record.WitnessParticipantReferences,
                ParticipantMemoryLimits.MaximumReferencesPerRole)
            || !References(
                record.AudienceParticipantReferences,
                ParticipantMemoryLimits.MaximumAudienceKeys)
            || record.Provenance is null
            || !Required(record.Provenance.SourceTurnId)
            || !Required(record.Provenance.SourceMessageId)
            || !Required(record.Provenance.SourceChannel)
            || !Optional(record.Provenance.ReportedByParticipantReference)
            || record.ConsentReceipts is null
            || record.ConsentReceipts.Count > ParticipantMemoryLimits.MaximumParticipantsPerTurn
            || record.ConsentReceipts.Any(receipt => receipt is null
                || !Required(receipt.ReceiptId)
                || !Required(receipt.GrantedByParticipantReference)
                || !Required(receipt.Operation)
                || !Required(receipt.ProposalFingerprint, 256)
                || !Required(receipt.ConsentSessionId)
                || !Required(receipt.SourceTurnId)
                || !Enum.IsDefined(receipt.Visibility)
                || !References(
                    receipt.AudienceParticipantReferences,
                    ParticipantMemoryLimits.MaximumAudienceKeys)
                || receipt.ExpiresUtc is not null
                    && receipt.ExpiresUtc <= receipt.GrantedUtc)
            || !Enum.IsDefined(record.ClaimKind)
            || !Enum.IsDefined(record.EvidenceKind)
            || !Enum.IsDefined(record.Visibility)
            || !Enum.IsDefined(record.Sensitivity)
            || !Enum.IsDefined(record.State)
            || !double.IsFinite(record.AttributionConfidence)
            || record.AttributionConfidence is < 0 or > 1
            || record.CreatedUtc == default
            || record.State == ParticipantMemoryState.Confirmed && record.ConfirmedUtc is null
            || record.Score is not null && !double.IsFinite(record.Score.Value)
            || record.SemanticScore is not null && !double.IsFinite(record.SemanticScore.Value)
            || record.KeywordScore is not null && !double.IsFinite(record.KeywordScore.Value))
        {
            return false;
        }
        try
        {
            return JsonSerializer.Serialize(record.Provenance, WorkerJsonOptions).Length <= 4_096
                && JsonSerializer.Serialize(record.ConsentReceipts, WorkerJsonOptions).Length <= 16_384;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ParticipantMemoryFailureReceipt? ValidateMutationReceiptRecords(
        ParticipantMemoryProposal proposal,
        IReadOnlyList<ParticipantMemoryRecord> records,
        IReadOnlyList<string> authorizedAccessKeys,
        string requestId,
        ParticipantMemoryProvenance expectedProvenance,
        IReadOnlyList<ParticipantMemoryConsentReceipt> expectedConsentReceipts)
    {
        if (records.Count != 1)
        {
            return ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.ProtocolFailure,
                proposal.Operation.ToString(),
                requestId,
                "Participant memory did not return exactly one durable mutation record.");
        }
        var record = records[0];
        if (!ParticipantRecordHasBoundedShape(record))
        {
            return ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.ProtocolFailure,
                proposal.Operation.ToString(),
                requestId,
                "Participant memory returned a malformed or unbounded mutation record.");
        }
        if (!ParticipantMemoryPolicy.BuildRecordAccessKeys(record)
            .Intersect(authorizedAccessKeys, StringComparer.Ordinal)
            .Any())
        {
            return ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.PermissionDenied,
                proposal.Operation.ToString(),
                requestId,
                "The mutation receipt is outside the exact authorized audience.");
        }

        var targetId = proposal.TargetMemoryId;
        var proposalMetadataIsExact =
            string.Equals(record.Text, proposal.Text, StringComparison.Ordinal)
            && string.Equals(record.Category, proposal.Category, StringComparison.Ordinal)
            && string.Equals(
                record.SpeakerParticipantReference,
                proposal.SpeakerParticipantReference,
                StringComparison.Ordinal)
            && record.SubjectParticipantReferences.SequenceEqual(
                proposal.SubjectParticipantReferences,
                StringComparer.Ordinal)
            && record.WitnessParticipantReferences.SequenceEqual(
                proposal.WitnessParticipantReferences,
                StringComparer.Ordinal)
            && string.Equals(
                record.SharedEventReference,
                proposal.SharedEventReference,
                StringComparison.Ordinal)
            && record.ClaimKind == proposal.ClaimKind
            && record.EvidenceKind == proposal.EvidenceKind
            && record.Visibility == proposal.Visibility
            && record.AudienceParticipantReferences.SequenceEqual(
                proposal.AudienceParticipantReferences,
                StringComparer.Ordinal)
            && record.Sensitivity == proposal.Sensitivity
            && record.AttributionConfidence.Equals(proposal.AttributionConfidence)
            && string.Equals(
                record.Provenance.ReportedByParticipantReference,
                proposal.ReportedByParticipantReference,
                StringComparison.Ordinal)
            && (proposal.Operation is not (ParticipantMemoryMutationKind.Add
                    or ParticipantMemoryMutationKind.Correct
                    or ParticipantMemoryMutationKind.Dispute)
                || (record.Provenance == expectedProvenance
                    && ConsentReceiptsAreExact(
                        record.ConsentReceipts,
                        expectedConsentReceipts)));
        var lineageIsValid = proposal.Operation switch
        {
            ParticipantMemoryMutationKind.Add =>
                proposalMetadataIsExact
                && record.State == ParticipantMemoryState.Confirmed
                && record.CorrectsMemoryId is null
                && record.SupersedesMemoryId is null
                && record.DisputesMemoryId is null,
            ParticipantMemoryMutationKind.Correct =>
                proposalMetadataIsExact
                && record.State == ParticipantMemoryState.Confirmed
                && !string.Equals(record.MemoryId, targetId, StringComparison.Ordinal)
                && string.Equals(record.CorrectsMemoryId, targetId, StringComparison.Ordinal)
                && string.Equals(record.SupersedesMemoryId, targetId, StringComparison.Ordinal)
                && record.DisputesMemoryId is null,
            ParticipantMemoryMutationKind.Dispute =>
                proposalMetadataIsExact
                && record.State == ParticipantMemoryState.Confirmed
                && !string.Equals(record.MemoryId, targetId, StringComparison.Ordinal)
                && string.Equals(record.DisputesMemoryId, targetId, StringComparison.Ordinal)
                && record.CorrectsMemoryId is null
                && record.SupersedesMemoryId is null,
            ParticipantMemoryMutationKind.Revoke =>
                record.State == ParticipantMemoryState.Revoked
                && string.Equals(record.MemoryId, targetId, StringComparison.Ordinal),
            ParticipantMemoryMutationKind.Archive =>
                record.State == ParticipantMemoryState.Archived
                && string.Equals(record.MemoryId, targetId, StringComparison.Ordinal),
            ParticipantMemoryMutationKind.Delete =>
                string.Equals(record.MemoryId, targetId, StringComparison.Ordinal),
            _ => false
        };
        return lineageIsValid
            ? null
            : ParticipantMemoryPolicy.Failure(
                ParticipantMemoryFailureCode.ProtocolFailure,
                proposal.Operation.ToString(),
                requestId,
                "Participant memory returned an invalid mutation state or lineage receipt.");
    }

    private static bool ReconciliationRecordsAreExact(
        string? operation,
        string? status,
        IReadOnlyList<ParticipantMemoryRecord> records,
        string tenantId,
        string embeddingSpaceId,
        IReadOnlyList<string> authorizedAccessKeys)
    {
        if (status == "rolled_back"
            || (operation == "delete" && status == "committed"))
        {
            return records.Count == 0;
        }
        if (records.Count != 1)
        {
            return false;
        }

        var record = records[0];
        if (!ParticipantRecordHasBoundedShape(record)
            || !string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)
            || !string.Equals(record.EmbeddingSpaceId, embeddingSpaceId, StringComparison.Ordinal)
            || !ParticipantMemoryPolicy.BuildRecordAccessKeys(record)
                .Intersect(authorizedAccessKeys, StringComparer.Ordinal)
                .Any())
        {
            return false;
        }

        return operation switch
        {
            "add" => record.State == ParticipantMemoryState.Confirmed
                && record.CorrectsMemoryId is null
                && record.SupersedesMemoryId is null
                && record.DisputesMemoryId is null,
            "correct" => record.State == ParticipantMemoryState.Confirmed
                && !string.IsNullOrWhiteSpace(record.CorrectsMemoryId)
                && !string.Equals(
                    record.MemoryId,
                    record.CorrectsMemoryId,
                    StringComparison.Ordinal)
                && string.Equals(
                    record.CorrectsMemoryId,
                    record.SupersedesMemoryId,
                    StringComparison.Ordinal)
                && record.DisputesMemoryId is null,
            "dispute" => record.State == ParticipantMemoryState.Confirmed
                && !string.IsNullOrWhiteSpace(record.DisputesMemoryId)
                && !string.Equals(
                    record.MemoryId,
                    record.DisputesMemoryId,
                    StringComparison.Ordinal)
                && record.CorrectsMemoryId is null
                && record.SupersedesMemoryId is null,
            "revoke" => record.State == ParticipantMemoryState.Revoked,
            "archive" => record.State == ParticipantMemoryState.Archived,
            "delete" when status == "delete_staged" =>
                record.State == ParticipantMemoryState.Confirmed,
            _ => false
        };
    }

    private static bool ConsentReceiptsAreExact(
        IReadOnlyList<ParticipantMemoryConsentReceipt> actual,
        IReadOnlyList<ParticipantMemoryConsentReceipt> expected) =>
        actual is not null
        && expected is not null
        && actual.Count == expected.Count
        && actual.All(receipt => receipt is not null
            && receipt.AudienceParticipantReferences is not null)
        && actual.Zip(expected).All(pair =>
            string.Equals(pair.First.ReceiptId, pair.Second.ReceiptId, StringComparison.Ordinal)
            && string.Equals(
                pair.First.GrantedByParticipantReference,
                pair.Second.GrantedByParticipantReference,
                StringComparison.Ordinal)
            && string.Equals(pair.First.Operation, pair.Second.Operation, StringComparison.Ordinal)
            && string.Equals(
                pair.First.ProposalFingerprint,
                pair.Second.ProposalFingerprint,
                StringComparison.Ordinal)
            && string.Equals(
                pair.First.ConsentSessionId,
                pair.Second.ConsentSessionId,
                StringComparison.Ordinal)
            && pair.First.Visibility == pair.Second.Visibility
            && pair.First.AudienceParticipantReferences.SequenceEqual(
                pair.Second.AudienceParticipantReferences,
                StringComparer.Ordinal)
            && pair.First.GrantedUtc == pair.Second.GrantedUtc
            && pair.First.ExpiresUtc == pair.Second.ExpiresUtc
            && string.Equals(
                pair.First.SourceTurnId,
                pair.Second.SourceTurnId,
                StringComparison.Ordinal));

    private static IReadOnlyList<string> SanitizePointIds(IReadOnlyList<string>? pointIds) =>
        (pointIds ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character => char.IsLetterOrDigit(character)
                || character is '-' or '_' or ':' or '.'))
        .Distinct(StringComparer.Ordinal)
        .Take(ParticipantMemoryLimits.MaximumRepairPointIds)
        .ToArray();

    internal static IReadOnlyList<UserMemory> FilterRecallMatches(
        IReadOnlyList<UserMemory> memories,
        UserMemorySettings settings,
        int maximumResults)
    {
        var normalized = settings.Normalize();
        var scored = memories
            .Where(memory => memory.Score.HasValue)
            .OrderByDescending(memory => memory.Score)
            .ToList();
        if (scored.Count == 0)
        {
            // A recall result without relevance evidence must never be treated
            // as a match. Listing memories remains available for inventory.
            return [];
        }

        var topScore = scored[0].Score!.Value;
        if (topScore < normalized.RecallMinimumScore)
        {
            return [];
        }

        var keywordSupported = scored[0].KeywordScore.GetValueOrDefault()
            >= normalized.RecallMinimumKeywordScore;
        if (!keywordSupported)
        {
            var semanticScore = scored[0].SemanticScore ?? topScore;
            if (semanticScore < normalized.RecallSemanticOnlyMinimumScore)
            {
                return [];
            }

            if (semanticScore < normalized.RecallSemanticOnlyStrongScore && scored.Count > 1)
            {
                var nextScore = scored[1].SemanticScore ?? scored[1].Score!.Value;
                if (semanticScore - nextScore < normalized.RecallSemanticOnlyMinimumLead)
                {
                    return [];
                }
            }
        }

        var threshold = Math.Max(
            normalized.RecallMinimumScore,
            topScore - normalized.RecallScoreWindow);
        return scored
            .Where(memory => memory.Score!.Value >= threshold)
            .Take(Math.Clamp(maximumResults, 1, 8))
            .ToList();
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
