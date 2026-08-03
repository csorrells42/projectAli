using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class RoslynActionDeckTests
{
    [Fact]
    public void FingerprintPinsEveryRoslyn56OptionProperty()
    {
        AliRoslynSolutionFingerprint.RequirePinnedRoslyn56OptionSurface();
        AssertSurface(typeof(CompilationOptions), AliRoslynSolutionFingerprint.CompilationOptionProperties);
        AssertSurface(
            typeof(CSharpCompilationOptions),
            AliRoslynSolutionFingerprint.CSharpCompilationOptionProperties);
        AssertSurface(typeof(ParseOptions), AliRoslynSolutionFingerprint.ParseOptionProperties);
        AssertSurface(
            typeof(CSharpParseOptions),
            AliRoslynSolutionFingerprint.CSharpParseOptionProperties);
        AssertSurface(typeof(Diagnostic), AliRoslynSolutionFingerprint.DiagnosticProperties);
        AssertSurface(
            typeof(DiagnosticDescriptor),
            AliRoslynSolutionFingerprint.DiagnosticDescriptorProperties);

        static void AssertSurface(Type type, IEnumerable<string> expected)
        {
            var actual = type.GetProperties(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);
        }

    }

    [Fact]
    public void CompilationFeaturesIsExplicitlyUnrepresentableInPinnedRoslyn56()
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var property = typeof(CompilationOptions).GetProperty(
            "Features",
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.NotNull(property);
        var readException = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => property.GetValue(options));
        Assert.IsType<NotImplementedException>(readException.InnerException);

        var mutation = typeof(CSharpCompilationOptions).GetMethod(
            "CommonWithFeatures",
            System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.NotNull(mutation);
        var mutationException = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            mutation.Invoke(options, [ImmutableArray.Create("ali-feature-probe")]));
        Assert.IsType<NotImplementedException>(mutationException.InnerException);
    }

    [Fact]
    public async Task RealMsBuildWorkspaceClonePreservesTheExactSemanticFingerprint()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var canonical = await fixture.WorkspaceLoader.LoadAsync(
            fixture.TargetVirtualPath,
            TestContext.Current.CancellationToken);
        _ = await fixture.BindSemanticWorkspaceAsync(
            canonical,
            documentPath: null);
        using var isolated = await fixture.PreviewWorkspaces.CreateAsync(
            canonical,
            TestContext.Current.CancellationToken);

        Assert.Equal(canonical.SemanticFingerprint, isolated.CanonicalFingerprint);
        Assert.Equal(isolated.CanonicalFingerprint, isolated.IsolatedFingerprint);
        Assert.NotSame(canonical.Solution.Workspace, isolated.Solution.Workspace);
    }

    [Fact]
    public async Task LoadedWorkspaceRetainsExactlyOneSemanticFingerprintBinding()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var canonical = await fixture.WorkspaceLoader.LoadAsync(
            fixture.TargetVirtualPath,
            TestContext.Current.CancellationToken);

        var bound = await fixture.BindSemanticWorkspaceAsync(
            canonical,
            fixture.DocumentVirtualPath);

        Assert.Equal(bound, canonical.SemanticFingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.BindSemanticWorkspaceAsync(canonical, fixture.DocumentVirtualPath));
    }

    [Fact]
    public async Task PreviewRejectsAWorkspaceThatDoesNotMatchItsBoundSemanticFingerprint()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var canonical = await fixture.WorkspaceLoader.LoadAsync(
            fixture.TargetVirtualPath,
            TestContext.Current.CancellationToken);
        canonical.BindSemanticFingerprint(new AliRoslynSolutionFingerprintSnapshot(
            new string('A', 64),
            0,
            0,
            0,
            0));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.PreviewWorkspaces.CreateAsync(
                canonical,
                TestContext.Current.CancellationToken));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionStagedRunnerRegistersMsBuildBeforeReadingItsToolsetIdentity()
    {
        var runner = new AliRoslynStagedBuildProcessRunner();

        Assert.Equal("dotnet-msbuild", runner.ToolsetIdentity.Name);
        Assert.False(string.IsNullOrWhiteSpace(runner.ToolsetIdentity.Version));
        Assert.Matches("^[0-9A-F]{64}$", runner.ToolsetIdentity.LocationSha256);
    }

    [Fact]
    public async Task PreviewRejectsAListedIdentityAfterTheExactDocumentChanges()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var listed = await fixture.ListAsync();
        var action = Assert.Single(listed.Actions);
        await File.AppendAllTextAsync(
            fixture.SourcePath,
            Environment.NewLine + "// changed after list",
            TestContext.Current.CancellationToken);

        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            Guid.NewGuid().ToString("N"),
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                action.IdentitySha256,
                "RenamedValue",
                TestContext.Current.CancellationToken));

        Assert.False(preview.Success);
        Assert.Equal("selected-action-not-applicable", preview.FailureCode);
        Assert.Contains(
            "changed after list",
            await File.ReadAllTextAsync(fixture.SourcePath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StableVerificationDigestExcludesVolatileHandleRunnerAndTimeFields()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var handle = new AliRoslynActionHandle(
            "handle-a",
            new string('A', 64),
            "provider",
            "1.0",
            "equivalence",
            "title",
            [],
            "target",
            "root",
            "project",
            "document",
            "document.cs",
            1,
            0,
            "value",
            new string('B', 64),
            "changeset-a",
            new string('C', 64),
            now,
            now.AddHours(1),
            AliRoslynActionHandleState.Previewed,
            1);
        var staged = new AliRoslynSolutionFingerprintSnapshot(new string('D', 64), 1, 1, 1, 1);
        var baseline = new AliRoslynDiagnosticSet(new string('E', 64), []);
        var candidate = new AliRoslynDiagnosticSet(new string('F', 64), []);
        var firstMaterialization = new AliSourceTreeMaterializationReceipt(
            "receipt-a",
            "changeset-a",
            new string('C', 64),
            new string('1', 64),
            2,
            100,
            now);
        var secondMaterialization = firstMaterialization with
        {
            CompletedAtUtc = now.AddMinutes(10)
        };
        var canonicalInputs = AliRoslynInputManifest.Create(
            AliRoslynStagedInputBinding.PolicyDigest,
            [new AliRoslynInputFileIdentity("Sample.cs", 100, new string('7', 64))]);
        var stagedInputs = AliRoslynInputManifest.Create(
            AliRoslynStagedInputBinding.PolicyDigest,
            [new AliRoslynInputFileIdentity("Sample.cs", 120, new string('8', 64))]);
        var inputBinding = new AliRoslynVerifiedInputBinding(
            "verificationreceipt",
            "changeset-a",
            new string('C', 64),
            firstMaterialization.ReceiptId,
            firstMaterialization.ManifestDigest,
            firstMaterialization.PolicyDigest,
            canonicalInputs,
            stagedInputs,
            new string('9', 64));
        var firstStep = new AliRoslynStagedVerificationStepReceipt(
            AliRoslynStagedRunnerOperation.RestoreAndBuild,
            "Sample.csproj",
            new string('2', 64),
            true,
            0,
            false,
            25,
            100,
            new string('3', 64),
            0,
            0,
            0,
            0);
        var firstBuild = new AliRoslynStagedBuildVerificationReceipt(
            true,
            "Sample.csproj",
            new string('2', 64),
            "Release",
            new AliRoslynStagedToolsetIdentity("dotnet", "10.0", new string('4', 64)),
            1,
            1,
            1,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            "verified",
            "first bounded output",
            [firstStep]);
        var secondBuild = firstBuild with
        {
            Summary = "different bounded output",
            Steps =
            [
                firstStep with
                {
                    DurationMilliseconds = 9_999,
                    OutputCharacters = 999,
                    OutputSha256 = new string('5', 64)
                }
            ]
        };

        var first = AliRoslynActionDeck.ComputeStableVerificationDigest(
            handle,
            staged,
            baseline,
            candidate,
            firstMaterialization,
            inputBinding,
            firstBuild);
        var second = AliRoslynActionDeck.ComputeStableVerificationDigest(
            handle with
            {
                Id = "handle-b",
                CreatedAtUtc = now.AddMinutes(5),
                ExpiresAtUtc = now.AddHours(2)
            },
            staged,
            baseline,
            candidate,
            secondMaterialization,
            inputBinding,
            secondBuild);

        Assert.Equal(first, second);
        Assert.NotEqual(
            first,
            AliRoslynActionDeck.ComputeStableVerificationDigest(
                handle,
                staged,
                baseline,
                candidate,
                firstMaterialization,
                inputBinding,
                firstBuild with
                {
                    Steps = [firstStep with { TargetSha256 = new string('6', 64) }]
                }));
        Assert.NotEqual(
            first,
            AliRoslynActionDeck.ComputeStableVerificationDigest(
                handle,
                staged,
                baseline,
                candidate,
                firstMaterialization,
                inputBinding with { BindingDigest = new string('0', 64) },
                firstBuild));
    }

    [Fact]
    public void OwnedStagedDirectoryDeletesOnlyItsExactMarkedTree()
    {
        var staged = AliRoslynStagedDirectory.Create();
        var root = staged.Path;
        var marker = staged.MarkerPath;
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "artifact.txt"), "staged");

        staged.Dispose();

        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task PreviewExecutesOnlyTheExactDiscoveredIdentityAndDoesNotWriteCanonicalSource()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var original = await File.ReadAllTextAsync(fixture.SourcePath, TestContext.Current.CancellationToken);
        var listed = await fixture.ListAsync();
        Assert.True(listed.Success, $"{listed.FailureCode}: {listed.Summary}");
        var action = Assert.Single(listed.Actions);

        var rejected = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            Guid.NewGuid().ToString("N"),
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                new string('0', 64),
                "RenamedValue",
                TestContext.Current.CancellationToken));

        Assert.False(rejected.Success);
        Assert.Equal("selected-action-not-applicable", rejected.FailureCode);
        Assert.Equal(original, await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken));

        var handleId = Guid.NewGuid().ToString("N");
        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            handleId,
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                action.IdentitySha256,
                "RenamedValue",
                TestContext.Current.CancellationToken));

        if (!preview.Success)
        {
            await fixture.DiagnosePreviewAsync(action.IdentitySha256, "RenamedValue");
        }
        Assert.True(preview.Success, $"{preview.FailureCode}: {preview.Summary}");
        Assert.Equal(handleId, preview.HandleId);
        Assert.Equal(action.IdentitySha256, preview.ActionIdentitySha256);
        Assert.Contains("Sample.cs", preview.ChangedRelativePaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken));
        var handle = await fixture.Handles.LoadAsync(handleId, TestContext.Current.CancellationToken);
        Assert.Equal(AliRoslynActionHandleState.Previewed, handle.State);
        Assert.Equal(action.ProviderIdentity, handle.ProviderIdentity);
        Assert.Equal(action.ProviderVersion, handle.ProviderVersion);
        Assert.Equal(action.EquivalenceKey, handle.EquivalenceKey);
    }

    [Fact]
    public async Task SemanticRenameStillRequiresAValidNonemptyIdentifier()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var listed = await fixture.ListAsync();
        var action = Assert.Single(listed.Actions);

        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            Guid.NewGuid().ToString("N"),
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                action.IdentitySha256,
                string.Empty,
                TestContext.Current.CancellationToken));

        Assert.False(preview.Success);
        Assert.Equal("invalid-rename-identifier", preview.FailureCode);
    }

    [Fact]
    public async Task OwnedCompilerCodeFixDiscoversAndPreviewsWithAnEmptyRequestedValue()
    {
        const string source =
            "namespace Available { public sealed class Widget { } } namespace Demo { public sealed class Sample { public Widget Build() => new(); } }";
        await using var fixture = new ActionDeckFixture(
            buildSucceeds: true,
            source,
            focusToken: "Widget Build",
            discovery: AliRoslynOwnedProviderCatalog.CreateDefault().CreateDiscovery());
        var original = await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken);
        var listed = await fixture.ListAsync();
        var matchingActions = listed.Actions.Where(item =>
            item.ProviderIdentity.Contains(
                nameof(AliRoslynUnambiguousNamespaceImportCodeFixProvider),
                StringComparison.Ordinal)).ToArray();
        Assert.True(
            matchingActions.Length == 1,
            $"Expected one owned CS0246 fix; actions={string.Join(" | ", listed.Actions.Select(item => item.ProviderIdentity + ":" + item.Title))}; failures={string.Join(" | ", listed.ProviderFailures.Select(item => item.ProviderIdentity + ":" + item.ExceptionType + ":" + item.MessageSha256))}.");
        var action = matchingActions[0];

        Assert.Contains(",Ali", action.ProviderIdentity, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]{64}$", action.ProviderAssemblySha256);
        Assert.Matches("^000@", action.NestedActionPath);
        Assert.Contains("CS0246", action.DiagnosticIds, StringComparer.Ordinal);

        var handleId = Guid.NewGuid().ToString("N");
        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            handleId,
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                action.IdentitySha256,
                string.Empty,
                TestContext.Current.CancellationToken));

        Assert.True(preview.Success, $"{preview.FailureCode}: {preview.Summary}");
        Assert.Equal(original, await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken));
        var handle = await fixture.Handles.LoadAsync(handleId, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, handle.RequestedValue);
        Assert.NotNull(handle.DocumentChanges);
        var changeSet = await fixture.SourceChangeSets.LoadAsync(
            preview.ChangeSetId!,
            TestContext.Current.CancellationToken);
        var operation = Assert.Single(changeSet.Operations);
        var postimage = await fixture.SourceChangeSets.ReadPostImageAsync(
            changeSet,
            operation.Sequence,
            TestContext.Current.CancellationToken);
        Assert.Contains(
            "using Available;",
            AliSourceTextEncoding.Decode(postimage, operation.Encoding!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnedDocumentFormattingRefactoringDiscoversAndPreviews()
    {
        const string source =
            "namespace Demo;public sealed class Sample{public int Value{get;set;}}";
        await using var fixture = new ActionDeckFixture(
            buildSucceeds: true,
            source,
            focusToken: "Value",
            discovery: AliRoslynOwnedProviderCatalog.CreateDefault().CreateDiscovery());
        var listed = await fixture.ListAsync();
        var action = Assert.Single(listed.Actions, item =>
            item.ProviderIdentity.Contains(
                nameof(AliRoslynFormatDocumentRefactoringProvider),
                StringComparison.Ordinal));

        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            Guid.NewGuid().ToString("N"),
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                action.IdentitySha256,
                string.Empty,
                TestContext.Current.CancellationToken));

        Assert.True(preview.Success, $"{preview.FailureCode}: {preview.Summary}");
        Assert.Equal(source, await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OwnedCompilerCodeFixRegistersNothingForAnAmbiguousNamespaceImport()
    {
        const string source =
            "namespace One { public sealed class Widget { } } namespace Two { public sealed class Widget { } } namespace Demo { public sealed class Sample { public Widget Build() => new(); } }";
        await using var fixture = new ActionDeckFixture(
            buildSucceeds: true,
            source,
            focusToken: "Widget Build",
            discovery: AliRoslynOwnedProviderCatalog.CreateDefault().CreateDiscovery());

        var listed = await fixture.ListAsync();

        Assert.DoesNotContain(listed.Actions, item => item.ProviderIdentity.Contains(
            nameof(AliRoslynUnambiguousNamespaceImportCodeFixProvider),
            StringComparison.Ordinal));
        Assert.DoesNotContain(listed.ProviderFailures, item => item.ProviderIdentity.Contains(
            nameof(AliRoslynUnambiguousNamespaceImportCodeFixProvider),
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task NestedProviderActionsExecuteOnlyTheSelectedOrdinalIdentity()
    {
        var provider = new NestedTrackingRefactoringProvider();
        await using var fixture = new ActionDeckFixture(
            buildSucceeds: true,
            discovery: new AliRoslynActionDiscovery(trustedRefactoringProviders: [provider]));
        var listed = await fixture.ListAsync();
        var actions = listed.Actions
            .Where(item => item.ProviderIdentity.Contains(
                nameof(NestedTrackingRefactoringProvider),
                StringComparison.Ordinal))
            .OrderBy(item => item.NestedActionPath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, actions.Length);
        Assert.NotEqual(actions[0].IdentitySha256, actions[1].IdentitySha256);
        Assert.NotEqual(actions[0].NestedActionPath, actions[1].NestedActionPath);
        var relisted = await fixture.ListAsync();
        var relistedActions = relisted.Actions
            .Where(item => item.ProviderIdentity.Contains(
                nameof(NestedTrackingRefactoringProvider),
                StringComparison.Ordinal))
            .OrderBy(item => item.NestedActionPath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            actions.Select(item => item.SolutionFingerprintSha256),
            relistedActions.Select(item => item.SolutionFingerprintSha256));
        Assert.Equal(
            actions.Select(item => item.DocumentTextSha256),
            relistedActions.Select(item => item.DocumentTextSha256));
        Assert.Equal(
            actions.Select(item => item.ProviderIdentity),
            relistedActions.Select(item => item.ProviderIdentity));
        Assert.Equal(
            actions.Select(item => item.ProviderVersion),
            relistedActions.Select(item => item.ProviderVersion));
        Assert.Equal(
            actions.Select(item => item.ProviderAssemblySha256),
            relistedActions.Select(item => item.ProviderAssemblySha256));
        Assert.Equal(
            actions.Select(item => item.EquivalenceKey),
            relistedActions.Select(item => item.EquivalenceKey));
        Assert.Equal(
            actions.Select(item => item.NestedActionPath),
            relistedActions.Select(item => item.NestedActionPath));
        Assert.Equal(
            actions.Select(item => item.IdentitySha256),
            relistedActions.Select(item => item.IdentitySha256));

        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            Guid.NewGuid().ToString("N"),
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                actions[1].IdentitySha256,
                string.Empty,
                TestContext.Current.CancellationToken));

        Assert.True(preview.Success, $"{preview.FailureCode}: {preview.Summary}");
        Assert.Equal(0, provider.FirstExecutions);
        Assert.Equal(1, provider.SecondExecutions);
    }

    [Fact]
    public async Task PreviewRejectsAProviderThatReturnsAnyExtraOperation()
    {
        var provider = new ExtraOperationRefactoringProvider();
        await using var fixture = new ActionDeckFixture(
            buildSucceeds: true,
            discovery: new AliRoslynActionDiscovery(trustedRefactoringProviders: [provider]));
        var listed = await fixture.ListAsync();
        var action = Assert.Single(listed.Actions, item =>
            item.ProviderIdentity.Contains(
                nameof(ExtraOperationRefactoringProvider),
                StringComparison.Ordinal));

        var preview = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynPreviewActionName,
            Guid.NewGuid().ToString("N"),
            () => fixture.Deck.PreviewActionAsync(
                fixture.TargetVirtualPath,
                fixture.DocumentVirtualPath,
                fixture.Line,
                fixture.Column,
                action.IdentitySha256,
                string.Empty,
                TestContext.Current.CancellationToken));

        Assert.False(preview.Success);
        Assert.Equal("provider-operation-set-rejected", preview.FailureCode);
        Assert.False(provider.ExtraOperationApplied);
    }

    [Fact]
    public async Task OrdinaryTemporaryWorkspaceTargetStateHashesExactRegularFiles()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var adapter = fixture.TargetStates;
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            targetPath = fixture.TargetVirtualPath,
            documentPath = fixture.DocumentVirtualPath
        }));

        var snapshot = adapter.Capture(
            AliCapabilityCatalog.RoslynListActionsName,
            arguments.RootElement);

        Assert.Single(snapshot.TargetVersions);
        Assert.Matches(
            "^[0-9a-f]{64}$",
            snapshot.TargetVersions[AliRoslynSemanticWorkspaceBinding.StaticSourceRevisionVersionKey]);
        Assert.Equal(snapshot.TargetVersions, snapshot.ArtifactVersions);
    }

    [Fact]
    public async Task ActionTargetCaptureDoesNotMaterializeDesignTimeOutputsBeforeDurableIntent()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var before = Directory.EnumerateFiles(
                fixture.ProjectDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.ProjectDirectory, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            targetPath = fixture.TargetVirtualPath,
            documentPath = fixture.DocumentVirtualPath
        }));

        _ = fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynListActionsName,
            arguments.RootElement);

        var after = Directory.EnumerateFiles(
                fixture.ProjectDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.ProjectDirectory, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(before, after, StringComparer.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, "obj")));
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.RoslynInspectTargetName)]
    [InlineData(AliCapabilityCatalog.RoslynListActionsName)]
    [InlineData(AliCapabilityCatalog.RoslynPreviewActionName)]
    public async Task TargetReadersRejectStaticSourceDriftAtExecution(
        string toolName)
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var listed = await fixture.ListAsync();
        var selected = Assert.Single(listed.Actions);
        var preparedDigest = fixture.CaptureTargetVersionDigest(toolName);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.ProjectDirectory, "Unrelated.cs"),
            "namespace Demo; internal static class Unrelated { internal const int Value = 1; }",
            TestContext.Current.CancellationToken);

        var rejected = toolName switch
        {
            AliCapabilityCatalog.RoslynInspectTargetName =>
                (await fixture.WithGrantAsync(
                    toolName,
                    Guid.NewGuid().ToString("N"),
                    () => fixture.Deck.InspectTargetAsync(
                        fixture.TargetVirtualPath,
                        TestContext.Current.CancellationToken),
                    preparedDigest)).Success,
            AliCapabilityCatalog.RoslynListActionsName =>
                (await fixture.WithGrantAsync(
                    toolName,
                    Guid.NewGuid().ToString("N"),
                    () => fixture.Deck.ListActionsAsync(
                        fixture.TargetVirtualPath,
                        fixture.DocumentVirtualPath,
                        fixture.Line,
                        fixture.Column,
                        TestContext.Current.CancellationToken),
                    preparedDigest)).Success,
            AliCapabilityCatalog.RoslynPreviewActionName =>
                (await fixture.WithGrantAsync(
                    toolName,
                    Guid.NewGuid().ToString("N"),
                    () => fixture.Deck.PreviewActionAsync(
                        fixture.TargetVirtualPath,
                        fixture.DocumentVirtualPath,
                        fixture.Line,
                        fixture.Column,
                        selected.IdentitySha256,
                        "RenamedValue",
                        TestContext.Current.CancellationToken),
                    preparedDigest)).Success,
            _ => throw new InvalidOperationException("Unknown Action Deck target reader.")
        };

        Assert.False(rejected);
    }

    [Fact]
    public async Task DurableDocumentDeltaReconstructsEveryDocumentKindAndOperationShape()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var workspace = new AdhocWorkspace();
        var graph = CreateDocumentGraph(workspace, fixture);
        var target = fixture.Resolver.ResolveExistingTarget(fixture.TargetVirtualPath);

        var prepared = await fixture.RoslynChangeSets.CreateAsync(
            graph.Canonical,
            graph.Staged,
            target,
            TestContext.Current.CancellationToken);
        var reconstructed = await fixture.Deck.ReconstructStagedSolutionAsync(
            graph.Canonical,
            target,
            prepared.SourceChangeSet,
            prepared.DocumentChanges,
            TestContext.Current.CancellationToken);
        var reconstructedFingerprint = await fixture.Fingerprint.CaptureAsync(
            reconstructed,
            TestContext.Current.CancellationToken);

        Assert.Equal(prepared.StagedFingerprint.Sha256, reconstructedFingerprint.Sha256);
        Assert.Equal(11, prepared.DocumentChanges.Length);
        Assert.Equal(12, prepared.SourceChangeSet.Operations.Length);
        Assert.Contains(prepared.DocumentChanges, change =>
            change.DocumentKind == AliRoslynDocumentKind.Regular
            && change.Kind == AliRoslynDocumentChangeKind.Add);
        Assert.Contains(prepared.DocumentChanges, change =>
            change.DocumentKind == AliRoslynDocumentKind.Regular
            && change.Kind == AliRoslynDocumentChangeKind.Replace);
        Assert.Contains(prepared.DocumentChanges, change =>
            change.DocumentKind == AliRoslynDocumentKind.Regular
            && change.Kind == AliRoslynDocumentChangeKind.Delete);
        Assert.Contains(prepared.DocumentChanges, change =>
            change.DocumentKind == AliRoslynDocumentKind.Regular
            && change.Kind == AliRoslynDocumentChangeKind.Rename);
        Assert.Contains(prepared.DocumentChanges, change =>
            change.DocumentKind == AliRoslynDocumentKind.Regular
            && change.Kind == AliRoslynDocumentChangeKind.RenameAndReplace);
        Assert.All(
            Enum.GetValues<AliRoslynDocumentKind>(),
            kind => Assert.Contains(prepared.DocumentChanges, change =>
                change.DocumentKind == kind
                && change.Kind == AliRoslynDocumentChangeKind.Add));
        Assert.All(
            Enum.GetValues<AliRoslynDocumentKind>(),
            kind => Assert.Contains(prepared.DocumentChanges, change =>
                change.DocumentKind == kind
                && change.Kind == AliRoslynDocumentChangeKind.Delete));
        Assert.All(
            Enum.GetValues<AliRoslynDocumentKind>(),
            kind => Assert.Contains(prepared.DocumentChanges, change =>
                change.DocumentKind == kind
                && change.Kind == AliRoslynDocumentChangeKind.Rename));
    }

    [Fact]
    public async Task DurableDocumentDeltaRejectsCompilationMetadataChanges()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var workspace = new AdhocWorkspace();
        var graph = CreateDocumentGraph(workspace, fixture);
        var project = Assert.Single(graph.Canonical.Projects);
        var options = Assert.IsType<CSharpCompilationOptions>(project.CompilationOptions);
        var staged = graph.Canonical.WithProjectCompilationOptions(
            project.Id,
            options.WithAllowUnsafe(!options.AllowUnsafe));
        var target = fixture.Resolver.ResolveExistingTarget(fixture.TargetVirtualPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.RoslynChangeSets.CreateAsync(
                graph.Canonical,
                staged,
                target,
                TestContext.Current.CancellationToken));

        Assert.Contains("project or reference metadata", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WithDebugPlusMode")]
    [InlineData("WithCurrentLocalTime")]
    [InlineData("WithReferencesSupersedeLowerVersions")]
    [InlineData("WithTopLevelBinderFlags")]
    [InlineData("WithMemorySafetyRules")]
    [InlineData("WithUpdatedMemorySafetyRules")]
    public async Task InternalCompilationOptionMutationChangesFingerprintAndIsRejected(
        string mutationMethod)
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var workspace = new AdhocWorkspace();
        var graph = CreateDocumentGraph(workspace, fixture);
        var project = Assert.Single(graph.Canonical.Projects);
        var canonicalOptions = Assert.IsType<CSharpCompilationOptions>(project.CompilationOptions);
        var stagedOptions = MutateInternalCompilationOption(canonicalOptions, mutationMethod);
        var staged = graph.Canonical.WithProjectCompilationOptions(project.Id, stagedOptions);
        var target = fixture.Resolver.ResolveExistingTarget(fixture.TargetVirtualPath);

        Assert.NotEqual(canonicalOptions, stagedOptions);
        var canonicalFingerprint = await fixture.Fingerprint.CaptureAsync(
            graph.Canonical,
            TestContext.Current.CancellationToken);
        var stagedFingerprint = await fixture.Fingerprint.CaptureAsync(
            staged,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(canonicalFingerprint.Sha256, stagedFingerprint.Sha256);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.RoslynChangeSets.CreateAsync(
                graph.Canonical,
                staged,
                target,
                TestContext.Current.CancellationToken));
        Assert.Contains("project or reference metadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseFeatureMutationChangesFingerprintAndIsRejected()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var workspace = new AdhocWorkspace();
        var graph = CreateDocumentGraph(workspace, fixture);
        var project = Assert.Single(graph.Canonical.Projects);
        var canonicalOptions = Assert.IsType<CSharpParseOptions>(project.ParseOptions);
        var stagedOptions = canonicalOptions.WithFeatures(
        [
            new KeyValuePair<string, string>("ali-test-feature", "enabled")
        ]);
        var staged = graph.Canonical.WithProjectParseOptions(project.Id, stagedOptions);
        var target = fixture.Resolver.ResolveExistingTarget(fixture.TargetVirtualPath);

        Assert.NotEqual(canonicalOptions, stagedOptions);
        var canonicalFingerprint = await fixture.Fingerprint.CaptureAsync(
            graph.Canonical,
            TestContext.Current.CancellationToken);
        var stagedFingerprint = await fixture.Fingerprint.CaptureAsync(
            staged,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(canonicalFingerprint.Sha256, stagedFingerprint.Sha256);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.RoslynChangeSets.CreateAsync(
                graph.Canonical,
                staged,
                target,
                TestContext.Current.CancellationToken));
        Assert.Contains("project or reference metadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedResolverStateFailsClosedInsteadOfUsingOnlyItsType()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        using var workspace = new AdhocWorkspace();
        var graph = CreateDocumentGraph(workspace, fixture);
        var project = Assert.Single(graph.Canonical.Projects);
        var options = Assert.IsType<CSharpCompilationOptions>(project.CompilationOptions)
            .WithMetadataReferenceResolver(new OpaqueMetadataReferenceResolver("state-a"));
        var unsupported = graph.Canonical.WithProjectCompilationOptions(project.Id, options);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Fingerprint.CaptureAsync(
                unsupported,
                TestContext.Current.CancellationToken));

        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyRefusesAStaleCanonicalFingerprintBeforeBuild()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var preview = await fixture.PreviewAsync("RenamedValue");
        await File.AppendAllTextAsync(
            fixture.SourcePath,
            Environment.NewLine + "// external canonical edit",
            TestContext.Current.CancellationToken);

        var verification = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            preview.HandleId!,
            () => fixture.Deck.VerifyAsync(
                preview.HandleId!,
                TestContext.Current.CancellationToken));

        Assert.False(verification.Success);
        Assert.Equal("stale-canonical-fingerprint", verification.OutcomeCode);
        Assert.Empty(fixture.Runner.Requests);
        Assert.Contains(
            "external canonical edit",
            await File.ReadAllTextAsync(fixture.SourcePath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyRecomparesAuthenticatedPreviewedStagedFingerprintBeforeBuild()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var preview = await fixture.PreviewAsync("RenamedValue");
        var original = await fixture.Handles.LoadAsync(
            preview.HandleId!,
            TestContext.Current.CancellationToken);
        var mismatched = original with
        {
            Id = Guid.NewGuid().ToString("N")
        };
        await fixture.Handles.CreateAsync(
            mismatched,
            new string('9', 64),
            TestContext.Current.CancellationToken);

        var verification = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            mismatched.Id,
            () => fixture.Deck.VerifyAsync(
                mismatched.Id,
                TestContext.Current.CancellationToken));

        Assert.False(verification.Success);
        Assert.Equal("staged-fingerprint-mismatch", verification.OutcomeCode);
        Assert.Empty(fixture.Runner.Requests);
        var retained = await fixture.Handles.LoadAsync(
            mismatched.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliRoslynActionHandleState.Previewed, retained.State);
        Assert.Null(retained.Verification);
    }

    [Fact]
    public async Task PreviewAndVerificationBuildOnlyTheStagedPostimage()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var original = await File.ReadAllTextAsync(fixture.SourcePath, TestContext.Current.CancellationToken);
        var preview = await fixture.PreviewAsync("RenamedValue");

        var verification = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            preview.HandleId!,
            () => fixture.Deck.VerifyAsync(
                preview.HandleId!,
                TestContext.Current.CancellationToken));

        Assert.True(verification.Success, verification.Summary);
        Assert.True(verification.RoslynSucceeded);
        Assert.True(verification.BuildSucceeded);
        Assert.True(verification.TestsSucceeded);
        Assert.Single(fixture.Runner.Requests);
        Assert.True(fixture.Runner.SawRenamedStagedSource);
        Assert.Equal(original, await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken));
        var handle = await fixture.Handles.LoadAsync(preview.HandleId!, TestContext.Current.CancellationToken);
        Assert.Equal(AliRoslynActionHandleState.Verified, handle.State);
        Assert.NotNull(handle.Verification);
    }

    [Fact]
    public async Task ApplyRefusesChangedNonRoslynInputBeforeCanonicalSourceMutation()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: true);
        var nonRoslynInput = Path.Combine(fixture.ProjectDirectory, "appsettings.json");
        await File.WriteAllTextAsync(
            nonRoslynInput,
            "{\"mode\":\"verified\"}",
            TestContext.Current.CancellationToken);
        var originalSource = await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken);
        var preview = await fixture.PreviewAsync("RenamedValue");
        var verification = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            preview.HandleId!,
            () => fixture.Deck.VerifyAsync(
                preview.HandleId!,
                TestContext.Current.CancellationToken));
        Assert.True(verification.Success, verification.Summary);
        var verified = await fixture.Handles.LoadAsync(
            preview.HandleId!,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            nonRoslynInput,
            "{\"mode\":\"changed-after-verify\"}",
            TestContext.Current.CancellationToken);
        var application = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynApplyActionName,
            verified.ChangeSetId,
            () => fixture.Publisher.ApplyAsync(
                verified.Id,
                TestContext.Current.CancellationToken));

        Assert.False(application.Success);
        Assert.False(application.Applied);
        Assert.Equal("stale-canonical-input-manifest", application.OutcomeCode);
        Assert.Equal(
            originalSource,
            await File.ReadAllTextAsync(
                fixture.SourcePath,
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.SourceChangeSets.GetReceiptPath(verified.ChangeSetId)));
        var failed = await fixture.Handles.LoadAsync(
            verified.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliRoslynActionHandleState.Failed, failed.State);
        Assert.Equal("stale-canonical-input-manifest", failed.FailureCode);
    }

    [Fact]
    public async Task BuildFailureStopsVerificationAndLeavesHandlePreviewed()
    {
        await using var fixture = new ActionDeckFixture(buildSucceeds: false);
        var original = await File.ReadAllTextAsync(fixture.SourcePath, TestContext.Current.CancellationToken);
        var preview = await fixture.PreviewAsync("RenamedValue");

        var verification = await fixture.WithGrantAsync(
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            preview.HandleId!,
            () => fixture.Deck.VerifyAsync(
                preview.HandleId!,
                TestContext.Current.CancellationToken));

        Assert.False(verification.Success);
        Assert.Equal("build-failed", verification.OutcomeCode);
        Assert.True(verification.RoslynSucceeded);
        Assert.False(verification.BuildSucceeded);
        Assert.Single(fixture.Runner.Requests);
        Assert.True(fixture.Runner.SawRenamedStagedSource);
        Assert.Equal(original, await File.ReadAllTextAsync(
            fixture.SourcePath,
            TestContext.Current.CancellationToken));
        var handle = await fixture.Handles.LoadAsync(preview.HandleId!, TestContext.Current.CancellationToken);
        Assert.Equal(AliRoslynActionHandleState.Previewed, handle.State);
        Assert.Null(handle.Verification);
    }

    private sealed class ActionDeckFixture : IAsyncDisposable
    {
        private readonly string _root;

        internal ActionDeckFixture(
            bool buildSucceeds,
            string? source = null,
            string? focusToken = null,
            AliRoslynActionDiscovery? discovery = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "AliRoslynActionDeckTests",
                Guid.NewGuid().ToString("N"));
            ProjectDirectory = Path.Combine(_root, "workspace", "Sample");
            Directory.CreateDirectory(ProjectDirectory);
            TargetPath = Path.Combine(ProjectDirectory, "Sample.csproj");
            SourcePath = Path.Combine(ProjectDirectory, "Sample.cs");
            File.WriteAllText(
                TargetPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <IsTestProject>false</IsTestProject>
                  </PropertyGroup>
                </Project>
                """);
            source ??=
                "namespace Demo; public sealed class Sample { public int Value { get; set; } public int Read() => Value; }";
            File.WriteAllText(SourcePath, source);
            Line = 1;
            focusToken ??= "Value {";
            Column = source.IndexOf(focusToken, StringComparison.Ordinal) + 1;
            if (Column <= 0)
            {
                throw new ArgumentException("The exact focus token is absent from the fixture source.");
            }

            var stateRoot = Path.Combine(_root, "state");
            var permissions = new AgentToolPermissionStore(stateRoot);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(_root, "workspace"))
            ], Path.Combine(_root, "trash"));
            var access = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(stateRoot, activeUsers: null),
                permissions);
            Resolver = new AliCodingProjectResolver(access);
            var referenceResolver = new AliRoslynTargetReferenceResolver();
            Fingerprint = new AliRoslynSolutionFingerprint(referenceResolver);
            var diagnostics = new AliRoslynChangeSetVerifier();
            WorkspaceLoader = new AliRoslynWorkspaceLoader(Resolver);
            PreviewWorkspaces = new AliRoslynPreviewWorkspaceManager(referenceResolver, Fingerprint);
            Discovery = discovery ?? new AliRoslynActionDiscovery();
            SourceChangeSets = new AliSourceChangeSetStore(
                Path.Combine(stateRoot, "changesets"),
                "action-deck-tests");
            Handles = new AliRoslynActionHandleStore(
                Path.Combine(stateRoot, "handles"),
                "action-deck-tests");
            SemanticWorkspace = new AliRoslynSemanticWorkspaceBinding(
                WorkspaceLoader,
                Fingerprint);
            TargetStates = new AliRoslynActionTargetStateAdapter(
                Resolver,
                Handles,
                SemanticWorkspace);
            Runner = new FakeBuildRunner(buildSucceeds);
            RoslynChangeSets = new AliRoslynChangeSetStore(SourceChangeSets, Fingerprint, diagnostics);
            Deck = new AliRoslynActionDeck(
                WorkspaceLoader,
                PreviewWorkspaces,
                Discovery,
                RoslynChangeSets,
                SourceChangeSets,
                Handles,
                TargetStates,
                Fingerprint,
                diagnostics,
                new AliRoslynStagedBuildVerifier(Runner, TimeSpan.FromSeconds(10)));
            var sourcePublisher = new AliSourceChangeSetPublisher(
                SourceChangeSets,
                new AliSourceChangeSetValidator(SourceChangeSets));
            Publisher = new AliRoslynActionPublisher(
                Handles,
                SourceChangeSets,
                sourcePublisher,
                WorkspaceLoader,
                Fingerprint);
        }

        internal string ProjectDirectory { get; }
        internal string TargetPath { get; }
        internal string SourcePath { get; }
        internal string TargetVirtualPath => "Workspace/Sample/Sample.csproj";
        internal string DocumentVirtualPath => "Workspace/Sample/Sample.cs";
        internal int Line { get; }
        internal int Column { get; }
        internal AliRoslynActionDeck Deck { get; }
        internal AliRoslynActionPublisher Publisher { get; }
        internal AliRoslynActionHandleStore Handles { get; }
        internal AliRoslynActionTargetStateAdapter TargetStates { get; }
        internal AliSourceChangeSetStore SourceChangeSets { get; }
        internal AliCodingProjectResolver Resolver { get; }
        internal FakeBuildRunner Runner { get; }
        internal AliRoslynWorkspaceLoader WorkspaceLoader { get; }
        internal AliRoslynPreviewWorkspaceManager PreviewWorkspaces { get; }
        internal AliRoslynSolutionFingerprint Fingerprint { get; }
        internal AliRoslynSemanticWorkspaceBinding SemanticWorkspace { get; }
        internal AliRoslynActionDiscovery Discovery { get; }
        internal AliRoslynChangeSetStore RoslynChangeSets { get; }

        internal async Task<AliRoslynActionListResult> ListAsync()
        {
            var result = await WithGrantAsync(
                AliCapabilityCatalog.RoslynListActionsName,
                Guid.NewGuid().ToString("N"),
                () => Deck.ListActionsAsync(
                    TargetVirtualPath,
                    DocumentVirtualPath,
                    Line,
                    Column,
                    TestContext.Current.CancellationToken));
            if (!result.Success)
            {
                using var canonical = await WorkspaceLoader.LoadAsync(
                    TargetVirtualPath,
                    TestContext.Current.CancellationToken);
                _ = await BindSemanticWorkspaceAsync(canonical, DocumentVirtualPath);
                var (document, position) = await WorkspaceLoader.ResolvePositionAsync(
                    canonical,
                    DocumentVirtualPath,
                    Line,
                    Column,
                    TestContext.Current.CancellationToken);
                using var preview = await PreviewWorkspaces.CreateAsync(
                    canonical,
                    TestContext.Current.CancellationToken);
                var direct = await Discovery.DiscoverAsync(
                    preview.Solution,
                    document.Id,
                    new Microsoft.CodeAnalysis.Text.TextSpan(position, 0),
                    preview.CanonicalFingerprint.Sha256,
                    TestContext.Current.CancellationToken);
                throw new InvalidOperationException(
                    $"Deck failed with {result.FailureCode}; direct discovery returned {direct.Actions.Count} action(s).");
            }
            return result;
        }

        internal async Task<AliRoslynActionPreview> PreviewAsync(string newName)
        {
            var listed = await ListAsync();
            Assert.True(listed.Success, $"{listed.FailureCode}: {listed.Summary}");
            var action = Assert.Single(listed.Actions);
            var preview = await WithGrantAsync(
                AliCapabilityCatalog.RoslynPreviewActionName,
                Guid.NewGuid().ToString("N"),
                () => Deck.PreviewActionAsync(
                    TargetVirtualPath,
                    DocumentVirtualPath,
                    Line,
                    Column,
                    action.IdentitySha256,
                    newName,
                    TestContext.Current.CancellationToken));
            if (!preview.Success)
            {
                await DiagnosePreviewAsync(action.IdentitySha256, newName);
            }
            return preview;
        }

        internal async Task DiagnosePreviewAsync(string actionIdentity, string newName)
        {
            using var canonical = await WorkspaceLoader.LoadAsync(
                TargetVirtualPath,
                TestContext.Current.CancellationToken);
            _ = await BindSemanticWorkspaceAsync(canonical, DocumentVirtualPath);
            var (document, position) = await WorkspaceLoader.ResolvePositionAsync(
                canonical,
                DocumentVirtualPath,
                Line,
                Column,
                TestContext.Current.CancellationToken);
            using var preview = await PreviewWorkspaces.CreateAsync(
                canonical,
                TestContext.Current.CancellationToken);
            var discovered = await Discovery.DiscoverAsync(
                preview.Solution,
                document.Id,
                new Microsoft.CodeAnalysis.Text.TextSpan(position, 0),
                preview.CanonicalFingerprint.Sha256,
                TestContext.Current.CancellationToken);
            var selected = discovered.Actions.Single(action =>
                string.Equals(action.IdentitySha256, actionIdentity, StringComparison.Ordinal));
            var previewDocument = preview.Solution.GetDocument(document.Id)!;
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
                previewDocument,
                position,
                TestContext.Current.CancellationToken);
            var renamed = await Renamer.RenameSymbolAsync(
                preview.Solution,
                symbol!,
                new SymbolRenameOptions(false, false, false, false),
                newName,
                TestContext.Current.CancellationToken);
            _ = selected;
            _ = await RoslynChangeSets.CreateAsync(
                preview.Solution,
                renamed,
                canonical.Target,
                TestContext.Current.CancellationToken);
            throw new InvalidOperationException(
                "The direct diagnostic preview succeeded although the Action Deck returned failure.");
        }

        internal async Task<T> WithGrantAsync<T>(
            string toolName,
            string preparationIdentity,
            Func<Task<T>> action,
            string? targetVersionDigest = null)
        {
            var rootBinding = toolName == AliCapabilityCatalog.RoslynVerifyChangesetName
                ? AliRoslynActionExecutionAdapter.StagedRootBinding(
                    ProjectDirectory,
                    AliExactDotNetHost.CaptureCurrent())
                : AliRoslynActionExecutionAdapter.RootBinding(ProjectDirectory);
            var grant = new AliExecutionGrant(
                "idempotency-" + Guid.NewGuid().ToString("N"),
                "call-" + Guid.NewGuid().ToString("N"),
                toolName,
                AliRoslynActionExecutionAdapter.CapabilityIdFor(toolName),
                new string('1', 64),
                targetVersionDigest ?? CaptureTargetVersionDigest(toolName),
                new string('3', 64),
                new string('4', 64),
                AliRoslynActionExecutionAdapter.ReconcilerIdFor(toolName),
                preparationIdentity,
                rootBinding);
            var frame = new AliExecutionGrantFrame(grant);
            var previous = AliExecutionGrantContext.Push(frame);
            try
            {
                return await action();
            }
            finally
            {
                frame.Deactivate();
                AliExecutionGrantContext.Pop(frame, previous);
            }
        }

        internal string CaptureTargetVersionDigest(string toolName)
        {
            if (toolName is not (AliCapabilityCatalog.RoslynInspectTargetName
                or AliCapabilityCatalog.RoslynListActionsName
                or AliCapabilityCatalog.RoslynPreviewActionName))
            {
                return new string('2', 64);
            }

            var arguments = toolName == AliCapabilityCatalog.RoslynInspectTargetName
                ? JsonSerializer.SerializeToElement(new { targetPath = TargetVirtualPath })
                : JsonSerializer.SerializeToElement(new
                {
                    targetPath = TargetVirtualPath,
                    documentPath = DocumentVirtualPath,
                    line = Line,
                    column = Column
                });
            var snapshot = TargetStates.Capture(toolName, arguments);
            return AliRoslynSemanticWorkspaceBinding.TargetVersionDigest(
                snapshot.TargetVersions);
        }

        internal Task<AliRoslynSolutionFingerprintSnapshot> BindSemanticWorkspaceAsync(
            AliRoslynWorkspaceSession session,
            string? documentPath)
        {
            var physicalDocument = documentPath is null
                ? null
                : Resolver.ResolveDocument(session.Target, documentPath);
            return SemanticWorkspace.BindLoadedAsync(
                session,
                session.Target,
                physicalDocument,
                TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            for (var attempt = 0; Directory.Exists(_root); attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    attempt < 20
                    && exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private static DocumentGraph CreateDocumentGraph(
        AdhocWorkspace workspace,
        ActionDeckFixture fixture)
    {
        const string sampleText =
            "namespace Demo; public sealed class Sample { public int Value { get; set; } public int Read() => Value; }";
        var encoding = new UTF8Encoding(false, true);
        var projectId = ProjectId.CreateNewId("document-delta-project");
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Sample",
            "Sample",
            LanguageNames.CSharp,
            filePath: fixture.TargetPath,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            ]));

        var sampleId = DocumentId.CreateNewId(projectId, "Sample.cs");
        solution = solution.AddDocument(
            sampleId,
            "Sample.cs",
            SourceText.From(sampleText, encoding),
            filePath: fixture.SourcePath);

        var regularDelete = AddRegular(
            ref solution,
            projectId,
            fixture,
            "RegularDelete.cs",
            "namespace Demo; public sealed class RegularDelete { }");
        var regularRename = AddRegular(
            ref solution,
            projectId,
            fixture,
            "RegularRename.cs",
            "namespace Demo; public sealed class RegularRename { }");
        var regularMove = AddRegular(
            ref solution,
            projectId,
            fixture,
            "RegularMove.cs",
            "namespace Demo; public sealed class RegularMove { }");
        var additionalDelete = AddAdditional(
            ref solution,
            projectId,
            fixture,
            "AdditionalDelete.txt",
            "delete additional");
        var additionalRename = AddAdditional(
            ref solution,
            projectId,
            fixture,
            "AdditionalRename.txt",
            "rename additional");
        var analyzerDelete = AddAnalyzerConfig(
            ref solution,
            projectId,
            fixture,
            "AnalyzerDelete.editorconfig",
            "root = false\n[*.cs]\ndotnet_diagnostic.CS0168.severity = warning\n");
        var analyzerRename = AddAnalyzerConfig(
            ref solution,
            projectId,
            fixture,
            "AnalyzerRename.editorconfig",
            "root = false\n[*.cs]\ndotnet_diagnostic.CS0219.severity = warning\n");

        var canonical = solution;
        var staged = canonical
            .WithDocumentText(
                sampleId,
                SourceText.From(sampleText.Replace("Read()", "ReadChanged()", StringComparison.Ordinal), encoding))
            .RemoveDocument(regularDelete)
            .WithDocumentName(regularRename, "RegularRenamed.cs")
            .WithDocumentFilePath(
                regularRename,
                Path.Combine(fixture.ProjectDirectory, "RegularRenamed.cs"))
            .WithDocumentText(
                regularMove,
                SourceText.From("namespace Demo; public sealed class RegularMovedAndChanged { }", encoding))
            .WithDocumentName(regularMove, "RegularMoved.cs")
            .WithDocumentFilePath(
                regularMove,
                Path.Combine(fixture.ProjectDirectory, "RegularMoved.cs"));
        staged = staged.AddDocument(
            DocumentId.CreateNewId(projectId, "RegularAdded.cs"),
            "RegularAdded.cs",
            SourceText.From("namespace Demo; public sealed class RegularAdded { }", encoding),
            filePath: Path.Combine(fixture.ProjectDirectory, "RegularAdded.cs"));
        staged = staged.RemoveAdditionalDocument(additionalDelete)
            .RemoveAdditionalDocument(additionalRename)
            .AddAdditionalDocument(
                additionalRename,
                "AdditionalRenamed.txt",
                SourceText.From("rename additional", encoding),
                filePath: Path.Combine(fixture.ProjectDirectory, "AdditionalRenamed.txt"))
            .AddAdditionalDocument(
                DocumentId.CreateNewId(projectId, "AdditionalAdded.txt"),
                "AdditionalAdded.txt",
                SourceText.From("added additional", encoding),
                filePath: Path.Combine(fixture.ProjectDirectory, "AdditionalAdded.txt"));
        staged = staged.RemoveAnalyzerConfigDocument(analyzerDelete)
            .RemoveAnalyzerConfigDocument(analyzerRename)
            .AddAnalyzerConfigDocument(
                analyzerRename,
                "AnalyzerRenamed.editorconfig",
                SourceText.From(
                    "root = false\n[*.cs]\ndotnet_diagnostic.CS0219.severity = warning\n",
                    encoding),
                filePath: Path.Combine(fixture.ProjectDirectory, "AnalyzerRenamed.editorconfig"))
            .AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(projectId, "AnalyzerAdded.globalconfig"),
                "AnalyzerAdded.globalconfig",
                SourceText.From("is_global = true\n", encoding),
                filePath: Path.Combine(fixture.ProjectDirectory, "AnalyzerAdded.globalconfig"));
        return new(canonical, staged);
    }

    private static CSharpCompilationOptions MutateInternalCompilationOption(
        CSharpCompilationOptions options,
        string methodName)
    {
        var method = typeof(CSharpCompilationOptions).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException($"The pinned mutation method {methodName} is missing.");
        var parameter = Assert.Single(method.GetParameters());
        object value = methodName switch
        {
            "WithCurrentLocalTime" => ReadPinnedProperty(
                    options,
                    typeof(CompilationOptions),
                    "CurrentLocalTime") is DateTime { } currentLocalTime
                && currentLocalTime != default
                    ? default(DateTime)
                    : new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            "WithDebugPlusMode" => !ReadPinnedBoolean(
                options,
                typeof(CompilationOptions),
                "DebugPlusMode"),
            "WithReferencesSupersedeLowerVersions" => !ReadPinnedBoolean(
                options,
                typeof(CompilationOptions),
                "ReferencesSupersedeLowerVersions"),
            "WithTopLevelBinderFlags" => Enum.ToObject(
                parameter.ParameterType,
                Convert.ToInt64(ReadPinnedProperty(
                    options,
                    typeof(CSharpCompilationOptions),
                    "TopLevelBinderFlags")) ^ 1L),
            "WithMemorySafetyRules" => checked(Convert.ToInt32(ReadPinnedProperty(
                options,
                typeof(CSharpCompilationOptions),
                "MemorySafetyRules")) + 1),
            "WithUpdatedMemorySafetyRules" => !ReadPinnedBoolean(
                options,
                typeof(CSharpCompilationOptions),
                "UseUpdatedMemorySafetyRules"),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName))
        };
        return Assert.IsType<CSharpCompilationOptions>(method.Invoke(options, [value]));

        static bool ReadPinnedBoolean(object target, Type declaringType, string propertyName) =>
            Assert.IsType<bool>(ReadPinnedProperty(target, declaringType, propertyName));

        static object ReadPinnedProperty(object target, Type declaringType, string propertyName) =>
            declaringType.GetProperty(
                    propertyName,
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)
                ?.GetValue(target)
            ?? throw new InvalidOperationException($"The pinned property {propertyName} is missing.");
    }

    private static DocumentId AddRegular(
        ref Solution solution,
        ProjectId projectId,
        ActionDeckFixture fixture,
        string name,
        string content)
    {
        var path = Path.Combine(fixture.ProjectDirectory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false, true));
        var id = DocumentId.CreateNewId(projectId, name);
        solution = solution.AddDocument(
            id,
            name,
            SourceText.From(content, new UTF8Encoding(false, true)),
            filePath: path);
        return id;
    }

    private static DocumentId AddAdditional(
        ref Solution solution,
        ProjectId projectId,
        ActionDeckFixture fixture,
        string name,
        string content)
    {
        var path = Path.Combine(fixture.ProjectDirectory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false, true));
        var id = DocumentId.CreateNewId(projectId, name);
        solution = solution.AddAdditionalDocument(
            id,
            name,
            SourceText.From(content, new UTF8Encoding(false, true)),
            filePath: path);
        return id;
    }

    private static DocumentId AddAnalyzerConfig(
        ref Solution solution,
        ProjectId projectId,
        ActionDeckFixture fixture,
        string name,
        string content)
    {
        var path = Path.Combine(fixture.ProjectDirectory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false, true));
        var id = DocumentId.CreateNewId(projectId, name);
        solution = solution.AddAnalyzerConfigDocument(
            id,
            name,
            SourceText.From(content, new UTF8Encoding(false, true)),
            filePath: path);
        return id;
    }

    private sealed record DocumentGraph(Solution Canonical, Solution Staged);

    private sealed class OpaqueMetadataReferenceResolver(string state) : MetadataReferenceResolver
    {
        private readonly string _state = state;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(
            string reference,
            string? baseFilePath,
            MetadataReferenceProperties properties) => [];

        public override bool Equals(object? other) =>
            other is OpaqueMetadataReferenceResolver resolver
            && string.Equals(_state, resolver._state, StringComparison.Ordinal);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_state);
    }

    private sealed class NestedTrackingRefactoringProvider : CodeRefactoringProvider
    {
        internal int FirstExecutions { get; private set; }
        internal int SecondExecutions { get; private set; }

        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var first = CodeAction.Create(
                "Same visible title",
                cancellationToken => ChangeAsync(context.Document, "FirstChoice", true, cancellationToken),
                "nested-first");
            var second = CodeAction.Create(
                "Same visible title",
                cancellationToken => ChangeAsync(context.Document, "SecondChoice", false, cancellationToken),
                equivalenceKey: null);
            context.RegisterRefactoring(CodeAction.Create(
                "Parent group",
                ImmutableArray.Create(first, second),
                isInlinable: false));
            return Task.CompletedTask;
        }

        private async Task<Document> ChangeAsync(
            Document document,
            string marker,
            bool first,
            CancellationToken cancellationToken)
        {
            if (first)
            {
                FirstExecutions++;
            }
            else
            {
                SecondExecutions++;
            }
            var text = await document.GetTextAsync(cancellationToken);
            return document.WithText(text.WithChanges(new TextChange(
                new TextSpan(text.Length, 0),
                Environment.NewLine + "// " + marker)));
        }
    }

    private sealed class ExtraOperationRefactoringProvider : CodeRefactoringProvider
    {
        internal bool ExtraOperationApplied { get; private set; }

        public override Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            context.RegisterRefactoring(new ExtraOperationAction(context.Document, this));
            return Task.CompletedTask;
        }

        private sealed class ExtraOperationAction(
            Document document,
            ExtraOperationRefactoringProvider owner) : CodeAction
        {
            public override string Title => "Apply plus forbidden operation";
            public override string? EquivalenceKey => "extra-operation";

            protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(
                CancellationToken cancellationToken)
            {
                var text = await document.GetTextAsync(cancellationToken);
                var changed = document.WithText(text.WithChanges(new TextChange(
                    new TextSpan(text.Length, 0),
                    Environment.NewLine + "// changed")));
                return
                [
                    new ApplyChangesOperation(changed.Project.Solution),
                    new TrackingOperation(owner)
                ];
            }
        }

        private sealed class TrackingOperation(ExtraOperationRefactoringProvider owner)
            : CodeActionOperation
        {
            public override void Apply(Workspace workspace, CancellationToken cancellationToken) =>
                owner.ExtraOperationApplied = true;
        }
    }

    internal sealed class FakeBuildRunner(bool buildSucceeds) : IAliRoslynStagedBuildRunner
    {
        internal List<AliRoslynStagedRunnerRequest> Requests { get; } = [];
        internal bool SawRenamedStagedSource { get; private set; }

        public AliRoslynStagedToolsetIdentity ToolsetIdentity { get; } =
            new("fake-dotnet", "1.0", new string('A', 64));

        public async Task<AliRoslynStagedRunnerResult> RunAsync(
            AliRoslynStagedRunnerRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var stagedSource = Path.Combine(request.StagedRoot, "Sample.cs");
            var text = await File.ReadAllTextAsync(stagedSource, cancellationToken);
            SawRenamedStagedSource = text.Contains("RenamedValue", StringComparison.Ordinal)
                && !text.Contains(" int Value ", StringComparison.Ordinal);
            return buildSucceeds
                ? new(true, 0, false, 1, "build succeeded")
                : new(false, 1, false, 1, "build failed");
        }
    }
}
