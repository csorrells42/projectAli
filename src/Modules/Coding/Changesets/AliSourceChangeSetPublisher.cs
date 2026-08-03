using System.Collections.Immutable;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Changesets;

internal enum AliSourcePublicationState
{
    Prepared,
    Committed,
    RolledBack,
    InDoubt
}

internal sealed record AliSourcePublicationReceipt(
    string ChangeSetId,
    string ManifestDigest,
    string GrantId,
    string AuthorizationBindingDigest,
    AliSourcePublicationState State,
    DateTimeOffset UpdatedAtUtc,
    long JournalSequence,
    string JournalAuthenticationTag,
    ImmutableArray<string> AffectedPaths,
    string Summary);

/// <summary>
/// One-use publication authority. Production issuance belongs to AliExecutionBroker after the
/// exact prepared action and permission receipt are durable. Tests issue grants directly only to
/// exercise this lower-level transaction boundary.
/// </summary>
internal sealed class AliSourcePublicationGrant
{
    private int _consumed;

    private AliSourcePublicationGrant(
        string grantId,
        string changeSetId,
        string manifestDigest,
        string authorizationBindingDigest)
    {
        GrantId = grantId;
        ChangeSetId = changeSetId;
        ManifestDigest = manifestDigest;
        AuthorizationBindingDigest = authorizationBindingDigest;
    }

    internal string GrantId { get; }
    internal string ChangeSetId { get; }
    internal string ManifestDigest { get; }
    internal string AuthorizationBindingDigest { get; }

    internal static AliSourcePublicationGrant Issue(
        AliSourceChangeSet changeSet,
        string authorizationBindingDigest)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return new AliSourcePublicationGrant(
            Guid.NewGuid().ToString("N"),
            changeSet.Id,
            AliSourceChangeSetStore.NormalizeSha256(changeSet.ManifestDigest),
            AliSourceChangeSetStore.NormalizeSha256(authorizationBindingDigest));
    }

    internal void ConsumeExact(AliSourceChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            throw new InvalidOperationException("The source publication grant has already been consumed.");
        }
        if (!string.Equals(ChangeSetId, changeSet.Id, StringComparison.Ordinal)
            || !string.Equals(ManifestDigest, changeSet.ManifestDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source publication grant does not authorize this exact manifest.");
        }
    }
}

internal enum AliSourceTransactionBoundary
{
    GrantConsumed,
    PublicationLeaseAcquired,
    PreparedReceiptPersisted,
    OperationIntentPersisted,
    OperationMutationCompleted,
    OperationAppliedPersisted,
    OperationVerifiedPersisted,
    TerminalJournalPersisted,
    RollbackIntentPersisted,
    RollbackMutationCompleted,
    RollbackAppliedPersisted,
    RollbackVerifiedPersisted,
    TerminalReceiptPersisted
}

internal readonly record struct AliSourceTransactionFault(
    AliSourceTransactionBoundary Boundary,
    int OperationSequence);

internal sealed class AliSourceSimulatedInterruptionException(
    AliSourceTransactionBoundary boundary,
    int operationSequence = -1)
    : IOException($"Simulated source transaction interruption at {boundary} ({operationSequence}).")
{
    internal AliSourceTransactionBoundary Boundary { get; } = boundary;
    internal int OperationSequence { get; } = operationSequence;
}

internal sealed class AliSourcePublicationException(
    string message,
    AliSourcePublicationReceipt receipt,
    Exception innerException)
    : IOException(message, innerException)
{
    internal AliSourcePublicationReceipt Receipt { get; } = receipt;
}

internal enum AliSourceJournalEventKind
{
    OperationIntent,
    OperationApplied,
    OperationVerified,
    RollbackIntent,
    RollbackApplied,
    RollbackVerified,
    Terminal
}

internal sealed record AliSourceJournalEntry(
    long Sequence,
    AliSourceJournalEventKind Kind,
    int OperationSequence,
    AliSourcePublicationState? TerminalState,
    DateTimeOffset TimestampUtc,
    string ManifestDigest,
    string PreviousAuthenticationTag,
    AliSourceFileIdentity? CanonicalObjectIdentity);

internal sealed record AliSourceJournalEnvelope(
    AliSourceJournalEntry Entry,
    string AuthenticationTag);

internal sealed record AliSourceJournalHead(long Sequence, string AuthenticationTag)
{
    internal static AliSourceJournalHead Empty { get; } = new(0, new string('0', 64));
}

internal sealed record AliSourceOperationJournalProof(
    bool ForwardApplied,
    AliSourceFileIdentity? ForwardIdentity,
    bool RollbackApplied,
    AliSourceFileIdentity? RollbackIdentity);

internal sealed record AliSourceJournalSnapshot(
    AliSourceJournalHead Head,
    ImmutableDictionary<int, AliSourceOperationJournalProof> OperationProofs)
{
    internal static AliSourceJournalSnapshot Empty { get; } = new(
        AliSourceJournalHead.Empty,
        ImmutableDictionary<int, AliSourceOperationJournalProof>.Empty);

    internal AliSourceOperationJournalProof ProofFor(int operationSequence) =>
        OperationProofs.TryGetValue(operationSequence, out var proof)
            ? proof
            : new(false, null, false, null);
}

internal sealed class AliSourceChangeSetPublisher
{
    private static readonly JsonSerializerOptions JournalJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly AliSourceChangeSetStore _store;
    private readonly AliSourceChangeSetValidator _validator;
    private readonly Action<AliSourceTransactionFault>? _faultInjector;

    internal AliSourceChangeSetPublisher(
        AliSourceChangeSetStore store,
        AliSourceChangeSetValidator validator,
        Action<AliSourceTransactionFault>? faultInjector = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _faultInjector = faultInjector;
    }

    internal AliSourceChangeSetStore Store => _store;

    internal async Task<AliSourcePublicationReceipt> PublishAsync(
        AliSourceChangeSet suppliedChangeSet,
        AliSourcePublicationGrant grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppliedChangeSet);
        ArgumentNullException.ThrowIfNull(grant);
        grant.ConsumeExact(suppliedChangeSet);
        Inject(AliSourceTransactionBoundary.GrantConsumed);
        var changeSet = await _store.LoadAsync(suppliedChangeSet.Id, cancellationToken).ConfigureAwait(false);
        AliSourceChangeSetStore.RequireExactManifest(suppliedChangeSet, changeSet);
        var validation = await _validator.ValidateAsync(changeSet, cancellationToken).ConfigureAwait(false);
        await using var lease = await AcquirePublicationLeaseAsync(
            changeSet.CanonicalSourceRoot,
            cancellationToken).ConfigureAwait(false);
        Inject(AliSourceTransactionBoundary.PublicationLeaseAcquired);

