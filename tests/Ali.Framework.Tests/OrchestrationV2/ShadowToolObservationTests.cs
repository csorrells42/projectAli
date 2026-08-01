using System.Diagnostics;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class ShadowToolObservationTests
{
    private static readonly EvidencePermissionMetadata NotRequired = new("not-required", "none");

    [Fact]
    public async Task LiveCapture_DoesNotEnumerateSerializeOrRetainLivePayloadObjects()
    {
        var sink = new CapturingSink();
        await using var service = new ShadowToolObservationService(sink);
        var identity = new TurnIdentity("user", "conversation", "message");
        var arguments = new HostileArguments();
        var result = new HostileCyclicResult();
        var exception = new InvalidOperationException("private detail");
        var started = DateTimeOffset.UtcNow;

        Assert.True(service.TryObserveReturned(
            identity,
            "call-returned",
            "tool",
            arguments,
            result,
            started,
            started.AddMilliseconds(1),
            NotRequired));
        Assert.True(service.TryObserveThrew(
            identity,
            "call-threw",
            "tool",
            arguments,
            exception,
            started,
            started.AddMilliseconds(2),
            NotRequired));

        await sink.WaitForCountAsync(2);
        var returned = sink.Items.Single(item => item.CallId == "call-returned");
        var threw = sink.Items.Single(item => item.CallId == "call-threw");
        Assert.Null(returned.ReportedSuccess);
        Assert.Null(returned.ExceptionType);
        Assert.Equal(typeof(InvalidOperationException).FullName, threw.ExceptionType);
        Assert.Equal(0, arguments.EnumerationAttempts);
        Assert.Equal(0, result.GetterReads);
        Assert.DoesNotContain(
            typeof(ShadowToolObservation).GetProperties(),
            property => property.PropertyType == typeof(object)
                        || typeof(Exception).IsAssignableFrom(property.PropertyType));

        var draft = Draft(returned);
        Assert.Equal("omitted-in-shadow", draft.Arguments.GetProperty("capture").GetString());
        Assert.Equal("arguments", draft.Arguments.GetProperty("field").GetString());
        Assert.Equal("omitted-in-shadow", draft.Result.GetProperty("capture").GetString());
        Assert.Equal("result", draft.Result.GetProperty("field").GetString());
        Assert.Equal(0, arguments.EnumerationAttempts);
        Assert.Equal(0, result.GetterReads);
    }

    [Fact]
    public async Task TryObserve_IsNoThrow_WhenIdentityOrObservationDataIsInvalid()
    {
        await using var service = new ShadowToolObservationService(new CapturingSink());
        var now = DateTimeOffset.UtcNow;

        var missingIdentity = service.TryObserveReturned(
            null,
            "call",
            "tool",
            null,
            null,
            now,
            now,
            NotRequired);
        var invalidTime = service.TryObserveReturned(
            new TurnIdentity("user", "conversation", "message"),
            "call",
            "tool",
            null,
            null,
            now,
            now.AddSeconds(-1),
            NotRequired);
        var invalidPermission = service.TryObserveReturned(
            new TurnIdentity("user", "conversation", "message"),
            "call",
            "tool",
            null,
            null,
            now,
            now,
            null!);

        Assert.False(missingIdentity);
        Assert.False(invalidTime);
        Assert.False(invalidPermission);
        Assert.Equal(1, service.Health.MissingIdentityDrops);
        var oversizedCallId = service.TryObserveReturned(
            new TurnIdentity("user", "conversation", "message"),
            new string('c', ShadowToolObservation.MaximumCallIdCharacters + 1),
            "tool",
            null,
            null,
            now,
            now,
            NotRequired);
        var invalidCancellation = service.TryObserveCancelled(
            new TurnIdentity("user", "conversation", "message"),
            "call-cancelled",
            "tool",
            null,
            null!,
            now,
            now,
            NotRequired);

        Assert.False(oversizedCallId);
        Assert.False(invalidCancellation);
        Assert.Equal(4, service.Health.InvalidObservationDrops);
    }

    [Fact]
    public async Task UnresolvedIdentity_CreatesNoProtectedEvidenceFiles()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var ledgerRoot = Path.Combine(directory.Path, "evidence");
        await using var service = new ShadowToolObservationService(
            new EvidenceLedger(ledgerRoot, "profile"));
        var now = DateTimeOffset.UtcNow;

        Assert.False(service.TryObserveReturned(
            null,
            "call",
            "tool",
            new { privateValue = "must-not-persist" },
            new { success = true },
            now,
            now,
            NotRequired));
        await service.DisposeAsync();

        Assert.False(Directory.Exists(ledgerRoot));
        Assert.Equal(1, service.Health.MissingIdentityDrops);
    }

    [Fact]
    public async Task FullChannel_DropsWithoutWaitingOrReplacingAcceptedItems()
    {
        var sink = new BlockingSink();
        await using var service = new ShadowToolObservationService(sink, capacity: 1);
        var identity = new TurnIdentity("user", "conversation", "message");
        var now = DateTimeOffset.UtcNow;

        Assert.True(ObserveReturned(service, identity, "call-1", now));
        await sink.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(ObserveReturned(service, identity, "call-2", now));

        var stopwatch = Stopwatch.StartNew();
        Assert.False(ObserveReturned(service, identity, "call-3", now));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(2, service.Health.Enqueued);
        Assert.Equal(1, service.Health.QueueFullDrops);
        sink.Release.TrySetResult();
    }

    [Fact]
    public async Task Reader_RetriesDuplicateAfterTransientFailure_ThenDeduplicatesAfterSuccess()
    {
        var sink = new FailFirstSink();
        await using var service = new ShadowToolObservationService(sink);
        var identity = new TurnIdentity("user", "conversation", "message");
        var now = DateTimeOffset.UtcNow;

        Assert.True(ObserveReturned(service, identity, "same-call", now));
        Assert.True(ObserveReturned(service, identity, "same-call", now));
        Assert.True(ObserveReturned(service, identity, "same-call", now));
        Assert.True(ObserveReturned(service, identity, "next-call", now));

        await sink.SecondAttempt.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.DisposeAsync();

        Assert.Equal(3, sink.Attempts);
        Assert.Equal(1, service.Health.DuplicateTerminals);
        Assert.Equal(1, service.Health.PersistenceFailures);
        Assert.Equal(2, service.Health.Persisted);
    }

    [Fact]
    public void EvidenceMapping_ReturnedIsUnreportedUnlessTypedSuccessIsSupplied()
    {
        var identity = new TurnIdentity("user", "conversation", "message");
        var now = DateTimeOffset.UtcNow;

        var exactFalse = Draft(ShadowToolObservation.Returned(
            identity,
            "call-1",
            "tool",
            now,
            now,
            NotRequired,
            reportedSuccess: false));
        var exactTrue = Draft(ShadowToolObservation.Returned(
            identity,
            "call-2",
            "tool",
            now,
            now,
            NotRequired,
            reportedSuccess: true));
        var unreported = Draft(ShadowToolObservation.Returned(
            identity,
            "call-3",
            "tool",
            now,
            now,
            NotRequired));

        Assert.Equal(DomainOutcome.Failed, exactFalse.Outcome.DomainOutcome);
        Assert.Equal("returned-failed", exactFalse.StableOutcomeCode);
        Assert.Equal(DomainOutcome.Succeeded, exactTrue.Outcome.DomainOutcome);
        Assert.Equal(DomainOutcome.Unreported, unreported.Outcome.DomainOutcome);
        Assert.Equal("returned-unreported", unreported.StableOutcomeCode);
    }

    [Fact]
    public void EvidenceMapping_KeepsDeniedThrewAndCancelledDistinct_WithoutExceptionMessage()
    {
        const string exceptionCanary = "exception-message-must-not-be-persisted";
        var identity = new TurnIdentity("user", "conversation", "message");
        var now = DateTimeOffset.UtcNow;
        var denied = Draft(ShadowToolObservation.Denied(
            identity,
            "call-denied",
            "tool",
            "approval-denied",
            now,
            now,
            new EvidencePermissionMetadata("denied", "once")));
        var threw = Draft(ShadowToolObservation.Threw(
            identity,
            "call-threw",
            "tool",
            typeof(InvalidOperationException).FullName!,
            now,
            now,
            NotRequired));
        var cancelled = Draft(ShadowToolObservation.Cancelled(
            identity,
            "call-cancelled",
            "tool",
            now,
            now,
            NotRequired));

        Assert.Equal(InvocationStatus.Denied, denied.Outcome.InvocationStatus);
        Assert.Equal(InvocationStatus.Threw, threw.Outcome.InvocationStatus);
        Assert.Equal(InvocationStatus.Cancelled, cancelled.Outcome.InvocationStatus);
        Assert.DoesNotContain(exceptionCanary, threw.Result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(exceptionCanary, JsonSerializer.Serialize(threw.Outcome), StringComparison.Ordinal);
        Assert.Equal(typeof(InvalidOperationException).FullName, threw.Outcome.FailureCode);
    }

    [Fact]
    public async Task ProductionSink_AppendsDeferredObservationToProtectedEvidenceLedger()
    {
        using var directory = new OutcomeAndEvidenceTests.TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile");
        await using var service = new ShadowToolObservationService(ledger);
        var now = DateTimeOffset.UtcNow;

        Assert.True(service.TryObserveReturned(
            identity,
            "call",
            "tool",
            new Dictionary<string, object?> { ["path"] = "private" },
            new { success = false },
            now,
            now.AddMilliseconds(1),
            NotRequired));
        await service.DisposeAsync();

        var replay = Assert.Single(await ledger.ReplayAsync(
            identity,
            TestContext.Current.CancellationToken));
        Assert.Equal(InvocationStatus.Returned, replay.Evidence.InvocationStatus);
        Assert.Equal(DomainOutcome.Unreported, replay.Evidence.DomainOutcome);
        var protectedContent = await ledger.ReadProtectedAsync(
            identity,
            replay.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal("omitted-in-shadow", protectedContent.Arguments.GetProperty("capture").GetString());
        Assert.Equal("arguments", protectedContent.Arguments.GetProperty("field").GetString());
        Assert.Equal("omitted-in-shadow", protectedContent.Result.GetProperty("capture").GetString());
        Assert.Equal("result", protectedContent.Result.GetProperty("field").GetString());
        var protectedJson = JsonSerializer.Serialize(protectedContent);
        Assert.DoesNotContain("private", protectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("success", protectedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthIsRedacted_AndDisposalIsBoundedAndIdempotent()
    {
        const string failureCanary = "health-must-not-retain-this-failure";
        var sink = new NeverCompletingSink(failureCanary);
        var service = new ShadowToolObservationService(
            sink,
            shutdownTimeout: TimeSpan.FromMilliseconds(50));
        var identity = new TurnIdentity("user", "conversation", "message");
        var now = DateTimeOffset.UtcNow;
        Assert.True(ObserveReturned(service, identity, "call", now));
        await sink.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask());
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, service.Health.ShutdownTimeouts);
        Assert.Equal(1, service.Health.ShutdownPendingAtTimeout);
        Assert.Equal(0, service.Health.ShutdownAbandoned);
        Assert.Equal(1, service.Health.Pending);
        Assert.DoesNotContain(
            failureCanary,
            JsonSerializer.Serialize(service.Health),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(ShadowObservationHealthSnapshot).GetProperties(),
            property => property.PropertyType == typeof(Exception) ||
                        property.PropertyType == typeof(string));
        sink.Release.TrySetException(new InvalidOperationException(failureCanary));
    }

    [Fact]
    public async Task ShutdownAccounting_OnlyCountsItemsTheCancelledReaderActuallyAbandons()
    {
        var sink = new CancellationAwareBlockingSink();
        var service = new ShadowToolObservationService(
            sink,
            capacity: 2,
            shutdownTimeout: TimeSpan.FromMilliseconds(50));
        var identity = new TurnIdentity("user", "conversation", "message");
        var now = DateTimeOffset.UtcNow;

        Assert.True(ObserveReturned(service, identity, "call-in-flight", now));
        await sink.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(ObserveReturned(service, identity, "call-queued", now));

        await service.DisposeAsync();
        await WaitUntilAsync(() => service.Health.IsReaderCompleted);

        Assert.Equal(1, service.Health.ShutdownTimeouts);
        Assert.Equal(2, service.Health.ShutdownPendingAtTimeout);
        Assert.Equal(2, service.Health.ShutdownAbandoned);
        Assert.Equal(0, service.Health.Pending);
        Assert.Equal(0, service.Health.Persisted);
    }

    private static bool ObserveReturned(
        ShadowToolObservationService service,
        TurnIdentity identity,
        string callId,
        DateTimeOffset now) =>
        service.TryObserveReturned(
            identity,
            callId,
            "tool",
            null,
            new { value = callId },
            now,
            now,
            NotRequired);

    private static EvidenceDraft Draft(ShadowToolObservation observation) =>
        ShadowEvidenceLedgerSink.CreateDraft(observation);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The shadow observation reader did not reach the expected state.");
    }

    private sealed class HostileArguments : IEnumerable<KeyValuePair<string, object?>>
    {
        public int EnumerationAttempts { get; private set; }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidOperationException("Shadow capture must not enumerate live arguments.");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class HostileCyclicResult
    {
        public int GetterReads { get; private set; }

        public HostileCyclicResult Self => this;

        public bool Success
        {
            get
            {
                GetterReads++;
                throw new InvalidOperationException("Shadow capture must not inspect live results.");
            }
        }
    }

    private sealed class CapturingSink : IShadowObservationSink
    {
        private readonly object _sync = new();
        private readonly List<ShadowToolObservation> _items = [];
        private readonly TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ShadowToolObservation> Items
        {
            get
            {
                lock (_sync)
                {
                    return _items.ToArray();
                }
            }
        }

        public ValueTask PersistAsync(
            ShadowToolObservation observation,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _items.Add(observation);
                if (_items.Count >= 2)
                {
                    _changed.TrySetResult();
                }
            }

            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int count)
        {
            lock (_sync)
            {
                if (_items.Count >= count)
                {
                    return;
                }
            }

            await _changed.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class BlockingSink : IShadowObservationSink
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PersistAsync(
            ShadowToolObservation observation,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CancellationAwareBlockingSink : IShadowObservationSink
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PersistAsync(
            ShadowToolObservation observation,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailFirstSink : IShadowObservationSink
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public TaskCompletionSource SecondAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PersistAsync(
            ShadowToolObservation observation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new IOException("simulated persistence failure");
            }

            SecondAttempt.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NeverCompletingSink(string failureCanary) : IShadowObservationSink
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask PersistAsync(
            ShadowToolObservation observation,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task;
            }
            catch
            {
                throw new IOException(failureCanary);
            }
        }
    }
}
