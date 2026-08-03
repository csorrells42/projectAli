using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed class AliRoslynPreviewWorkspaceSession : IDisposable
{
    public AliRoslynPreviewWorkspaceSession(
        AdhocWorkspace workspace,
        Solution solution,
        AliRoslynSolutionFingerprintSnapshot canonicalFingerprint,
        AliRoslynSolutionFingerprintSnapshot isolatedFingerprint,
        IReadOnlyList<AliRoslynResolvedMetadataReference> metadataReferences,
        IReadOnlyList<AliRoslynResolvedAnalyzerReference> analyzerReferences)
    {
        Workspace = workspace;
        Solution = solution;
        CanonicalFingerprint = canonicalFingerprint;
        IsolatedFingerprint = isolatedFingerprint;
        MetadataReferences = metadataReferences;
        AnalyzerReferences = analyzerReferences;
    }

    public AdhocWorkspace Workspace { get; }
    public Solution Solution { get; }
    public AliRoslynSolutionFingerprintSnapshot CanonicalFingerprint { get; }
    public AliRoslynSolutionFingerprintSnapshot IsolatedFingerprint { get; }
    public IReadOnlyList<AliRoslynResolvedMetadataReference> MetadataReferences { get; }
    public IReadOnlyList<AliRoslynResolvedAnalyzerReference> AnalyzerReferences { get; }

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Clones the exact solution produced by the canonical MSBuildWorkspace into a
/// short-lived AdhocWorkspace used only for semantic preview and verification.
/// </summary>
internal sealed class AliRoslynPreviewWorkspaceManager
{
    private readonly AliRoslynTargetReferenceResolver _referenceResolver;
    private readonly AliRoslynSolutionFingerprint _fingerprint;

    public AliRoslynPreviewWorkspaceManager(
        AliRoslynTargetReferenceResolver? referenceResolver = null,
        AliRoslynSolutionFingerprint? fingerprint = null)
    {
        _referenceResolver = referenceResolver ?? new AliRoslynTargetReferenceResolver();
        _fingerprint = fingerprint ?? new AliRoslynSolutionFingerprint(_referenceResolver);
    }

    public Task<AliRoslynPreviewWorkspaceSession> CreateAsync(
        AliRoslynWorkspaceSession canonicalSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonicalSession);
        return CreateAsync(
            canonicalSession.Solution,
            canonicalSession.Warnings,
            canonicalSession.SemanticFingerprint,
            cancellationToken);
    }

    internal Task<AliRoslynPreviewWorkspaceSession> CreateAsync(
        Solution canonicalSolution,
        IReadOnlyList<string> canonicalWorkspaceWarnings,
        CancellationToken cancellationToken) =>
        CreateAsync(
            canonicalSolution,
            canonicalWorkspaceWarnings,
            expectedCanonicalFingerprint: null,
            cancellationToken);

    private async Task<AliRoslynPreviewWorkspaceSession> CreateAsync(
        Solution canonicalSolution,
        IReadOnlyList<string> canonicalWorkspaceWarnings,
        AliRoslynSolutionFingerprintSnapshot? expectedCanonicalFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canonicalSolution);
        ArgumentNullException.ThrowIfNull(canonicalWorkspaceWarnings);
        var warnings = canonicalWorkspaceWarnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (warnings.Length != 0)
        {
            throw new InvalidOperationException(
                "Roslyn refused to create an isolated preview because the canonical MSBuildWorkspace reported: "
                + string.Join(" | ", warnings));
        }

        var projectIds = canonicalSolution.ProjectIds.ToHashSet();
        foreach (var project in canonicalSolution.Projects)
        {
            foreach (var reference in project.ProjectReferences)
            {
                if (!projectIds.Contains(reference.ProjectId))
                {
                    throw new InvalidOperationException(
                        $"Project '{project.Name}' contains an unresolved project reference '{reference.ProjectId}'.");
                }
            }
        }

        var capturedCanonicalFingerprint = await _fingerprint.CaptureAsync(
                canonicalSolution,
                cancellationToken)
            .ConfigureAwait(false);
        if (expectedCanonicalFingerprint is not null
            && !Equals(expectedCanonicalFingerprint, capturedCanonicalFingerprint))
        {
            throw new InvalidOperationException(
                "The canonical Roslyn solution changed after its exact semantic fingerprint was bound.");
        }
        var canonicalFingerprint = expectedCanonicalFingerprint
            ?? capturedCanonicalFingerprint;
        var projectInfos = new List<ProjectInfo>(canonicalSolution.ProjectIds.Count);
        var analyzerConfigInfos = ImmutableArray.CreateBuilder<DocumentInfo>();
        var allMetadata = new List<AliRoslynResolvedMetadataReference>();
        var allAnalyzers = new List<AliRoslynResolvedAnalyzerReference>();

        foreach (var project in canonicalSolution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var references = await _referenceResolver.ResolveAsync(project, cancellationToken).ConfigureAwait(false);
            allMetadata.AddRange(references.MetadataReferences);
            allAnalyzers.AddRange(references.AnalyzerReferences);

            var documents = await CloneDocumentsAsync(project.Documents, cancellationToken).ConfigureAwait(false);
            var additional = await CloneTextDocumentsAsync(project.AdditionalDocuments, cancellationToken)
                .ConfigureAwait(false);
            analyzerConfigInfos.AddRange(
                await CloneTextDocumentsAsync(project.AnalyzerConfigDocuments, cancellationToken).ConfigureAwait(false));

            projectInfos.Add(ProjectInfo.Create(
                project.Id,
                project.Version,
                project.Name,
                project.AssemblyName ?? project.Name,
                project.Language,
                project.FilePath,
                project.OutputFilePath,
                project.CompilationOptions,
                project.ParseOptions,
                documents,
                project.ProjectReferences,
                references.MetadataReferences.Select(item => item.Reference),
                references.AnalyzerReferences.Select(item => item.Reference),
                additional,
                project.IsSubmission,
                null,
                project.DefaultNamespace));
        }

        var solutionAnalyzers = await _referenceResolver.ResolveSolutionAnalyzersAsync(
                canonicalSolution,
                cancellationToken)
            .ConfigureAwait(false);
        allAnalyzers.AddRange(solutionAnalyzers);

        var workspace = new AdhocWorkspace();
        var previewWarnings = new ConcurrentQueue<string>();
        workspace.RegisterWorkspaceFailedHandler(args => previewWarnings.Enqueue(args.Diagnostic.Message));
        try
        {
            var solution = workspace.AddSolution(SolutionInfo.Create(
                canonicalSolution.Id,
                canonicalSolution.Version,
                canonicalSolution.FilePath,
                projectInfos,
                solutionAnalyzers.Select(item => item.Reference)));
            if (analyzerConfigInfos.Count != 0)
            {
                solution = solution.AddAnalyzerConfigDocuments(analyzerConfigInfos.ToImmutable());
            }

            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException("Roslyn could not materialize the exact isolated preview solution.");
            }

            solution = workspace.CurrentSolution;
            // AdhocWorkspace normalizes these two MSBuild-only project values when a
            // SolutionInfo is applied. Rebind the exact canonical values on the detached
            // semantic solution; every subsequent preview and fingerprint uses this value.
            foreach (var project in canonicalSolution.Projects)
            {
                if (project.CompilationOptions is not null)
                {
                    solution = solution.WithProjectCompilationOptions(
                        project.Id,
                        project.CompilationOptions);
                }
                if (project.ParseOptions is not null)
                {
                    solution = solution.WithProjectParseOptions(project.Id, project.ParseOptions);
                }
                if (!string.IsNullOrWhiteSpace(project.OutputRefFilePath))
                {
                    solution = solution.WithProjectOutputRefFilePath(project.Id, project.OutputRefFilePath);
                }
                if (!string.IsNullOrWhiteSpace(project.DefaultNamespace))
                {
                    solution = solution.WithProjectDefaultNamespace(project.Id, project.DefaultNamespace);
                }
            }
            foreach (var project in solution.Projects)
            {
                if (await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    throw new InvalidOperationException(
                        $"Roslyn could not compile isolated preview project '{project.Name}'.");
                }
            }

            if (!previewWarnings.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Roslyn's isolated preview workspace reported: "
                    + string.Join(" | ", previewWarnings.Distinct(StringComparer.Ordinal)));
            }

            var previewFingerprint = await _fingerprint.CaptureAsync(solution, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(canonicalFingerprint.Sha256, previewFingerprint.Sha256, StringComparison.Ordinal))
            {
                var mismatch = await DescribeMismatchAsync(
                        canonicalSolution,
                        solution,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Roslyn's isolated preview did not preserve the canonical solution's exact semantic fingerprint"
                    + $" ({string.Join(",", mismatch)})." );
            }

            return new(
                workspace,
                solution,
                canonicalFingerprint,
                previewFingerprint,
                allMetadata,
                allAnalyzers);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static async Task<IReadOnlyList<DocumentInfo>> CloneDocumentsAsync(
        IEnumerable<Document> documents,
        CancellationToken cancellationToken)
    {
        var clones = new List<DocumentInfo>();
        foreach (var document in documents)
        {
            var loader = await CreateLoaderAsync(document, cancellationToken).ConfigureAwait(false);
            clones.Add(DocumentInfo.Create(
                document.Id,
                document.Name,
                document.Folders,
                document.SourceCodeKind,
                loader,
                document.FilePath,
                isGenerated: false));
        }

        return clones;
    }

    private static async Task<IReadOnlyList<DocumentInfo>> CloneTextDocumentsAsync<TDocument>(
        IEnumerable<TDocument> documents,
        CancellationToken cancellationToken)
        where TDocument : TextDocument
    {
        var clones = new List<DocumentInfo>();
        foreach (var document in documents)
        {
            var loader = await CreateLoaderAsync(document, cancellationToken).ConfigureAwait(false);
            clones.Add(DocumentInfo.Create(
                document.Id,
                document.Name,
                document.Folders,
                SourceCodeKind.Regular,
                loader,
                document.FilePath,
                isGenerated: false));
        }

        return clones;
    }

    private static async Task<TextLoader> CreateLoaderAsync(
        TextDocument document,
        CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
        return TextLoader.From(TextAndVersion.Create(text, version, document.FilePath));
    }

    private async Task<IReadOnlyList<string>> DescribeMismatchAsync(
        Solution canonical,
        Solution preview,
        CancellationToken cancellationToken)
    {
        var differences = new List<string>();
        if (!string.Equals(canonical.FilePath, preview.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add("solution-path");
        }
        if (!canonical.ProjectIds.SequenceEqual(preview.ProjectIds))
        {
            differences.Add("project-identities");
        }

        foreach (var projectId in canonical.ProjectIds.Intersect(preview.ProjectIds))
        {
            var left = canonical.GetProject(projectId)!;
            var right = preview.GetProject(projectId)!;
            AddDifference(differences, "project-name", left.Name, right.Name);
            AddDifference(differences, "project-assembly-name", left.AssemblyName, right.AssemblyName);
            AddDifference(differences, "project-language", left.Language, right.Language);
            AddPathDifference(differences, "project-path", left.FilePath, right.FilePath);
            AddPathDifference(differences, "project-output-path", left.OutputFilePath, right.OutputFilePath);
            AddPathDifference(differences, "project-output-ref-path", left.OutputRefFilePath, right.OutputRefFilePath);
            AddDifference(differences, "project-default-namespace", left.DefaultNamespace, right.DefaultNamespace);
            differences.AddRange(CompilationOptionDifferences(left.CompilationOptions, right.CompilationOptions));
            if (!Equals(left.ParseOptions, right.ParseOptions))
            {
                differences.Add("parse-options");
            }
            if (!left.ProjectReferences.SequenceEqual(right.ProjectReferences))
            {
                differences.Add("project-references");
            }

            var leftReferences = await _referenceResolver.ResolveAsync(left, cancellationToken)
                .ConfigureAwait(false);
            var rightReferences = await _referenceResolver.ResolveAsync(right, cancellationToken)
                .ConfigureAwait(false);
            if (!leftReferences.MetadataReferences
                    .Select(item => (item.PhysicalPath, item.Sha256, item.Properties))
                    .SequenceEqual(rightReferences.MetadataReferences
                        .Select(item => (item.PhysicalPath, item.Sha256, item.Properties))))
            {
                differences.Add("metadata-references");
            }
            if (!leftReferences.AnalyzerReferences
                    .Select(item => (item.PhysicalPath, item.Sha256, item.Reference.GetType().AssemblyQualifiedName))
                    .SequenceEqual(rightReferences.AnalyzerReferences
                        .Select(item => (item.PhysicalPath, item.Sha256, item.Reference.GetType().AssemblyQualifiedName))))
            {
                differences.Add("analyzer-references");
            }

            if (!await TextDocumentsEqualAsync(left.Documents, right.Documents, cancellationToken)
                    .ConfigureAwait(false))
            {
                differences.Add("documents");
            }
            if (!await TextDocumentsEqualAsync(
                    left.AdditionalDocuments,
                    right.AdditionalDocuments,
                    cancellationToken).ConfigureAwait(false))
            {
                differences.Add("additional-documents");
            }
            if (!await TextDocumentsEqualAsync(
                    left.AnalyzerConfigDocuments,
                    right.AnalyzerConfigDocuments,
                    cancellationToken).ConfigureAwait(false))
            {
                differences.Add("analyzer-config-documents");
            }
        }

        var leftSolutionAnalyzers = await _referenceResolver.ResolveSolutionAnalyzersAsync(
                canonical,
                cancellationToken)
            .ConfigureAwait(false);
        var rightSolutionAnalyzers = await _referenceResolver.ResolveSolutionAnalyzersAsync(
                preview,
                cancellationToken)
            .ConfigureAwait(false);
        if (!leftSolutionAnalyzers
                .Select(item => (item.PhysicalPath, item.Sha256, item.Reference.GetType().AssemblyQualifiedName))
                .SequenceEqual(rightSolutionAnalyzers
                    .Select(item => (item.PhysicalPath, item.Sha256, item.Reference.GetType().AssemblyQualifiedName))))
        {
            differences.Add("solution-analyzers");
        }

        return differences.Count == 0 ? ["unclassified-input"] : differences.Distinct().ToArray();
    }

    private static IReadOnlyList<string> CompilationOptionDifferences(
        CompilationOptions? left,
        CompilationOptions? right)
    {
        var differences = new List<string>();
        if (left is null || right is null)
        {
            return left is null && right is null ? differences : ["compilation-options-null"];
        }
        AddDifference(differences, "compilation-options-type", left.GetType().AssemblyQualifiedName, right.GetType().AssemblyQualifiedName);
        AddDifference(differences, "compilation-output-kind", left.OutputKind, right.OutputKind);
        AddDifference(differences, "compilation-module-name", left.ModuleName, right.ModuleName);
        AddDifference(differences, "compilation-main-type", left.MainTypeName, right.MainTypeName);
        AddDifference(differences, "compilation-script-class", left.ScriptClassName, right.ScriptClassName);
        AddDifference(differences, "compilation-optimization", left.OptimizationLevel, right.OptimizationLevel);
        AddDifference(differences, "compilation-overflow", left.CheckOverflow, right.CheckOverflow);
        AddDifference(differences, "compilation-platform", left.Platform, right.Platform);
        AddDifference(differences, "compilation-general-diagnostic", left.GeneralDiagnosticOption, right.GeneralDiagnosticOption);
        AddDifference(differences, "compilation-warning-level", left.WarningLevel, right.WarningLevel);
        AddDifference(differences, "compilation-concurrent", left.ConcurrentBuild, right.ConcurrentBuild);
        AddDifference(differences, "compilation-deterministic", left.Deterministic, right.Deterministic);
        AddDifference(differences, "compilation-report-suppressed", left.ReportSuppressedDiagnostics, right.ReportSuppressedDiagnostics);
        AddDifference(differences, "compilation-metadata-import", left.MetadataImportOptions, right.MetadataImportOptions);
        if (!left.SpecificDiagnosticOptions.OrderBy(item => item.Key, StringComparer.Ordinal)
                .SequenceEqual(right.SpecificDiagnosticOptions.OrderBy(item => item.Key, StringComparer.Ordinal)))
        {
            differences.Add("compilation-specific-diagnostics");
        }
        AddDifference(differences, "compilation-metadata-resolver", left.MetadataReferenceResolver?.GetType().AssemblyQualifiedName, right.MetadataReferenceResolver?.GetType().AssemblyQualifiedName);
        AddDifference(differences, "compilation-xml-resolver", left.XmlReferenceResolver?.GetType().AssemblyQualifiedName, right.XmlReferenceResolver?.GetType().AssemblyQualifiedName);
        AddDifference(differences, "compilation-source-resolver", left.SourceReferenceResolver?.GetType().AssemblyQualifiedName, right.SourceReferenceResolver?.GetType().AssemblyQualifiedName);
        AddDifference(differences, "compilation-strong-name-provider", left.StrongNameProvider?.GetType().AssemblyQualifiedName, right.StrongNameProvider?.GetType().AssemblyQualifiedName);
        AddDifference(differences, "compilation-assembly-comparer", left.AssemblyIdentityComparer?.GetType().AssemblyQualifiedName, right.AssemblyIdentityComparer?.GetType().AssemblyQualifiedName);
        if (left is Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions leftCSharp
            && right is Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions rightCSharp)
        {
            AddDifference(differences, "compilation-unsafe", leftCSharp.AllowUnsafe, rightCSharp.AllowUnsafe);
            AddDifference(differences, "compilation-nullable", leftCSharp.NullableContextOptions, rightCSharp.NullableContextOptions);
            AddDifference(differences, "compilation-key-container", leftCSharp.CryptoKeyContainer, rightCSharp.CryptoKeyContainer);
            AddDifference(differences, "compilation-key-file", leftCSharp.CryptoKeyFile, rightCSharp.CryptoKeyFile);
            AddDifference(differences, "compilation-delay-sign", leftCSharp.DelaySign, rightCSharp.DelaySign);
            AddDifference(differences, "compilation-public-sign", leftCSharp.PublicSign, rightCSharp.PublicSign);
            if (!leftCSharp.Usings.SequenceEqual(rightCSharp.Usings, StringComparer.Ordinal))
            {
                differences.Add("compilation-usings");
            }
        }
        return differences;
    }

    private static void AddDifference<T>(
        ICollection<string> differences,
        string code,
        T left,
        T right)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
        {
            differences.Add(code);
        }
    }

    private static void AddPathDifference(
        ICollection<string> differences,
        string code,
        string? left,
        string? right)
    {
        if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add(code);
        }
    }

    private static async Task<bool> TextDocumentsEqualAsync<TDocument>(
        IEnumerable<TDocument> left,
        IEnumerable<TDocument> right,
        CancellationToken cancellationToken)
        where TDocument : TextDocument
    {
        var leftArray = left.OrderBy(document => document.Id.Id).ToArray();
        var rightArray = right.OrderBy(document => document.Id.Id).ToArray();
        if (leftArray.Length != rightArray.Length)
        {
            return false;
        }
        for (var index = 0; index < leftArray.Length; index++)
        {
            var leftDocument = leftArray[index];
            var rightDocument = rightArray[index];
            if (leftDocument.Id != rightDocument.Id
                || !string.Equals(leftDocument.Name, rightDocument.Name, StringComparison.Ordinal)
                || !string.Equals(leftDocument.FilePath, rightDocument.FilePath, StringComparison.OrdinalIgnoreCase)
                || !leftDocument.Folders.SequenceEqual(rightDocument.Folders, StringComparer.Ordinal))
            {
                return false;
            }
            var leftText = await leftDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var rightText = await rightDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (leftText.ChecksumAlgorithm != rightText.ChecksumAlgorithm
                || !leftText.GetChecksum().AsSpan().SequenceEqual(rightText.GetChecksum().AsSpan())
                || !string.Equals(leftText.Encoding?.WebName, rightText.Encoding?.WebName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }
}
