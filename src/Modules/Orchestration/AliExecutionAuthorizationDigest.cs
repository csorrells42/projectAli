using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.State;

namespace Ali.Modules.Orchestration;

/// <summary>
/// Binds a publication receipt to the exact durable authorization that was prepared before
/// execution. Recovery computes from the persisted intent; it never recreates a one-use grant.
/// </summary>
internal static class AliExecutionAuthorizationDigest
{
    internal const string SourcePublicationDomain =
        "ali-source-publication-authorization-v1";

    internal const string FrameworkFilePublicationDomain =
        "ali-framework-file-publication-authorization-v1";

    internal static string Compute(string domain, AliExecutionGrant grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(grant);
        return ComputeCore(
            domain,
            grant.IdempotencyKey,
            grant.CallId,
            grant.ToolName,
            grant.CapabilityId,
            grant.CanonicalArgumentsDigest,
            grant.TargetVersionDigest,
            grant.PermissionReceiptDigest,
            grant.RegistryIdentityDigest,
            grant.ReconcilerId,
            grant.PreparationIdentity,
            grant.RootBinding);
    }

    internal static bool TryCompute(
        string domain,
        PreparedActionIntent intent,
        out string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(intent);
        digest = string.Empty;

        try
        {
            intent.Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.AcceptedCallId)
            || string.IsNullOrWhiteSpace(intent.PreparationIdentity))
        {
            return false;
        }

        digest = ComputeCore(
            domain,
            intent.IdempotencyKey,
            intent.AcceptedCallId,
            intent.ToolName,
            intent.CapabilityId,
            intent.CanonicalArgumentsDigest,
            intent.TargetVersionDigest,
            intent.PermissionReceiptDigest,
            intent.ExecutionRegistryIdentityDigest,
            intent.ReconcilerId,
            intent.PreparationIdentity,
            intent.RootBinding);
        return true;
    }

    private static string ComputeCore(
        string domain,
        string idempotencyKey,
        string callId,
        string toolName,
        string capabilityId,
        string canonicalArgumentsDigest,
        string targetVersionDigest,
        string permissionReceiptDigest,
        string registryIdentityDigest,
        string reconcilerId,
        string preparationIdentity,
        string rootBinding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\0",
            domain,
            idempotencyKey,
            callId,
            toolName,
            capabilityId,
            canonicalArgumentsDigest,
            targetVersionDigest,
            permissionReceiptDigest,
            registryIdentityDigest,
            reconcilerId,
            preparationIdentity,
            rootBinding)))).ToLowerInvariant();
}
