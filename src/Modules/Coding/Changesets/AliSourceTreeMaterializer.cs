using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Changesets;

internal sealed class AliSourceTreeMaterializationPolicy
{
    private const int MaximumPolicyEntries = 256;
    private const int MaximumPolicyEntryCharacters = 1024;
    private const int MaximumAllowedEntries = 1_000_000;
    private const long MaximumAllowedBytes = 8L * 1024 * 1024 * 1024 * 1024;

    private AliSourceTreeMaterializationPolicy(
        ImmutableArray<string> excludedRelativePaths,
        ImmutableArray<string> excludedDirectoryNames,
        int maximumEntries,
        long maximumBytes,
        string digest)
    {
        ExcludedRelativePaths = excludedRelativePaths;
        ExcludedDirectoryNames = excludedDirectoryNames;
        MaximumEntries = maximumEntries;
        MaximumBytes = maximumBytes;
        Digest = digest;
    }

    internal static AliSourceTreeMaterializationPolicy SafeDefault { get; } = Create(
        excludedRelativePaths: [],
        excludedDirectoryNames:
        [
            ".git",
            ".vs",
            "bin",
            "obj",
            ".ali",
            "TestResults",
            "artifacts",
            "node_modules"
        ],
        maximumEntries: 100_000,
        maximumBytes: 4L * 1024 * 1024 * 1024);

    internal ImmutableArray<string> ExcludedRelativePaths { get; }
    internal ImmutableArray<string> ExcludedDirectoryNames { get; }
    internal int MaximumEntries { get; }
    internal long MaximumBytes { get; }
    internal string Digest { get; }

