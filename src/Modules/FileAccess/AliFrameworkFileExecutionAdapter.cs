using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.WorkstationFiles;

/// <summary>
/// Exact broker adapter for one Agent Framework file mutation schema. Each instance owns one
/// complete tool/capability/reconciler identity; no prefix or category grants authority.
/// </summary>
internal sealed class AliFrameworkFileExecutionAdapter : IAliExecutionEffectAdapter
{
    private readonly AliFrameworkFileMutationTransaction _transaction;

    private AliFrameworkFileExecutionAdapter(
        string toolName,
        AliFrameworkFileMutationTransaction transaction)
    {
        ToolName = toolName;
        CapabilityId = AliFrameworkFileMutationTransaction.CapabilityIdFor(toolName);
        ReconcilerId = AliFrameworkFileMutationTransaction.ReconcilerIdFor(toolName);
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public string ToolName { get; }

    public string CapabilityId { get; }

    public string ReconcilerId { get; }

    internal static IReadOnlyList<IAliExecutionEffectAdapter> CreateAll(
        AliFrameworkFileMutationTransaction transaction) =>
    [
        new AliFrameworkFileExecutionAdapter(
            AliCapabilityCatalog.FileWriteName,
            transaction),
        new AliFrameworkFileExecutionAdapter(
            AliCapabilityCatalog.FileReplaceName,
            transaction),
        new AliFrameworkFileExecutionAdapter(
            AliCapabilityCatalog.FileReplaceLinesName,
            transaction)
    ];

    public async ValueTask<AliExecutionPreparation> PrepareAsync(
        AliExecutionPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.ToolName, ToolName, StringComparison.Ordinal)
            || !string.Equals(request.CapabilityId, CapabilityId, StringComparison.Ordinal)
            || !string.Equals(request.ReconcilerId, ReconcilerId, StringComparison.Ordinal))
        {
            throw new AliExecutionPreparationException(
                "The Agent Framework file adapter received a mismatched execution identity.");
        }

        try
        {
            request.Validate();
            return await _transaction.PrepareAsync(
                    ToolName,
                    request.Arguments,
                    request.TargetVersionDigest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AliExecutionPreparationException)
        {
            throw;
        }
        catch (Exception exception) when (IsPreparationFailure(exception))
        {
            throw new AliExecutionPreparationException(
                "The exact Agent Framework file mutation could not be prepared safely.",
                exception);
        }
    }

