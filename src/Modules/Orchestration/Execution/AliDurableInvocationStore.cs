using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Microsoft.Win32.SafeHandles;

namespace Ali.Modules.Orchestration.Execution;

/// <summary>
/// Stores one protected invocation plan and one revisioned protected receipt. The plan receives
/// a one-way start anchor before execution is released, so loss of a receipt cannot regress a
/// started invocation to Prepared. Each file is written through a flushed same-directory
/// temporary file and published relative to an authenticated, handle-held directory spine. No
/// target-domain effect is authorized until the exact one-use grant has been consumed and its
/// start anchor is durable.
///
/// The namespace binding prevents ordinary root/leaf interposition while this store instance is
/// alive. DPAPI and the authorization/domain entropy protect contents, but deliberate rollback or
/// replay by the same logged-in Windows user across a process restart remains outside this trust
/// boundary; preventing that requires non-rollback operating-system state.
/// </summary>
internal sealed class AliDurableInvocationStore
{
    internal const int MaximumProtectedArtifactBytes = 256 * 1024;
    internal const string AuthorizationDomain = "ali-durable-invocation-authorization-v1";
    private const string ReplacementJournalLeaf =
        ".invocations.replacement-journal.protected";
    private const string ReplacementJournalTemporaryLeaf =
        ".invocations.replacement-journal.tmp";
    internal const string ReplacementTemporaryLeaf =
        ".invocations.replacement.new";
    internal const string ReplacementBackupLeaf =
        ".invocations.replacement.previous";
    private const string ReplacementJournalDomain =
        "ali-durable-invocation-replacement-v1";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _root;
    private readonly string _profileBinding;
    private readonly Action<AliDurableInvocationStoreCheckpoint, string>? _testHook;
    private readonly object _namespaceBindingGate = new();
    private AliDurableInvocationNamespaceBinding? _namespaceBinding;

