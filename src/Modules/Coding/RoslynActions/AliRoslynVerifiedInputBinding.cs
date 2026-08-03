using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Authenticated exact-file evidence joining the canonical preimage and the staged tree that
/// actually passed build and test verification.
/// </summary>
internal sealed record AliRoslynVerifiedInputBinding(
    string ReceiptId,
    string ChangeSetId,
    string ChangeSetManifestDigest,
    string MaterializationReceiptId,
    string MaterializationManifestDigest,
    string MaterializationPolicyDigest,
    AliRoslynInputManifest CanonicalPreimage,
    AliRoslynInputManifest StagedPostimage,
    string BindingDigest)
{
    internal static AliRoslynVerifiedInputBinding Create(
        string receiptId,
        AliSourceChangeSet changeSet,
        AliSourceTreeMaterializationReceipt materialization,
        AliRoslynInputManifest canonicalPreimage,
        AliRoslynInputManifest stagedPostimage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(materialization);
        canonicalPreimage.Validate();
        stagedPostimage.Validate();
        if (!string.Equals(materialization.ChangeSetId, changeSet.Id, StringComparison.Ordinal)
            || !string.Equals(
                materialization.ManifestDigest,
                changeSet.ManifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                materialization.PolicyDigest,
                AliSourceTreeMaterializationPolicy.SafeDefault.Digest,
                StringComparison.Ordinal)
            || !string.Equals(
                canonicalPreimage.PolicyDigest,
                AliRoslynStagedInputBinding.PolicyDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                stagedPostimage.PolicyDigest,
                AliRoslynStagedInputBinding.PolicyDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Roslyn input manifests do not match the authenticated materialization policy.");
        }

        var canonical = CopyManifest(canonicalPreimage);
        var staged = CopyManifest(stagedPostimage);
        var binding = new AliRoslynVerifiedInputBinding(
            receiptId,
            changeSet.Id,
            changeSet.ManifestDigest,
            materialization.ReceiptId,
            materialization.ManifestDigest,
            materialization.PolicyDigest,
            canonical,
            staged,
            string.Empty);
        return binding with { BindingDigest = ComputeDigest(binding) };
    }

    internal void Validate()
    {
        ValidateId(ReceiptId);
        ValidateId(ChangeSetId);
        ValidateId(MaterializationReceiptId);
        ValidateDigest(ChangeSetManifestDigest);
        ValidateDigest(MaterializationManifestDigest);
        ValidateDigest(MaterializationPolicyDigest);
        ValidateDigest(BindingDigest);
        CanonicalPreimage.Validate();
        StagedPostimage.Validate();
        if (!string.Equals(ChangeSetManifestDigest, MaterializationManifestDigest, StringComparison.Ordinal)
            || !string.Equals(
                MaterializationPolicyDigest,
                AliSourceTreeMaterializationPolicy.SafeDefault.Digest,
                StringComparison.Ordinal)
            || !string.Equals(
                CanonicalPreimage.PolicyDigest,
                AliRoslynStagedInputBinding.PolicyDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                StagedPostimage.PolicyDigest,
                AliRoslynStagedInputBinding.PolicyDigest,
                StringComparison.Ordinal)
            || !string.Equals(ComputeDigest(this), BindingDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Roslyn verified input binding is inconsistent.");
        }
    }

    internal static string ComputeDigest(AliRoslynVerifiedInputBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var canonical = string.Join(
            '\0',
            "ali-roslyn-verified-input-binding-v1",
            binding.ReceiptId,
            binding.ChangeSetId,
            binding.ChangeSetManifestDigest,
            binding.MaterializationReceiptId,
            binding.MaterializationManifestDigest,
            binding.MaterializationPolicyDigest,
            binding.CanonicalPreimage.PolicyDigest,
            binding.CanonicalPreimage.ManifestSha256,
            binding.CanonicalPreimage.FileCount.ToString(CultureInfo.InvariantCulture),
            binding.CanonicalPreimage.TotalBytes.ToString(CultureInfo.InvariantCulture),
            binding.StagedPostimage.PolicyDigest,
            binding.StagedPostimage.ManifestSha256,
            binding.StagedPostimage.FileCount.ToString(CultureInfo.InvariantCulture),
            binding.StagedPostimage.TotalBytes.ToString(CultureInfo.InvariantCulture));
        return HashText(canonical);
    }

    private static AliRoslynInputManifest CopyManifest(AliRoslynInputManifest manifest) =>
        AliRoslynInputManifest.Create(
            manifest.PolicyDigest,
            manifest.Files.Select(file => file with { }).ToArray());

    private static void ValidateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length is < 16 or > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidDataException("A Roslyn verified input artifact ID is invalid.");
        }
    }

    private static void ValidateDigest(string value)
    {
        if (value?.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException("A Roslyn verified input identity is not a SHA-256 digest.");
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal static class AliRoslynVerifiedInputBindingStore
{
    private const int MaximumArtifactBytes = 256 * 1024 * 1024;

    internal static async Task SaveAsync(
        AliSourceChangeSetStore changeSets,
        AliRoslynVerifiedInputBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSets);
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        var directory = VerificationDirectory(changeSets, binding.ChangeSetId);
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            directory,
            "The Roslyn verified input artifact directory is not a regular local directory.");
        await changeSets.WriteAuthenticatedDocumentAsync(
                Path.Combine(directory, binding.ReceiptId + ".json"),
                Purpose(binding.ChangeSetId, binding.ReceiptId),
                binding,
                MaximumArtifactBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<AliRoslynVerifiedInputBinding> LoadAsync(
        AliSourceChangeSetStore changeSets,
        AliRoslynPreverificationReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSets);
        ArgumentNullException.ThrowIfNull(receipt);
        var binding = await changeSets.ReadAuthenticatedDocumentAsync<AliRoslynVerifiedInputBinding>(
                Path.Combine(
                    VerificationDirectory(changeSets, receipt.ChangeSetId),
                    receipt.Id + ".json"),
                Purpose(receipt.ChangeSetId, receipt.Id),
                MaximumArtifactBytes,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The authenticated Roslyn input binding is missing.");
        binding.Validate();
        if (!string.Equals(binding.ReceiptId, receipt.Id, StringComparison.Ordinal)
            || !string.Equals(binding.ChangeSetId, receipt.ChangeSetId, StringComparison.Ordinal)
            || !string.Equals(
                binding.ChangeSetManifestDigest,
                receipt.ChangeSetManifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.MaterializationReceiptId,
                receipt.MaterializationReceiptId,
                StringComparison.Ordinal)
            || !string.Equals(binding.BindingDigest, receipt.InputBindingDigest, StringComparison.Ordinal)
            || !string.Equals(
                binding.CanonicalPreimage.ManifestSha256,
                receipt.CanonicalInputManifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.StagedPostimage.ManifestSha256,
                receipt.StagedInputManifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                binding.CanonicalPreimage.PolicyDigest,
                receipt.InputManifestPolicyDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The authenticated Roslyn input binding does not match its protected verification receipt.");
        }

        var materialization = await changeSets
            .ReadAuthenticatedDocumentAsync<AliSourceTreeMaterializationReceipt>(
                Path.Combine(
                    changeSets.GetTransactionDirectory(binding.ChangeSetId),
                    "materializations",
                    binding.MaterializationReceiptId + ".json"),
                $"materialization:{binding.ChangeSetId}:{binding.MaterializationReceiptId}",
                AliSourceChangeSetStore.MaximumReceiptBytes,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The authenticated source materialization receipt is missing.");
        if (!string.Equals(materialization.ReceiptId, binding.MaterializationReceiptId, StringComparison.Ordinal)
            || !string.Equals(materialization.ChangeSetId, binding.ChangeSetId, StringComparison.Ordinal)
            || !string.Equals(
                materialization.ManifestDigest,
                binding.MaterializationManifestDigest,
                StringComparison.Ordinal)
            || !string.Equals(
                materialization.PolicyDigest,
                binding.MaterializationPolicyDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Roslyn input binding does not match its authenticated source materialization receipt.");
        }

        return binding;
    }

    private static string VerificationDirectory(
        AliSourceChangeSetStore changeSets,
        string changeSetId) =>
        Path.Combine(changeSets.GetTransactionDirectory(changeSetId), "roslyn-verifications");

    private static string Purpose(string changeSetId, string receiptId) =>
        $"roslyn-input-binding:{changeSetId}:{receiptId}";
}
