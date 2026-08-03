using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.WorkstationFiles;

/// <summary>
/// Owns durable preparation, exact grant consumption, and journaled publication for the three
/// Agent Framework text-file mutation tools. Canonical workstation files are never written by
/// this class except through <see cref="AliSourceChangeSetPublisher"/>.
/// </summary>
internal sealed class AliFrameworkFileMutationTransaction
{
    private static readonly UTF8Encoding FrameworkWriteEncoding = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly AliWorkstationFileStore _rawStore;
    private readonly AliSourceChangeSetStore _changeSets;
    private readonly AliSourceChangeSetPublisher _publisher;

    internal AliFrameworkFileMutationTransaction(
        AliWorkstationFileStore rawStore,
        string durableOrchestrationRoot,
        string assistantProfileBinding,
        EvidenceLedger? evidence = null,
        Action<AliSourceTransactionFault>? faultInjector = null)
    {
        _rawStore = rawStore ?? throw new ArgumentNullException(nameof(rawStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(durableOrchestrationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        var durableRoot = Path.GetFullPath(durableOrchestrationRoot);
        var mutationRoot = Path.Combine(durableRoot, "FrameworkFileMutations");
        _changeSets = new AliSourceChangeSetStore(
            Path.Combine(mutationRoot, "Changesets"),
            assistantProfileBinding);
        var validator = new AliSourceChangeSetValidator(_changeSets);
        _publisher = new AliSourceChangeSetPublisher(
            _changeSets,
            validator,
            faultInjector);
        Reconciler = new AliSourceChangeSetReconciler(_changeSets, _publisher);
        Evidence = evidence ?? new EvidenceLedger(durableRoot, assistantProfileBinding);
    }

    internal AliSourceChangeSetReconciler Reconciler { get; }

    internal EvidenceLedger Evidence { get; }

    internal Task<AliSourceChangeSet> LoadAsync(
        string changeSetId,
        CancellationToken cancellationToken) =>
        _changeSets.LoadAsync(changeSetId, cancellationToken);

    internal Task<AliSourcePublicationReceipt?> ReadReceiptAsync(
        string changeSetId,
        CancellationToken cancellationToken) =>
        _publisher.ReadReceiptAsync(changeSetId, cancellationToken);

    internal async Task<AliExecutionPreparation> PrepareAsync(
        string toolName,
        JsonElement arguments,
        string expectedTargetVersionDigest,
        CancellationToken cancellationToken)
    {
        var fileName = AliFrameworkFileMutationPlan.ReadExactFileName(toolName, arguments);
        var resolved = _rawStore.ResolvePhysicalFilePath(fileName);
        var preimageBytes = await ReadOptionalExactFileAsync(
                resolved.PhysicalPath,
                cancellationToken)
            .ConfigureAwait(false);
        byte[]? postimageBytes = null;
        try
        {
            var currentContent = preimageBytes is null
                || string.Equals(
                    toolName,
                    AliCapabilityCatalog.FileWriteName,
                    StringComparison.Ordinal)
                ? null
                : DecodeFrameworkText(preimageBytes);
            var plan = AliFrameworkFileMutationPlan.Create(
                toolName,
                arguments,
                currentContent);
            if (!string.Equals(plan.FileName, fileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The exact file path changed while parsing the accepted arguments.");
            }
            if (plan.RequiresExistingFile && preimageBytes is null)
            {
                throw new FileNotFoundException(
                    "The Agent Framework edit target does not exist.",
                    fileName);
            }
            if (!plan.AllowsExistingFile && preimageBytes is not null)
            {
                throw new IOException(
                    "The Agent Framework write target already exists and overwrite is false.");
            }

            var preimage = preimageBytes is null
                ? AliSourceFileImage.Absent
                : AliSourceFileImage.FromBytes(preimageBytes);
            var currentTargetDigest = TargetVersionDigest(fileName, preimage);
            if (!string.Equals(
                    currentTargetDigest,
                    expectedTargetVersionDigest,
                    StringComparison.Ordinal))
            {
                throw new AliExecutionPreparationException(
                    "The exact workstation file changed after the accepted decision.");
            }

            postimageBytes = FrameworkWriteEncoding.GetBytes(plan.PostContent);
            AliSourceChangeRequest changeRequest = preimage.Exists
                ? AliSourceChangeRequest.ReplaceBytes(
                    resolved.RelativePath,
                    postimageBytes,
                    AliSourceExpectedFile.Present(preimage.Sha256!, preimage.Length))
                : AliSourceChangeRequest.AddBytes(
                    resolved.RelativePath,
                    postimageBytes,
                    AliSourceExpectedFile.Absent);
            var changeSet = await _changeSets.CreateAsync(
                    resolved.MountRoot,
                    [changeRequest],
                    cancellationToken)
                .ConfigureAwait(false);
            return new AliExecutionPreparation(
                changeSet.Id,
                RootBinding(resolved.MountRoot),
                expectedTargetVersionDigest);
        }
        finally
        {
            if (preimageBytes is not null)
            {
                CryptographicOperations.ZeroMemory(preimageBytes);
            }
            if (postimageBytes is not null)
            {
                CryptographicOperations.ZeroMemory(postimageBytes);
            }
        }
    }

    internal async Task PublishFrameworkWriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _rawStore.ResolvePhysicalFilePath(path);
        if (!TryConsumeExactFileMutationGrant(out var grant) || grant is null)
        {
            throw new InvalidOperationException(
                "A production Agent Framework file mutation requires one exact durable execution grant.");
        }

        var changeSet = await _changeSets.LoadAsync(
                grant.PreparationIdentity,
                cancellationToken)
            .ConfigureAwait(false);
        RequireExactInvocation(changeSet, resolved, path, grant);
        var postimage = await _changeSets.ReadPostImageAsync(
                changeSet,
                operationIndex: 0,
                cancellationToken)
            .ConfigureAwait(false);
        byte[]? frameworkBytes = null;
        try
        {
            frameworkBytes = FrameworkWriteEncoding.GetBytes(content);
            if (!CryptographicOperations.FixedTimeEquals(postimage, frameworkBytes))
            {
                throw new InvalidOperationException(
                    "The Agent Framework-produced write content does not match the authenticated postimage.");
            }

            var publicationGrant = AliSourcePublicationGrant.Issue(
                changeSet,
                AuthorizationBindingDigest(grant));
            var receipt = await _publisher.PublishAsync(
                    changeSet,
                    publicationGrant,
                    cancellationToken)
                .ConfigureAwait(false);
            if (receipt.State != AliSourcePublicationState.Committed)
            {
                throw new IOException(
                    "The journaled workstation file transaction did not commit: " + receipt.Summary);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(postimage);
            if (frameworkBytes is not null)
            {
                CryptographicOperations.ZeroMemory(frameworkBytes);
            }
        }
    }

    internal static string CapabilityIdFor(string toolName) => "ali.tool." + toolName;

    internal static string ReconcilerIdFor(string toolName) => "ali.reconcile." + toolName;

    internal static string RootBinding(string sourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }
        return HashText("ali-source-root-v1\0" + normalized);
    }

    internal static string AuthorizationBindingDigest(AliExecutionGrant grant)
        => AliExecutionAuthorizationDigest.Compute(
            AliExecutionAuthorizationDigest.FrameworkFilePublicationDomain,
            grant);

    internal static string TargetVersionDigest(
        string virtualPath,
        AliSourceFileImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        ArgumentNullException.ThrowIfNull(image);
        var version = image.Exists ? "sha256:" + image.Sha256 : "absent";
        return WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["file:" + virtualPath.Trim().Replace('\\', '/')] = version
            });
    }

    private static string DecodeFrameworkText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static async Task<byte[]?> ReadOptionalExactFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(
                "An Agent Framework file mutation target has no parent directory.");
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            parent,
            "An Agent Framework file mutation parent is not a regular local directory.");
        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.ReparsePoint
                               | FileAttributes.Directory
                               | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    "An Agent Framework file mutation target is a reparse point or non-regular entry.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "An Agent Framework file mutation target is not a regular local file.");
        var length = stream.Length;
        if (length < 0 || length > AliSourceChangeSetStore.MaximumFileBytes)
        {
            throw new IOException(
                $"An Agent Framework text-file mutation cannot exceed {AliSourceChangeSetStore.MaximumFileBytes} bytes.");
        }
        var bytes = new byte[checked((int)length)];
        try
        {
            if (bytes.Length > 0)
            {
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            if (stream.Position != length || stream.Length != length)
            {
                throw new InvalidDataException(
                    "The Agent Framework file changed while its exact preimage was captured.");
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static bool TryConsumeExactFileMutationGrant(out AliExecutionGrant? grant)
    {
        foreach (var toolName in FileMutationToolNames.All)
        {
            if (AliExecutionGrantContext.TryConsumeCurrent(
                    toolName,
                    CapabilityIdFor(toolName),
                    ReconcilerIdFor(toolName),
                    out grant))
            {
                return true;
            }
        }
        grant = null;
        return false;
    }

    private static void RequireExactInvocation(
        AliSourceChangeSet changeSet,
        AliResolvedWorkstationPath resolved,
        string invocationPath,
        AliExecutionGrant grant)
    {
        if (!string.Equals(changeSet.Id, grant.PreparationIdentity, StringComparison.Ordinal)
            || !string.Equals(
                RootBinding(changeSet.CanonicalSourceRoot),
                grant.RootBinding,
                StringComparison.Ordinal)
            || !Path.GetFullPath(changeSet.CanonicalSourceRoot).Equals(
                Path.GetFullPath(resolved.MountRoot),
                StringComparison.OrdinalIgnoreCase)
            || changeSet.Operations.Length != 1)
        {
            throw new InvalidOperationException(
                "The durable execution grant does not authorize this exact workstation changeset.");
        }

        var operation = changeSet.Operations[0];
        var expectedPhysicalPath = AliSourceChangeSetStore.ResolveContainedPath(
            changeSet.CanonicalSourceRoot,
            operation.SourceRelativePath);
        if (operation.Kind is not (AliSourceChangeKind.Add or AliSourceChangeKind.Replace)
            || !Path.GetFullPath(expectedPhysicalPath).Equals(
                Path.GetFullPath(resolved.PhysicalPath),
                StringComparison.OrdinalIgnoreCase)
            || operation.ContentBlobSha256 is null
            || !operation.SourcePostimage.Exists
            || !string.Equals(
                TargetVersionDigest(invocationPath, operation.SourcePreimage),
                grant.TargetVersionDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The durable execution grant does not authorize this exact workstation file postimage.");
        }
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static class FileMutationToolNames
    {
        internal static readonly string[] All =
        [
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReplaceName,
            AliCapabilityCatalog.FileReplaceLinesName
        ];
    }
}
