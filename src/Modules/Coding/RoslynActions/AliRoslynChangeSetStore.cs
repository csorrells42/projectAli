using System.Collections.Immutable;
using System.Security.Cryptography;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Orchestration.Evidence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynPreparedSourceChangeSet(
    AliSourceChangeSet SourceChangeSet,
    AliRoslynSolutionFingerprintSnapshot CanonicalFingerprint,
    AliRoslynSolutionFingerprintSnapshot StagedFingerprint,
    AliRoslynDiagnosticSet BaselineDiagnostics,
    AliRoslynDiagnosticSet StagedDiagnostics,
    ImmutableArray<string> ChangedFiles,
    ImmutableArray<AliRoslynDocumentChange> DocumentChanges);

/// <summary>
/// Converts one exact Roslyn solution delta into the shared durable source manifest. Disk
/// preimages must still equal the text loaded by the canonical MSBuildWorkspace.
/// </summary>
internal sealed class AliRoslynChangeSetStore(
    AliSourceChangeSetStore sourceStore,
    AliRoslynSolutionFingerprint fingerprint,
    AliRoslynChangeSetVerifier verifier)
{
    internal async Task<AliRoslynPreparedSourceChangeSet> CreateAsync(
        Solution canonical,
        Solution staged,
        AliResolvedCodingTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(target);
        if (!canonical.ProjectIds.ToHashSet().SetEquals(staged.ProjectIds))
        {
            throw new InvalidOperationException(
                "Roslyn project additions and removals require an explicit project-document action.");
        }

        var canonicalFingerprint = await fingerprint.CaptureAsync(canonical, cancellationToken)
            .ConfigureAwait(false);
        var stagedFingerprint = await fingerprint.CaptureAsync(staged, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(canonicalFingerprint.Sha256, stagedFingerprint.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Roslyn action produced no semantic source change.");
        }

        var baselineDiagnostics = await verifier.CaptureAsync(canonical, cancellationToken)
            .ConfigureAwait(false);
        var stagedDiagnostics = await verifier.CaptureAsync(staged, cancellationToken)
            .ConfigureAwait(false);
        var requests = new List<AliSourceChangeRequest>();
        var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectChange in staged.GetChanges(canonical).GetProjectChanges())
        {
            var oldProject = canonical.GetProject(projectChange.ProjectId)
                ?? throw new InvalidOperationException("The canonical Roslyn project disappeared during preview.");
            var newProject = staged.GetProject(projectChange.ProjectId)
                ?? throw new InvalidOperationException("The staged Roslyn project disappeared during preview.");
            RejectNonDocumentProjectChanges(oldProject, newProject);

            await AddChangedAsync(
                oldProject,
                newProject,
                projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: false),
                AliRoslynDocumentKind.Regular,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken).ConfigureAwait(false);
            await AddChangedAsync(
                oldProject,
                newProject,
                projectChange.GetChangedAdditionalDocuments(),
                AliRoslynDocumentKind.Additional,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken).ConfigureAwait(false);
            await AddChangedAsync(
                oldProject,
                newProject,
                projectChange.GetChangedAnalyzerConfigDocuments(),
                AliRoslynDocumentKind.AnalyzerConfig,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken).ConfigureAwait(false);

            await AddAddedAsync(
                newProject,
                projectChange.GetAddedDocuments(),
                AliRoslynDocumentKind.Regular,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken).ConfigureAwait(false);
            await AddAddedAsync(
                newProject,
                projectChange.GetAddedAdditionalDocuments(),
                AliRoslynDocumentKind.Additional,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken).ConfigureAwait(false);
            await AddAddedAsync(
                newProject,
                projectChange.GetAddedAnalyzerConfigDocuments(),
                AliRoslynDocumentKind.AnalyzerConfig,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken).ConfigureAwait(false);

            AddRemoved(
                oldProject,
                projectChange.GetRemovedDocuments(),
                AliRoslynDocumentKind.Regular,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken);
            AddRemoved(
                oldProject,
                projectChange.GetRemovedAdditionalDocuments(),
                AliRoslynDocumentKind.Additional,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken);
            AddRemoved(
                oldProject,
                projectChange.GetRemovedAnalyzerConfigDocuments(),
                AliRoslynDocumentKind.AnalyzerConfig,
                target,
                requests,
                signatures,
                changedFiles,
                cancellationToken);
        }

        if (requests.Count == 0)
        {
            throw new InvalidOperationException("The Roslyn action produced no publishable document changes.");
        }

        var changeSet = await sourceStore
            .CreateAsync(target.RootDirectory, requests, cancellationToken)
            .ConfigureAwait(false);
        var documentChanges = await CaptureDocumentChangesAsync(
                canonical,
                staged,
                target,
                changeSet,
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            changeSet,
            canonicalFingerprint,
            stagedFingerprint,
            baselineDiagnostics,
            stagedDiagnostics,
            changedFiles.ToImmutableArray(),
            documentChanges);
    }

    private static async Task AddChangedAsync(
        Project oldProject,
        Project newProject,
        IEnumerable<DocumentId> ids,
        AliRoslynDocumentKind kind,
        AliResolvedCodingTarget target,
        ICollection<AliSourceChangeRequest> requests,
        IDictionary<string, string> signatures,
        ISet<string> changedFiles,
        CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldDocument = GetDocument(oldProject, id, kind)
                ?? throw new InvalidOperationException("The canonical Roslyn document is unavailable.");
            var newDocument = GetDocument(newProject, id, kind)
                ?? throw new InvalidOperationException("The staged Roslyn document is unavailable.");
            var oldPath = ValidatePath(target, oldDocument.FilePath);
            var newPath = ValidatePath(target, newDocument.FilePath);
            var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var disk = await ReadCanonicalTextAsync(oldPath, oldText, cancellationToken).ConfigureAwait(false);
            try
            {
                var oldRelative = NormalizeRelative(target.RootDirectory, oldPath);
                var newRelative = NormalizeRelative(target.RootDirectory, newPath);
                var expected = AliSourceExpectedFile.Present(
                    AliSourceChangeSetStore.Hash(disk),
                    disk.LongLength);
                if (!string.Equals(oldRelative, newRelative, StringComparison.OrdinalIgnoreCase))
                {
                    if (oldText.ContentEquals(newText))
                    {
                        AddRequest(
                            AliSourceChangeRequest.Rename(
                                oldRelative,
                                newRelative,
                                expected,
                                AliSourceExpectedFile.Absent),
                            "rename:" + newRelative,
                            [oldRelative, newRelative],
                            requests,
                            signatures);
                    }
                    else
                    {
                        AddRequest(
                            AliSourceChangeRequest.Delete(oldRelative, expected),
                            "delete",
                            [oldRelative],
                            requests,
                            signatures);
                        var addedBytes = EncodeNewDocument(newText);
                        try
                        {
                            AddRequest(
                                AliSourceChangeRequest.AddBytes(
                                    newRelative,
                                    addedBytes,
                                    AliSourceExpectedFile.Absent),
                                "add:" + AliSourceChangeSetStore.Hash(addedBytes),
                                [newRelative],
                                requests,
                                signatures);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(addedBytes);
                        }
                    }

                    changedFiles.Add(oldRelative);
                    changedFiles.Add(newRelative);
                    continue;
                }

                if (oldText.ContentEquals(newText))
                {
                    continue;
                }

                AddRequest(
                    AliSourceChangeRequest.ReplaceText(oldRelative, newText.ToString(), expected),
                    "replace:" + HashText(newText),
                    [oldRelative],
                    requests,
                    signatures);
                changedFiles.Add(oldRelative);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(disk);
            }
        }
    }

    private static async Task AddAddedAsync(
        Project project,
        IEnumerable<DocumentId> ids,
        AliRoslynDocumentKind kind,
        AliResolvedCodingTarget target,
        ICollection<AliSourceChangeRequest> requests,
        IDictionary<string, string> signatures,
        ISet<string> changedFiles,
        CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            var document = GetDocument(project, id, kind)
                ?? throw new InvalidOperationException("The staged Roslyn document is unavailable.");
            var path = ValidatePath(target, document.FilePath);
            var relative = NormalizeRelative(target.RootDirectory, path);
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var bytes = EncodeNewDocument(text);
            try
            {
                AddRequest(
                    AliSourceChangeRequest.AddBytes(relative, bytes, AliSourceExpectedFile.Absent),
                    "add:" + AliSourceChangeSetStore.Hash(bytes),
                    [relative],
                    requests,
                    signatures);
                changedFiles.Add(relative);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static void AddRemoved(
        Project project,
        IEnumerable<DocumentId> ids,
        AliRoslynDocumentKind kind,
        AliResolvedCodingTarget target,
        ICollection<AliSourceChangeRequest> requests,
        IDictionary<string, string> signatures,
        ISet<string> changedFiles,
        CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = GetDocument(project, id, kind)
                ?? throw new InvalidOperationException("The canonical Roslyn document is unavailable.");
            var path = ValidatePath(target, document.FilePath);
            var relative = NormalizeRelative(target.RootDirectory, path);
            using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                writeThrough: false,
                "The removed Roslyn document is not a regular local file.");
            if (stream.Length > AliSourceChangeSetStore.MaximumFileBytes)
            {
                throw new InvalidOperationException("The removed Roslyn document is unbounded.");
            }

            var hash = SHA256.HashData(stream);
            var expected = AliSourceExpectedFile.Present(Convert.ToHexString(hash), stream.Length);
            CryptographicOperations.ZeroMemory(hash);
            AddRequest(
                AliSourceChangeRequest.Delete(relative, expected),
                "delete",
                [relative],
                requests,
                signatures);
            changedFiles.Add(relative);
        }
    }

    private static async Task<byte[]> ReadCanonicalTextAsync(
        string path,
        SourceText canonicalText,
        CancellationToken cancellationToken)
    {
        await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            "The canonical Roslyn document is not a regular local file.");
        if (stream.Length > AliSourceChangeSetStore.MaximumFileBytes)
        {
            throw new InvalidOperationException("The canonical Roslyn document is unbounded.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = AliSourceTextEncoding.Detect(bytes);
            if (!string.Equals(
                    AliSourceTextEncoding.Decode(bytes, metadata),
                    canonicalText.ToString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The canonical source bytes changed after Roslyn loaded the preview workspace.");
            }

            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static byte[] EncodeNewDocument(SourceText text)
    {
        var encoding = text.Encoding ?? new System.Text.UTF8Encoding(false, true);
        var body = encoding.GetBytes(text.ToString());
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
        {
            return body;
        }

        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        CryptographicOperations.ZeroMemory(body);
        return result;
    }

    private static async Task<ImmutableArray<AliRoslynDocumentChange>> CaptureDocumentChangesAsync(
        Solution canonical,
        Solution staged,
        AliResolvedCodingTarget target,
        AliSourceChangeSet sourceChangeSet,
        CancellationToken cancellationToken)
    {
        var changes = new List<AliRoslynDocumentChange>();
        foreach (var projectChange in staged.GetChanges(canonical).GetProjectChanges())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldProject = canonical.GetProject(projectChange.ProjectId)
                ?? throw new InvalidOperationException("The canonical Roslyn project is unavailable.");
            var newProject = staged.GetProject(projectChange.ProjectId)
                ?? throw new InvalidOperationException("The staged Roslyn project is unavailable.");
            var projectRelativePath = NormalizeRelative(
                target.RootDirectory,
                ValidatePath(target, newProject.FilePath));

            await CaptureChangedDocumentsAsync(
                oldProject,
                newProject,
                projectChange.GetChangedDocuments(onlyGetDocumentsWithTextChanges: false),
                AliRoslynDocumentKind.Regular,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes,
                cancellationToken).ConfigureAwait(false);
            await CaptureChangedDocumentsAsync(
                oldProject,
                newProject,
                projectChange.GetChangedAdditionalDocuments(),
                AliRoslynDocumentKind.Additional,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes,
                cancellationToken).ConfigureAwait(false);
            await CaptureChangedDocumentsAsync(
                oldProject,
                newProject,
                projectChange.GetChangedAnalyzerConfigDocuments(),
                AliRoslynDocumentKind.AnalyzerConfig,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes,
                cancellationToken).ConfigureAwait(false);

            CaptureAddedDocuments(
                newProject,
                projectChange.GetAddedDocuments(),
                AliRoslynDocumentKind.Regular,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes);
            CaptureAddedDocuments(
                newProject,
                projectChange.GetAddedAdditionalDocuments(),
                AliRoslynDocumentKind.Additional,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes);
            CaptureAddedDocuments(
                newProject,
                projectChange.GetAddedAnalyzerConfigDocuments(),
                AliRoslynDocumentKind.AnalyzerConfig,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes);

            CaptureRemovedDocuments(
                oldProject,
                projectChange.GetRemovedDocuments(),
                AliRoslynDocumentKind.Regular,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes);
            CaptureRemovedDocuments(
                oldProject,
                projectChange.GetRemovedAdditionalDocuments(),
                AliRoslynDocumentKind.Additional,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes);
            CaptureRemovedDocuments(
                oldProject,
                projectChange.GetRemovedAnalyzerConfigDocuments(),
                AliRoslynDocumentKind.AnalyzerConfig,
                projectRelativePath,
                target,
                sourceChangeSet,
                changes);
        }

        if (changes.Count == 0 || changes.Count > AliSourceChangeSetStore.MaximumOperations)
        {
            throw new InvalidOperationException(
                "The exact Roslyn document delta is empty or exceeds the source operation bound.");
        }
        var claimed = changes.SelectMany(change => change.SourceOperationSequences).ToHashSet();
        if (sourceChangeSet.Operations.Any(operation => !claimed.Contains(operation.Sequence)))
        {
            throw new InvalidOperationException(
                "A durable source operation is not bound to an exact Roslyn document delta.");
        }
        return changes.ToImmutableArray();
    }

    private static async Task CaptureChangedDocumentsAsync(
        Project oldProject,
        Project newProject,
        IEnumerable<DocumentId> ids,
        AliRoslynDocumentKind documentKind,
        string projectRelativePath,
        AliResolvedCodingTarget target,
        AliSourceChangeSet sourceChangeSet,
        ICollection<AliRoslynDocumentChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldDocument = GetDocument(oldProject, id, documentKind)
                ?? throw new InvalidOperationException("The canonical Roslyn document is unavailable.");
            var newDocument = GetDocument(newProject, id, documentKind)
                ?? throw new InvalidOperationException("The staged Roslyn document is unavailable.");
            var sourceRelativePath = NormalizeRelative(
                target.RootDirectory,
                ValidatePath(target, oldDocument.FilePath));
            var destinationRelativePath = NormalizeRelative(
                target.RootDirectory,
                ValidatePath(target, newDocument.FilePath));
            var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var pathChanged = !PathComparer.Equals(sourceRelativePath, destinationRelativePath);
            var textChanged = !oldText.ContentEquals(newText);
            var metadataChanged = !DocumentMetadataEquals(oldDocument, newDocument, documentKind);
            if (!pathChanged && !textChanged)
            {
                if (metadataChanged)
                {
                    throw new InvalidOperationException(
                        "Roslyn document metadata changes require an explicit project-document changeset.");
                }
                continue;
            }

            var kind = (pathChanged, textChanged) switch
            {
                (false, true) => AliRoslynDocumentChangeKind.Replace,
                (true, false) => AliRoslynDocumentChangeKind.Rename,
                (true, true) => AliRoslynDocumentChangeKind.RenameAndReplace,
                _ => throw new InvalidOperationException("The Roslyn document delta is empty.")
            };
            changes.Add(BindOperations(
                new(
                    kind,
                    documentKind,
                    projectRelativePath,
                    sourceRelativePath,
                    destinationRelativePath,
                    oldDocument.Name,
                    oldDocument.Folders.ToArray(),
                    newDocument.Name,
                    newDocument.Folders.ToArray(),
                    SourceCodeKindOf(newDocument, documentKind),
                    []),
                sourceChangeSet));
        }
    }

    private static void CaptureAddedDocuments(
        Project project,
        IEnumerable<DocumentId> ids,
        AliRoslynDocumentKind documentKind,
        string projectRelativePath,
        AliResolvedCodingTarget target,
        AliSourceChangeSet sourceChangeSet,
        ICollection<AliRoslynDocumentChange> changes)
    {
        foreach (var id in ids)
        {
            var document = GetDocument(project, id, documentKind)
                ?? throw new InvalidOperationException("The staged Roslyn document is unavailable.");
            var destinationRelativePath = NormalizeRelative(
                target.RootDirectory,
                ValidatePath(target, document.FilePath));
            changes.Add(BindOperations(
                new(
                    AliRoslynDocumentChangeKind.Add,
                    documentKind,
                    projectRelativePath,
                    null,
                    destinationRelativePath,
                    null,
                    [],
                    document.Name,
                    document.Folders.ToArray(),
                    SourceCodeKindOf(document, documentKind),
                    []),
                sourceChangeSet));
        }
    }

    private static void CaptureRemovedDocuments(
        Project project,
        IEnumerable<DocumentId> ids,
        AliRoslynDocumentKind documentKind,
        string projectRelativePath,
        AliResolvedCodingTarget target,
        AliSourceChangeSet sourceChangeSet,
        ICollection<AliRoslynDocumentChange> changes)
    {
        foreach (var id in ids)
        {
            var document = GetDocument(project, id, documentKind)
                ?? throw new InvalidOperationException("The canonical Roslyn document is unavailable.");
            var sourceRelativePath = NormalizeRelative(
                target.RootDirectory,
                ValidatePath(target, document.FilePath));
            changes.Add(BindOperations(
                new(
                    AliRoslynDocumentChangeKind.Delete,
                    documentKind,
                    projectRelativePath,
                    sourceRelativePath,
                    null,
                    document.Name,
                    document.Folders.ToArray(),
                    null,
                    [],
                    SourceCodeKindOf(document, documentKind),
                    []),
                sourceChangeSet));
        }
    }

    private static AliRoslynDocumentChange BindOperations(
        AliRoslynDocumentChange change,
        AliSourceChangeSet sourceChangeSet)
    {
        IEnumerable<AliSourceChangeOperation> matches = change.Kind switch
        {
            AliRoslynDocumentChangeKind.Add => sourceChangeSet.Operations.Where(operation =>
                operation.Kind == AliSourceChangeKind.Add
                && PathComparer.Equals(operation.SourceRelativePath, change.DestinationRelativePath)),
            AliRoslynDocumentChangeKind.Replace => sourceChangeSet.Operations.Where(operation =>
                operation.Kind == AliSourceChangeKind.Replace
                && PathComparer.Equals(operation.SourceRelativePath, change.SourceRelativePath)),
            AliRoslynDocumentChangeKind.Delete => sourceChangeSet.Operations.Where(operation =>
                operation.Kind == AliSourceChangeKind.Delete
                && PathComparer.Equals(operation.SourceRelativePath, change.SourceRelativePath)),
            AliRoslynDocumentChangeKind.Rename => sourceChangeSet.Operations.Where(operation =>
                operation.Kind == AliSourceChangeKind.Rename
                && PathComparer.Equals(operation.SourceRelativePath, change.SourceRelativePath)
                && PathComparer.Equals(operation.DestinationRelativePath, change.DestinationRelativePath)),
            AliRoslynDocumentChangeKind.RenameAndReplace => sourceChangeSet.Operations.Where(operation =>
                operation.Kind == AliSourceChangeKind.Delete
                    && PathComparer.Equals(operation.SourceRelativePath, change.SourceRelativePath)
                || operation.Kind == AliSourceChangeKind.Add
                    && PathComparer.Equals(operation.SourceRelativePath, change.DestinationRelativePath)),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var bound = matches.OrderBy(operation => operation.Sequence).ToArray();
        var expectedCount = change.Kind == AliRoslynDocumentChangeKind.RenameAndReplace ? 2 : 1;
        if (bound.Length != expectedCount)
        {
            throw new InvalidOperationException(
                "The exact Roslyn document delta does not match the durable source manifest.");
        }
        return change with { SourceOperationSequences = bound.Select(operation => operation.Sequence).ToArray() };
    }

    private static bool DocumentMetadataEquals(
        TextDocument oldDocument,
        TextDocument newDocument,
        AliRoslynDocumentKind kind) =>
        string.Equals(oldDocument.Name, newDocument.Name, StringComparison.Ordinal)
        && oldDocument.Folders.SequenceEqual(newDocument.Folders, StringComparer.Ordinal)
        && SourceCodeKindOf(oldDocument, kind) == SourceCodeKindOf(newDocument, kind);

    private static SourceCodeKind SourceCodeKindOf(
        TextDocument document,
        AliRoslynDocumentKind kind) =>
        kind == AliRoslynDocumentKind.Regular
            ? ((Document)document).SourceCodeKind
            : SourceCodeKind.Regular;

    private static string HashText(SourceText text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));

    private static TextDocument? GetDocument(
        Project project,
        DocumentId id,
        AliRoslynDocumentKind kind) => kind switch
    {
        AliRoslynDocumentKind.Regular => project.GetDocument(id),
        AliRoslynDocumentKind.Additional => project.GetAdditionalDocument(id),
        AliRoslynDocumentKind.AnalyzerConfig => project.GetAnalyzerConfigDocument(id),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ValidatePath(AliResolvedCodingTarget target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("A publishable Roslyn document requires a physical path.");
        }

        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(target.RootDirectory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Roslyn attempted to stage a document outside the approved source root.");
        }

        AliCodingProjectResolver.RejectReparsePoints(target.MountRoot, full);
        return full;
    }

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static void RejectNonDocumentProjectChanges(Project oldProject, Project newProject)
    {
        if (!string.Equals(oldProject.Name, newProject.Name, StringComparison.Ordinal)
            || !string.Equals(oldProject.AssemblyName, newProject.AssemblyName, StringComparison.Ordinal)
            || !string.Equals(oldProject.Language, newProject.Language, StringComparison.Ordinal)
            || !PathComparer.Equals(oldProject.FilePath, newProject.FilePath)
            || !PathComparer.Equals(oldProject.OutputFilePath, newProject.OutputFilePath)
            || !PathComparer.Equals(oldProject.OutputRefFilePath, newProject.OutputRefFilePath)
            || !string.Equals(oldProject.DefaultNamespace, newProject.DefaultNamespace, StringComparison.Ordinal)
            || oldProject.IsSubmission != newProject.IsSubmission
            || !Equals(oldProject.CompilationOptions, newProject.CompilationOptions)
            || !Equals(oldProject.ParseOptions, newProject.ParseOptions)
            || !oldProject.ProjectReferences.SequenceEqual(newProject.ProjectReferences)
            || !oldProject.MetadataReferences.SequenceEqual(newProject.MetadataReferences)
            || !oldProject.AnalyzerReferences.SequenceEqual(newProject.AnalyzerReferences))
        {
            throw new InvalidOperationException(
                "This Roslyn action changes project or reference metadata; use an explicit project-document changeset.");
        }
    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void AddRequest(
        AliSourceChangeRequest request,
        string signature,
        IReadOnlyList<string> occupiedPaths,
        ICollection<AliSourceChangeRequest> requests,
        IDictionary<string, string> signatures)
    {
        var duplicate = true;
        foreach (var path in occupiedPaths)
        {
            if (signatures.TryGetValue(path, out var existing))
            {
                if (!string.Equals(existing, signature, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Linked Roslyn documents produced conflicting changes for the same physical path.");
                }
            }
            else
            {
                signatures.Add(path, signature);
                duplicate = false;
            }
        }

        if (!duplicate)
        {
            requests.Add(request);
        }
    }

}
