using Ali.Modules.Coding;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests.Coding;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class RoslynActionPublicationRecoveryTests
{
    [Fact]
    public async Task CommittedSourceWithApplyingHandle_IsPostverifiedAndSealedWithoutRepublish()
    {
        await using var fixture = new RecoveryFixture();
        var prepared = await fixture.CreateApplyingHandleAsync();
        var receipt = await fixture.PublishAsync(prepared.ChangeSet);

        var first = await fixture.Recovery.ReconcileAsync(
            prepared.ChangeSet.Id,
            AliSourcePublicationState.Committed,
            fixture.AuthorizationBindingDigest,
            Cancellation);
        var second = await fixture.Recovery.ReconcileAsync(
            prepared.ChangeSet.Id,
            AliSourcePublicationState.Committed,
            fixture.AuthorizationBindingDigest,
            Cancellation);

        Assert.Equal(
            AliRoslynActionPublicationRecoveryDisposition.AppliedAndPostverified,
            first.Disposition);
        Assert.Equal(
            AliRoslynActionPublicationRecoveryDisposition.AppliedAndPostverified,
            second.Disposition);
        var sealedHandle = await fixture.Handles.LoadAsync(prepared.Handle.Id, Cancellation);
        Assert.Equal(AliRoslynActionHandleState.Applied, sealedHandle.State);
        Assert.Equal(receipt.GrantId, sealedHandle.PublicationTransactionId);
        Assert.Equal(RecoveryFixture.AfterSource, await File.ReadAllTextAsync(fixture.SourcePath, Cancellation));
    }

    [Fact]
    public async Task CommittedSourceWithDivergedSemanticState_IsAppliedButNeverReportedPostverified()
    {
        await using var fixture = new RecoveryFixture();
        var prepared = await fixture.CreateApplyingHandleAsync();
        _ = await fixture.PublishAsync(prepared.ChangeSet);
        await File.WriteAllTextAsync(
            fixture.AdditionalSourcePath,
            "namespace Demo; internal sealed class UnrelatedAfterCommit { }",
            Cancellation);

        var result = await fixture.Recovery.ReconcileAsync(
            prepared.ChangeSet.Id,
            AliSourcePublicationState.Committed,
            fixture.AuthorizationBindingDigest,
            Cancellation);

        Assert.Equal(
            AliRoslynActionPublicationRecoveryDisposition.AppliedNeedsReview,
            result.Disposition);
        Assert.Equal(
            "roslyn-publication-committed-postverify-failed",
            result.OutcomeCode);
        var failed = await fixture.Handles.LoadAsync(prepared.Handle.Id, Cancellation);
        Assert.Equal(AliRoslynActionHandleState.Failed, failed.State);
        Assert.Equal("canonical-postverify-failed", failed.FailureCode);
        Assert.Equal(RecoveryFixture.AfterSource, await File.ReadAllTextAsync(fixture.SourcePath, Cancellation));
    }

    [Fact]
    public async Task RolledBackSource_ClosesApplyingHandleAndIsSafeOnlyAsAbsent()
    {
        await using var fixture = new RecoveryFixture();
        var prepared = await fixture.CreateApplyingHandleAsync();

        var source = await fixture.SourceReconciler.ReconcileAsync(
            prepared.ChangeSet.Id,
            Cancellation);
        var result = await fixture.Recovery.ReconcileAsync(
            prepared.ChangeSet.Id,
            source.State,
            expectedAuthorizationBindingDigest: null,
            cancellationToken: Cancellation);

        Assert.Equal(AliSourcePublicationState.RolledBack, source.State);
        Assert.Equal(AliRoslynActionPublicationRecoveryDisposition.Absent, result.Disposition);
        var failed = await fixture.Handles.LoadAsync(prepared.Handle.Id, Cancellation);
        Assert.Equal(AliRoslynActionHandleState.Failed, failed.State);
        Assert.Equal("publication-rolled-back", failed.FailureCode);
        Assert.Equal(RecoveryFixture.BeforeSource, await File.ReadAllTextAsync(fixture.SourcePath, Cancellation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommittedSourceWithChangedDurableAuthorization_RemainsUnknownAndUnsealed(
        bool changeExecutionRegistryIdentity)
    {
        await using var fixture = new RecoveryFixture();
        var prepared = await fixture.CreateApplyingHandleAsync();
        var intent = fixture.Intent(prepared.ChangeSet);
        Assert.True(AliExecutionAuthorizationDigest.TryCompute(
            AliExecutionAuthorizationDigest.SourcePublicationDomain,
            intent,
            out var exactAuthorizationBindingDigest));
        _ = await fixture.PublishAsync(prepared.ChangeSet, exactAuthorizationBindingDigest);
        var changedIntent = changeExecutionRegistryIdentity
            ? intent with { ExecutionRegistryIdentityDigest = RecoveryFixture.Digest('9') }
            : intent with { RootBinding = RecoveryFixture.Digest('8') };
        Assert.True(AliExecutionAuthorizationDigest.TryCompute(
            AliExecutionAuthorizationDigest.SourcePublicationDomain,
            changedIntent,
            out var changedAuthorizationBindingDigest));

        var result = await fixture.Recovery.ReconcileAsync(
            prepared.ChangeSet.Id,
            AliSourcePublicationState.Committed,
            changedAuthorizationBindingDigest,
            Cancellation);

        Assert.Equal(AliRoslynActionPublicationRecoveryDisposition.Unknown, result.Disposition);
        Assert.Equal("roslyn-publication-authorization-mismatch", result.OutcomeCode);
        var unsealed = await fixture.Handles.LoadAsync(prepared.Handle.Id, Cancellation);
        Assert.Equal(AliRoslynActionHandleState.Applying, unsealed.State);
        Assert.Null(unsealed.PublicationTransactionId);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly AliRoslynWorkspaceLoader _workspaceLoader;
        private readonly AliRoslynSolutionFingerprint _fingerprint;
        private readonly AliSourceChangeSetPublisher _sourcePublisher;

        internal RecoveryFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "AliRoslynPublicationRecoveryTests",
                Guid.NewGuid().ToString("N"));
            var workspaceRoot = Path.Combine(_root, "workspace");
            ProjectDirectory = Path.Combine(workspaceRoot, "Sample");
            Directory.CreateDirectory(ProjectDirectory);
            TargetPath = Path.Combine(ProjectDirectory, "Sample.csproj");
            SourcePath = Path.Combine(ProjectDirectory, "Sample.cs");
            AdditionalSourcePath = Path.Combine(ProjectDirectory, "Additional.cs");
            File.WriteAllText(
                TargetPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(SourcePath, BeforeSource);

            var stateRoot = Path.Combine(_root, "state");
            var permissions = new AgentToolPermissionStore(stateRoot);
            var fileStore = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", workspaceRoot)
            ], Path.Combine(_root, "trash"));
            var fileAccess = new AliWorkstationFileAccess(
                fileStore,
                new AgentFileActionAuditStore(stateRoot, activeUsers: null),
                permissions);
            var resolver = new AliCodingProjectResolver(fileAccess);
            _workspaceLoader = new AliRoslynWorkspaceLoader(resolver);
            _fingerprint = new AliRoslynSolutionFingerprint(
                new AliRoslynTargetReferenceResolver());
            ChangeSets = new AliSourceChangeSetStore(
                Path.Combine(stateRoot, "changesets"),
                "publication-recovery-tests");
            _sourcePublisher = new AliSourceChangeSetPublisher(
                ChangeSets,
                new AliSourceChangeSetValidator(ChangeSets));
            SourceReconciler = new AliSourceChangeSetReconciler(
                ChangeSets,
                _sourcePublisher);
            Handles = new AliRoslynActionHandleStore(
                Path.Combine(stateRoot, "handles"),
                "publication-recovery-tests");
            Recovery = new AliRoslynActionPublicationRecovery(
                Handles,
                ChangeSets,
                _sourcePublisher,
                _workspaceLoader,
                _fingerprint);
        }

        internal const string BeforeSource =
            "namespace Demo; public sealed class Sample { public int Value { get; set; } }";
        internal const string AfterSource =
            "namespace Demo; public sealed class Sample { public int RenamedValue { get; set; } }";

        internal string ProjectDirectory { get; }
        internal string TargetPath { get; }
        internal string SourcePath { get; }
        internal string AdditionalSourcePath { get; }
        internal AliSourceChangeSetStore ChangeSets { get; }
        internal AliSourceChangeSetReconciler SourceReconciler { get; }
        internal AliRoslynActionHandleStore Handles { get; }
        internal AliRoslynActionPublicationRecovery Recovery { get; }

        internal string AuthorizationBindingDigest => Digest('e');

        internal PreparedActionIntent Intent(AliSourceChangeSet changeSet) =>
            new(
                Digest('1'),
                "work-1",
                AliCapabilityCatalog.RoslynApplyActionName,
                AliRoslynActionExecutionAdapter.CapabilityIdFor(
                    AliCapabilityCatalog.RoslynApplyActionName),
                Digest('2'),
                Digest('3'),
                Digest('4'),
                Digest('5'),
                Digest('6'),
                AliRoslynActionExecutionAdapter.ReconcilerIdFor(
                    AliCapabilityCatalog.RoslynApplyActionName),
                AliRoslynActionExecutionAdapter.RootBinding(ProjectDirectory),
                RequiresApproval: true,
                AcceptedCallId: "call-1",
                PreparationIdentity: changeSet.Id);

        internal async Task<(AliSourceChangeSet ChangeSet, AliRoslynActionHandle Handle)>
            CreateApplyingHandleAsync()
        {
            string beforeFingerprint;
            using (var before = await _workspaceLoader.LoadAsync(
                       "Workspace/Sample/Sample.csproj",
                       Cancellation))
            {
                Assert.Empty(before.Warnings);
                beforeFingerprint = (await _fingerprint.CaptureAsync(
                    before.Solution,
                    Cancellation)).Sha256;
            }

            File.WriteAllText(SourcePath, AfterSource);
            string afterFingerprint;
            using (var after = await _workspaceLoader.LoadAsync(
                       "Workspace/Sample/Sample.csproj",
                       Cancellation))
            {
                Assert.Empty(after.Warnings);
                afterFingerprint = (await _fingerprint.CaptureAsync(
                    after.Solution,
                    Cancellation)).Sha256;
            }

            // Restore the exact preimage before creating the authenticated publication manifest.
            File.WriteAllText(SourcePath, BeforeSource);
            var fresh = await ChangeSets.CreateAsync(
                ProjectDirectory,
                [AliSourceChangeRequest.ReplaceText("Sample.cs", AfterSource)],
                Cancellation);
            var now = DateTimeOffset.UtcNow;
            var handle = new AliRoslynActionHandle(
                Guid.NewGuid().ToString("N"),
                Digest('A'),
                "ali.roslyn.semantic-rename",
                "1.0.0",
                "microsoft.codeanalysis.semantic-rename",
                "Semantic rename",
                [],
                "Workspace/Sample/Sample.csproj",
                ProjectDirectory,
                "sample-project",
                "sample-document",
                SourcePath,
                0,
                5,
                "RenamedValue",
                beforeFingerprint,
                fresh.Id,
                fresh.ManifestDigest,
                now,
                now.AddHours(1),
                AliRoslynActionHandleState.Previewed,
                1);
            await Handles.CreateAsync(handle, afterFingerprint, Cancellation);
            var verified = await Handles.RecordVerificationAsync(
                handle.Id,
                handle.Revision,
                new AliRoslynPreverificationReceipt(
                    Guid.NewGuid().ToString("N"),
                    fresh.Id,
                    fresh.ManifestDigest,
                    beforeFingerprint,
                    afterFingerprint,
                    Digest('B'),
                    Digest('C'),
                    Guid.NewGuid().ToString("N"),
                    Digest('E'),
                    Digest('F'),
                    Digest('1'),
                    Digest('2'),
                    RoslynSucceeded: true,
                    BuildSucceeded: true,
                    TestsRun: 0,
                    TestsSucceeded: true,
                    Digest('D'),
                    now,
                    now.AddMinutes(30)),
                Cancellation);
            var applying = await Handles.BeginApplyAsync(
                verified.Id,
                verified.Revision,
                Cancellation);
            return (fresh, applying);
        }

        internal Task<AliSourcePublicationReceipt> PublishAsync(
            AliSourceChangeSet changeSet,
            string? authorizationBindingDigest = null) =>
            _sourcePublisher.PublishAsync(
                changeSet,
                AliSourcePublicationGrant.Issue(
                    changeSet,
                    authorizationBindingDigest ?? AuthorizationBindingDigest),
                Cancellation);

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

        internal static string Digest(char value) => new(value, 64);
    }
}
