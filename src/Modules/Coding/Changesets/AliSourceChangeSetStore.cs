using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Changesets;

internal sealed partial class AliSourceChangeSetStore
{
    internal const int MaximumOperations = 256;
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    internal const long MaximumAggregateBytes = 128L * 1024 * 1024;
    internal const int MaximumRelativePathCharacters = 1024;
    internal const int MaximumManifestBytes = 1024 * 1024;
    internal const int MaximumReceiptBytes = 256 * 1024;
    internal const int MaximumJournalLineBytes = 64 * 1024;

    private const string ManifestFileName = "manifest.json";
    private const string BlobsDirectoryName = "blobs";
    private const string BackupsDirectoryName = "backups";
    private const string TransactionsDirectoryName = "transactions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly string _root;
    private readonly string _assistantProfileBinding;
    private readonly WindowsCurrentUserEvidenceProtector _protector;

    internal AliSourceChangeSetStore(string root, string assistantProfileBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(assistantProfileBinding);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _assistantProfileBinding = assistantProfileBinding.Trim();
        _protector = new WindowsCurrentUserEvidenceProtector(_root, _assistantProfileBinding);
    }

    internal string RootDirectory => _root;

    internal async Task<AliSourceChangeSet> CreateAsync(
        string sourceRoot,
        IReadOnlyList<AliSourceChangeRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is <= 0 or > MaximumOperations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requests),
                $"A source changeset requires between 1 and {MaximumOperations} operations.");
        }

        var requestedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        using var sourceNamespace = AliSourceWindowsNamespace.Capture(requestedRoot);
        var canonicalRoot = sourceNamespace.RootFinalPath;
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            _root,
            "The source changeset store is not a regular local directory.");

        var changeSetId = Guid.NewGuid().ToString("N");
        var operationBuilders = new List<PendingOperation>(requests.Count);
        var occupiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occupiedObjects = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupiedFinalNames = new Dictionary<string, string>(StringComparer.Ordinal);
        long aggregateBytes = 0;
        for (var sequence = 0; sequence < requests.Count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[sequence]
                ?? throw new ArgumentException("A source changeset request cannot be null.", nameof(requests));
            request.ExpectedSource?.Validate(nameof(request.ExpectedSource));
            request.ExpectedDestination?.Validate(nameof(request.ExpectedDestination));
            var sourceRelative = NormalizeRelativePath(request.SourceRelativePath);
            RequireUniquePath(occupiedPaths, sourceRelative);
            string? destinationRelative = null;
            if (request.Kind == AliSourceChangeKind.Rename)
            {
                destinationRelative = NormalizeRelativePath(request.DestinationRelativePath
                    ?? throw new ArgumentException("A rename requires a destination path.", nameof(requests)));
                RequireUniquePath(occupiedPaths, destinationRelative);
                if (string.Equals(sourceRelative, destinationRelative, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A rename source and destination must differ.");
                }
            }
            else if (request.DestinationRelativePath is not null)
            {
                throw new ArgumentException("Only a rename may declare a destination path.", nameof(requests));
            }

            sourceNamespace.EnsureParentOf(sourceRelative);
            if (destinationRelative is not null)
            {
                sourceNamespace.EnsureParentOf(destinationRelative);
            }

            var capturedSource = await sourceNamespace
                .CaptureOptionalFileAsync(sourceRelative, cancellationToken)
                .ConfigureAwait(false);
            var sourceBytes = capturedSource?.Bytes;
            var sourceIdentity = capturedSource?.Identity;
            var sourceImage = sourceBytes is null
                ? AliSourceFileImage.Absent
                : AliSourceFileImage.FromBytes(sourceBytes);
            if (sourceIdentity is not null)
            {
                RegisterPhysicalPath(
                    occupiedObjects,
                    occupiedFinalNames,
                    sourceRelative,
                    sourceIdentity);
            }
            RequireExpectedImage(request.ExpectedSource, sourceImage, sourceRelative, "source");

            byte[]? destinationBytes = null;
            AliSourceFileIdentity? destinationIdentity = null;
            var destinationImage = AliSourceFileImage.Absent;
            if (destinationRelative is not null)
            {
                var capturedDestination = await sourceNamespace
                    .CaptureOptionalFileAsync(destinationRelative, cancellationToken)
                    .ConfigureAwait(false);
                destinationBytes = capturedDestination?.Bytes;
                destinationIdentity = capturedDestination?.Identity;
                destinationImage = destinationBytes is null
                    ? AliSourceFileImage.Absent
                    : AliSourceFileImage.FromBytes(destinationBytes);
                if (destinationIdentity is not null)
                {
                    RegisterPhysicalPath(
                        occupiedObjects,
                        occupiedFinalNames,
                        destinationRelative,
                        destinationIdentity);
                }
                RequireExpectedImage(
                    request.ExpectedDestination,
                    destinationImage,
                    destinationRelative!,
                    "destination");
            }

            byte[]? postBytes = null;
            AliSourceEncodingMetadata? encoding = request.Encoding;
            try
            {
                switch (request.Kind)
                {
                    case AliSourceChangeKind.Add:
                        if (sourceImage.Exists)
                        {
                            throw new InvalidOperationException($"The add target already exists: {sourceRelative}");
                        }
                        postBytes = CreatePostBytes(request, existingBytes: null, ref encoding);
                        break;
                    case AliSourceChangeKind.Replace:
                        if (!sourceImage.Exists)
                        {
                            throw new FileNotFoundException($"The replace target does not exist: {sourceRelative}");
                        }
                        postBytes = CreatePostBytes(request, sourceBytes, ref encoding);
                        if (sourceImage.Matches(AliSourceFileImage.FromBytes(postBytes)))
                        {
                            throw new InvalidOperationException($"The replacement has no effect: {sourceRelative}");
                        }
                        break;
                    case AliSourceChangeKind.Delete:
                        RequireNoContent(request);
                        if (!sourceImage.Exists)
                        {
                            throw new FileNotFoundException($"The delete target does not exist: {sourceRelative}");
                        }
                        break;
                    case AliSourceChangeKind.Rename:
                        RequireNoContent(request);
                        if (!sourceImage.Exists)
                        {
                            throw new FileNotFoundException($"The rename source does not exist: {sourceRelative}");
                        }
                        if (destinationImage.Exists)
                        {
                            throw new InvalidOperationException($"The rename destination already exists: {destinationRelative}");
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(requests), "The source change kind is invalid.");
                }

                if (postBytes is not null && postBytes.Length > MaximumFileBytes)
                {
                    throw new InvalidOperationException(
                        $"A source postimage cannot exceed {MaximumFileBytes} bytes: {sourceRelative}");
                }

                aggregateBytes = checked(
                    aggregateBytes
                    + (sourceBytes?.LongLength ?? 0)
                    + (destinationBytes?.LongLength ?? 0)
                    + (postBytes?.LongLength ?? 0));
                if (aggregateBytes > MaximumAggregateBytes)
                {
                    throw new InvalidOperationException(
                        $"A source changeset cannot exceed {MaximumAggregateBytes} aggregate bytes.");
                }

                var sourcePostimage = request.Kind switch
                {
                    AliSourceChangeKind.Add or AliSourceChangeKind.Replace =>
                        AliSourceFileImage.FromBytes(postBytes!),
                    AliSourceChangeKind.Delete or AliSourceChangeKind.Rename =>
                        AliSourceFileImage.Absent,
                    _ => throw new ArgumentOutOfRangeException()
                };
                var destinationPostimage = request.Kind == AliSourceChangeKind.Rename
                    ? sourceImage
                    : null;
                operationBuilders.Add(new PendingOperation(
                    new AliSourceChangeOperation(
                        sequence,
                        request.Kind,
                        sourceRelative,
                        destinationRelative,
                        sourceImage,
                        sourcePostimage,
                        request.Kind == AliSourceChangeKind.Rename ? destinationImage : null,
                        destinationPostimage,
                        sourceIdentity,
                        request.Kind == AliSourceChangeKind.Rename ? destinationIdentity : null,
                        postBytes is null ? null : Hash(postBytes),
                        postBytes?.LongLength ?? 0,
                        encoding),
                    postBytes?.ToArray(),
                    sourceBytes?.ToArray()));
            }
            finally
            {
                if (sourceBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(sourceBytes);
                }
                if (destinationBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(destinationBytes);
                }
                if (postBytes is not null)
                {
                    CryptographicOperations.ZeroMemory(postBytes);
                }
            }
        }

        var unsigned = new AliSourceChangeSetUnsigned(
            changeSetId,
            canonicalRoot,
            DateTimeOffset.UtcNow,
            sourceNamespace.RootIdentity,
            sourceNamespace.CapturedSpines,
            operationBuilders.Select(item => item.Operation).ToImmutableArray());
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions);
        if (unsignedBytes.Length is <= 0 or > MaximumManifestBytes)
        {
            ZeroPending(operationBuilders);
            throw new InvalidOperationException("The source changeset manifest is invalid or unbounded.");
        }

        var manifestDigest = Hash(unsignedBytes);
        var changeSet = new AliSourceChangeSet(
            unsigned.Id,
            unsigned.CanonicalSourceRoot,
            unsigned.CreatedAtUtc,
            manifestDigest,
            unsigned.SourceRootIdentity,
            unsigned.NamespaceSpines,
            unsigned.Operations);
        try
        {
            await SaveAsync(changeSet, operationBuilders, unsignedBytes, cancellationToken).ConfigureAwait(false);
            return changeSet;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unsignedBytes);
            ZeroPending(operationBuilders);
        }
    }

    [Obsolete("Use typed AliSourceChangeRequest entries with explicit Roslyn preimage bindings.")]
    internal Task<AliSourceChangeSet> CreateAsync(
        string sourceRoot,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count is <= 0 or > MaximumOperations)
        {
            throw new ArgumentOutOfRangeException(nameof(replacements));
        }

        var requests = replacements
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => AliSourceChangeRequest.ReplaceText(
                Path.GetRelativePath(Path.GetFullPath(sourceRoot), Path.GetFullPath(item.Key)),
                item.Value ?? string.Empty))
            .ToArray();
        return CreateAsync(sourceRoot, requests, cancellationToken);
    }

    internal async Task<AliSourceChangeSet> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ValidateId(id);
        var manifestPath = GetManifestPath(id);
        var bytes = await ReadBoundedStoreFileAsync(
            manifestPath,
            MaximumManifestBytes,
            "The source changeset manifest is missing, invalid, or unbounded.",
            cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<AliSourceManifestEnvelope>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The source changeset manifest could not be read.");
            var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Manifest, JsonOptions);
            try
            {
                if (!await VerifyAuthenticationAsync(
                        "manifest",
                        unsignedBytes,
                        envelope.AuthenticationTag,
                        cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The source changeset manifest authentication failed.");
                }

                var digest = Hash(unsignedBytes);
                var changeSet = new AliSourceChangeSet(
                    envelope.Manifest.Id,
                    envelope.Manifest.CanonicalSourceRoot,
                    envelope.Manifest.CreatedAtUtc,
                    digest,
                    envelope.Manifest.SourceRootIdentity,
                    envelope.Manifest.NamespaceSpines,
                    envelope.Manifest.Operations);
                ValidateManifest(changeSet, id);
                foreach (var operation in changeSet.Operations)
                {
                    if (operation.ContentBlobSha256 is not null)
                    {
                        var content = await ReadProtectedPayloadAsync(
                            changeSet,
                            operation,
                            isBackup: false,
                            cancellationToken).ConfigureAwait(false);
                        CryptographicOperations.ZeroMemory(content);
                    }
                    if (operation.SourcePreimage.Exists)
                    {
                        var backup = await ReadProtectedPayloadAsync(
                            changeSet,
                            operation,
                            isBackup: true,
                            cancellationToken).ConfigureAwait(false);
                        CryptographicOperations.ZeroMemory(backup);
                    }
                }

                return changeSet;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(unsignedBytes);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The source changeset manifest is malformed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal async Task<byte[]> ReadPostImageAsync(
        string changeSetId,
        int operationIndex,
        CancellationToken cancellationToken)
    {
        var changeSet = await LoadAsync(changeSetId, cancellationToken).ConfigureAwait(false);
        if ((uint)operationIndex >= (uint)changeSet.Operations.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(operationIndex));
        }

        var operation = changeSet.Operations[operationIndex];
        if (operation.ContentBlobSha256 is null)
        {
            if (operation.Kind == AliSourceChangeKind.Rename)
            {
                return await ReadProtectedPayloadAsync(
                    changeSet,
                    operation,
                    isBackup: true,
                    cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The selected operation has no materialized postimage.");
        }

        return await ReadProtectedPayloadAsync(
            changeSet,
            operation,
            isBackup: false,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<byte[]> ReadPostImageAsync(
        AliSourceChangeSet changeSet,
        int operationIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return ReadPostImageAsync(changeSet.Id, operationIndex, cancellationToken);
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string NormalizeSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hexadecimal characters.", nameof(value));
        }
        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("A SHA-256 digest is malformed.", nameof(value), ex);
        }
        return value.ToLowerInvariant();
    }

    internal static void RequireExactManifest(AliSourceChangeSet supplied, AliSourceChangeSet loaded)
    {
        if (!string.Equals(supplied.Id, loaded.Id, StringComparison.Ordinal)
            || !string.Equals(supplied.ManifestDigest, loaded.ManifestDigest, StringComparison.Ordinal)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(supplied.CanonicalSourceRoot)),
                loaded.CanonicalSourceRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The supplied source changeset does not match its authenticated manifest.");
        }
    }

    private static void ValidateManifest(AliSourceChangeSet changeSet, string requestedId)
    {
        ValidateId(changeSet.Id);
        if (!string.Equals(changeSet.Id, requestedId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A source manifest was replayed under a different changeset ID.");
        }
        if (changeSet.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A source manifest timestamp is not normalized to UTC.");
        }
        _ = NormalizeSha256(changeSet.ManifestDigest);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(changeSet.CanonicalSourceRoot));
        if (!string.Equals(canonicalRoot, changeSet.CanonicalSourceRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A source manifest root is not canonical.");
        }
        if (changeSet.SourceRootIdentity is null || changeSet.NamespaceSpines.IsDefault)
        {
            throw new InvalidDataException("A source manifest has no authenticated namespace identity.");
        }
        changeSet.SourceRootIdentity.Validate("source root");
        if (changeSet.Operations.Length is <= 0 or > MaximumOperations)
        {
            throw new InvalidDataException("A source manifest operation count is invalid or unbounded.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var physicalFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var finalNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var requiredSpines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = 0;
        for (var index = 0; index < changeSet.Operations.Length; index++)
        {
            var operation = changeSet.Operations[index];
            if (operation.Sequence != index || !Enum.IsDefined(operation.Kind))
            {
                throw new InvalidDataException("A source manifest operation sequence or kind is invalid.");
            }
            var source = NormalizeRelativePath(operation.SourceRelativePath);
            if (!string.Equals(source, operation.SourceRelativePath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A source manifest path is not canonical.");
            }
            RequireUniquePath(paths, source);
            _ = ResolveContainedPath(canonicalRoot, source);
            AddRequiredSpines(requiredSpines, source);
            operation.SourcePreimage.Validate();
            operation.SourcePostimage.Validate();
            operation.DestinationPreimage?.Validate();
            operation.DestinationPostimage?.Validate();
            operation.Encoding?.Validate();
            ValidateManifestFileIdentity(
                operation.SourcePreimage,
                operation.SourcePreimageIdentity,
                changeSet.SourceRootIdentity,
                source,
                physicalFiles,
                finalNames);

            switch (operation.Kind)
            {
                case AliSourceChangeKind.Add:
                    RequireManifestImages(
                        operation,
                        sourcePreExists: false,
                        sourcePostExists: true,
                        destinationExpected: false,
                        contentExpected: true);
                    break;
                case AliSourceChangeKind.Replace:
                    RequireManifestImages(
                        operation,
                        sourcePreExists: true,
                        sourcePostExists: true,
                        destinationExpected: false,
                        contentExpected: true);
                    if (operation.SourcePreimage.Matches(operation.SourcePostimage))
                    {
                        throw new InvalidDataException("A replacement manifest has no effect.");
                    }
                    break;
                case AliSourceChangeKind.Delete:
                    RequireManifestImages(
                        operation,
                        sourcePreExists: true,
                        sourcePostExists: false,
                        destinationExpected: false,
                        contentExpected: false);
                    break;
                case AliSourceChangeKind.Rename:
                    RequireManifestImages(
                        operation,
                        sourcePreExists: true,
                        sourcePostExists: false,
                        destinationExpected: true,
                        contentExpected: false);
                    var destination = NormalizeRelativePath(operation.DestinationRelativePath!);
                    if (!string.Equals(destination, operation.DestinationRelativePath, StringComparison.Ordinal)
                        || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("A rename manifest destination is invalid.");
                    }
                    RequireUniquePath(paths, destination);
                    _ = ResolveContainedPath(canonicalRoot, destination);
                    AddRequiredSpines(requiredSpines, destination);
                    ValidateManifestFileIdentity(
                        operation.DestinationPreimage!,
                        operation.DestinationPreimageIdentity,
                        changeSet.SourceRootIdentity,
                        destination,
                        physicalFiles,
                        finalNames);
                    if (operation.DestinationPreimage!.Exists
                        || !operation.DestinationPostimage!.Matches(operation.SourcePreimage))
                    {
                        throw new InvalidDataException("A rename manifest has inconsistent destination images.");
                    }
                    break;
                default:
                    throw new InvalidDataException("A source manifest operation kind is invalid.");
            }

            aggregate = checked(
                aggregate
                + operation.SourcePreimage.Length
                + (operation.DestinationPreimage?.Length ?? 0)
                + operation.ContentLength);
            if (aggregate > MaximumAggregateBytes)
            {
                throw new InvalidDataException("A source manifest exceeds its aggregate byte bound.");
            }
        }

        var orderedSpines = changeSet.NamespaceSpines
            .OrderBy(spine => spine.RelativeDirectoryPath.Count(character => character == '/'))
            .ThenBy(spine => spine.RelativeDirectoryPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedSpines.Length != requiredSpines.Count
            || orderedSpines.Length != changeSet.NamespaceSpines.Length)
        {
            throw new InvalidDataException("A source manifest namespace spine is incomplete or duplicated.");
        }
        var directoryObjects = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [changeSet.SourceRootIdentity.ObjectKey] = string.Empty
        };
        for (var index = 0; index < orderedSpines.Length; index++)
        {
            var spine = orderedSpines[index]
                ?? throw new InvalidDataException("A source manifest namespace spine is null.");
            var normalized = NormalizeRelativePath(spine.RelativeDirectoryPath);
            if (!string.Equals(normalized, spine.RelativeDirectoryPath, StringComparison.Ordinal)
                || !requiredSpines.Remove(normalized)
                || !Equals(spine, changeSet.NamespaceSpines[index]))
            {
                throw new InvalidDataException("A source manifest namespace spine is not canonical and ordered.");
            }
            spine.Identity.Validate("source namespace spine");
            if (spine.Identity.VolumeSerialNumber != changeSet.SourceRootIdentity.VolumeSerialNumber
                || !directoryObjects.TryAdd(spine.Identity.ObjectKey, normalized))
            {
                throw new InvalidDataException(
                    "A source manifest namespace spine is cross-volume or physically aliased.");
            }
        }
        if (requiredSpines.Count != 0)
        {
            throw new InvalidDataException("A source manifest namespace spine is incomplete.");
        }
    }

    private static void RequireManifestImages(
        AliSourceChangeOperation operation,
        bool sourcePreExists,
        bool sourcePostExists,
        bool destinationExpected,
        bool contentExpected)
    {
        if (operation.SourcePreimage.Exists != sourcePreExists
            || operation.SourcePostimage.Exists != sourcePostExists
            || (operation.DestinationRelativePath is not null) != destinationExpected
            || (operation.DestinationPreimage is not null) != destinationExpected
            || (operation.DestinationPostimage is not null) != destinationExpected
            || operation.DestinationPreimageIdentity is not null
            || (operation.ContentBlobSha256 is not null) != contentExpected)
        {
            throw new InvalidDataException("A source manifest operation has inconsistent images.");
        }
        if (contentExpected)
        {
            var digest = NormalizeSha256(operation.ContentBlobSha256!);
            if (!string.Equals(digest, operation.SourcePostimage.Sha256, StringComparison.Ordinal)
                || operation.ContentLength != operation.SourcePostimage.Length)
            {
                throw new InvalidDataException("A source manifest content blob does not match its postimage.");
            }
        }
        else if (operation.ContentLength != 0 || operation.Encoding is not null)
        {
            throw new InvalidDataException("A content-free source operation carries content metadata.");
        }
    }

    private static byte[] CreatePostBytes(
        AliSourceChangeRequest request,
        byte[]? existingBytes,
        ref AliSourceEncodingMetadata? encoding)
    {
        if (request.Content is not null && request.Text is not null)
        {
            throw new ArgumentException("A source request cannot carry both bytes and text.");
        }
        if (request.Content is not null)
        {
            encoding = null;
            return request.Content.ToArray();
        }
        if (request.Text is null)
        {
            throw new ArgumentException("An add or replacement requires content.");
        }
        if (encoding is null)
        {
            encoding = existingBytes is null
                ? AliSourceTextEncoding.CreateMetadata("utf-8", emitPreamble: false)
                : AliSourceTextEncoding.Detect(existingBytes);
        }
        encoding.Validate();
        return AliSourceTextEncoding.Encode(request.Text, encoding);
    }

    private static void RequireNoContent(AliSourceChangeRequest request)
    {
        if (request.Content is not null || request.Text is not null || request.Encoding is not null)
        {
            throw new ArgumentException("A delete or rename cannot carry source content.");
        }
    }

    private static void RequireExpectedImage(
        AliSourceExpectedFile? expected,
        AliSourceFileImage actual,
        string relativePath,
        string role)
    {
        if (expected is null)
        {
            return;
        }
        var normalized = expected.Exists
            ? new AliSourceFileImage(true, NormalizeSha256(expected.Sha256!), expected.Length)
            : AliSourceFileImage.Absent;
        if (!normalized.Matches(actual))
        {
            throw new InvalidOperationException(
                $"The exact expected {role} preimage is stale: {relativePath}");
        }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Length != 32
            || id.Any(character => !char.IsAsciiHexDigit(character))
            || !string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The source changeset ID is invalid.", nameof(id));
        }
    }

    private static void RequireUniquePath(ISet<string> paths, string relativePath)
    {
        if (!paths.Add(relativePath))
        {
            throw new InvalidOperationException($"A source changeset path is duplicated: {relativePath}");
        }
    }

    private static void RegisterPhysicalPath(
        IDictionary<string, string> objectPaths,
        IDictionary<string, string> finalNames,
        string relativePath,
        AliSourceFileIdentity identity)
    {
        string? alias = null;
        if (objectPaths.TryGetValue(identity.ObjectKey, out var objectAlias))
        {
            alias = objectAlias;
        }
        else if (finalNames.TryGetValue(identity.FinalNameDigest, out var nameAlias))
        {
            alias = nameAlias;
        }
        if (alias is not null)
        {
            throw new InvalidOperationException(
                $"One physical source file entered the changeset through two namespace names: "
                + $"{alias} and {relativePath}");
        }
        objectPaths.Add(identity.ObjectKey, relativePath);
        finalNames.Add(identity.FinalNameDigest, relativePath);
    }

    private static void ValidateManifestFileIdentity(
        AliSourceFileImage image,
        AliSourceFileIdentity? identity,
        AliSourceFileIdentity rootIdentity,
        string relativePath,
        IDictionary<string, string> objectPaths,
        IDictionary<string, string> finalNames)
    {
        if (image.Exists != (identity is not null))
        {
            throw new InvalidDataException(
                "A source manifest file image and FILE_ID_INFO binding disagree.");
        }
        if (identity is null)
        {
            return;
        }
        identity.Validate("source file");
        if (identity.VolumeSerialNumber != rootIdentity.VolumeSerialNumber
            || identity.NumberOfLinks != 1
            || objectPaths.ContainsKey(identity.ObjectKey)
            || finalNames.ContainsKey(identity.FinalNameDigest))
        {
            throw new InvalidDataException(
                $"A source manifest file is cross-volume, hard-linked, or physically aliased: {relativePath}");
        }
        objectPaths.Add(identity.ObjectKey, relativePath);
        finalNames.Add(identity.FinalNameDigest, relativePath);
    }

    private static void AddRequiredSpines(ISet<string> required, string relativePath)
    {
        var segments = NormalizeRelativePath(relativePath).Split('/');
        var current = string.Empty;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = current.Length == 0 ? segments[index] : current + "/" + segments[index];
            required.Add(current);
        }
    }

    private static void ZeroPending(IEnumerable<PendingOperation> pending)
    {
        foreach (var item in pending)
        {
            if (item.PostBytes is not null)
            {
                CryptographicOperations.ZeroMemory(item.PostBytes);
            }
            if (item.BackupBytes is not null)
            {
                CryptographicOperations.ZeroMemory(item.BackupBytes);
            }
        }
    }

    private sealed record PendingOperation(
        AliSourceChangeOperation Operation,
        byte[]? PostBytes,
        byte[]? BackupBytes);

    private sealed record AliSourceChangeSetUnsigned(
        string Id,
        string CanonicalSourceRoot,
        DateTimeOffset CreatedAtUtc,
        AliSourceFileIdentity SourceRootIdentity,
        ImmutableArray<AliSourceNamespaceSpineIdentity> NamespaceSpines,
        ImmutableArray<AliSourceChangeOperation> Operations);

    private sealed record AliSourceManifestEnvelope(
        AliSourceChangeSetUnsigned Manifest,
        string AuthenticationTag);

}
