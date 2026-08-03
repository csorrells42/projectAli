using System.Collections.Immutable;
using Ali.Modules.Coding.RoslynActions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Framework.Tests;

public sealed class RoslynPreviewWorkspaceManagerTests
{
    [Fact]
    public async Task SemanticFingerprintHashesAnalyzerFilesWithoutLoadingAnalyzersOrGenerators()
    {
        using var root = new TemporaryDirectory();
        var analyzerPath = Path.Combine(root.Path, "never-loaded-analyzer.dll");
        await File.WriteAllTextAsync(
            analyzerPath,
            "fingerprint-only",
            TestContext.Current.CancellationToken);
        var analyzerReference = new FailsIfLoadedAnalyzerReference(analyzerPath);
        using var workspace = new AdhocWorkspace();
        var solution = CreateSingleProjectSolution(
            workspace,
            root.Path,
            "public sealed class Sample { }",
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var projectId = solution.ProjectIds.Single();
        solution = solution.AddAnalyzerReference(projectId, analyzerReference);

        var fingerprint = await new AliRoslynSolutionFingerprint(
                new AliRoslynTargetReferenceResolver())
            .CaptureAsync(solution, TestContext.Current.CancellationToken);

        Assert.Equal(1, fingerprint.AnalyzerReferenceCount);
        Assert.Equal(0, analyzerReference.LoadAttempts);
    }

    [Fact]
    public async Task ClonePreservesExactReferencesProjectGraphOptionsAndDocuments()
    {
        using var root = new TemporaryDirectory();
        using var canonicalWorkspace = new AdhocWorkspace();
        var coreReference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var canonical = CreateTwoProjectSolution(canonicalWorkspace, root.Path, coreReference);

        using var preview = await new AliRoslynPreviewWorkspaceManager().CreateAsync(
            canonical,
            [],
            TestContext.Current.CancellationToken);

        Assert.NotSame(canonical.Workspace, preview.Workspace);
        Assert.Equal(preview.CanonicalFingerprint, preview.IsolatedFingerprint);
        Assert.Equal(2, preview.Solution.ProjectIds.Count);
        Assert.Equal(2, preview.MetadataReferences.Count);
        Assert.All(preview.MetadataReferences, reference =>
        {
            Assert.Equal(Path.GetFullPath(typeof(object).Assembly.Location), reference.PhysicalPath);
            Assert.False(string.IsNullOrWhiteSpace(reference.Sha256));
        });

        var library = preview.Solution.Projects.Single(project => project.Name == "Library");
        var application = preview.Solution.Projects.Single(project => project.Name == "Application");
        Assert.Single(application.ProjectReferences);
        Assert.Equal(library.Id, application.ProjectReferences.Single().ProjectId);
        Assert.IsType<CSharpParseOptions>(library.ParseOptions);
        Assert.Equal(LanguageVersion.Preview, ((CSharpParseOptions)library.ParseOptions!).LanguageVersion);
        Assert.Equal(NullableContextOptions.Enable, ((CSharpCompilationOptions)library.CompilationOptions!).NullableContextOptions);
        Assert.Single(library.AdditionalDocuments);
        Assert.Single(library.AnalyzerConfigDocuments);

        var compilation = await application.GetCompilationAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(compilation);
        Assert.DoesNotContain(
            compilation.GetDiagnostics(TestContext.Current.CancellationToken),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task CloneFailsClosedWhenAnExactMetadataReferenceDisappears()
    {
        using var root = new TemporaryDirectory();
        var copiedReference = System.IO.Path.Combine(root.Path, "missing-reference.dll");
        File.Copy(typeof(object).Assembly.Location, copiedReference);
        var reference = MetadataReference.CreateFromFile(copiedReference);
        using var canonicalWorkspace = new AdhocWorkspace();
        var canonical = CreateSingleProjectSolution(
            canonicalWorkspace,
            root.Path,
            "public sealed class Sample { }",
            reference);
        File.Delete(copiedReference);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AliRoslynPreviewWorkspaceManager().CreateAsync(
                canonical,
                [],
                TestContext.Current.CancellationToken));

        Assert.Contains("reference is missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticVerifierRejectsSameCountWithDifferentDiagnosticIdentity()
    {
        using var root = new TemporaryDirectory();
        using var baselineWorkspace = new AdhocWorkspace();
        using var candidateWorkspace = new AdhocWorkspace();
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var baseline = CreateSingleProjectSolution(
            baselineWorkspace,
            root.Path,
            "public sealed class Sample { public int Read() => MissingA; }",
            reference);
        var candidate = CreateSingleProjectSolution(
            candidateWorkspace,
            root.Path,
            "public sealed class Sample { public int Read() => MissingB; }",
            reference);
        var verifier = new AliRoslynChangeSetVerifier();

        var baselineDiagnostics = await verifier.CaptureAsync(
            baseline,
            TestContext.Current.CancellationToken);
        var candidateDiagnostics = await verifier.CaptureAsync(
            candidate,
            TestContext.Current.CancellationToken);
        var comparison = verifier.Compare(baselineDiagnostics, candidateDiagnostics);

        Assert.Equal(baselineDiagnostics.Diagnostics.Count, candidateDiagnostics.Diagnostics.Count);
        Assert.False(comparison.Equivalent);
        Assert.False(comparison.NoRegressions);
        Assert.Contains(comparison.Removed, diagnostic => diagnostic.Message.Contains("MissingA", StringComparison.Ordinal));
        Assert.Contains(comparison.Added, diagnostic => diagnostic.Message.Contains("MissingB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnosticVerifierAllowsRemovalOfBaselineDiagnostics()
    {
        using var root = new TemporaryDirectory();
        using var baselineWorkspace = new AdhocWorkspace();
        using var repairedWorkspace = new AdhocWorkspace();
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var baseline = CreateSingleProjectSolution(
            baselineWorkspace,
            root.Path,
            "public sealed class Sample { public int Read() => Missing; }",
            reference);
        var repaired = CreateSingleProjectSolution(
            repairedWorkspace,
            root.Path,
            "public sealed class Sample { public int Read() => 42; }",
            reference);
        var verifier = new AliRoslynChangeSetVerifier();

        var comparison = verifier.Compare(
            await verifier.CaptureAsync(baseline, TestContext.Current.CancellationToken),
            await verifier.CaptureAsync(repaired, TestContext.Current.CancellationToken));

        Assert.False(comparison.Equivalent);
        Assert.True(comparison.NoRegressions);
        Assert.Empty(comparison.Added);
        Assert.NotEmpty(comparison.Removed);
    }

    [Fact]
    public async Task DiscoveryKeepsBuiltInSemanticRenameWhenInjectedProviderFails()
    {
        using var root = new TemporaryDirectory();
        using var workspace = new AdhocWorkspace();
        var solution = CreateSingleProjectSolution(
            workspace,
            root.Path,
            "public sealed class Sample { public int Value { get; set; } }",
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var document = solution.Projects.Single().Documents.Single();
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        var position = text.ToString().IndexOf("Value", StringComparison.Ordinal);
        var discovery = new AliRoslynActionDiscovery([new ThrowingActionProvider()]);
        var referenceResolver = new AliRoslynTargetReferenceResolver();
        var fingerprint = await new AliRoslynSolutionFingerprint(referenceResolver).CaptureAsync(
            solution,
            TestContext.Current.CancellationToken);

        var result = await discovery.DiscoverAsync(
            solution,
            document.Id,
            new TextSpan(position, 0),
            fingerprint.Sha256,
            TestContext.Current.CancellationToken);

        var rename = Assert.Single(result.Actions);
        Assert.Equal("microsoft.codeanalysis.semantic-rename", rename.EquivalenceKey);
        Assert.False(string.IsNullOrWhiteSpace(rename.ProviderIdentity));
        Assert.False(string.IsNullOrWhiteSpace(rename.ProviderVersion));
        Assert.False(string.IsNullOrWhiteSpace(rename.IdentitySha256));
        Assert.Equal(fingerprint.Sha256, rename.SolutionFingerprintSha256);
        Assert.Equal(64, rename.DocumentTextSha256.Length);
        var failure = Assert.Single(result.ProviderFailures);
        Assert.Contains(nameof(ThrowingActionProvider), failure.ProviderIdentity, StringComparison.Ordinal);
        Assert.Equal(64, failure.ProviderAssemblySha256.Length);
        Assert.Equal(64, failure.MessageSha256.Length);
    }

    private static Solution CreateTwoProjectSolution(
        AdhocWorkspace workspace,
        string root,
        MetadataReference coreReference)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable,
            deterministic: true);
        var libraryId = ProjectId.CreateNewId("Library");
        var applicationId = ProjectId.CreateNewId("Application");
        var libraryPath = System.IO.Path.Combine(root, "Library", "Library.csproj");
        var applicationPath = System.IO.Path.Combine(root, "Application", "Application.csproj");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                libraryId,
                VersionStamp.Create(),
                "Library",
                "Library",
                LanguageNames.CSharp,
                filePath: libraryPath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                metadataReferences: [coreReference]))
            .AddProject(ProjectInfo.Create(
                applicationId,
                VersionStamp.Create(),
                "Application",
                "Application",
                LanguageNames.CSharp,
                filePath: applicationPath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                projectReferences: [new ProjectReference(libraryId)],
                metadataReferences: [coreReference]));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(libraryId),
            "Library.cs",
            SourceText.From("public static class Helper { public static int Value => 42; }"),
            filePath: System.IO.Path.Combine(root, "Library", "Library.cs"));
        solution = solution.AddAdditionalDocument(
            DocumentId.CreateNewId(libraryId),
            "settings.json",
            SourceText.From("{}"),
            filePath: System.IO.Path.Combine(root, "Library", "settings.json"));
        solution = solution.AddAnalyzerConfigDocument(
            DocumentId.CreateNewId(libraryId),
            ".editorconfig",
            SourceText.From("root = true"),
            filePath: System.IO.Path.Combine(root, ".editorconfig"));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(applicationId),
            "Application.cs",
            SourceText.From("public sealed class Use { public int Read() => Helper.Value; }"),
            filePath: System.IO.Path.Combine(root, "Application", "Application.cs"));
        Assert.True(workspace.TryApplyChanges(solution));
        return workspace.CurrentSolution;
    }

    private static Solution CreateSingleProjectSolution(
        AdhocWorkspace workspace,
        string root,
        string source,
        MetadataReference reference)
    {
        var projectId = ProjectId.CreateNewId("Project");
        var projectPath = System.IO.Path.Combine(root, "Project", "Project.csproj");
        var documentPath = System.IO.Path.Combine(root, "Project", "Sample.cs");
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Project",
            "Project",
            LanguageNames.CSharp,
            filePath: projectPath,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default,
            metadataReferences: [reference]));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Sample.cs",
            SourceText.From(source),
            filePath: documentPath);
        Assert.True(workspace.TryApplyChanges(solution));
        return workspace.CurrentSolution;
    }

    private sealed class ThrowingActionProvider : IAliRoslynActionProvider
    {
        private readonly AliRoslynProviderIdentity _identity;

        internal ThrowingActionProvider()
        {
            _identity = AliRoslynProviderIdentity.Create(this, "test-owned");
        }

        public string ProviderIdentity => _identity.StableIdentity;
        public string ProviderVersion => _identity.AssemblyVersion;
        public string ProviderAssemblySha256 => _identity.AssemblyFileSha256;

        public Task<IReadOnlyList<AliRoslynProviderAction>> DiscoverAsync(
            AliRoslynActionDiscoveryContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("provider failure");
    }

    private sealed class FailsIfLoadedAnalyzerReference(string fullPath) : AnalyzerReference
    {
        public int LoadAttempts { get; private set; }

        public override string FullPath { get; } = Path.GetFullPath(fullPath);

        public override string Display => Path.GetFileName(FullPath);

        public override object Id => FullPath;

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) =>
            Fail<DiagnosticAnalyzer>();

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() =>
            Fail<DiagnosticAnalyzer>();

        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) =>
            Fail<ISourceGenerator>();

        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() =>
            Fail<ISourceGenerator>();

        private ImmutableArray<T> Fail<T>()
        {
            LoadAttempts++;
            throw new InvalidOperationException(
                "Fingerprinting must not instantiate analyzers or generators.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AliRoslynPreviewTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