        var existing = await ReadReceiptAsync(changeSet.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "This source changeset already has a durable publication attempt and cannot be replayed.");
        }

        var affectedPaths = AffectedPaths(changeSet);
        if (!validation.Valid)
        {
            var rejected = CreateReceipt(
                changeSet,
                grant,
                AliSourcePublicationState.RolledBack,
                AliSourceJournalHead.Empty,
                affectedPaths,
                "The staged source was rejected before publication: "
                + Bound(string.Join(" | ", validation.Errors), 4096));
            await WriteReceiptAsync(rejected, cancellationToken).ConfigureAwait(false);
            Inject(AliSourceTransactionBoundary.TerminalReceiptPersisted);
            return rejected;
        }

        AliSourceWindowsNamespace sourceNamespace;
        AliSourceBoundOperationSet boundOperations;
        try
        {
            sourceNamespace = AliSourceWindowsNamespace.OpenAuthenticated(changeSet);
            try
            {
                boundOperations = await BindPreimagesAsync(
                        sourceNamespace,
                        changeSet,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                sourceNamespace.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            var stale = CreateReceipt(
                changeSet,
                grant,
                AliSourcePublicationState.RolledBack,
                AliSourceJournalHead.Empty,
                affectedPaths,
                "The authenticated source root, namespace spine, or exact preimage is stale; canonical source was not changed.");
            await WriteReceiptAsync(stale, cancellationToken).ConfigureAwait(false);
            Inject(AliSourceTransactionBoundary.TerminalReceiptPersisted);
            return stale;
        }

        using (sourceNamespace)
        using (boundOperations)
        {
            var prepared = CreateReceipt(
                changeSet,
                grant,
                AliSourcePublicationState.Prepared,
                AliSourceJournalHead.Empty,
                affectedPaths,
                "The source transaction is prepared; canonical source is unchanged.");
            await WriteReceiptAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            Inject(AliSourceTransactionBoundary.PreparedReceiptPersisted);

            var head = AliSourceJournalHead.Empty;
            try
            {
                foreach (var operation in changeSet.Operations)
                {
                    head = await AppendJournalAsync(
                        changeSet,
                        head,
                        AliSourceJournalEventKind.OperationIntent,
                        operation.Sequence,
                        terminalState: null,
                        canonicalObjectIdentity: null).ConfigureAwait(false);
                    Inject(AliSourceTransactionBoundary.OperationIntentPersisted, operation.Sequence);

                    var appliedIdentity = await ApplyOperationAsync(
                            sourceNamespace,
                            changeSet,
                            operation,
                            boundOperations[operation.Sequence])
                        .ConfigureAwait(false);
                    Inject(AliSourceTransactionBoundary.OperationMutationCompleted, operation.Sequence);

                    head = await AppendJournalAsync(
                        changeSet,
                        head,
                        AliSourceJournalEventKind.OperationApplied,
                        operation.Sequence,
                        terminalState: null,
                        canonicalObjectIdentity: appliedIdentity).ConfigureAwait(false);
                    Inject(AliSourceTransactionBoundary.OperationAppliedPersisted, operation.Sequence);

                    await RequireBoundOperationStateAsync(
                        sourceNamespace,
                        operation,
                        boundOperations[operation.Sequence],
                        AliSourceOperationState.Postimage,
                        CancellationToken.None).ConfigureAwait(false);
                    head = await AppendJournalAsync(
                        changeSet,
                        head,
                        AliSourceJournalEventKind.OperationVerified,
                        operation.Sequence,
                        terminalState: null,
                        canonicalObjectIdentity: appliedIdentity).ConfigureAwait(false);
                    Inject(AliSourceTransactionBoundary.OperationVerifiedPersisted, operation.Sequence);
                }

                foreach (var operation in changeSet.Operations)
                {
                    await RequireBoundOperationStateAsync(
                            sourceNamespace,
                            operation,
                            boundOperations[operation.Sequence],
                            AliSourceOperationState.Postimage,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                head = await AppendJournalAsync(
                    changeSet,
                    head,
                    AliSourceJournalEventKind.Terminal,
                    operationSequence: -1,
                    terminalState: AliSourcePublicationState.Committed,
                    canonicalObjectIdentity: null).ConfigureAwait(false);
                Inject(AliSourceTransactionBoundary.TerminalJournalPersisted);
                var committed = CreateReceipt(
                    changeSet,
                    grant,
                    AliSourcePublicationState.Committed,
                    head,
                    affectedPaths,
                    $"Published and identity/hash-verified {changeSet.Operations.Length} source operation(s).");
                await WriteReceiptAsync(committed, CancellationToken.None).ConfigureAwait(false);
                Inject(AliSourceTransactionBoundary.TerminalReceiptPersisted);
                return committed;
            }
            catch (AliSourceSimulatedInterruptionException)
            {
                throw;
            }
            catch (Exception publicationFailure)
            {
                AliSourcePublicationReceipt terminal;
                try
                {
                    var durable = await ReadJournalSnapshotAsync(changeSet, CancellationToken.None)
                        .ConfigureAwait(false);
                    head = durable.Head;
                    var rollback = await RollbackUnderLeaseAsync(
                            changeSet,
                            sourceNamespace,
                            boundOperations,
                            durable,
                            allowUnjournaledLiveBindings: true,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    head = rollback.Head;
                    var terminalState = rollback.Complete
                        ? AliSourcePublicationState.RolledBack
                        : AliSourcePublicationState.InDoubt;
                    head = await AppendJournalAsync(
                        changeSet,
                        head,
                        AliSourceJournalEventKind.Terminal,
                        operationSequence: -1,
                        terminalState: terminalState,
                        canonicalObjectIdentity: null).ConfigureAwait(false);
                    Inject(AliSourceTransactionBoundary.TerminalJournalPersisted);
                    terminal = CreateReceipt(
                        changeSet,
                        grant,
                        terminalState,
                        head,
                        affectedPaths,
                        rollback.Complete
                            ? "Publication failed; every canonical source operation was restored and identity/hash-verified."
                            : "Publication failed; exact rollback could not be proven and the transaction is in doubt.");
                    await WriteReceiptAsync(terminal, CancellationToken.None).ConfigureAwait(false);
                    Inject(AliSourceTransactionBoundary.TerminalReceiptPersisted);
                }
                catch (AliSourceSimulatedInterruptionException)
                {
                    throw;
                }
                catch (Exception rollbackFailure)
                {
                    terminal = CreateReceipt(
                        changeSet,
                        grant,
                        AliSourcePublicationState.InDoubt,
                        head,
                        affectedPaths,
                        "Publication failed; rollback identity could not be proven and the transaction is in doubt.");
                    try
                    {
                        await WriteReceiptAsync(terminal, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The authenticated Prepared receipt and journal remain the recovery authority.
                    }
                    publicationFailure = new AggregateException(publicationFailure, rollbackFailure);
                }

                if (publicationFailure is OperationCanceledException
                    && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                throw new AliSourcePublicationException(
                    terminal.Summary,
                    terminal,
                    publicationFailure);
            }
        }
    }

    internal async Task<AliSourcePublicationReceipt?> ReadReceiptAsync(
        string changeSetId,
        CancellationToken cancellationToken)
    {
        var receipt = await _store.ReadAuthenticatedDocumentAsync<AliSourcePublicationReceipt>(
            _store.GetReceiptPath(changeSetId),
            "publication-receipt",
            AliSourceChangeSetStore.MaximumReceiptBytes,
            cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }
        ValidateReceipt(receipt, changeSetId);
        return receipt;
    }

    internal async Task WriteReceiptAsync(
        AliSourcePublicationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ValidateReceipt(receipt, receipt.ChangeSetId);
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _store.GetTransactionDirectory(receipt.ChangeSetId),
            "The source transaction directory is not a regular local directory.");
        await _store.WriteAuthenticatedDocumentAsync(
            _store.GetReceiptPath(receipt.ChangeSetId),
            "publication-receipt",
            receipt,
            AliSourceChangeSetStore.MaximumReceiptBytes,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FileStream> AcquirePublicationLeaseAsync(
        string canonicalSourceRoot,
        CancellationToken cancellationToken)
    {
        var lockPath = _store.GetRootLeasePath(canonicalSourceRoot);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return WindowsOrchestrationFileBoundary.OpenRegularFile(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    writeThrough: true,
                    "The source publication lease is not a regular local file.");
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<AliSourceJournalHead> ReadJournalAsync(
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken) =>
        (await ReadJournalSnapshotAsync(changeSet, cancellationToken).ConfigureAwait(false)).Head;

    internal async Task<AliSourceJournalSnapshot> ReadJournalSnapshotAsync(
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        var path = _store.GetJournalPath(changeSet.Id);
        if (!File.Exists(path))
        {
            return AliSourceJournalSnapshot.Empty;
        }
        await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            writeThrough: true,
            "The source transaction journal is not a regular local file.");
        var maximumJournalBytes = checked(
            (long)AliSourceChangeSetStore.MaximumJournalLineBytes
            * (changeSet.Operations.Length * 7L + 4));
        if (stream.Length < 0 || stream.Length > maximumJournalBytes)
        {
            throw new InvalidDataException("The source transaction journal is unbounded.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            if (bytes.Length > 0)
            {
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            var committedLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (committedLength < bytes.Length)
            {
                stream.SetLength(committedLength);
                stream.Flush(flushToDisk: true);
            }
            if (committedLength == 0)
            {
                return AliSourceJournalSnapshot.Empty;
            }

            var head = AliSourceJournalHead.Empty;
            var proofs = ImmutableDictionary.CreateBuilder<int, AliSourceOperationJournalProof>();
            var start = 0;
            while (start < committedLength)
            {
                var end = Array.IndexOf(bytes, (byte)'\n', start, committedLength - start);
                if (end < 0)
                {
                    break;
                }
                var line = bytes.AsMemory(start, end - start);
                if (line.Length is <= 0 or > AliSourceChangeSetStore.MaximumJournalLineBytes)
                {
                    throw new InvalidDataException("A source transaction journal entry is invalid or unbounded.");
                }
                AliSourceJournalEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<AliSourceJournalEnvelope>(line.Span, JournalJsonOptions)
                        ?? throw new InvalidDataException("A source transaction journal entry could not be read.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("A source transaction journal entry is malformed.", ex);
                }
                ValidateJournalEntry(changeSet, head, envelope.Entry);
                var unsigned = JsonSerializer.SerializeToUtf8Bytes(envelope.Entry, JournalJsonOptions);
                try
                {
                    if (!await _store.VerifyAuthenticationAsync(
                            "publication-journal",
                            unsigned,
                            envelope.AuthenticationTag,
                            cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("A source transaction journal entry failed authentication.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(unsigned);
                }
                head = new AliSourceJournalHead(envelope.Entry.Sequence, envelope.AuthenticationTag);
                ApplyJournalProof(proofs, envelope.Entry);
                start = end + 1;
            }
            return new AliSourceJournalSnapshot(head, proofs.ToImmutable());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal async Task<AliSourceRollbackResult> RollbackUnderLeaseAsync(
        AliSourceChangeSet changeSet,
        AliSourceJournalHead initialHead,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadJournalSnapshotAsync(changeSet, cancellationToken).ConfigureAwait(false);
        if (snapshot.Head != initialHead)
        {
            return new AliSourceRollbackResult(false, snapshot.Head);
        }
        AliSourceWindowsNamespace sourceNamespace;
        AliSourceBoundOperationSet boundOperations;
        try
        {
            sourceNamespace = AliSourceWindowsNamespace.OpenAuthenticated(changeSet);
            try
            {
                boundOperations = await BindRecoveryStateAsync(
                        sourceNamespace,
                        changeSet,
                        snapshot,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                sourceNamespace.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            return new AliSourceRollbackResult(false, snapshot.Head);
        }

        using (sourceNamespace)
        using (boundOperations)
        {
            return await RollbackUnderLeaseAsync(
                    changeSet,
                    sourceNamespace,
                    boundOperations,
                    snapshot,
                    allowUnjournaledLiveBindings: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task<AliSourceRecoveryBinding> BindRecoveryUnderLeaseAsync(
        AliSourceChangeSet changeSet,
        AliSourceJournalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var sourceNamespace = AliSourceWindowsNamespace.OpenAuthenticated(changeSet);
        try
        {
            var operations = await BindRecoveryStateAsync(
                    sourceNamespace,
                    changeSet,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            return new AliSourceRecoveryBinding(sourceNamespace, operations, snapshot);
        }
        catch
        {
            sourceNamespace.Dispose();
            throw;
        }
    }

    internal Task<AliSourceRollbackResult> RollbackUnderLeaseAsync(
        AliSourceChangeSet changeSet,
        AliSourceRecoveryBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return RollbackUnderLeaseAsync(
            changeSet,
            binding.SourceNamespace,
            binding.Operations,
            binding.Snapshot,
            allowUnjournaledLiveBindings: false,
            cancellationToken);
    }

    internal async Task<bool> ReverifyRecoveryBindingAsync(
        AliSourceChangeSet changeSet,
        AliSourceRecoveryBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Operations.Count != changeSet.Operations.Length)
        {
            return false;
        }

        try
        {
            foreach (var operation in changeSet.Operations)
            {
                var state = binding.Operations[operation.Sequence].State;
                if (state == AliSourceOperationState.Unknown)
                {
                    return false;
                }
                await RequireBoundOperationStateAsync(
                        binding.SourceNamespace,
                        operation,
                        binding.Operations[operation.Sequence],
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            return false;
        }
    }

    private async Task<AliSourceRollbackResult> RollbackUnderLeaseAsync(
        AliSourceChangeSet changeSet,
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceBoundOperationSet boundOperations,
        AliSourceJournalSnapshot snapshot,
        bool allowUnjournaledLiveBindings,
        CancellationToken cancellationToken)
    {
        var head = snapshot.Head;
        for (var index = changeSet.Operations.Length - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = changeSet.Operations[index];
            var bound = boundOperations[index];
            if (bound.State == AliSourceOperationState.Preimage)
            {
                continue;
            }
            if (bound.State != AliSourceOperationState.Postimage
                && !(allowUnjournaledLiveBindings && bound.MutationBegan))
            {
                return new AliSourceRollbackResult(false, head);
            }

            head = await AppendJournalAsync(
                changeSet,
                head,
                AliSourceJournalEventKind.RollbackIntent,
                operation.Sequence,
                terminalState: null,
                canonicalObjectIdentity: null).ConfigureAwait(false);
            Inject(AliSourceTransactionBoundary.RollbackIntentPersisted, operation.Sequence);

            AliSourceFileIdentity? rollbackIdentity;
            try
            {
                rollbackIdentity = await RollbackOperationAsync(
                        sourceNamespace,
                        changeSet,
                        operation,
                        bound,
                        allowUnjournaledLiveBindings)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsNamespaceBindingFailure(exception))
            {
                return new AliSourceRollbackResult(false, head);
            }
            Inject(AliSourceTransactionBoundary.RollbackMutationCompleted, operation.Sequence);

            head = await AppendJournalAsync(
                changeSet,
                head,
                AliSourceJournalEventKind.RollbackApplied,
                operation.Sequence,
                terminalState: null,
                canonicalObjectIdentity: rollbackIdentity).ConfigureAwait(false);
            Inject(AliSourceTransactionBoundary.RollbackAppliedPersisted, operation.Sequence);

            await RequireBoundOperationStateAsync(
                sourceNamespace,
                operation,
                bound,
                AliSourceOperationState.Preimage,
                CancellationToken.None).ConfigureAwait(false);
            head = await AppendJournalAsync(
                changeSet,
                head,
                AliSourceJournalEventKind.RollbackVerified,
                operation.Sequence,
                terminalState: null,
                canonicalObjectIdentity: rollbackIdentity).ConfigureAwait(false);
            Inject(AliSourceTransactionBoundary.RollbackVerifiedPersisted, operation.Sequence);
        }

        if (!boundOperations.All(operation => operation.State == AliSourceOperationState.Preimage))
        {
            return new AliSourceRollbackResult(false, head);
        }
        try
        {
            foreach (var operation in changeSet.Operations)
            {
                await RequireBoundOperationStateAsync(
                        sourceNamespace,
                        operation,
                        boundOperations[operation.Sequence],
                        AliSourceOperationState.Preimage,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            return new AliSourceRollbackResult(false, head);
        }
        return new AliSourceRollbackResult(true, head);
    }

    internal AliSourcePublicationReceipt CreateRecoveredReceipt(
        AliSourceChangeSet changeSet,
        AliSourcePublicationReceipt? prepared,
        AliSourcePublicationState state,
        AliSourceJournalHead head,
        string summary)
    {
        var grantId = prepared?.GrantId ?? "recovery" + changeSet.Id[..24];
        var authorization = prepared?.AuthorizationBindingDigest ?? changeSet.ManifestDigest;
        return new AliSourcePublicationReceipt(
            changeSet.Id,
            changeSet.ManifestDigest,
            grantId,
            authorization,
            state,
            DateTimeOffset.UtcNow,
            head.Sequence,
            head.AuthenticationTag,
            AffectedPaths(changeSet),
            Bound(summary, 4096));
    }

    internal async Task<AliSourceJournalHead> AppendTerminalAsync(
        AliSourceChangeSet changeSet,
        AliSourceJournalHead head,
        AliSourcePublicationState state) =>
        await AppendJournalAsync(
            changeSet,
            head,
            AliSourceJournalEventKind.Terminal,
            operationSequence: -1,
            terminalState: state,
            canonicalObjectIdentity: null).ConfigureAwait(false);

    internal Task CleanupStagedFilesAsync(AliSourceChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        // Source publication no longer creates path-addressed staging files. Canonical leaves are
        // written, renamed, or deleted only through exact handles held by the namespace transaction.
        return Task.CompletedTask;
    }

    private static async Task<AliSourceBoundOperationSet> BindPreimagesAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        var result = new AliSourceBoundOperationSet(changeSet.Operations.Length);
        try
        {
            foreach (var operation in changeSet.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bound = new AliSourceBoundOperation(operation.Sequence);
                result[operation.Sequence] = bound;
                switch (operation.Kind)
                {
                    case AliSourceChangeKind.Add:
                        if (!sourceNamespace.IsAbsent(operation.SourceRelativePath))
                        {
                            throw new InvalidDataException("An authenticated add target is no longer absent.");
                        }
                        bound.SetPreimage(file: null, identity: null);
                        break;
                    case AliSourceChangeKind.Replace:
                    case AliSourceChangeKind.Delete:
                    case AliSourceChangeKind.Rename:
                    {
                        var access = operation.Kind == AliSourceChangeKind.Replace
                            ? AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Write
                            : AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Delete;
                        var file = sourceNamespace.TryOpenExisting(
                            operation.SourceRelativePath,
                            access,
                            operation.SourcePreimageIdentity)
                            ?? throw new InvalidDataException("An authenticated source preimage disappeared.");
                        bound.SetPreimage(file, file.Identity);
                        var image = await sourceNamespace.CaptureImageAsync(file, cancellationToken)
                            .ConfigureAwait(false);
                        if (!operation.SourcePreimage.Matches(image))
                        {
                            throw new InvalidDataException("An authenticated source preimage changed.");
                        }
                        if (operation.Kind == AliSourceChangeKind.Rename
                            && !sourceNamespace.IsAbsent(operation.DestinationRelativePath!))
                        {
                            throw new InvalidDataException("An authenticated rename destination is no longer absent.");
                        }
                        break;
                    }
                    default:
                        throw new InvalidDataException("The source publication operation kind is invalid.");
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static async Task<AliSourceBoundOperationSet> BindRecoveryStateAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeSet changeSet,
        AliSourceJournalSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var result = new AliSourceBoundOperationSet(changeSet.Operations.Length);
        try
        {
            foreach (var operation in changeSet.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var proof = snapshot.ProofFor(operation.Sequence);
                AliSourceBoundOperation bound;
                if (proof.RollbackApplied)
                {
                    bound = await TryBindPreimageAsync(
                            sourceNamespace,
                            operation,
                            operation.Kind == AliSourceChangeKind.Delete
                                ? proof.RollbackIdentity
                                : operation.SourcePreimageIdentity,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? new AliSourceBoundOperation(operation.Sequence);
                }
                else
                {
                    bound = await TryBindPreimageAsync(
                            sourceNamespace,
                            operation,
                            operation.SourcePreimageIdentity,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? (proof.ForwardApplied
                            ? await TryBindPostimageAsync(
                                    sourceNamespace,
                                    operation,
                                    proof.ForwardIdentity,
                                    cancellationToken)
                                .ConfigureAwait(false)
                            : null)
                        ?? new AliSourceBoundOperation(operation.Sequence);
                }
                result[operation.Sequence] = bound;
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static async Task<AliSourceBoundOperation?> TryBindPreimageAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeOperation operation,
        AliSourceFileIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Kind == AliSourceChangeKind.Add)
            {
                if (!sourceNamespace.IsAbsent(operation.SourceRelativePath))
                {
                    return null;
                }
                var absent = new AliSourceBoundOperation(operation.Sequence);
                absent.SetPreimage(file: null, identity: null);
                return absent;
            }

            var access = operation.Kind == AliSourceChangeKind.Replace
                ? AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Write
                : AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Delete;
            var file = sourceNamespace.TryOpenExisting(
                operation.SourceRelativePath,
                access,
                expectedIdentity);
            if (file is null)
            {
                return null;
            }
            var image = await sourceNamespace.CaptureImageAsync(file, cancellationToken)
                .ConfigureAwait(false);
            if (!operation.SourcePreimage.Matches(image)
                || (operation.Kind == AliSourceChangeKind.Rename
                    && !sourceNamespace.IsAbsent(operation.DestinationRelativePath!)))
            {
                file.Dispose();
                return null;
            }
            var result = new AliSourceBoundOperation(operation.Sequence);
            result.SetPreimage(file, file.Identity);
            return result;
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            return null;
        }
    }

    private static async Task<AliSourceBoundOperation?> TryBindPostimageAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeOperation operation,
        AliSourceFileIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            if (operation.Kind == AliSourceChangeKind.Delete)
            {
                if (!sourceNamespace.IsAbsent(operation.SourceRelativePath))
                {
                    return null;
                }
                var deleted = new AliSourceBoundOperation(operation.Sequence);
                deleted.SetPostimage(file: null, identity: null);
                return deleted;
            }
            if (expectedIdentity is null)
            {
                return null;
            }
            var relativePath = operation.Kind == AliSourceChangeKind.Rename
                ? operation.DestinationRelativePath!
                : operation.SourceRelativePath;
            if (operation.Kind == AliSourceChangeKind.Rename
                && !sourceNamespace.IsAbsent(operation.SourceRelativePath))
            {
                return null;
            }
            var access = operation.Kind == AliSourceChangeKind.Replace
                ? AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Write
                : AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Delete;
            var file = sourceNamespace.TryOpenExisting(relativePath, access, expectedIdentity);
            if (file is null)
            {
                return null;
            }
            var image = await sourceNamespace.CaptureImageAsync(file, cancellationToken)
                .ConfigureAwait(false);
            var expectedImage = operation.Kind == AliSourceChangeKind.Rename
                ? operation.DestinationPostimage!
                : operation.SourcePostimage;
            if (!expectedImage.Matches(image))
            {
                file.Dispose();
                return null;
            }
            var result = new AliSourceBoundOperation(operation.Sequence);
            result.SetPostimage(file, file.Identity);
            return result;
        }
        catch (Exception exception) when (IsNamespaceBindingFailure(exception))
        {
            return null;
        }
    }

    private async Task<AliSourceFileIdentity?> ApplyOperationAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        AliSourceBoundOperation bound)
    {
        if (bound.State != AliSourceOperationState.Preimage)
        {
            throw new InvalidDataException("A source operation lost its exact preimage binding.");
        }
        switch (operation.Kind)
        {
            case AliSourceChangeKind.Add:
            {
                var created = sourceNamespace.CreateNew(operation.SourceRelativePath);
                bound.BeginMutation(created);
                var content = await _store.ReadContentAsync(changeSet, operation, CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await sourceNamespace.WriteExactAsync(created, content, CancellationToken.None)
                        .ConfigureAwait(false);
                    sourceNamespace.RequireBoundAt(created, operation.SourceRelativePath);
                    bound.SetPostimage(created, created.Identity);
                    return created.Identity;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }
            case AliSourceChangeKind.Replace:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A replacement has no exact source handle.");
                bound.BeginMutation(file);
                var content = await _store.ReadContentAsync(changeSet, operation, CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await sourceNamespace.WriteExactAsync(file, content, CancellationToken.None)
                        .ConfigureAwait(false);
                    sourceNamespace.RequireBoundAt(file, operation.SourceRelativePath);
                    bound.SetPostimage(file, file.Identity);
                    return file.Identity;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }
            case AliSourceChangeKind.Delete:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A deletion has no exact source handle.");
                bound.BeginMutation(file);
                sourceNamespace.Delete(file);
                bound.SetPostimage(file, identity: null);
                return null;
            }
            case AliSourceChangeKind.Rename:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A rename has no exact source handle.");
                bound.BeginMutation(file);
                var identity = sourceNamespace.Rename(file, operation.DestinationRelativePath!);
                bound.SetPostimage(file, identity);
                return identity;
            }
            default:
                throw new InvalidDataException("The source publication operation kind is invalid.");
        }
    }

    private async Task<AliSourceFileIdentity?> RollbackOperationAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        AliSourceBoundOperation bound,
        bool allowUnjournaledLiveBinding)
    {
        switch (operation.Kind)
        {
            case AliSourceChangeKind.Add:
            {
                var file = bound.File;
                if (file is null)
                {
                    return allowUnjournaledLiveBinding && sourceNamespace.IsAbsent(operation.SourceRelativePath)
                        ? null
                        : throw new InvalidDataException("An added source object identity is missing.");
                }
                sourceNamespace.Delete(file);
                bound.ReleaseFile();
                bound.SetPreimage(file: null, identity: null);
                return null;
            }
            case AliSourceChangeKind.Replace:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A replacement rollback object identity is missing.");
                var backup = await _store.ReadBackupAsync(changeSet, operation, CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await sourceNamespace.WriteExactAsync(file, backup, CancellationToken.None)
                        .ConfigureAwait(false);
                    sourceNamespace.RequireBoundAt(file, operation.SourceRelativePath);
                    bound.SetPreimage(file, file.Identity);
                    return file.Identity;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(backup);
                }
            }
            case AliSourceChangeKind.Delete:
            {
                if (bound.File is not null && allowUnjournaledLiveBinding)
                {
                    sourceNamespace.CancelDelete(bound.File);
                    sourceNamespace.RequireBoundAt(bound.File, operation.SourceRelativePath);
                    bound.SetPreimage(bound.File, bound.File.Identity);
                    return bound.File.Identity;
                }
                var restored = sourceNamespace.CreateNew(operation.SourceRelativePath);
                bound.BeginMutation(restored);
                var backup = await _store.ReadBackupAsync(changeSet, operation, CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await sourceNamespace.WriteExactAsync(restored, backup, CancellationToken.None)
                        .ConfigureAwait(false);
                    sourceNamespace.RequireBoundAt(restored, operation.SourceRelativePath);
                    bound.SetPreimage(restored, restored.Identity);
                    return restored.Identity;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(backup);
                }
            }
            case AliSourceChangeKind.Rename:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A rename rollback object identity is missing.");
                if (allowUnjournaledLiveBinding
                    && sourceNamespace.IsBoundAt(file, operation.SourceRelativePath))
                {
                    bound.SetPreimage(file, file.Identity);
                    return file.Identity;
                }
                if (!sourceNamespace.IsBoundAt(file, operation.DestinationRelativePath!))
                {
                    throw new InvalidDataException("A renamed source object is bound under an ambiguous name.");
                }
                var identity = sourceNamespace.Rename(file, operation.SourceRelativePath);
                bound.SetPreimage(file, identity);
                return identity;
            }
            default:
                throw new InvalidDataException("The source rollback operation kind is invalid.");
        }
    }

    private static async Task RequireBoundOperationStateAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeOperation operation,
        AliSourceBoundOperation bound,
        AliSourceOperationState required,
        CancellationToken cancellationToken)
    {
        AliSourceFileImage actual;
        AliSourceFileImage expected;
        if (required == AliSourceOperationState.Preimage)
        {
            if (operation.Kind == AliSourceChangeKind.Add)
            {
                if (!sourceNamespace.IsAbsent(operation.SourceRelativePath))
                {
                    throw new IOException("An added source path was not removed by rollback.");
                }
                actual = AliSourceFileImage.Absent;
            }
            else
            {
                var file = bound.File
                    ?? throw new IOException("A restored source object identity is missing.");
                sourceNamespace.RequireBoundAt(file, operation.SourceRelativePath);
                actual = await sourceNamespace.CaptureImageAsync(file, cancellationToken)
                    .ConfigureAwait(false);
                if (operation.Kind == AliSourceChangeKind.Rename
                    && !sourceNamespace.IsAbsent(operation.DestinationRelativePath!))
                {
                    throw new IOException("A rename destination remained after rollback.");
                }
            }
            expected = operation.SourcePreimage;
        }
        else if (required == AliSourceOperationState.Postimage)
        {
            if (operation.Kind == AliSourceChangeKind.Delete)
            {
                if (bound.File is not null)
                {
                    if (!sourceNamespace.IsDeletePending(bound.File))
                    {
                        throw new IOException("The exact deleted source object is not delete-pending.");
                    }
                }
                else if (!sourceNamespace.IsAbsent(operation.SourceRelativePath))
                {
                    throw new IOException("A deleted source path remained in the namespace.");
                }
                actual = AliSourceFileImage.Absent;
                expected = operation.SourcePostimage;
            }
            else
            {
                var file = bound.File
                    ?? throw new IOException("A published source object identity is missing.");
                var relativePath = operation.Kind == AliSourceChangeKind.Rename
                    ? operation.DestinationRelativePath!
                    : operation.SourceRelativePath;
                sourceNamespace.RequireBoundAt(file, relativePath);
                actual = await sourceNamespace.CaptureImageAsync(file, cancellationToken)
                    .ConfigureAwait(false);
                expected = operation.Kind == AliSourceChangeKind.Rename
                    ? operation.DestinationPostimage!
                    : operation.SourcePostimage;
                if (operation.Kind == AliSourceChangeKind.Rename
                    && !sourceNamespace.IsAbsent(operation.SourceRelativePath))
                {
                    throw new IOException("A rename source remained after publication.");
                }
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(required));
        }
        if (!expected.Matches(actual))
        {
            throw new IOException(
                $"Source operation {operation.Sequence} did not reach its required identity/hash state.");
        }
        bound.SetState(required);
    }

    private async Task<AliSourceJournalHead> AppendJournalAsync(
        AliSourceChangeSet changeSet,
        AliSourceJournalHead previous,
        AliSourceJournalEventKind kind,
        int operationSequence,
        AliSourcePublicationState? terminalState,
        AliSourceFileIdentity? canonicalObjectIdentity)
    {
        var entry = new AliSourceJournalEntry(
            previous.Sequence + 1,
            kind,
            operationSequence,
            terminalState,
            DateTimeOffset.UtcNow,
            changeSet.ManifestDigest,
            previous.AuthenticationTag,
            canonicalObjectIdentity);
        ValidateJournalEntry(changeSet, previous, entry);
        var unsigned = JsonSerializer.SerializeToUtf8Bytes(entry, JournalJsonOptions);
        try
        {
            var authenticationTag = await _store.AuthenticateAsync(
                "publication-journal",
                unsigned,
                CancellationToken.None).ConfigureAwait(false);
            var line = JsonSerializer.SerializeToUtf8Bytes(
                new AliSourceJournalEnvelope(entry, authenticationTag),
                JournalJsonOptions);
            try
            {
                if (line.Length is <= 0 or > AliSourceChangeSetStore.MaximumJournalLineBytes)
                {
                    throw new InvalidDataException("A source transaction journal entry is invalid or unbounded.");
                }
                WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
                    _store.GetTransactionDirectory(changeSet.Id),
                    "The source transaction directory is not a regular local directory.");
                await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                    _store.GetJournalPath(changeSet.Id),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    writeThrough: true,
                    "The source transaction journal is not a regular local file.");
                stream.Position = stream.Length;
                await stream.WriteAsync(line, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                await stream.WriteAsync("\n"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                return new AliSourceJournalHead(entry.Sequence, authenticationTag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(line);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unsigned);
        }
    }

    private static AliSourcePublicationReceipt CreateReceipt(
        AliSourceChangeSet changeSet,
        AliSourcePublicationGrant grant,
        AliSourcePublicationState state,
        AliSourceJournalHead head,
        ImmutableArray<string> affectedPaths,
        string summary) =>
        new(
            changeSet.Id,
            changeSet.ManifestDigest,
            grant.GrantId,
            grant.AuthorizationBindingDigest,
            state,
            DateTimeOffset.UtcNow,
            head.Sequence,
            head.AuthenticationTag,
            affectedPaths,
            Bound(summary, 4096));

    private static ImmutableArray<string> AffectedPaths(AliSourceChangeSet changeSet) =>
        changeSet.Operations
            .SelectMany(operation => operation.DestinationRelativePath is null
                ? [operation.SourceRelativePath]
                : new[] { operation.SourceRelativePath, operation.DestinationRelativePath })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static void ValidateReceipt(AliSourcePublicationReceipt receipt, string requestedId)
    {
        if (!string.Equals(receipt.ChangeSetId, requestedId, StringComparison.Ordinal)
            || receipt.ChangeSetId.Length != 32
            || receipt.GrantId.Length is <= 0 or > 64
            || receipt.GrantId.Any(character => !char.IsAsciiLetterOrDigit(character))
            || !Enum.IsDefined(receipt.State)
            || receipt.UpdatedAtUtc.Offset != TimeSpan.Zero
            || receipt.JournalSequence < 0
            || receipt.AffectedPaths.Length > AliSourceChangeSetStore.MaximumOperations * 2
            || string.IsNullOrWhiteSpace(receipt.Summary)
            || receipt.Summary.Length > 4110)
        {
            throw new InvalidDataException("A source publication receipt is invalid or unbounded.");
        }
        _ = AliSourceChangeSetStore.NormalizeSha256(receipt.ManifestDigest);
        _ = AliSourceChangeSetStore.NormalizeSha256(receipt.AuthorizationBindingDigest);
        _ = AliSourceChangeSetStore.NormalizeSha256(receipt.JournalAuthenticationTag);
        foreach (var path in receipt.AffectedPaths)
        {
            if (!string.Equals(
                    AliSourceChangeSetStore.NormalizeRelativePath(path),
                    path,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("A source publication receipt path is not canonical.");
            }
        }
    }

    private static void ValidateJournalEntry(
        AliSourceChangeSet changeSet,
        AliSourceJournalHead previous,
        AliSourceJournalEntry entry)
    {
        if (entry.Sequence != previous.Sequence + 1
            || !Enum.IsDefined(entry.Kind)
            || entry.TimestampUtc.Offset != TimeSpan.Zero
            || !string.Equals(entry.ManifestDigest, changeSet.ManifestDigest, StringComparison.Ordinal)
            || !string.Equals(entry.PreviousAuthenticationTag, previous.AuthenticationTag, StringComparison.Ordinal)
            || (entry.Kind == AliSourceJournalEventKind.Terminal) != entry.TerminalState.HasValue
            || (entry.TerminalState.HasValue
                && entry.TerminalState.Value is not (
                    AliSourcePublicationState.Committed
                    or AliSourcePublicationState.RolledBack
                    or AliSourcePublicationState.InDoubt))
            || (entry.Kind == AliSourceJournalEventKind.Terminal
                ? entry.OperationSequence != -1
                : entry.OperationSequence < 0 || entry.OperationSequence >= changeSet.Operations.Length))
        {
            throw new InvalidDataException("A source transaction journal entry is inconsistent.");
        }

        var identityRequired = false;
        if (entry.Kind is AliSourceJournalEventKind.OperationApplied
            or AliSourceJournalEventKind.OperationVerified)
        {
            identityRequired = changeSet.Operations[entry.OperationSequence].Kind != AliSourceChangeKind.Delete;
        }
        else if (entry.Kind is AliSourceJournalEventKind.RollbackApplied
                 or AliSourceJournalEventKind.RollbackVerified)
        {
            identityRequired = changeSet.Operations[entry.OperationSequence].Kind != AliSourceChangeKind.Add;
        }
        if (identityRequired != (entry.CanonicalObjectIdentity is not null))
        {
            throw new InvalidDataException("A source journal object identity is missing or unexpected.");
        }
        if (entry.CanonicalObjectIdentity is not null)
        {
            entry.CanonicalObjectIdentity.Validate("source journal object");
            if (entry.CanonicalObjectIdentity.VolumeSerialNumber
                    != changeSet.SourceRootIdentity.VolumeSerialNumber
                || entry.CanonicalObjectIdentity.NumberOfLinks != 1)
            {
                throw new InvalidDataException("A source journal object identity is cross-volume or ambiguous.");
            }
            var operation = changeSet.Operations[entry.OperationSequence];
            var existingObjectOperation = operation.Kind is AliSourceChangeKind.Replace
                or AliSourceChangeKind.Rename;
            var forwardObjectProof = entry.Kind is AliSourceJournalEventKind.OperationApplied
                or AliSourceJournalEventKind.OperationVerified;
            var rollbackObjectProof = entry.Kind is AliSourceJournalEventKind.RollbackApplied
                or AliSourceJournalEventKind.RollbackVerified;
            if (existingObjectOperation && (forwardObjectProof || rollbackObjectProof))
            {
                if (operation.SourcePreimageIdentity is null
                    || !operation.SourcePreimageIdentity.SameObject(entry.CanonicalObjectIdentity))
                {
                    throw new InvalidDataException("A source journal rebound an existing physical object.");
                }
            }
        }
    }

    private static void ApplyJournalProof(
        ImmutableDictionary<int, AliSourceOperationJournalProof>.Builder proofs,
        AliSourceJournalEntry entry)
    {
        if (entry.OperationSequence < 0)
        {
            return;
        }
        var current = proofs.TryGetValue(entry.OperationSequence, out var existing)
            ? existing
            : new AliSourceOperationJournalProof(false, null, false, null);
        switch (entry.Kind)
        {
            case AliSourceJournalEventKind.OperationApplied:
                if (current.ForwardApplied
                    && !IdentityEquals(current.ForwardIdentity, entry.CanonicalObjectIdentity))
                {
                    throw new InvalidDataException("A source journal rebound an applied operation.");
                }
                current = current with
                {
                    ForwardApplied = true,
                    ForwardIdentity = entry.CanonicalObjectIdentity
                };
                break;
            case AliSourceJournalEventKind.OperationVerified:
                if (!current.ForwardApplied
                    || !IdentityEquals(current.ForwardIdentity, entry.CanonicalObjectIdentity))
                {
                    throw new InvalidDataException("A source journal verified an unapplied or rebound operation.");
                }
                break;
            case AliSourceJournalEventKind.RollbackApplied:
                if (current.RollbackApplied
                    && !IdentityEquals(current.RollbackIdentity, entry.CanonicalObjectIdentity))
                {
                    throw new InvalidDataException("A source journal rebound an applied rollback.");
                }
                current = current with
                {
                    RollbackApplied = true,
                    RollbackIdentity = entry.CanonicalObjectIdentity
                };
                break;
            case AliSourceJournalEventKind.RollbackVerified:
                if (!current.RollbackApplied
                    || !IdentityEquals(current.RollbackIdentity, entry.CanonicalObjectIdentity))
                {
                    throw new InvalidDataException("A source journal verified an unapplied or rebound rollback.");
                }
                break;
        }
        proofs[entry.OperationSequence] = current;
    }

    private static bool IdentityEquals(
        AliSourceFileIdentity? left,
        AliSourceFileIdentity? right) =>
        left is null
            ? right is null
            : right is not null && left.MatchesExactNamespace(right);

    private void Inject(AliSourceTransactionBoundary boundary, int operationSequence = -1) =>
        _faultInjector?.Invoke(new AliSourceTransactionFault(boundary, operationSequence));

    private static string Bound(string value, int maximumCharacters)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Source transaction state recorded." : value.Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + " [bounded]";
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var error = exception.HResult & 0xFFFF;
        return error is 32 or 33
            || exception.InnerException is Win32Exception { NativeErrorCode: 32 or 33 };
    }

    private static bool IsNamespaceBindingFailure(Exception exception) =>
        exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or NotSupportedException;
}

internal sealed class AliSourceBoundOperation(int sequence) : IDisposable
{
    internal int Sequence { get; } = sequence;
    internal AliSourceOperationState State { get; private set; } = AliSourceOperationState.Unknown;
    internal AliSourceBoundFile? File { get; private set; }
    internal AliSourceFileIdentity? CanonicalIdentity { get; private set; }
    internal bool MutationBegan { get; private set; }

    internal void SetPreimage(AliSourceBoundFile? file, AliSourceFileIdentity? identity)
    {
        Assign(file, identity);
        State = AliSourceOperationState.Preimage;
    }

    internal void SetPostimage(AliSourceBoundFile? file, AliSourceFileIdentity? identity)
    {
        Assign(file, identity);
        State = AliSourceOperationState.Postimage;
    }

    internal void BeginMutation(AliSourceBoundFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Assign(file, file.Identity);
        MutationBegan = true;
        State = AliSourceOperationState.Unknown;
    }

    internal void SetState(AliSourceOperationState state) => State = state;

    internal void ReleaseFile()
    {
        File?.Dispose();
        File = null;
        CanonicalIdentity = null;
    }

    public void Dispose() => ReleaseFile();

    private void Assign(AliSourceBoundFile? file, AliSourceFileIdentity? identity)
    {
        if (!ReferenceEquals(File, file))
        {
            File?.Dispose();
        }
        File = file;
        CanonicalIdentity = identity;
    }
}

internal sealed class AliSourceBoundOperationSet : IDisposable
{
    private readonly AliSourceBoundOperation?[] _operations;

    internal AliSourceBoundOperationSet(int count)
    {
        _operations = new AliSourceBoundOperation?[count];
    }

    internal AliSourceBoundOperation this[int index]
    {
        get => _operations[index]
            ?? throw new InvalidDataException("A source operation binding is missing.");
        set
        {
            _operations[index]?.Dispose();
            _operations[index] = value;
        }
    }

    internal int Count => _operations.Length;

    internal bool All(Func<AliSourceBoundOperation, bool> predicate) =>
        _operations.All(operation => operation is not null && predicate(operation));

    public void Dispose()
    {
        foreach (var operation in _operations)
        {
            operation?.Dispose();
        }
        Array.Clear(_operations, 0, _operations.Length);
    }
}

internal sealed class AliSourceRecoveryBinding : IDisposable
{
    internal AliSourceRecoveryBinding(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceBoundOperationSet operations,
        AliSourceJournalSnapshot snapshot)
    {
        SourceNamespace = sourceNamespace;
        Operations = operations;
        Snapshot = snapshot;
    }

    internal AliSourceWindowsNamespace SourceNamespace { get; }
    internal AliSourceBoundOperationSet Operations { get; }
    internal AliSourceJournalSnapshot Snapshot { get; }
    internal ImmutableArray<AliSourceOperationState> States =>
        Enumerable.Range(0, Operations.Count)
            .Select(index => Operations[index].State)
            .ToImmutableArray();

    public void Dispose()
    {
        Operations.Dispose();
        SourceNamespace.Dispose();
    }
}

internal sealed record AliSourceRollbackResult(bool Complete, AliSourceJournalHead Head);

internal enum AliSourceOperationState
{
    Preimage,
    Postimage,
    Unknown
}