    internal static AliSourceTreeMaterializationPolicy Create(
        IEnumerable<string>? excludedRelativePaths,
        IEnumerable<string>? excludedDirectoryNames,
        int maximumEntries,
        long maximumBytes)
    {
        if (maximumEntries is <= 0 or > MaximumAllowedEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }
        if (maximumBytes is <= 0 or > MaximumAllowedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var paths = NormalizeDistinct(
            excludedRelativePaths,
            static value => AliSourceChangeSetStore.NormalizeRelativePath(value),
            nameof(excludedRelativePaths));
        var names = NormalizeDistinct(
            excludedDirectoryNames,
            static value =>
            {
                var normalized = AliSourceChangeSetStore.NormalizeRelativePath(value);
                if (normalized.Contains('/'))
                {
                    throw new ArgumentException("An excluded directory name must contain exactly one path segment.");
                }
                return normalized;
            },
            nameof(excludedDirectoryNames));
        if (paths.Length + names.Length > MaximumPolicyEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(excludedRelativePaths),
                "A source materialization policy has too many exclusions.");
        }

        var canonical = string.Join(
            '\0',
            "Ali.SourceTreeMaterializationPolicy.v1",
            maximumEntries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            maximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join('\n', paths.Select(path => path.ToLowerInvariant())),
            string.Join('\n', names.Select(name => name.ToLowerInvariant())));
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return new AliSourceTreeMaterializationPolicy(
                paths,
                names,
                maximumEntries,
                maximumBytes,
                AliSourceChangeSetStore.Hash(canonicalBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
        }
    }

    private static ImmutableArray<string> NormalizeDistinct(
        IEnumerable<string>? values,
        Func<string, string> normalize,
        string parameterName)
    {
        var result = (values ?? [])
            .Select(value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
                if (value.Length > MaximumPolicyEntryCharacters)
                {
                    throw new ArgumentOutOfRangeException(parameterName);
                }
                return normalize(value);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (result.Length > MaximumPolicyEntries)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        return result;
    }
}

internal sealed record AliSourceTreeMaterializationReceipt(
    string ReceiptId,
    string ChangeSetId,
    string ManifestDigest,
    string PolicyDigest,
    int CopiedEntries,
    long CopiedBytes,
    DateTimeOffset CompletedAtUtc);

internal enum AliSourceTreeMaterializationBoundary
{
    SourceFileBound,
    DestinationTreeCopied,
    StagedPreimagesBound,
    StagedOperationMutationCompleted
}

internal readonly record struct AliSourceTreeMaterializationFault(
    AliSourceTreeMaterializationBoundary Boundary,
    string RelativePath);

internal sealed partial class AliSourceChangeSetStore
{
    internal Task<AliSourceTreeMaterializationReceipt> MaterializeStagedTreeAsync(
        AliSourceChangeSet changeSet,
        string destinationRoot,
        CancellationToken cancellationToken) =>
        MaterializeStagedTreeAsync(
            changeSet,
            destinationRoot,
            AliSourceTreeMaterializationPolicy.SafeDefault,
            cancellationToken);

    internal Task<AliSourceTreeMaterializationReceipt> MaterializeStagedTreeAsync(
        AliSourceChangeSet suppliedChangeSet,
        string destinationRoot,
        AliSourceTreeMaterializationPolicy policy,
        CancellationToken cancellationToken) =>
        MaterializeStagedTreeCoreAsync(
            suppliedChangeSet,
            destinationRoot,
            policy,
            interposer: null,
            cancellationToken);

    internal Task<AliSourceTreeMaterializationReceipt> MaterializeStagedTreeForTestingAsync(
        AliSourceChangeSet suppliedChangeSet,
        string destinationRoot,
        AliSourceTreeMaterializationPolicy policy,
        Action<AliSourceTreeMaterializationFault> interposer,
        CancellationToken cancellationToken) =>
        MaterializeStagedTreeCoreAsync(
            suppliedChangeSet,
            destinationRoot,
            policy,
            interposer ?? throw new ArgumentNullException(nameof(interposer)),
            cancellationToken);

    private async Task<AliSourceTreeMaterializationReceipt> MaterializeStagedTreeCoreAsync(
        AliSourceChangeSet suppliedChangeSet,
        string destinationRoot,
        AliSourceTreeMaterializationPolicy policy,
        Action<AliSourceTreeMaterializationFault>? interposer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppliedChangeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(policy);

        var changeSet = await LoadAsync(suppliedChangeSet.Id, cancellationToken).ConfigureAwait(false);
        RequireExactManifest(suppliedChangeSet, changeSet);
        RequireOperationsIncluded(changeSet, policy);

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (PathsOverlap(changeSet.CanonicalSourceRoot, destination))
        {
            throw new InvalidOperationException("A staged verification tree cannot overlap canonical source.");
        }

        using var sourceNamespace = AliSourceWindowsNamespace.OpenAuthenticated(changeSet);
        await RequireCanonicalPreimagesAsync(sourceNamespace, changeSet, cancellationToken)
            .ConfigureAwait(false);
        using var stagedNamespace = AliSourceWindowsNamespace.Capture(destination);
        if (PathsOverlap(sourceNamespace.RootFinalPath, stagedNamespace.RootFinalPath))
        {
            throw new InvalidOperationException(
                "A staged verification tree cannot physically overlap canonical source.");
        }
        if (stagedNamespace.EnumerateDirectory(string.Empty).Length != 0)
        {
            throw new InvalidOperationException("The staged verification root must be empty.");
        }

        var copy = await CopyTreeBoundedAsync(
                sourceNamespace,
                stagedNamespace,
                policy,
                interposer,
                cancellationToken)
            .ConfigureAwait(false);
        interposer?.Invoke(new AliSourceTreeMaterializationFault(
            AliSourceTreeMaterializationBoundary.DestinationTreeCopied,
            string.Empty));

        using var stagedBindings = await BindStagedPreimagesAsync(
                stagedNamespace,
                changeSet,
                cancellationToken)
            .ConfigureAwait(false);
        interposer?.Invoke(new AliSourceTreeMaterializationFault(
            AliSourceTreeMaterializationBoundary.StagedPreimagesBound,
            string.Empty));
        foreach (var operation in changeSet.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyStagedOperationAsync(
                    stagedNamespace,
                    changeSet,
                    operation,
                    stagedBindings[operation.Sequence])
                .ConfigureAwait(false);
            interposer?.Invoke(new AliSourceTreeMaterializationFault(
                AliSourceTreeMaterializationBoundary.StagedOperationMutationCompleted,
                operation.SourceRelativePath));
        }

        foreach (var operation in changeSet.Operations)
        {
            await RequireStagedPostimageAsync(
                    stagedNamespace,
                    operation,
                    stagedBindings[operation.Sequence],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        await RequireCanonicalPreimagesAsync(sourceNamespace, changeSet, cancellationToken)
            .ConfigureAwait(false);
        foreach (var operation in changeSet.Operations)
        {
            await RequireStagedPostimageAsync(
                    stagedNamespace,
                    operation,
                    stagedBindings[operation.Sequence],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var receipt = new AliSourceTreeMaterializationReceipt(
            Guid.NewGuid().ToString("N"),
            changeSet.Id,
            changeSet.ManifestDigest,
            policy.Digest,
            copy.Entries,
            copy.Bytes,
            DateTimeOffset.UtcNow);
        var receiptDirectory = Path.Combine(GetTransactionDirectory(changeSet.Id), "materializations");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            receiptDirectory,
            "The source materialization receipt directory is not a regular local directory.");
        await WriteAuthenticatedDocumentAsync(
                Path.Combine(receiptDirectory, receipt.ReceiptId + ".json"),
                $"materialization:{changeSet.Id}:{receipt.ReceiptId}",
                receipt,
                MaximumReceiptBytes,
                CancellationToken.None)
            .ConfigureAwait(false);
        return receipt;
    }

    private static async Task RequireCanonicalPreimagesAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        foreach (var operation in changeSet.Operations)
        {
            var source = await sourceNamespace.CaptureImageAsync(
                    operation.SourceRelativePath,
                    operation.SourcePreimageIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
            AliSourceFileImage? destination = null;
            if (operation.DestinationRelativePath is not null)
            {
                destination = await sourceNamespace.CaptureImageAsync(
                        operation.DestinationRelativePath,
                        operation.DestinationPreimageIdentity,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!operation.SourcePreimage.Matches(source)
                || (operation.DestinationPreimage is not null
                    && !operation.DestinationPreimage.Matches(destination!)))
            {
                throw new InvalidOperationException(
                    "Canonical source changed while the verification tree was materialized.");
            }
        }
    }

    private static async Task<AliSourceBoundOperationSet> BindStagedPreimagesAsync(
        AliSourceWindowsNamespace stagedNamespace,
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
                if (operation.Kind == AliSourceChangeKind.Add)
                {
                    if (!stagedNamespace.IsAbsent(operation.SourceRelativePath))
                    {
                        throw new InvalidDataException("A staged add target is no longer absent.");
                    }
                    bound.SetPreimage(file: null, identity: null);
                    continue;
                }

                var access = operation.Kind == AliSourceChangeKind.Replace
                    ? AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Write
                    : AliSourceNativeFileAccess.Read | AliSourceNativeFileAccess.Delete;
                var file = stagedNamespace.TryOpenExisting(
                    operation.SourceRelativePath,
                    access,
                    expectedIdentity: null)
                    ?? throw new InvalidDataException("A staged source preimage disappeared.");
                bound.SetPreimage(file, file.Identity);
                var image = await stagedNamespace.CaptureImageAsync(file, cancellationToken)
                    .ConfigureAwait(false);
                if (!operation.SourcePreimage.Matches(image))
                {
                    throw new InvalidDataException("A staged source preimage changed.");
                }
                if (operation.Kind == AliSourceChangeKind.Rename
                    && !stagedNamespace.IsAbsent(operation.DestinationRelativePath!))
                {
                    throw new InvalidDataException("A staged rename destination is no longer absent.");
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

    private async Task ApplyStagedOperationAsync(
        AliSourceWindowsNamespace stagedNamespace,
        AliSourceChangeSet changeSet,
        AliSourceChangeOperation operation,
        AliSourceBoundOperation bound)
    {
        if (bound.State != AliSourceOperationState.Preimage)
        {
            throw new InvalidDataException("A staged operation lost its exact preimage binding.");
        }
        switch (operation.Kind)
        {
            case AliSourceChangeKind.Add:
            {
                var created = stagedNamespace.CreateNew(operation.SourceRelativePath);
                bound.BeginMutation(created);
                var content = await ReadPostImageAsync(
                        changeSet,
                        operation.Sequence,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await stagedNamespace.WriteExactAsync(created, content, CancellationToken.None)
                        .ConfigureAwait(false);
                    stagedNamespace.RequireBoundAt(created, operation.SourceRelativePath);
                    bound.SetPostimage(created, created.Identity);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
                break;
            }
            case AliSourceChangeKind.Replace:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A staged replacement has no exact source handle.");
                bound.BeginMutation(file);
                var content = await ReadPostImageAsync(
                        changeSet,
                        operation.Sequence,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    await stagedNamespace.WriteExactAsync(file, content, CancellationToken.None)
                        .ConfigureAwait(false);
                    stagedNamespace.RequireBoundAt(file, operation.SourceRelativePath);
                    bound.SetPostimage(file, file.Identity);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
                break;
            }
            case AliSourceChangeKind.Delete:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A staged deletion has no exact source handle.");
                bound.BeginMutation(file);
                stagedNamespace.Delete(file);
                bound.SetPostimage(file, identity: null);
                break;
            }
            case AliSourceChangeKind.Rename:
            {
                var file = bound.File
                    ?? throw new InvalidDataException("A staged rename has no exact source handle.");
                bound.BeginMutation(file);
                var identity = stagedNamespace.Rename(file, operation.DestinationRelativePath!);
                bound.SetPostimage(file, identity);
                break;
            }
            default:
                throw new InvalidDataException("The staged source operation kind is invalid.");
        }
    }

    private static async Task RequireStagedPostimageAsync(
        AliSourceWindowsNamespace stagedNamespace,
        AliSourceChangeOperation operation,
        AliSourceBoundOperation bound,
        CancellationToken cancellationToken)
    {
        AliSourceFileImage actual;
        AliSourceFileImage expected;
        if (operation.Kind == AliSourceChangeKind.Delete)
        {
            var file = bound.File
                ?? throw new IOException("A staged deleted object identity is missing.");
            if (!stagedNamespace.IsDeletePending(file))
            {
                throw new IOException("The exact staged deletion is not delete-pending.");
            }
            actual = AliSourceFileImage.Absent;
            expected = operation.SourcePostimage;
        }
        else
        {
            var file = bound.File
                ?? throw new IOException("A staged published object identity is missing.");
            var relativePath = operation.Kind == AliSourceChangeKind.Rename
                ? operation.DestinationRelativePath!
                : operation.SourceRelativePath;
            stagedNamespace.RequireBoundAt(file, relativePath);
            actual = await stagedNamespace.CaptureImageAsync(file, cancellationToken)
                .ConfigureAwait(false);
            expected = operation.Kind == AliSourceChangeKind.Rename
                ? operation.DestinationPostimage!
                : operation.SourcePostimage;
            if (operation.Kind == AliSourceChangeKind.Rename
                && !stagedNamespace.IsAbsent(operation.SourceRelativePath))
            {
                throw new IOException("A staged rename source remained after publication.");
            }
        }
        if (!expected.Matches(actual))
        {
            throw new IOException(
                $"Staged source operation {operation.Sequence} did not reach its authenticated postimage.");
        }
        bound.SetState(AliSourceOperationState.Postimage);
    }

    private static void RequireOperationsIncluded(
        AliSourceChangeSet changeSet,
        AliSourceTreeMaterializationPolicy policy)
    {
        foreach (var operation in changeSet.Operations)
        {
            if (IsOperationPathExcluded(operation.SourceRelativePath, policy)
                || (operation.DestinationRelativePath is not null
                    && IsOperationPathExcluded(operation.DestinationRelativePath, policy)))
            {
                throw new InvalidOperationException(
                    $"A changed source path is excluded by the materialization policy: {operation.SourceRelativePath}");
            }
        }
    }

    private static bool IsOperationPathExcluded(
        string relativePath,
        AliSourceTreeMaterializationPolicy policy)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (policy.ExcludedRelativePaths.Any(path => IsSameOrDescendant(normalized, path)))
        {
            return true;
        }

        var segments = normalized.Split('/');
        return segments.Take(Math.Max(0, segments.Length - 1)).Any(
            segment => policy.ExcludedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<(int Entries, long Bytes)> CopyTreeBoundedAsync(
        AliSourceWindowsNamespace sourceNamespace,
        AliSourceWindowsNamespace destinationNamespace,
        AliSourceTreeMaterializationPolicy policy,
        Action<AliSourceTreeMaterializationFault>? interposer,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        var entries = 0;
        long bytesCopied = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentRelative = pending.Pop();
            foreach (var entry in sourceNamespace.EnumerateDirectory(parentRelative))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = string.IsNullOrEmpty(parentRelative)
                    ? entry.Name
                    : parentRelative + "/" + entry.Name;
                if (IsExcludedEntry(relative, entry.Name, entry.IsDirectory, policy))
                {
                    continue;
                }
                if (++entries > policy.MaximumEntries)
                {
                    throw new InvalidOperationException("A staged verification tree exceeds its entry bound.");
                }
                if (entry.IsReparsePoint)
                {
                    throw new InvalidDataException("A staged verification tree cannot follow a reparse point.");
                }

                if (entry.IsDirectory)
                {
                    sourceNamespace.HoldEnumeratedDirectory(relative, entry);
                    destinationNamespace.CreateDirectory(relative);
                    pending.Push(relative);
                    continue;
                }

                using var input = sourceNamespace.OpenEnumeratedFileForRead(relative, entry);
                interposer?.Invoke(new AliSourceTreeMaterializationFault(
                    AliSourceTreeMaterializationBoundary.SourceFileBound,
                    relative));
                using var output = destinationNamespace.CreateNew(relative);
                var copied = await destinationNamespace.CopyExactAsync(
                        input,
                        output,
                        checked(policy.MaximumBytes - bytesCopied),
                        cancellationToken)
                    .ConfigureAwait(false);
                bytesCopied = checked(bytesCopied + copied);
            }
        }

        return (entries, bytesCopied);
    }

    private static bool IsExcludedEntry(
        string relativePath,
        string fileName,
        bool isDirectory,
        AliSourceTreeMaterializationPolicy policy) =>
        policy.ExcludedRelativePaths.Any(path => IsSameOrDescendant(relativePath, path))
        || (isDirectory
            && policy.ExcludedDirectoryNames.Contains(fileName, StringComparer.OrdinalIgnoreCase));

    private static bool IsSameOrDescendant(string candidate, string excludedPath) =>
        string.Equals(candidate, excludedPath, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(excludedPath + "/", StringComparison.OrdinalIgnoreCase);

    private static bool PathsOverlap(string left, string right)
    {
        var leftRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var rightRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase)
            || leftRoot.StartsWith(rightRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || rightRoot.StartsWith(leftRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
