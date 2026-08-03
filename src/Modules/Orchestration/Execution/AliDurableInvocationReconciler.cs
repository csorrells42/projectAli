using System.Security.Cryptography;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration.Execution;

/// <summary>
/// Mechanically classifies one exact adapter's durable invocation record. It never selects a
/// domain reconciler by tool prefix, effect kind, executable, or user text.
/// </summary>
internal sealed class AliDurableInvocationReconciler
{
    private readonly AliDurableInvocationStore _store;
    private readonly AliExactExecutionAdapterIdentity _exactIdentity;
    private readonly IAliStartedInvocationDomainReconciler? _startedReconciler;

    internal AliDurableInvocationReconciler(
        AliDurableInvocationStore store,
        AliExactExecutionAdapterIdentity exactIdentity,
        IAliStartedInvocationDomainReconciler? startedReconciler = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _exactIdentity = new AliExactExecutionAdapterIdentity(
            exactIdentity.ToolName,
            exactIdentity.CapabilityId,
            exactIdentity.ReconcilerId);
        if (startedReconciler is not null)
        {
            var reconcilerIdentity = startedReconciler.ExactIdentity;
            var validatedReconcilerIdentity = new AliExactExecutionAdapterIdentity(
                reconcilerIdentity.ToolName,
                reconcilerIdentity.CapabilityId,
                reconcilerIdentity.ReconcilerId);
            if (validatedReconcilerIdentity != _exactIdentity)
            {
                throw new ArgumentException(
                    "A started-invocation reconciler must own the same exact adapter tuple.",
                    nameof(startedReconciler));
            }
        }
        _startedReconciler = startedReconciler;
    }

    internal async ValueTask<AliDurableInvocationRecoveryResult> ReconcileAsync(
        string planId,
        string expectedAuthorizationDigest,
        CancellationToken cancellationToken)
    {
        TurnStateIntegrity.RequireDigest(
            expectedAuthorizationDigest,
            nameof(expectedAuthorizationDigest));
        var snapshot = await _store.LoadAsync(planId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.Plan.ExactIdentity != _exactIdentity)
        {
            return AliDurableInvocationRecoveryResult.Unknown(
                "invocation-adapter-identity-mismatch");
        }
        if (snapshot.State != AliDurableInvocationState.Prepared
            && !AuthorizationMatches(snapshot.Receipt, expectedAuthorizationDigest))
        {
            return AliDurableInvocationRecoveryResult.Unknown(
                "invocation-authorization-mismatch");
        }

        return snapshot.State switch
        {
            AliDurableInvocationState.Prepared =>
                AliDurableInvocationRecoveryResult.Absent(
                    "invocation-prepared-not-started"),
            AliDurableInvocationState.Started when _startedReconciler is null =>
                AliDurableInvocationRecoveryResult.Unknown(
                    "invocation-started-no-terminal-receipt"),
            AliDurableInvocationState.Started =>
                await ReconcileStartedAsync(
                        snapshot,
                        cancellationToken)
                    .ConfigureAwait(false),
            AliDurableInvocationState.Completed =>
                AliDurableInvocationRecoveryResult.Applied(
                    snapshot.Receipt!.StableOutcomeCode!),
            AliDurableInvocationState.Failed =>
                AliDurableInvocationRecoveryResult.Failed(
                    snapshot.Receipt!.FailureCode!),
            _ => AliDurableInvocationRecoveryResult.Unknown(
                snapshot.Receipt?.FailureCode ?? "invocation-in-doubt")
        };
    }

    private async ValueTask<AliDurableInvocationRecoveryResult> ReconcileStartedAsync(
        AliDurableInvocationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var result = await _startedReconciler!.ReconcileStartedAsync(
                snapshot.Plan,
                snapshot.Receipt!,
                cancellationToken)
            .ConfigureAwait(false)
            ?? AliDurableInvocationRecoveryResult.Unknown(
                "invocation-domain-reconciler-returned-null");
        result.Validate();
        return result;
    }

    private static bool AuthorizationMatches(
        AliDurableInvocationReceipt? receipt,
        string expected)
    {
        if (receipt is null)
        {
            return false;
        }

        byte[] actualBytes;
        byte[] expectedBytes;
        try
        {
            actualBytes = Convert.FromHexString(receipt.AuthorizationDigest);
            expectedBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return actualBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }
}