    public async ValueTask<ActionReconciliationResult> ReconcileAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(intent);
        if (!string.Equals(intent.ToolName, ToolName, StringComparison.Ordinal)
            || !string.Equals(intent.CapabilityId, CapabilityId, StringComparison.Ordinal)
            || !string.Equals(intent.ReconcilerId, ReconcilerId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
        {
            return ActionReconciliationResult.Unknown("file-mutation-adapter-identity-mismatch");
        }

        try
        {
            var reconciliation = await _transaction.Reconciler.ReconcileAsync(
                    intent.PreparationIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
            return reconciliation.State switch
            {
                AliSourcePublicationState.Committed =>
                    await ReconcileCommittedAsync(identity, intent, cancellationToken)
                        .ConfigureAwait(false),
                AliSourcePublicationState.RolledBack =>
                    ActionReconciliationResult.Absent("file-mutation-rolled-back"),
                _ => ActionReconciliationResult.Unknown("file-mutation-in-doubt")
            };
        }
        catch (FileNotFoundException)
        {
            return ActionReconciliationResult.Unknown("file-mutation-artifact-missing");
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return ActionReconciliationResult.Unknown(
                "file-mutation-reconcile-" + StableExceptionCode(exception));
        }
    }

    private async Task<ActionReconciliationResult> ReconcileCommittedAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        CancellationToken cancellationToken)
    {
        if (!AliExecutionAuthorizationDigest.TryCompute(
                AliExecutionAuthorizationDigest.FrameworkFilePublicationDomain,
                intent,
                out var expectedAuthorizationBindingDigest))
        {
            return ActionReconciliationResult.Unknown(
                "file-mutation-authorization-identity-missing");
        }

        var changeSet = await _transaction.LoadAsync(
                intent.PreparationIdentity!,
                cancellationToken)
            .ConfigureAwait(false);
        var receipt = await _transaction.ReadReceiptAsync(
                intent.PreparationIdentity!,
                cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null
            || receipt.State != AliSourcePublicationState.Committed
            || !string.Equals(
                receipt.ManifestDigest,
                changeSet.ManifestDigest,
                StringComparison.Ordinal))
        {
            return ActionReconciliationResult.Unknown("file-mutation-receipt-mismatch");
        }
        if (!string.Equals(
                receipt.AuthorizationBindingDigest,
                expectedAuthorizationBindingDigest,
                StringComparison.Ordinal))
        {
            return ActionReconciliationResult.Unknown("file-mutation-authorization-mismatch");
        }

        return ActionReconciliationResult.Applied(
            "file-mutation-committed",
            await AppendReconciliationEvidenceAsync(
                    identity,
                    intent,
                    changeSet,
                    cancellationToken)
                .ConfigureAwait(false));
    }

    private async Task<CommittedEvidenceReference> AppendReconciliationEvidenceAsync(
        TurnIdentity identity,
        PreparedActionIntent intent,
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        if (changeSet.Operations.Length != 1)
        {
            throw new InvalidDataException(
                "A framework file mutation changeset must contain exactly one operation.");
        }
        var operation = changeSet.Operations[0];
        var outcomeCode = "file-mutation-committed";
        var resultBytes = Encoding.UTF8.GetBytes(outcomeCode);
        try
        {
            var fixedTime = DateTimeOffset.UnixEpoch;
            var draft = new EvidenceDraft
            {
                EvidenceId = HashText(
                    "ali-framework-file-reconciliation-evidence-v1\0"
                    + identity.StorageKey + "\0" + intent.IdempotencyKey + "\0" + outcomeCode),
                CallId = intent.AcceptedCallId ?? intent.IdempotencyKey,
                WorkItemId = intent.WorkItemId,
                ToolName = intent.ToolName,
                CapabilityGroup = "workstation-files",
                ProviderId = "ali-agent-framework",
                RegistryRevision = intent.RegistryRevisionDigest,
                EffectKind = operation.Kind == AliSourceChangeKind.Add ? "create" : "update",
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    intent.CanonicalArgumentsDigest,
                    intent.PreparationIdentity
                }),
                Result = JsonSerializer.SerializeToElement(new { outcomeCode }),
                NormalizedTarget = JsonSerializer.SerializeToElement(new
                {
                    changeSet.CanonicalSourceRoot,
                    operation.SourceRelativePath,
                    intent.TargetVersionDigest
                }),
                NormalizedEffectResult = JsonSerializer.SerializeToElement(new
                {
                    outcomeCode,
                    changeSet.ManifestDigest,
                    afterVersion = operation.SourcePostimage.Sha256
                }),
                Outcome = ToolInvocationOutcome.Returned(resultBytes, reportedSuccess: true),
                StableOutcomeCode = outcomeCode,
                StartedAtUtc = fixedTime,
                CompletedAtUtc = fixedTime,
                Artifacts =
                [
                    new EvidenceArtifactDraft(
                        operation.SourceRelativePath,
                        "file",
                        operation.SourcePreimage.Sha256,
                        operation.SourcePostimage.Sha256)
                ],
                Permission = new EvidencePermissionMetadata("unknown", "unknown"),
                ProtectedPermissionReceipt = JsonSerializer.SerializeToElement(new
                {
                    intent.PermissionReceiptDigest,
                    intent.RequiresApproval
                }),
                Source = new EvidenceSourceMetadata(
                    "file",
                    "ali-agent-framework",
                    "trusted-local",
                    FreshAtUtc: null,
                    intent.RegistryRevisionDigest),
                ProtectedProvenance = JsonSerializer.SerializeToElement(new
                {
                    reconciler = intent.ReconcilerId,
                    changeSet.Id,
                    changeSet.ManifestDigest,
                    replayedEffect = false
                })
            };
            await _transaction.Evidence.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            var committed = await _transaction.Evidence.AppendAsync(
                    identity,
                    draft,
                    cancellationToken)
                .ConfigureAwait(false);
            return new CommittedEvidenceReference(
                committed.Evidence.EvidenceId,
                committed.Cursor,
                committed.Checksum);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(resultBytes);
        }
    }

    private static bool IsPreparationFailure(Exception exception) =>
        exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string StableExceptionCode(Exception exception)
    {
        var name = exception.GetType().Name;
        var filtered = new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(filtered)
            ? "failed"
            : filtered[..Math.Min(filtered.Length, 72)];
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