    internal AliDurableInvocationStore(
        string root,
        string profileBinding,
        Action<AliDurableInvocationStoreCheckpoint, string>? testHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileBinding);
        _root = Path.GetFullPath(root);
        _profileBinding = profileBinding;
        _testHook = testHook;
    }

    internal async Task<AliDurableInvocationPlan> PrepareAsync(
        AliDurableInvocationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (plan.StartAnchor is not null)
        {
            throw new InvalidOperationException(
                "Only an unstarted durable invocation plan can be prepared.");
        }
        using var namespaceLease = OpenNamespace();
        using var writerLease = await AcquireWriterLeaseAsync(
                namespaceLease,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcilePendingReplacementAsync(namespaceLease, cancellationToken)
            .ConfigureAwait(false);
        using var existingPlan = namespaceLease.TryOpenExistingFile(
            PlanLeaf(plan.Id),
            AliDurableInvocationNativeAccess.Read,
            FileShare.Read);
        using var existingReceipt = namespaceLease.TryOpenExistingFile(
            ReceiptLeaf(plan.Id),
            AliDurableInvocationNativeAccess.Read,
            FileShare.Read);
        if (existingPlan is not null || existingReceipt is not null)
        {
            throw new InvalidOperationException(
                "The durable invocation plan identity already exists.");
        }

        await WriteProtectedAsync(
                namespaceLease,
                PlanLeaf(plan.Id),
                plan.Id,
                "plan",
                plan,
                expectedDestinationIdentity: null)
            .ConfigureAwait(false);
        namespaceLease.Verify();
        return plan with { };
    }

    internal async Task<AliDurableInvocationSnapshot> LoadAsync(
        string planId,
        CancellationToken cancellationToken)
    {
        AliDurableInvocationValidation.RequireId(planId, nameof(planId));
        using var namespaceLease = OpenNamespace();
        using var writerLease = await AcquireWriterLeaseAsync(
                namespaceLease,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcilePendingReplacementAsync(namespaceLease, cancellationToken)
            .ConfigureAwait(false);
        var loaded = await LoadCoreAsync(
                namespaceLease,
                planId,
                cancellationToken)
            .ConfigureAwait(false);
        namespaceLease.Verify();
        return loaded.Snapshot;
    }

    private async Task<AliDurableInvocationLoadedSnapshot> LoadCoreAsync(
        AliDurableInvocationNamespaceLease namespaceLease,
        string planId,
        CancellationToken cancellationToken)
    {
        var loadedPlan = await ReadProtectedAsync<AliDurableInvocationPlan>(
                namespaceLease,
                PlanLeaf(planId),
                planId,
                "plan",
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                "The durable invocation plan does not exist.",
                PlanPath(planId));
        var plan = loadedPlan.Value;
        plan.Validate();
        if (!string.Equals(plan.Id, planId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected durable invocation plan identity does not match its file.");
        }

        var loadedReceipt = await ReadProtectedAsync<AliDurableInvocationReceipt>(
                namespaceLease,
                ReceiptLeaf(planId),
                planId,
                "receipt",
                cancellationToken)
            .ConfigureAwait(false);
        var receipt = loadedReceipt?.Value;
        if (receipt is null)
        {
            if (plan.StartAnchor is not null)
            {
                var anchoredReceipt = new AliDurableInvocationReceipt(
                    plan.Id,
                    Revision: 1,
                    AliDurableInvocationState.Started,
                    plan.StartAnchor.StartedAtUtc,
                    TerminalAtUtc: null,
                    plan.StartAnchor.AuthorizationDigest,
                    StableOutcomeCode: null,
                    ResultDigest: null,
                    FailureCode: null);
                anchoredReceipt.Validate();
                return new AliDurableInvocationLoadedSnapshot(
                    new AliDurableInvocationSnapshot(
                        plan with { },
                        AliDurableInvocationState.Started,
                        anchoredReceipt),
                    loadedPlan.Identity,
                    ReceiptIdentity: null);
            }

            return new AliDurableInvocationLoadedSnapshot(
                new AliDurableInvocationSnapshot(
                    plan with { },
                    AliDurableInvocationState.Prepared,
                    Receipt: null),
                loadedPlan.Identity,
                ReceiptIdentity: null);
        }

        receipt.Validate();
        if (!string.Equals(receipt.PlanId, planId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected durable invocation receipt identity does not match its plan.");
        }
        if (plan.StartAnchor is null
            || plan.StartAnchor.StartedAtUtc != receipt.StartedAtUtc
            || !string.Equals(
                plan.StartAnchor.AuthorizationDigest,
                receipt.AuthorizationDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The durable invocation receipt does not match its protected start anchor.");
        }
        return new AliDurableInvocationLoadedSnapshot(
            new AliDurableInvocationSnapshot(
                plan with { },
                receipt.State,
                receipt with { }),
            loadedPlan.Identity,
            loadedReceipt!.Identity);
    }

    internal async Task<AliDurableInvocationSnapshot> ConsumeAndStartAsync(
        AliDurableInvocationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (!AliExecutionGrantContext.TryConsumeCurrent(
                plan.ToolName,
                plan.CapabilityId,
                plan.ReconcilerId,
                plan.Id,
                plan.RootBinding,
                out var grant)
            || grant is null)
        {
            throw new InvalidOperationException(
                "The durable invocation requires its exact unconsumed execution grant.");
        }

        return await StartConsumedAsync(plan.Id, grant, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<AliDurableInvocationSnapshot> ConsumeCurrentAndStartAsync(
        AliExactExecutionAdapterIdentity exactIdentity,
        CancellationToken cancellationToken)
    {
        var validatedIdentity = new AliExactExecutionAdapterIdentity(
            exactIdentity.ToolName,
            exactIdentity.CapabilityId,
            exactIdentity.ReconcilerId);
        if (!AliExecutionGrantContext.TryConsumeCurrent(
                validatedIdentity.ToolName,
                validatedIdentity.CapabilityId,
                validatedIdentity.ReconcilerId,
                out var grant)
            || grant is null)
        {
            throw new InvalidOperationException(
                "The durable invocation requires its exact unconsumed execution grant.");
        }

        return await StartConsumedAsync(
                grant.PreparationIdentity,
                grant,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AliDurableInvocationSnapshot> StartConsumedAsync(
        string planId,
        AliExecutionGrant grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        AliDurableInvocationValidation.RequireId(planId, nameof(planId));
        using var namespaceLease = OpenNamespace();
        using var writerLease = await AcquireWriterLeaseAsync(
                namespaceLease,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcilePendingReplacementAsync(namespaceLease, cancellationToken)
            .ConfigureAwait(false);
        var loaded = await LoadCoreAsync(namespaceLease, planId, cancellationToken)
            .ConfigureAwait(false);
        var current = loaded.Snapshot;
        if (current.State != AliDurableInvocationState.Prepared
            || current.Receipt is not null)
        {
            throw new InvalidOperationException(
                "Only a prepared durable invocation can begin execution.");
        }

        current.Plan.RequireExactGrant(grant);
        var authorizationDigest = AliExecutionAuthorizationDigest.Compute(
            AuthorizationDomain,
            grant);
        var startedAtUtc = DateTimeOffset.UtcNow;
        var receipt = new AliDurableInvocationReceipt(
            planId,
            Revision: 1,
            AliDurableInvocationState.Started,
            startedAtUtc,
            TerminalAtUtc: null,
            authorizationDigest,
            StableOutcomeCode: null,
            ResultDigest: null,
            FailureCode: null);
        receipt.Validate();
        var anchoredPlan = current.Plan with
        {
            StartAnchor = new AliDurableInvocationStartAnchor(
                startedAtUtc,
                authorizationDigest)
        };
        anchoredPlan.Validate();
        await WriteProtectedAsync(
                namespaceLease,
                PlanLeaf(planId),
                planId,
                "plan",
                anchoredPlan,
                loaded.PlanIdentity)
            .ConfigureAwait(false);
        await WriteProtectedAsync(
                namespaceLease,
                ReceiptLeaf(planId),
                planId,
                "receipt",
                receipt,
                expectedDestinationIdentity: null)
            .ConfigureAwait(false);
        namespaceLease.Verify();
        return new AliDurableInvocationSnapshot(
            anchoredPlan with { },
            receipt.State,
            receipt with { });
    }

    internal Task<AliDurableInvocationSnapshot> CompleteAsync(
        string planId,
        int expectedRevision,
        string stableOutcomeCode,
        string resultDigest,
        CancellationToken cancellationToken) =>
        TransitionTerminalAsync(
            planId,
            expectedRevision,
            AliDurableInvocationState.Completed,
            stableOutcomeCode,
            resultDigest,
            failureCode: null,
            cancellationToken);

    internal Task<AliDurableInvocationSnapshot> FailAsync(
        string planId,
        int expectedRevision,
        string failureCode,
        CancellationToken cancellationToken) =>
        TransitionTerminalAsync(
            planId,
            expectedRevision,
            AliDurableInvocationState.Failed,
            stableOutcomeCode: null,
            resultDigest: null,
            failureCode,
            cancellationToken);

    internal Task<AliDurableInvocationSnapshot> MarkInDoubtAsync(
        string planId,
        int expectedRevision,
        string failureCode,
        CancellationToken cancellationToken) =>
        TransitionTerminalAsync(
            planId,
            expectedRevision,
            AliDurableInvocationState.InDoubt,
            stableOutcomeCode: null,
            resultDigest: null,
            failureCode,
            cancellationToken);

    private async Task<AliDurableInvocationSnapshot> TransitionTerminalAsync(
        string planId,
        int expectedRevision,
        AliDurableInvocationState state,
        string? stableOutcomeCode,
        string? resultDigest,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        AliDurableInvocationValidation.RequireId(planId, nameof(planId));
        if (expectedRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        if (state is not (AliDurableInvocationState.Completed
            or AliDurableInvocationState.Failed
            or AliDurableInvocationState.InDoubt))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        using var namespaceLease = OpenNamespace();
        using var writerLease = await AcquireWriterLeaseAsync(
                namespaceLease,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcilePendingReplacementAsync(namespaceLease, cancellationToken)
            .ConfigureAwait(false);
        var loaded = await LoadCoreAsync(namespaceLease, planId, cancellationToken)
            .ConfigureAwait(false);
        var current = loaded.Snapshot;
        var started = current.Receipt;
        if (current.State != AliDurableInvocationState.Started
            || started is null
            || started.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                "The durable invocation is not at the expected started revision.");
        }

        var terminalAtUtc = DateTimeOffset.UtcNow;
        if (terminalAtUtc < started.StartedAtUtc)
        {
            terminalAtUtc = started.StartedAtUtc;
        }
        var terminal = started with
        {
            Revision = checked(started.Revision + 1),
            State = state,
            TerminalAtUtc = terminalAtUtc,
            StableOutcomeCode = stableOutcomeCode,
            ResultDigest = resultDigest,
            FailureCode = failureCode
        };
        terminal.Validate();
        var receiptIdentity = loaded.ReceiptIdentity
            ?? throw new InvalidDataException(
                "The started durable invocation receipt has no exact file identity.");
        await WriteProtectedAsync(
                namespaceLease,
                ReceiptLeaf(planId),
                planId,
                "receipt",
                terminal,
                receiptIdentity)
            .ConfigureAwait(false);
        namespaceLease.Verify();
        return new AliDurableInvocationSnapshot(
            current.Plan with { },
            terminal.State,
            terminal with { });
    }

    private static async Task<AliDurableInvocationBoundFile> AcquireWriterLeaseAsync(
        AliDurableInvocationNamespaceLease namespaceLease,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return namespaceLease.OpenOrCreateWriterLock(
                    ".invocations.writer.lock");
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteProtectedAsync<T>(
        AliDurableInvocationNamespaceLease namespaceLease,
        string finalLeaf,
        string planId,
        string artifactKind,
        T value,
        AliDurableInvocationFileIdentity? expectedDestinationIdentity)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (plaintext.Length is <= 0 or > MaximumProtectedArtifactBytes / 2)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException(
                "The durable invocation payload is empty or unbounded.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Protect(planId, artifactKind, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        try
        {
            if (protectedBytes.Length > MaximumProtectedArtifactBytes)
            {
                throw new InvalidDataException(
                    "The protected durable invocation payload is unbounded.");
            }

            var transactionId = Guid.NewGuid().ToString("N");
            var temporaryLeaf = expectedDestinationIdentity is null
                ? $".{planId}.{artifactKind}.{transactionId}.tmp"
                : ReplacementTemporaryLeaf;
            var backupLeaf = ReplacementBackupLeaf;
            AliDurableInvocationBoundFile? temporary = null;
            AliDurableInvocationBoundFile? destination = null;
            AliDurableInvocationBoundFile? journal = null;
            AliDurableInvocationReplacementIntent? replacementIntent = null;
            var published = false;
            var journalDurable = false;
            var temporaryDeleteIssued = false;
            var retainRecoveryArtifacts = false;
            try
            {
                temporary = namespaceLease.CreateTemporaryFile(temporaryLeaf);
                await namespaceLease.WriteAndFlushAsync(
                        temporary,
                        protectedBytes,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                _testHook?.Invoke(
                    AliDurableInvocationStoreCheckpoint.TemporaryFlushed,
                    artifactKind);
                _testHook?.Invoke(
                    AliDurableInvocationStoreCheckpoint.BeforePublication,
                    artifactKind);
                if (expectedDestinationIdentity is null)
                {
                    namespaceLease.RenameForPublication(
                        temporary,
                        finalLeaf);
                    published = true;
                }
                else
                {
                    replacementIntent = new AliDurableInvocationReplacementIntent(
                        transactionId,
                        planId,
                        artifactKind,
                        finalLeaf,
                        temporaryLeaf,
                        backupLeaf,
                        expectedDestinationIdentity,
                        temporary.Identity);
                    replacementIntent.Validate();
                    journal = await WriteReplacementJournalAsync(
                            namespaceLease,
                            replacementIntent,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    journalDurable = true;
                    _testHook?.Invoke(
                        AliDurableInvocationStoreCheckpoint.ReplacementJournalDurable,
                        artifactKind);

                    destination = namespaceLease.TryOpenExistingFile(
                        finalLeaf,
                        AliDurableInvocationNativeAccess.Read
                        | AliDurableInvocationNativeAccess.Delete,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (destination is null
                        || !expectedDestinationIdentity.SameObject(destination.Identity))
                    {
                        throw new InvalidDataException(
                            "The durable invocation destination identity changed before publication.");
                    }
                    _testHook?.Invoke(
                        AliDurableInvocationStoreCheckpoint.DestinationIdentityChecked,
                        artifactKind);
                    namespaceLease.RequireExactDestination(
                        destination,
                        finalLeaf,
                        expectedDestinationIdentity);

                    namespaceLease.RenameExactNoReplace(destination, backupLeaf);
                    _testHook?.Invoke(
                        AliDurableInvocationStoreCheckpoint.DestinationDisplaced,
                        artifactKind);
                    namespaceLease.RequireExactObjectAt(
                        destination,
                        backupLeaf,
                        expectedDestinationIdentity);
                    namespaceLease.RequireAbsent(finalLeaf);
                    _testHook?.Invoke(
                        AliDurableInvocationStoreCheckpoint.BeforeNoReplacePublication,
                        artifactKind);
                    namespaceLease.RequireAbsent(finalLeaf);

                    namespaceLease.RenameForPublication(
                        temporary,
                        finalLeaf);
                    published = true;
                    _testHook?.Invoke(
                        AliDurableInvocationStoreCheckpoint.ReplacementPublishedBeforeCleanup,
                        artifactKind);
                    namespaceLease.RequireHeldObject(
                        destination,
                        expectedDestinationIdentity);
                    namespaceLease.DeleteExact(destination);
                    namespaceLease.DeleteExact(journal);
                }

                _testHook?.Invoke(
                    AliDurableInvocationStoreCheckpoint.AfterPublication,
                    artifactKind);
            }
            catch (AliDurableInvocationSimulatedCrashException)
            {
                retainRecoveryArtifacts = journalDurable && !published;
                throw;
            }
            catch
            {
                if (journalDurable
                    && !published
                    && replacementIntent is not null
                    && journal is not null
                    && temporary is not null)
                {
                    var restored = TryRestoreFailedReplacement(
                        namespaceLease,
                        replacementIntent,
                        destination);
                    if (restored)
                    {
                        namespaceLease.DeleteExact(temporary);
                        temporaryDeleteIssued = true;
                        namespaceLease.DeleteExact(journal);
                    }
                    else
                    {
                        retainRecoveryArtifacts = true;
                    }
                }
                throw;
            }
            finally
            {
                destination?.Dispose();
                journal?.Dispose();
                if (temporary is not null)
                {
                    if (!published
                        && !temporaryDeleteIssued
                        && !retainRecoveryArtifacts)
                    {
                        namespaceLease.DeleteExact(temporary);
                    }
                    temporary.Dispose();
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private async Task<AliDurableInvocationLoadedArtifact<T>?> ReadProtectedAsync<T>(
        AliDurableInvocationNamespaceLease namespaceLease,
        string leaf,
        string planId,
        string artifactKind,
        CancellationToken cancellationToken)
    {
        using var artifact = namespaceLease.TryOpenExistingFile(
            leaf,
            AliDurableInvocationNativeAccess.Read,
            FileShare.Read);
        if (artifact is null)
        {
            return default;
        }
        var protectedBytes = await namespaceLease.ReadBoundedAsync(
                artifact,
                minimumLength: 1,
                MaximumProtectedArtifactBytes,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var plaintext = Unprotect(planId, artifactKind, protectedBytes);
            try
            {
                var value = JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException(
                        "The protected durable invocation payload is empty.");
                return new AliDurableInvocationLoadedArtifact<T>(
                    value,
                    artifact.Identity);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private async Task<AliDurableInvocationBoundFile> WriteReplacementJournalAsync(
        AliDurableInvocationNamespaceLease namespaceLease,
        AliDurableInvocationReplacementIntent intent,
        CancellationToken cancellationToken)
    {
        intent.Validate();
        namespaceLease.RequireAbsent(ReplacementJournalLeaf);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
        if (plaintext.Length is <= 0 or > MaximumProtectedArtifactBytes / 2)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException(
                "The durable invocation replacement intent is empty or unbounded.");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectReplacementJournal(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        AliDurableInvocationBoundFile? journal = null;
        var published = false;
        try
        {
            if (protectedBytes.Length > MaximumProtectedArtifactBytes)
            {
                throw new InvalidDataException(
                    "The protected durable invocation replacement intent is unbounded.");
            }
            journal = namespaceLease.CreateTemporaryFile(
                ReplacementJournalTemporaryLeaf);
            await namespaceLease.WriteAndFlushAsync(
                    journal,
                    protectedBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            namespaceLease.RenameForPublication(
                journal,
                ReplacementJournalLeaf);
            published = true;
            return journal;
        }
        catch
        {
            if (journal is not null)
            {
                if (!published)
                {
                    namespaceLease.DeleteExact(journal);
                }
                journal.Dispose();
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private async Task ReconcilePendingReplacementAsync(
        AliDurableInvocationNamespaceLease namespaceLease,
        CancellationToken cancellationToken)
    {
        using var journal = namespaceLease.TryOpenExistingFile(
            ReplacementJournalLeaf,
            AliDurableInvocationNativeAccess.Read
            | AliDurableInvocationNativeAccess.Delete,
            FileShare.Read);
        if (journal is null)
        {
            using var abandonedJournalTemporary = namespaceLease.TryOpenExistingFile(
                ReplacementJournalTemporaryLeaf,
                AliDurableInvocationNativeAccess.Read
                | AliDurableInvocationNativeAccess.Delete,
                FileShare.Read);
            using var abandonedReplacement = namespaceLease.TryOpenExistingFile(
                ReplacementTemporaryLeaf,
                AliDurableInvocationNativeAccess.Read
                | AliDurableInvocationNativeAccess.Delete,
                FileShare.Read);
            using var unexplainedBackup = namespaceLease.TryOpenExistingFile(
                ReplacementBackupLeaf,
                AliDurableInvocationNativeAccess.Read,
                FileShare.Read);
            if (unexplainedBackup is not null)
            {
                throw new InvalidDataException(
                    "A durable invocation replacement backup exists without its protected recovery intent.");
            }
            // The exact old destination is never displaced until the fixed protected journal is
            // durable. Without that journal or a backup, these two fixed staging names can only be
            // pre-intent crash residue from this protocol and are safe to remove by held handle.
            if (abandonedReplacement is not null)
            {
                namespaceLease.DeleteExact(abandonedReplacement);
            }
            if (abandonedJournalTemporary is not null)
            {
                namespaceLease.DeleteExact(abandonedJournalTemporary);
            }
            namespaceLease.Verify();
            return;
        }

        var intent = await ReadReplacementJournalAsync(
                namespaceLease,
                journal,
                cancellationToken)
            .ConfigureAwait(false);
        using var final = namespaceLease.TryOpenExistingFile(
            intent.FinalLeaf,
            AliDurableInvocationNativeAccess.Read,
            FileShare.Read);
        using var temporary = namespaceLease.TryOpenExistingFile(
            intent.TemporaryLeaf,
            AliDurableInvocationNativeAccess.Read
            | AliDurableInvocationNativeAccess.Delete,
            FileShare.Read);
        using var backup = namespaceLease.TryOpenExistingFile(
            intent.BackupLeaf,
            AliDurableInvocationNativeAccess.Read
            | AliDurableInvocationNativeAccess.Delete,
            FileShare.Read);

        var finalIsPrevious = final is not null
            && intent.PreviousIdentity.SameObject(final.Identity);
        var finalIsReplacement = final is not null
            && intent.ReplacementIdentity.SameObject(final.Identity);
        var temporaryIsReplacement = temporary is not null
            && intent.ReplacementIdentity.SameObject(temporary.Identity);
        var backupIsPrevious = backup is not null
            && intent.PreviousIdentity.SameObject(backup.Identity);
        if (final is not null && !finalIsPrevious && !finalIsReplacement
            || temporary is not null && !temporaryIsReplacement
            || backup is not null && !backupIsPrevious)
        {
            throw new InvalidDataException(
                "The durable invocation replacement journal found an unrelated namespace object; recovery is in doubt.");
        }

        if (finalIsReplacement)
        {
            _testHook?.Invoke(
                AliDurableInvocationStoreCheckpoint.RecoveryFinalIdentityChecked,
                intent.ArtifactKind);
            namespaceLease.RequireExactObjectAt(
                final!,
                intent.FinalLeaf,
                intent.ReplacementIdentity);
            if (temporary is not null)
            {
                throw new InvalidDataException(
                    "The durable invocation replacement identity appears under two recovery names.");
            }
            if (backup is not null)
            {
                namespaceLease.DeleteExact(backup);
            }
            namespaceLease.DeleteExact(journal);
            namespaceLease.Verify();
            return;
        }

        if (finalIsPrevious)
        {
            _testHook?.Invoke(
                AliDurableInvocationStoreCheckpoint.RecoveryFinalIdentityChecked,
                intent.ArtifactKind);
            namespaceLease.RequireExactObjectAt(
                final!,
                intent.FinalLeaf,
                intent.PreviousIdentity);
            if (backup is not null)
            {
                throw new InvalidDataException(
                    "The durable invocation previous identity appears under two recovery names.");
            }
            if (temporary is not null)
            {
                namespaceLease.DeleteExact(temporary);
            }
            namespaceLease.DeleteExact(journal);
            namespaceLease.Verify();
            return;
        }

        if (final is null && backupIsPrevious)
        {
            if (temporaryIsReplacement)
            {
                namespaceLease.RenameForPublication(
                    temporary!,
                    intent.FinalLeaf);
                namespaceLease.DeleteExact(backup!);
            }
            else
            {
                namespaceLease.RenameExactNoReplace(
                    backup!,
                    intent.FinalLeaf);
            }
            namespaceLease.DeleteExact(journal);
            namespaceLease.Verify();
            return;
        }

        throw new InvalidDataException(
            "The durable invocation replacement journal cannot prove a safe recovery state.");
    }

    private async Task<AliDurableInvocationReplacementIntent> ReadReplacementJournalAsync(
        AliDurableInvocationNamespaceLease namespaceLease,
        AliDurableInvocationBoundFile journal,
        CancellationToken cancellationToken)
    {
        var protectedBytes = await namespaceLease.ReadBoundedAsync(
                journal,
                minimumLength: 1,
                MaximumProtectedArtifactBytes,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var plaintext = UnprotectReplacementJournal(protectedBytes);
            try
            {
                var intent = JsonSerializer.Deserialize<AliDurableInvocationReplacementIntent>(
                        plaintext,
                        JsonOptions)
                    ?? throw new InvalidDataException(
                        "The durable invocation replacement intent is empty.");
                intent.Validate();
                return intent;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static bool TryRestoreFailedReplacement(
        AliDurableInvocationNamespaceLease namespaceLease,
        AliDurableInvocationReplacementIntent intent,
        AliDurableInvocationBoundFile? destination)
    {
        try
        {
            using var final = namespaceLease.TryOpenExistingFile(
                intent.FinalLeaf,
                AliDurableInvocationNativeAccess.Read,
                FileShare.Read);
            if (final is not null)
            {
                if (intent.PreviousIdentity.SameObject(final.Identity))
                {
                    namespaceLease.RequireExactObjectAt(
                        final,
                        intent.FinalLeaf,
                        intent.PreviousIdentity);
                    return true;
                }
                TryQuarantineHeldPrevious(
                    namespaceLease,
                    intent,
                    destination);
                return false;
            }

            if (destination is null
                || !intent.PreviousIdentity.SameObject(destination.Identity))
            {
                return false;
            }
            try
            {
                namespaceLease.RequireExactObjectAt(
                    destination,
                    intent.BackupLeaf,
                    intent.PreviousIdentity);
            }
            catch (InvalidDataException)
            {
                namespaceLease.RenameExactNoReplace(
                    destination,
                    intent.BackupLeaf);
                namespaceLease.RequireExactObjectAt(
                    destination,
                    intent.BackupLeaf,
                    intent.PreviousIdentity);
            }

            namespaceLease.RequireAbsent(intent.FinalLeaf);
            namespaceLease.RenameExactNoReplace(destination, intent.FinalLeaf);
            namespaceLease.RequireExactObjectAt(
                destination,
                intent.FinalLeaf,
                intent.PreviousIdentity);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryQuarantineHeldPrevious(
        AliDurableInvocationNamespaceLease namespaceLease,
        AliDurableInvocationReplacementIntent intent,
        AliDurableInvocationBoundFile? destination)
    {
        if (destination is null
            || !intent.PreviousIdentity.SameObject(destination.Identity))
        {
            return;
        }
        try
        {
            namespaceLease.RequireExactObjectAt(
                destination,
                intent.BackupLeaf,
                intent.PreviousIdentity);
        }
        catch (InvalidDataException)
        {
            namespaceLease.RenameExactNoReplace(destination, intent.BackupLeaf);
            namespaceLease.RequireExactObjectAt(
                destination,
                intent.BackupLeaf,
                intent.PreviousIdentity);
        }
    }

    private byte[] ProtectReplacementJournal(byte[] plaintext)
    {
        var entropy = ReplacementJournalEntropy();
        try
        {
            return ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] UnprotectReplacementJournal(byte[] protectedBytes)
    {
        var entropy = ReplacementJournalEntropy();
        try
        {
            return ProtectedData.Unprotect(
                protectedBytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The durable invocation replacement intent failed its current-user integrity check.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] ReplacementJournalEntropy() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\0",
            "Ali.Orchestration.DurableInvocation",
            _profileBinding,
            ReplacementJournalDomain)));

    private byte[] Protect(
        string planId,
        string artifactKind,
        byte[] plaintext)
    {
        var entropy = Entropy(planId, artifactKind);
        try
        {
            return ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] Unprotect(
        string planId,
        string artifactKind,
        byte[] protectedBytes)
    {
        var entropy = Entropy(planId, artifactKind);
        try
        {
            return ProtectedData.Unprotect(
                protectedBytes,
                entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The durable invocation artifact failed its current-user integrity check.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private byte[] Entropy(string planId, string artifactKind) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\0",
            "Ali.Orchestration.DurableInvocation",
            _profileBinding,
            planId,
            artifactKind)));

    private AliDurableInvocationNamespaceLease OpenNamespace()
    {
        AliDurableInvocationNamespaceLease namespaceLease;
        lock (_namespaceBindingGate)
        {
            namespaceLease = AliDurableInvocationWindowsNamespace.Open(
                _root,
                _namespaceBinding);
            _namespaceBinding ??= namespaceLease.Binding;
        }
        try
        {
            _testHook?.Invoke(
                AliDurableInvocationStoreCheckpoint.NamespaceAcquired,
                "namespace");
            return namespaceLease;
        }
        catch
        {
            namespaceLease.Dispose();
            throw;
        }
    }

    private static string PlanLeaf(string planId) =>
        planId + ".invocation-plan.protected";

    private static string ReceiptLeaf(string planId) =>
        planId + ".invocation-receipt.protected";

    private string PlanPath(string planId) =>
        Path.Combine(_root, PlanLeaf(planId));

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33
            || exception.InnerException is Win32Exception
            {
                NativeErrorCode: 32 or 33
            };
    }

    private sealed record AliDurableInvocationLoadedSnapshot(
        AliDurableInvocationSnapshot Snapshot,
        AliDurableInvocationFileIdentity PlanIdentity,
        AliDurableInvocationFileIdentity? ReceiptIdentity);
}

internal enum AliDurableInvocationStoreCheckpoint
{
    NamespaceAcquired,
    TemporaryFlushed,
    BeforePublication,
    ReplacementJournalDurable,
    DestinationIdentityChecked,
    DestinationDisplaced,
    BeforeNoReplacePublication,
    ReplacementPublishedBeforeCleanup,
    RecoveryFinalIdentityChecked,
    AfterPublication
}

internal sealed class AliDurableInvocationSimulatedCrashException : Exception
{
}

[Flags]
internal enum AliDurableInvocationNativeAccess
{
    Read = 1,
    Write = 2,
    Delete = 4
}

internal sealed record AliDurableInvocationLoadedArtifact<T>(
    T Value,
    AliDurableInvocationFileIdentity Identity);

internal sealed record AliDurableInvocationReplacementIntent(
    string TransactionId,
    string PlanId,
    string ArtifactKind,
    string FinalLeaf,
    string TemporaryLeaf,
    string BackupLeaf,
    AliDurableInvocationFileIdentity PreviousIdentity,
    AliDurableInvocationFileIdentity ReplacementIdentity)
{
    internal void Validate()
    {
        AliDurableInvocationValidation.RequireId(TransactionId, nameof(TransactionId));
        AliDurableInvocationValidation.RequireId(PlanId, nameof(PlanId));
        if (ArtifactKind is not ("plan" or "receipt"))
        {
            throw new InvalidDataException(
                "The durable invocation replacement artifact kind is invalid.");
        }
        var expectedFinal = PlanId + (ArtifactKind == "plan"
            ? ".invocation-plan.protected"
            : ".invocation-receipt.protected");
        var expectedTemporary = AliDurableInvocationStore.ReplacementTemporaryLeaf;
        var expectedBackup = AliDurableInvocationStore.ReplacementBackupLeaf;
        if (PreviousIdentity is null
            || ReplacementIdentity is null
            || !string.Equals(FinalLeaf, expectedFinal, StringComparison.Ordinal)
            || !string.Equals(TemporaryLeaf, expectedTemporary, StringComparison.Ordinal)
            || !string.Equals(BackupLeaf, expectedBackup, StringComparison.Ordinal)
            || !string.Equals(
                PreviousIdentity.FinalLeaf,
                FinalLeaf,
                StringComparison.Ordinal)
            || !string.Equals(
                ReplacementIdentity.FinalLeaf,
                TemporaryLeaf,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The durable invocation replacement namespace binding is invalid.");
        }
    }
}

internal sealed record AliDurableInvocationDirectoryIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh)
{
    internal bool SameObject(AliDurableInvocationDirectoryIdentity other) =>
        VolumeSerialNumber == other.VolumeSerialNumber
        && FileIdLow == other.FileIdLow
        && FileIdHigh == other.FileIdHigh;
}

internal sealed record AliDurableInvocationFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    uint NumberOfLinks,
    string FinalLeaf)
{
    internal bool SameObject(AliDurableInvocationFileIdentity other) =>
        VolumeSerialNumber == other.VolumeSerialNumber
        && FileIdLow == other.FileIdLow
        && FileIdHigh == other.FileIdHigh
        && NumberOfLinks == other.NumberOfLinks;
}

internal sealed record AliDurableInvocationNamespaceComponentBinding(
    string RelativePath,
    AliDurableInvocationDirectoryIdentity Identity);

internal sealed record AliDurableInvocationNamespaceBinding(
    string CanonicalRoot,
    IReadOnlyList<AliDurableInvocationNamespaceComponentBinding> Components);

internal sealed class AliDurableInvocationBoundFile(
    string leaf,
    SafeFileHandle handle,
    AliDurableInvocationFileIdentity identity) : IDisposable
{
    internal string Leaf { get; private set; } = leaf;

    internal SafeFileHandle Handle { get; } = handle;

    internal AliDurableInvocationFileIdentity Identity { get; private set; } = identity;

    internal void Rebind(string leaf, AliDurableInvocationFileIdentity identity)
    {
        Leaf = leaf;
        Identity = identity;
    }

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// Owns one authenticated volume-to-store directory spine. Every held directory omits delete
/// sharing, every child open is relative to the held store-root handle, and every mutation targets
/// an already-opened file handle. The binding is intentionally process-instance state: it detects
/// namespace replacement during this runtime, not deliberate same-user rollback across restart.
/// </summary>
internal sealed class AliDurableInvocationNamespaceLease(
    IReadOnlyList<SafeFileHandle> directoryHandles,
    AliDurableInvocationNamespaceBinding binding) : IDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _directoryHandles = directoryHandles;

    internal AliDurableInvocationNamespaceBinding Binding { get; } = binding;

    private SafeFileHandle RootHandle => _directoryHandles[^1];

    internal AliDurableInvocationBoundFile OpenOrCreateWriterLock(string leaf) =>
        OpenFile(
            leaf,
            AliDurableInvocationNativeAccess.Read | AliDurableInvocationNativeAccess.Write,
            AliDurableInvocationWindowsNamespace.FileOpenIf,
            FileShare.None,
            allowMissing: false)
        ?? throw new IOException("The durable invocation writer lease could not be opened.");

    internal AliDurableInvocationBoundFile CreateTemporaryFile(string leaf) =>
        OpenFile(
            leaf,
            AliDurableInvocationNativeAccess.Read
            | AliDurableInvocationNativeAccess.Write
            | AliDurableInvocationNativeAccess.Delete,
            AliDurableInvocationWindowsNamespace.FileCreate,
            FileShare.Read,
            allowMissing: false)
        ?? throw new IOException("The durable invocation temporary file could not be created.");

    internal AliDurableInvocationBoundFile? TryOpenExistingFile(
        string leaf,
        AliDurableInvocationNativeAccess access,
        FileShare share) =>
        OpenFile(
            leaf,
            access,
            AliDurableInvocationWindowsNamespace.FileOpen,
            share,
            allowMissing: true);

    internal async Task WriteAndFlushAsync(
        AliDurableInvocationBoundFile file,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var before = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            file.Handle,
            file.Leaf);
        if (!file.Identity.SameObject(before))
        {
            throw new InvalidDataException(
                "The durable invocation temporary identity changed before it was written.");
        }

        RandomAccess.SetLength(file.Handle, 0);
        if (!bytes.IsEmpty)
        {
            await RandomAccess.WriteAsync(
                    file.Handle,
                    bytes,
                    fileOffset: 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        RandomAccess.SetLength(file.Handle, bytes.Length);
        RandomAccess.FlushToDisk(file.Handle);

        var after = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            file.Handle,
            file.Leaf);
        if (!before.SameObject(after)
            || RandomAccess.GetLength(file.Handle) != bytes.Length)
        {
            throw new InvalidDataException(
                "The durable invocation temporary identity changed while it was written.");
        }
        file.Rebind(file.Leaf, after);
    }

    internal async Task<byte[]> ReadBoundedAsync(
        AliDurableInvocationBoundFile file,
        int minimumLength,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var before = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            file.Handle,
            file.Leaf);
        if (!file.Identity.SameObject(before))
        {
            throw new InvalidDataException(
                "The durable invocation artifact identity changed before it was read.");
        }
        var length = RandomAccess.GetLength(file.Handle);
        if (length < minimumLength || length > maximumLength)
        {
            throw new InvalidDataException(
                "The durable invocation artifact has an invalid size.");
        }

        var bytes = new byte[checked((int)length)];
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await RandomAccess.ReadAsync(
                        file.Handle,
                        bytes.AsMemory(offset),
                        offset,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        "The durable invocation artifact changed while it was read.");
                }
                offset += read;
            }

            var after = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
                file.Handle,
                file.Leaf);
            if (!before.SameObject(after)
                || RandomAccess.GetLength(file.Handle) != length)
            {
                throw new InvalidDataException(
                    "The durable invocation artifact changed while it was read.");
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    internal void RenameForPublication(
        AliDurableInvocationBoundFile temporary,
        string destinationLeaf)
    {
        ArgumentNullException.ThrowIfNull(temporary);
        AliDurableInvocationWindowsNamespace.RenameByHandle(
            temporary.Handle,
            RootHandle,
            destinationLeaf);

        var renamed = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            temporary.Handle,
            destinationLeaf);
        if (!temporary.Identity.SameObject(renamed))
        {
            throw new InvalidDataException(
                "The durable invocation publication changed physical object identity.");
        }
        temporary.Rebind(destinationLeaf, renamed);

        using var published = TryOpenExistingFile(
            destinationLeaf,
            AliDurableInvocationNativeAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (published is null || !renamed.SameObject(published.Identity))
        {
            throw new InvalidDataException(
                "The durable invocation publication is not bound to its exact destination name.");
        }
    }

    internal void RenameExactNoReplace(
        AliDurableInvocationBoundFile file,
        string destinationLeaf) =>
        RenameForPublication(file, destinationLeaf);

    internal void RequireAbsent(string leaf)
    {
        using var existing = TryOpenExistingFile(
            leaf,
            AliDurableInvocationNativeAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (existing is not null)
        {
            throw new InvalidDataException(
                "The durable invocation no-replace destination is unexpectedly occupied.");
        }
    }

    internal void RequireExactDestination(
        AliDurableInvocationBoundFile destination,
        string expectedLeaf,
        AliDurableInvocationFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var current = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            destination.Handle,
            expectedLeaf);
        if (!string.Equals(
                expectedIdentity.FinalLeaf,
                expectedLeaf,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                destination.Identity.FinalLeaf,
                expectedLeaf,
                StringComparison.OrdinalIgnoreCase)
            || !destination.Identity.SameObject(current)
            || !expectedIdentity.SameObject(current))
        {
            throw new InvalidDataException(
                "The held durable invocation destination changed before publication.");
        }
    }

    internal void RequireHeldObject(
        AliDurableInvocationBoundFile destination,
        AliDurableInvocationFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var current = AliDurableInvocationWindowsNamespace.CaptureFileObjectIdentity(
            destination.Handle);
        if (current.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber
            || current.FileIdLow != expectedIdentity.FileIdLow
            || current.FileIdHigh != expectedIdentity.FileIdHigh)
        {
            throw new InvalidDataException(
                "The held durable invocation destination object changed during publication.");
        }
    }

    internal void RequireExactObjectAt(
        AliDurableInvocationBoundFile file,
        string expectedLeaf,
        AliDurableInvocationFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var current = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            file.Handle,
            expectedLeaf);
        if (!file.Identity.SameObject(current)
            || !expectedIdentity.SameObject(current))
        {
            throw new InvalidDataException(
                "The exact durable invocation object is not bound at its recovery name.");
        }
    }

    internal void DeleteExact(AliDurableInvocationBoundFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var current = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
            file.Handle,
            file.Leaf);
        if (!file.Identity.SameObject(current))
        {
            throw new InvalidDataException(
                "The durable invocation temporary identity changed before cleanup.");
        }
        AliDurableInvocationWindowsNamespace.DeleteByHandle(file.Handle);
    }

    internal void Verify() =>
        AliDurableInvocationWindowsNamespace.VerifySpine(
            _directoryHandles,
            Binding);

    private AliDurableInvocationBoundFile? OpenFile(
        string leaf,
        AliDurableInvocationNativeAccess access,
        uint disposition,
        FileShare share,
        bool allowMissing)
    {
        var handle = AliDurableInvocationWindowsNamespace.OpenRelativeFile(
            RootHandle,
            leaf,
            access,
            disposition,
            share,
            allowMissing);
        if (handle is null)
        {
            return null;
        }
        try
        {
            var identity = AliDurableInvocationWindowsNamespace.CaptureFileIdentity(
                handle,
                leaf);
            return new AliDurableInvocationBoundFile(leaf, handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        for (var index = _directoryHandles.Count - 1; index >= 0; index--)
        {
            _directoryHandles[index].Dispose();
        }
    }
}

internal static class AliDurableInvocationWindowsNamespace
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileAddFile = 0x00000002;
    private const uint FileAddSubdirectory = 0x00000004;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileTypeDisk = 0x0001;
    private const uint ObjectCaseInsensitive = 0x00000040;
    internal const uint FileOpen = 1;
    internal const uint FileCreate = 2;
    internal const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint DriveUnknown = 0;
    private const uint DriveNoRootDirectory = 1;
    private const uint DriveRemote = 4;
    private const int MaximumFinalPathCharacters = 64 * 1024;

    internal static AliDurableInvocationNamespaceLease Open(
        string root,
        AliDurableInvocationNamespaceBinding? expectedBinding)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Durable invocation namespace authentication requires Windows handle-relative file APIs.");
        }

        var canonicalRoot = NormalizeDirectoryPath(root);
        var volumeRoot = Path.GetPathRoot(canonicalRoot)
            ?? throw new InvalidDataException(
                "The durable invocation root has no volume anchor.");
        RequireLocalVolume(volumeRoot);
        if (expectedBinding is not null
            && !string.Equals(
                canonicalRoot,
                expectedBinding.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The durable invocation root path changed after it was authenticated.");
        }

        var relative = Path.GetRelativePath(volumeRoot, canonicalRoot);
        var leaves = string.Equals(relative, ".", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        if (expectedBinding is not null
            && expectedBinding.Components.Count != leaves.Length + 1)
        {
            throw new InvalidDataException(
                "The durable invocation namespace spine shape changed after authentication.");
        }
        var firstMissing = expectedBinding is null
            ? FindFirstMissingDirectoryIndex(volumeRoot, leaves)
            : -1;

        var handles = new List<SafeFileHandle>(leaves.Length + 1);
        var components = new List<AliDurableInvocationNamespaceComponentBinding>(
            leaves.Length + 1);
        try
        {
            var anchor = OpenAbsoluteDirectory(
                volumeRoot,
                addSubdirectory: firstMissing == 0,
                addFile: leaves.Length == 0);
            handles.Add(anchor);
            var anchorIdentity = CaptureDirectoryIdentity(anchor);
            RequireExpectedComponent(expectedBinding, 0, ".", anchorIdentity);
            components.Add(new AliDurableInvocationNamespaceComponentBinding(
                ".",
                anchorIdentity));

            var currentRelative = string.Empty;
            for (var index = 0; index < leaves.Length; index++)
            {
                var leaf = leaves[index];
                RequireLeaf(leaf);
                var handle = OpenRelativeDirectory(
                    handles[^1],
                    leaf,
                    disposition: firstMissing >= 0 && index >= firstMissing
                        ? FileOpenIf
                        : FileOpen,
                    allowMissing: false,
                    addSubdirectory: firstMissing >= 0
                        && index >= firstMissing - 1
                        && index < leaves.Length - 1,
                    addFile: index == leaves.Length - 1)
                    ?? throw new DirectoryNotFoundException(
                        "The durable invocation namespace spine is missing.");
                handles.Add(handle);
                RequireLiteralLeaf(handle, leaf);
                var identity = CaptureDirectoryIdentity(handle);
                currentRelative = currentRelative.Length == 0
                    ? leaf
                    : currentRelative + "/" + leaf;
                RequireExpectedComponent(
                    expectedBinding,
                    index + 1,
                    currentRelative,
                    identity);
                components.Add(new AliDurableInvocationNamespaceComponentBinding(
                    currentRelative,
                    identity));
            }

            var openedRoot = NormalizeDirectoryPath(ReadFinalPath(
                handles[^1],
                "The durable invocation root final path could not be authenticated."));
            if (!string.Equals(
                    openedRoot,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The durable invocation root resolves through a non-literal namespace alias.");
            }

            var binding = expectedBinding
                ?? new AliDurableInvocationNamespaceBinding(
                    canonicalRoot,
                    components.ToArray());
            return new AliDurableInvocationNamespaceLease(handles, binding);
        }
        catch
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
            throw;
        }
    }

    internal static void VerifySpine(
        IReadOnlyList<SafeFileHandle> handles,
        AliDurableInvocationNamespaceBinding binding)
    {
        if (handles.Count != binding.Components.Count || handles.Count == 0)
        {
            throw new InvalidDataException(
                "The held durable invocation namespace spine is incomplete.");
        }
        for (var index = 0; index < handles.Count; index++)
        {
            var expected = binding.Components[index];
            var actual = CaptureDirectoryIdentity(handles[index]);
            if (!expected.Identity.SameObject(actual))
            {
                throw new InvalidDataException(
                    "The durable invocation namespace spine identity changed while held.");
            }
            if (index > 0)
            {
                RequireLiteralLeaf(
                    handles[index],
                    LeafOfRelative(expected.RelativePath));
            }
        }
        var openedRoot = NormalizeDirectoryPath(ReadFinalPath(
            handles[^1],
            "The durable invocation root final path could not be revalidated."));
        if (!string.Equals(
                openedRoot,
                binding.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The durable invocation root binding changed while held.");
        }
    }

    internal static SafeFileHandle? OpenRelativeFile(
        SafeFileHandle parent,
        string leaf,
        AliDurableInvocationNativeAccess access,
        uint disposition,
        FileShare share,
        bool allowMissing)
    {
        RequireLeaf(leaf);
        var desiredAccess = FileReadAttributes | Synchronize;
        if ((access & AliDurableInvocationNativeAccess.Read) != 0)
        {
            desiredAccess |= GenericRead;
        }
        if ((access & AliDurableInvocationNativeAccess.Write) != 0)
        {
            desiredAccess |= GenericWrite;
        }
        if ((access & AliDurableInvocationNativeAccess.Delete) != 0)
        {
            desiredAccess |= DeleteAccess;
        }

        var options = FileNonDirectoryFile
            | FileOpenReparsePoint
            | FileSynchronousIoNonAlert;
        if ((access & AliDurableInvocationNativeAccess.Write) != 0)
        {
            options |= FileWriteThrough;
        }
        var handle = OpenRelative(
            parent,
            leaf,
            desiredAccess,
            share,
            disposition,
            options,
            allowMissing);
        if (handle is not null)
        {
            ValidateHandleKind(
                handle,
                requireDirectory: false,
                "The durable invocation leaf is not a regular local file.");
            RequireLiteralLeaf(handle, leaf);
        }
        return handle;
    }

    internal static AliDurableInvocationFileIdentity CaptureFileIdentity(
        SafeFileHandle handle,
        string expectedLeaf)
    {
        ValidateHandleKind(
            handle,
            requireDirectory: false,
            "The durable invocation leaf is not a regular local file.");
        RequireLiteralLeaf(handle, expectedLeaf);
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation fileId,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            ThrowIo(
                "The durable invocation leaf FILE_ID_INFO identity could not be captured.",
                Marshal.GetLastWin32Error());
        }
        if (!GetFileInformationByHandle(handle, out var basic))
        {
            ThrowIo(
                "The durable invocation leaf link topology could not be captured.",
                Marshal.GetLastWin32Error());
        }
        if (basic.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                "A multiply-linked durable invocation leaf is not accepted.");
        }
        return new AliDurableInvocationFileIdentity(
            fileId.VolumeSerialNumber,
            fileId.FileId.Low,
            fileId.FileId.High,
            basic.NumberOfLinks,
            expectedLeaf);
    }

    internal static AliDurableInvocationDirectoryIdentity CaptureFileObjectIdentity(
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation information,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            ThrowIo(
                "The held durable invocation destination FILE_ID_INFO identity could not be captured.",
                Marshal.GetLastWin32Error());
        }
        return new AliDurableInvocationDirectoryIdentity(
            information.VolumeSerialNumber,
            information.FileId.Low,
            information.FileId.High);
    }

    internal static void RenameByHandle(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationLeaf)
    {
        RequireLeaf(destinationLeaf);
        var sourceIdentity = CaptureFileIdentity(
            source,
            LeafOfFinalPath(ReadFinalPath(
                source,
                "The durable invocation temporary final path could not be authenticated.")));
        var parentIdentity = CaptureDirectoryIdentity(destinationParent);
        if (sourceIdentity.VolumeSerialNumber != parentIdentity.VolumeSerialNumber)
        {
            throw new IOException(
                "A durable invocation publication cannot cross filesystem volumes.");
        }

        var fileNameBytes = Encoding.Unicode.GetBytes(destinationLeaf);
        var fileNameOffset = Marshal.OffsetOf<FileRenameInformationHeader>(
                nameof(FileRenameInformationHeader.FileNameLength))
            .ToInt32() + sizeof(uint);
        var size = Math.Max(
            Marshal.SizeOf<FileRenameInformationHeader>(),
            checked(fileNameOffset + fileNameBytes.Length));
        var buffer = Marshal.AllocHGlobal(size);
        var addedRef = false;
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            // Replacement is deliberately forbidden. Existing durable leaves are first moved by
            // their exact held handle to a recovery name, then publication uses no-replace.
            destinationParent.DangerousAddRef(ref addedRef);
            Marshal.StructureToPtr(
                new FileRenameInformationHeader
                {
                    ReplaceIfExists = 0,
                    RootDirectory = destinationParent.DangerousGetHandle(),
                    FileNameLength = checked((uint)fileNameBytes.Length)
                },
                buffer,
                fDeleteOld: false);
            Marshal.Copy(
                fileNameBytes,
                0,
                IntPtr.Add(buffer, fileNameOffset),
                fileNameBytes.Length);
            if (!SetFileInformationByHandle(
                    source,
                    FileRenameInfo,
                    buffer,
                    checked((uint)size)))
            {
                ThrowIo(
                    "The durable invocation artifact could not be published by exact handle.",
                    Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (addedRef)
            {
                destinationParent.DangerousRelease();
            }
            CryptographicOperations.ZeroMemory(fileNameBytes);
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void DeleteByHandle(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                1))
        {
            ThrowIo(
                "The durable invocation temporary could not be deleted by exact handle.",
                Marshal.GetLastWin32Error());
        }
    }

    private static int FindFirstMissingDirectoryIndex(
        string volumeRoot,
        IReadOnlyList<string> leaves)
    {
        var handles = new List<SafeFileHandle>(leaves.Count + 1);
        try
        {
            handles.Add(OpenAbsoluteDirectory(
                volumeRoot,
                addSubdirectory: false,
                addFile: false));
            for (var index = 0; index < leaves.Count; index++)
            {
                RequireLeaf(leaves[index]);
                var handle = OpenRelativeDirectory(
                    handles[^1],
                    leaves[index],
                    FileOpen,
                    allowMissing: true,
                    addSubdirectory: false,
                    addFile: false);
                if (handle is null)
                {
                    return index;
                }
                handles.Add(handle);
                RequireLiteralLeaf(handle, leaves[index]);
            }
            return -1;
        }
        finally
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectory(
        string path,
        bool addSubdirectory,
        bool addFile)
    {
        var desiredAccess = FileListDirectory | FileTraverse
            | FileReadAttributes | Synchronize;
        if (addSubdirectory)
        {
            desiredAccess |= FileAddSubdirectory;
        }
        if (addFile)
        {
            desiredAccess |= FileAddFile;
        }
        var handle = CreateFileW(
            ToExtendedLengthPath(path),
            desiredAccess,
            FileShare.ReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            ThrowIo(
                "The durable invocation volume root could not be opened without following links.",
                Marshal.GetLastWin32Error(),
                handle);
        }
        ValidateHandleKind(
            handle,
            requireDirectory: true,
            "The durable invocation volume root is not a regular local directory.");
        return handle;
    }

    private static SafeFileHandle? OpenRelativeDirectory(
        SafeFileHandle parent,
        string leaf,
        uint disposition,
        bool allowMissing,
        bool addSubdirectory,
        bool addFile)
    {
        var desiredAccess = FileListDirectory | FileTraverse
            | FileReadAttributes | Synchronize;
        if (addSubdirectory)
        {
            desiredAccess |= FileAddSubdirectory;
        }
        if (addFile)
        {
            desiredAccess |= FileAddFile;
        }
        var handle = OpenRelative(
            parent,
            leaf,
            desiredAccess,
            FileShare.ReadWrite,
            disposition,
            FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert,
            allowMissing);
        if (handle is null)
        {
            return null;
        }
        ValidateHandleKind(
            handle,
            requireDirectory: true,
            "The durable invocation namespace spine is not a regular local directory.");
        return handle;
    }

    private static SafeFileHandle? OpenRelative(
        SafeFileHandle parent,
        string leaf,
        uint desiredAccess,
        FileShare share,
        uint disposition,
        uint options,
        bool allowMissing)
    {
        RequireLeaf(leaf);
        var nameBytes = Encoding.Unicode.GetBytes(leaf);
        if (nameBytes.Length > ushort.MaxValue)
        {
            CryptographicOperations.ZeroMemory(nameBytes);
            throw new PathTooLongException(
                "A durable invocation namespace leaf exceeds the native Windows limit.");
        }

        var nameBuffer = Marshal.StringToHGlobalUni(leaf);
        var unicodePointer = IntPtr.Zero;
        var addedRef = false;
        try
        {
            var unicode = new UnicodeString
            {
                Length = checked((ushort)nameBytes.Length),
                MaximumLength = checked((ushort)nameBytes.Length),
                Buffer = nameBuffer
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, fDeleteOld: false);
            parent.DangerousAddRef(ref addedRef);
            var attributes = new ObjectAttributes
            {
                Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = ObjectCaseInsensitive
            };
            var status = NtCreateFile(
                out var handle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                disposition is FileCreate or FileOpenIf ? FileAttributeNormal : 0,
                (uint)share,
                disposition,
                options,
                IntPtr.Zero,
                0);
            if (status >= 0 && !handle.IsInvalid)
            {
                return handle;
            }

            handle?.Dispose();
            var error = unchecked((int)RtlNtStatusToDosError(status));
            if (allowMissing && error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }
            ThrowIo(
                "A durable invocation namespace leaf could not be opened relative to its held parent.",
                error);
            return null;
        }
        finally
        {
            if (addedRef)
            {
                parent.DangerousRelease();
            }
            if (unicodePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodePointer);
            }
            Marshal.FreeHGlobal(nameBuffer);
            CryptographicOperations.ZeroMemory(nameBytes);
        }
    }

    private static AliDurableInvocationDirectoryIdentity CaptureDirectoryIdentity(
        SafeFileHandle handle)
    {
        ValidateHandleKind(
            handle,
            requireDirectory: true,
            "The durable invocation namespace component is not a regular local directory.");
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation information,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            ThrowIo(
                "The durable invocation directory FILE_ID_INFO identity could not be captured.",
                Marshal.GetLastWin32Error());
        }
        return new AliDurableInvocationDirectoryIdentity(
            information.VolumeSerialNumber,
            information.FileId.Low,
            information.FileId.High);
    }

    private static void RequireExpectedComponent(
        AliDurableInvocationNamespaceBinding? expectedBinding,
        int index,
        string relativePath,
        AliDurableInvocationDirectoryIdentity identity)
    {
        if (expectedBinding is null)
        {
            return;
        }
        var expected = expectedBinding.Components[index];
        if (!string.Equals(
                expected.RelativePath,
                relativePath,
                StringComparison.OrdinalIgnoreCase)
            || !expected.Identity.SameObject(identity))
        {
            throw new InvalidDataException(
                "The durable invocation namespace spine changed after it was authenticated.");
        }
    }

    private static void ValidateHandleKind(
        SafeFileHandle handle,
        bool requireDirectory,
        string invalidMessage)
    {
        if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk)
        {
            throw new InvalidDataException(invalidMessage);
        }
        var attributes = File.GetAttributes(handle);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != requireDirectory
            || (attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(invalidMessage);
        }
    }

    private static void RequireLiteralLeaf(
        SafeFileHandle handle,
        string expectedLeaf)
    {
        var actualLeaf = LeafOfFinalPath(ReadFinalPath(
            handle,
            "The durable invocation namespace final name could not be authenticated."));
        if (!string.Equals(actualLeaf, expectedLeaf, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The durable invocation namespace resolves through a non-literal Windows alias.");
        }
    }

    private static string ReadFinalPath(SafeFileHandle handle, string invalidMessage)
    {
        var capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                0);
            if (length == 0)
            {
                ThrowIo(invalidMessage, Marshal.GetLastWin32Error());
            }
            if (length < buffer.Capacity)
            {
                var value = buffer.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\" + value[8..];
                }
                return value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                    ? value[4..]
                    : value;
            }
            capacity = checked((int)length + 1);
        }
        throw new InvalidDataException(invalidMessage);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.TrimEndingDirectorySeparator(full);
    }

    private static string LeafOfFinalPath(string path)
    {
        var trimmed = path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var separator = trimmed.LastIndexOfAny(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    private static string LeafOfRelative(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    private static void RequireLocalVolume(string volumeRoot)
    {
        var driveType = GetDriveTypeW(volumeRoot);
        if (driveType is DriveUnknown or DriveNoRootDirectory or DriveRemote)
        {
            throw new InvalidDataException(
                "The durable invocation root must be on an available local Windows volume.");
        }
    }

    private static void RequireLeaf(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf)
            || leaf.Length > 255
            || leaf is "." or ".."
            || leaf.Any(character => character < ' ')
            || leaf.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || leaf.EndsWith(' ')
            || leaf.EndsWith('.')
            || IsReservedDeviceName(leaf))
        {
            throw new InvalidDataException(
                "A durable invocation handle-relative operation requires one ordinary literal Windows leaf name.");
        }
    }

    private static bool IsReservedDeviceName(string leaf)
    {
        var stem = leaf.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$")
        {
            return true;
        }
        if (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal)
                || stem.StartsWith("LPT", StringComparison.Ordinal)))
        {
            return stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';
        }
        return false;
    }

    private static string ToExtendedLengthPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private static void ThrowIo(
        string message,
        int error,
        SafeFileHandle? handle = null)
    {
        handle?.Dispose();
        if (error == ErrorFileNotFound)
        {
            throw new FileNotFoundException(message);
        }
        if (error == ErrorPathNotFound)
        {
            throw new DirectoryNotFoundException(message);
        }
        throw new IOException(message, new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInformationHeader
    {
        internal uint ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathCharacters,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetDriveTypeW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}

internal static class AliDurableInvocationGrantConsumer
{
    internal static async Task<AliDurableInvocationSnapshot> ConsumeAndStartAsync(
        AliDurableInvocationStore store,
        AliDurableInvocationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plan);
        return await store.ConsumeAndStartAsync(plan, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<AliDurableInvocationSnapshot> ConsumeCurrentAndStartAsync(
        AliDurableInvocationStore store,
        AliExactExecutionAdapterIdentity exactIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        return await store.ConsumeCurrentAndStartAsync(
                exactIdentity,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
