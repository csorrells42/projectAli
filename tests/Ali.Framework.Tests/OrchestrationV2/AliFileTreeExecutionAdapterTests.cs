using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliFileTreeExecutionAdapterTests
{
    [Fact]
    public void Registration_ContainsOnlyTheFourExplicitTreeAdapterIdentities()
    {
        using var fixture = new Fixture();
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.FileDeleteName,
            AliCapabilityCatalog.FileMoveName,
            AliCapabilityCatalog.FileCopyName,
            AliCapabilityCatalog.FileCreateDirectoryName
        };
        var actual = fixture.Access.ExecutionEffectAdapters
            .Where(adapter => expected.Contains(adapter.ToolName))
            .ToArray();

        Assert.Equal(4, actual.Length);
        Assert.Equal(expected, actual.Select(adapter => adapter.ToolName).ToHashSet(StringComparer.Ordinal));
        Assert.All(actual, adapter =>
        {
            Assert.Equal("ali.tool." + adapter.ToolName, adapter.CapabilityId);
            Assert.Equal("ali.reconcile." + adapter.ToolName, adapter.ReconcilerId);
            Assert.DoesNotContain('*', adapter.ToolName);
            Assert.DoesNotContain('*', adapter.CapabilityId);
            Assert.DoesNotContain('*', adapter.ReconcilerId);
        });
    }

    [Fact]
    public async Task CopyTree_ConsumesOneExactGrant_CommitsAndReconcilesWithoutReplay()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/a.txt"),
            "alpha",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/b.txt"),
            "beta",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        Assert.False(Directory.Exists(fixture.PhysicalPath("copied")));

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source",
            "Workspace/copied",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.Equal(
            "alpha",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/a.txt"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "beta",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/b.txt"),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
                "Workspace/source",
                "Workspace/replay",
                TestContext.Current.CancellationToken));

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.True(
            reconciled.Disposition == ActionReconciliationDisposition.Applied,
            $"Expected Applied but received {reconciled.Disposition}: {reconciled.OutcomeCode}");
        Assert.NotNull(reconciled.AppliedEvidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_PrePublicationStagingDriftNeverPublishesAFileOrTree(bool directorySource)
    {
        string? stagingPath = null;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.CopyStagingPopulated)
            {
                return;
            }

            if (directorySource)
            {
                File.WriteAllText(Path.Combine(stagingPath!, "unexpected.txt"), "drifted");
            }
            else
            {
                File.WriteAllText(stagingPath!, "drifted");
            }
        });
        if (directorySource)
        {
            Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
            await File.WriteAllTextAsync(
                fixture.PhysicalPath("source/nested/value.txt"),
                "authenticated",
                TestContext.Current.CancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(
                fixture.PhysicalPath("source.txt"),
                "authenticated",
                TestContext.Current.CancellationToken);
        }

        var source = directorySource ? "Workspace/source" : "Workspace/source.txt";
        var destination = directorySource ? "Workspace/copied" : "Workspace/copied.txt";
        var arguments = Arguments(("sourcePath", source), ("destinationPath", destination));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        stagingPath = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            source,
            destination,
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.PhysicalPath("copied.txt")));
        Assert.False(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.True(directorySource ? Directory.Exists(stagingPath) : File.Exists(stagingPath));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task Copy_PrePublicationSourceDriftNeverPublishesTheAuthenticatedStagingFile()
    {
        Fixture? fixtureReference = null;
        var mutationBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyStagingPopulated)
            {
                try
                {
                    File.WriteAllText(
                        fixtureReference!.PhysicalPath("source.txt"),
                        "changed-after-staging");
                }
                catch (IOException)
                {
                    mutationBlocked = true;
                    throw new IOException("Injected failure after the source lock proved effective.");
                }
            }
        });
        fixtureReference = fixture;
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/copied.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var stagingPath = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source.txt",
            "Workspace/copied.txt",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.PhysicalPath("copied.txt")));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(stagingPath, TestContext.Current.CancellationToken));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source.txt"),
                TestContext.Current.CancellationToken));
        Assert.True(mutationBlocked);
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task Copy_PrePublicationDestinationCreationPreservesExternalFileAndStaging()
    {
        Fixture? fixtureReference = null;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyStagingPopulated)
            {
                File.WriteAllText(
                    fixtureReference!.PhysicalPath("copied.txt"),
                    "external");
            }
        });
        fixtureReference = fixture;
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/copied.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var stagingPath = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source.txt",
            "Workspace/copied.txt",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.Equal(
            "external",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied.txt"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(stagingPath, TestContext.Current.CancellationToken));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source.txt"),
                TestContext.Current.CancellationToken));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task Copy_CancellationAfterBoundedChunkRetainsPartialStagingAndNeverPublishes()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyChunkWritten)
            {
                cancellation.Cancel();
            }
        });
        var sourceBytes = new byte[400_000];
        for (var index = 0; index < sourceBytes.Length; index++)
        {
            sourceBytes[index] = (byte)(index % 251);
        }
        await File.WriteAllBytesAsync(
            fixture.PhysicalPath("source.bin"),
            sourceBytes,
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.bin"),
            ("destinationPath", "Workspace/copied.bin"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var stagingPath = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
                "Workspace/source.bin",
                "Workspace/copied.bin",
                cancellation.Token));

        Assert.False(File.Exists(fixture.PhysicalPath("copied.bin")));
        Assert.True(File.Exists(stagingPath));
        var partialLength = new FileInfo(stagingPath).Length;
        Assert.InRange(partialLength, 1, sourceBytes.LongLength - 1);
        Assert.Equal(
            sourceBytes,
            await File.ReadAllBytesAsync(
                fixture.PhysicalPath("source.bin"),
                TestContext.Current.CancellationToken));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);

        await activation.FailAsync(exception, CancellationToken.None);
        await activation.DisposeAsync();
        Assert.True(File.Exists(stagingPath));
    }

    [Fact]
    public async Task MoveCreateAndDelete_EachRequireTheirOwnExactGrantAndPostState()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("move.txt"),
            "move-me",
            TestContext.Current.CancellationToken);

        var moveArguments = Arguments(
            ("sourcePath", "Workspace/move.txt"),
            ("destinationPath", "Workspace/moved.txt"));
        var move = fixture.Adapter(AliCapabilityCatalog.FileMoveName);
        var movePreparation = await fixture.PrepareAsync(move, moveArguments);
        var moveActivation = fixture.EnterGrant(move, moveArguments, movePreparation);
        var moveResult = await fixture.Access.MoveAsync(
            "Workspace/move.txt",
            "Workspace/moved.txt",
            TestContext.Current.CancellationToken);
        await moveActivation.CompleteAsync(moveResult, CancellationToken.None);
        await moveActivation.DisposeAsync();
        Assert.True(moveResult.Success);
        Assert.False(File.Exists(fixture.PhysicalPath("move.txt")));
        Assert.True(File.Exists(fixture.PhysicalPath("moved.txt")));

        var createArguments = Arguments(("path", "Workspace/new-folder"));
        var create = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var createPreparation = await fixture.PrepareAsync(create, createArguments);
        var createActivation = fixture.EnterGrant(create, createArguments, createPreparation);
        var createResult = await new AliWorkstationFileUtilities(fixture.Access)
            .CreateDirectoryAsync(
                "Workspace/new-folder",
                TestContext.Current.CancellationToken);
        await createActivation.CompleteAsync(createResult, CancellationToken.None);
        await createActivation.DisposeAsync();
        Assert.True(createResult.Success);
        Assert.True(Directory.Exists(fixture.PhysicalPath("new-folder")));

        var deleteArguments = Arguments(("fileName", "Workspace/moved.txt"));
        var delete = fixture.Adapter(AliCapabilityCatalog.FileDeleteName);
        var deletePreparation = await fixture.PrepareAsync(delete, deleteArguments);
        var deleteActivation = fixture.EnterGrant(delete, deleteArguments, deletePreparation);
        var deleted = await fixture.Access.FrameworkStore.DeleteAsync(
            "Workspace/moved.txt",
            TestContext.Current.CancellationToken);
        await deleteActivation.CompleteAsync(deleted, CancellationToken.None);
        await deleteActivation.DisposeAsync();
        Assert.True(deleted);
        Assert.False(File.Exists(fixture.PhysicalPath("moved.txt")));
        Assert.True(Directory.EnumerateFiles(
            fixture.Access.RecoverableTrashPath,
            "moved.txt",
            SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.FileCopyName)]
    [InlineData(AliCapabilityCatalog.FileMoveName)]
    public async Task Transfer_MissingDestinationParentIsRejectedWithoutResidue(string toolName)
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "source",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/missing/child.txt"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.PrepareAsync(fixture.Adapter(toolName), arguments));

        Assert.False(Directory.Exists(fixture.PhysicalPath("missing")));
        Assert.Equal(
            "source",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source.txt"),
                TestContext.Current.CancellationToken));
        Assert.Empty(fixture.DomainPlanPaths());
    }

    [Theory]
    [InlineData(AliCapabilityCatalog.FileCopyName)]
    [InlineData(AliCapabilityCatalog.FileMoveName)]
    public async Task DirectoryTransfer_ToDescendantIsRejectedBeforePlanOrStaging(
        string toolName)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PhysicalPath("source/existing-parent"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/value.txt"),
            "source",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/source/existing-parent/descendant"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.PrepareAsync(fixture.Adapter(toolName), arguments));

        Assert.Empty(fixture.DomainPlanPaths());
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            fixture.PhysicalPath("source/existing-parent")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            fixture.PhysicalPath("source"),
            ".ali-durable-*",
            SearchOption.AllDirectories));
        Assert.Equal(
            "source",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source/value.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_SourceContainingRecoverableTrashIsRejectedBeforePlanPublication()
    {
        using var fixture = new Fixture(trashRootRelativePath: "container/RecoverableTrash");
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("container/preserved.txt"),
            "preserved",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(("fileName", "Workspace/container"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileDeleteName);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync(adapter, arguments));

        Assert.Empty(fixture.DomainPlanPaths());
        Assert.Equal(
            "preserved",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("container/preserved.txt"),
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(fixture.PhysicalPath("container/RecoverableTrash")));
    }

    [Fact]
    public async Task NestedCreateFailure_CompensatesTheEntireUnpublishedChain()
    {
        var injected = 0;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.DirectoryStagingChildCreated
                && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                throw new IOException("Injected nested-directory staging failure.");
            }
        });
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access)
            .CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.False(Directory.Exists(fixture.PhysicalPath("outer")));
        Assert.False(Directory.Exists(staging));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
    }

    [Fact]
    public async Task NestedCreateInterruption_RecoveryCompensatesOwnedStagingBeforeReportingAbsent()
    {
        var injected = 0;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.DirectoryStagingChildCreated
                && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                throw new AliFileTreeSimulatedInterruptionException(checkpoint);
            }
        });
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(staging));
        Assert.False(Directory.Exists(fixture.PhysicalPath("outer")));

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(fixture.PhysicalPath("outer")));

        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    [Theory]
    [InlineData(
        nameof(AliFileTreeExecutionCheckpoint.DirectoryStagingRootCreated),
        1,
        ActionReconciliationDisposition.Absent)]
    [InlineData(
        nameof(AliFileTreeExecutionCheckpoint.DirectoryStagingChildCreated),
        1,
        ActionReconciliationDisposition.Absent)]
    [InlineData(
        nameof(AliFileTreeExecutionCheckpoint.DirectoryStagingChildCreated),
        2,
        ActionReconciliationDisposition.Absent)]
    [InlineData(
        nameof(AliFileTreeExecutionCheckpoint.DirectoryChainPublished),
        1,
        ActionReconciliationDisposition.Applied)]
    public async Task NestedCreateInterruption_FreshCoordinatorReconcilesEachPublicationBoundary(
        string checkpointName,
        int checkpointOccurrence,
        ActionReconciliationDisposition expectedDisposition)
    {
        var checkpoint = Enum.Parse<AliFileTreeExecutionCheckpoint>(
            checkpointName,
            ignoreCase: false);
        var observed = 0;
        using var fixture = new Fixture(observedCheckpoint =>
        {
            if (observedCheckpoint == checkpoint
                && Interlocked.Increment(ref observed) == checkpointOccurrence)
            {
                throw new AliFileTreeSimulatedInterruptionException(observedCheckpoint);
            }
        });
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken));

        var restarted = fixture.CreateRestartCoordinator();
        var restartedAdapter = restarted.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCreateDirectoryName);
        var reconciled = await restartedAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(restartedAdapter, arguments, prepared),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedDisposition, reconciled.Disposition);
        Assert.False(Directory.Exists(staging));
        if (expectedDisposition == ActionReconciliationDisposition.Applied)
        {
            Assert.True(Directory.Exists(fixture.PhysicalPath("outer/middle/leaf")));
        }
        else
        {
            Assert.False(Directory.Exists(fixture.PhysicalPath("outer")));
        }

        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task NestedCreateInterruption_FreshCoordinatorPreservesUnexpectedStagingAsUnknown()
    {
        var injected = 0;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.DirectoryStagingRootCreated
                && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                throw new AliFileTreeSimulatedInterruptionException(checkpoint);
            }
        });
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken));
        var unexpected = Path.Combine(staging, "unexpected.txt");
        await File.WriteAllTextAsync(
            unexpected,
            "not-owned-by-the-authenticated-directory-chain",
            TestContext.Current.CancellationToken);

        var restarted = fixture.CreateRestartCoordinator();
        var restartedAdapter = restarted.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCreateDirectoryName);
        var reconciled = await restartedAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(restartedAdapter, arguments, prepared),
            TestContext.Current.CancellationToken);

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.True(File.Exists(unexpected));
        Assert.False(Directory.Exists(fixture.PhysicalPath("outer")));

        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task NestedCreateWithPartialCanonicalParents_NeverReconcilesAsAbsent()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        Directory.CreateDirectory(fixture.PhysicalPath("outer"));

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access)
            .CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.NotEqual(ActionReconciliationDisposition.Absent, reconciled.Disposition);
        Assert.True(Directory.Exists(fixture.PhysicalPath("outer")));
        Assert.False(Directory.Exists(fixture.PhysicalPath("outer/middle")));
    }

    [Fact]
    public async Task DestinationDriftAfterStart_FailsClosedAsUnknown()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "source",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/destination.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("destination.txt"),
            "external",
            TestContext.Current.CancellationToken);

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source.txt",
            "Workspace/destination.txt",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("file-tree-result-state-mismatch", reconciled.OutcomeCode);
        Assert.Equal(
            "external",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("destination.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SourceDriftDuringPreparation_IsRejectedBeforeDomainPlanPublication()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "accepted",
            TestContext.Current.CancellationToken);
        var coordinator = fixture.CreateCoordinator(checkpoint =>
        {
            if (checkpoint == AliFileTreePreparationCheckpoint.ExactTargetCaptured)
            {
                File.WriteAllText(fixture.PhysicalPath("source.txt"), "drifted");
            }
        });
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/destination.txt"));
        var adapter = coordinator.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCopyName);
        var target = Assert.Single(coordinator.TargetStateAdapters);

        await Assert.ThrowsAsync<AliExecutionPreparationException>(() =>
            fixture.PrepareAsync(adapter, arguments, target));

        Assert.Equal(
            "drifted",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source.txt"),
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.PhysicalPath("destination.txt")));
    }

    [Fact]
    public async Task DestinationDriftDuringPreparation_IsRejectedBeforeDomainPlanPublication()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "accepted",
            TestContext.Current.CancellationToken);
        var coordinator = fixture.CreateCoordinator(checkpoint =>
        {
            if (checkpoint == AliFileTreePreparationCheckpoint.ExactTargetCaptured)
            {
                File.WriteAllText(fixture.PhysicalPath("destination.txt"), "external");
            }
        });
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/destination.txt"));
        var adapter = coordinator.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCopyName);
        var target = Assert.Single(coordinator.TargetStateAdapters);

        await Assert.ThrowsAsync<AliExecutionPreparationException>(() =>
            fixture.PrepareAsync(adapter, arguments, target));

        Assert.Equal(
            "external",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("destination.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CopyStagingResidueAfterPreparation_IsBoundAndCannotReconcileAsAbsent()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "source",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/destination.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var planPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(
                fixture.Root,
                "OrchestrationV2",
                "FileTreeInvocations",
                "Domain"),
            "*.file-tree-plan.json",
            SearchOption.TopDirectoryOnly));
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(
            planPath,
            TestContext.Current.CancellationToken));
        var stagingPath = plan.RootElement.GetProperty("StagingPhysicalPath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(stagingPath));
        await File.WriteAllTextAsync(
            stagingPath!,
            "partial-staging-residue",
            TestContext.Current.CancellationToken);

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source.txt",
            "Workspace/destination.txt",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("file-tree-result-state-mismatch", reconciled.OutcomeCode);
        Assert.True(File.Exists(stagingPath));
        Assert.False(File.Exists(fixture.PhysicalPath("destination.txt")));
        Assert.Equal(
            "source",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RootEscapeIsRejectedBeforeGrantCreation()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "source",
            TestContext.Current.CancellationToken);
        var escape = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/../escape.txt"));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync(
            fixture.Adapter(AliCapabilityCatalog.FileCopyName),
            escape));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.txt")));
    }

    [Fact]
    public async Task DirectoryReparseIsRejectedBeforeGrantCreation()
    {
        using var fixture = new Fixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(outside, "secret.txt"),
            "secret",
            TestContext.Current.CancellationToken);
        var link = fixture.PhysicalPath("linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            fixture.RegisterLink(link);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Assert.Skip("Directory symbolic-link creation is unavailable: " + exception.Message);
        }

        var reparseArguments = Arguments(
            ("sourcePath", "Workspace/linked"),
            ("destinationPath", "Workspace/copied-link"));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync(
            fixture.Adapter(AliCapabilityCatalog.FileCopyName),
            reparseArguments));
        Assert.False(Directory.Exists(fixture.PhysicalPath("copied-link")));
    }

    [Theory]
    [InlineData("name:stream")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("COM1")]
    [InlineData("COM\u00B9.log")]
    [InlineData("LPT9")]
    [InlineData("LPT\u00B3.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("control\u0001")]
    public void NativeLeafValidation_RejectsAliasesThatCouldEscapeOneOrdinaryLeaf(
        string leaf)
    {
        Assert.Throws<InvalidDataException>(() =>
            AliFileTreeWindowsBoundary.ValidateNativeLeaf(leaf));
    }

    [Fact]
    public async Task Move_OneCharacterDestinationUsesTheMinimumSafeRenameBuffer()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("a"),
            "one-character-rename",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/a"),
            ("destinationPath", "Workspace/b"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileMoveName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await fixture.Access.MoveAsync(
            "Workspace/a",
            "Workspace/b",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.PhysicalPath("a")));
        Assert.Equal(
            "one-character-rename",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("b"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_PreparedAncestorReboundThroughSymlinkNeverUsesTheReboundTree()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.PhysicalPath("parent"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("parent/source.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/parent/source.txt"),
            ("destinationPath", "Workspace/destination.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileMoveName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var movedParent = Path.Combine(fixture.Root, "moved-parent");
        Directory.Move(fixture.PhysicalPath("parent"), movedParent);
        try
        {
            Directory.CreateSymbolicLink(fixture.PhysicalPath("parent"), movedParent);
            fixture.RegisterLink(fixture.PhysicalPath("parent"));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Assert.Skip("Directory symbolic-link creation is unavailable: " + exception.Message);
        }

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await fixture.Access.MoveAsync(
            "Workspace/parent/source.txt",
            "Workspace/destination.txt",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.PhysicalPath("destination.txt")));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                Path.Combine(movedParent, "source.txt"),
                TestContext.Current.CancellationToken));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task NestedCreate_HeldChildCannotBeReplacedBeforeTheDeeperCreate()
    {
        string? staging = null;
        var replacementBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.DirectoryStagingChildCreated
                || replacementBlocked)
            {
                return;
            }
            try
            {
                Directory.Delete(Path.Combine(staging!, "middle"), recursive: false);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                replacementBlocked = true;
            }
        });
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access)
            .CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(replacementBlocked);
        Assert.True(Directory.Exists(fixture.PhysicalPath("outer/middle/leaf")));
    }

    [Fact]
    public async Task CopyTree_HeldSourceAndDestinationChildrenRejectNestedReplacement()
    {
        string? staging = null;
        var sourceReplacementBlocked = false;
        var destinationReplacementBlocked = false;
        Fixture? fixtureReference = null;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopySourceDirectoryChildOpened)
            {
                try
                {
                    Directory.Move(
                        fixtureReference!.PhysicalPath("source/nested"),
                        fixtureReference.PhysicalPath("source/rebound"));
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException)
                {
                    sourceReplacementBlocked = true;
                }
            }
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyDestinationDirectoryChildCreated)
            {
                try
                {
                    Directory.Delete(Path.Combine(staging!, "nested"), recursive: false);
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException)
                {
                    destinationReplacementBlocked = true;
                }
            }
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "held-child",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source",
            "Workspace/copied",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(sourceReplacementBlocked);
        Assert.True(destinationReplacementBlocked);
        Assert.Equal(
            "held-child",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/value.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CopyBeforeHandleRename_CompleteSourceAndStagingClosuresBlockNestedMutation()
    {
        Fixture? fixtureReference = null;
        string? staging = null;
        var sourceMutationBlocked = false;
        var stagingMutationBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.CopyBeforeHandleRename)
            {
                return;
            }
            try
            {
                File.WriteAllText(
                    fixtureReference!.PhysicalPath("source/nested/value.txt"),
                    "source-interposition");
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                sourceMutationBlocked = true;
            }
            try
            {
                File.WriteAllText(
                    Path.Combine(staging!, "nested", "value.txt"),
                    "staging-interposition");
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                stagingMutationBlocked = true;
            }
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source",
            "Workspace/copied",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(sourceMutationBlocked);
        Assert.True(stagingMutationBlocked);
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/value.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MoveBeforeHandleRename_CompleteSourceClosureBlocksNestedMutation()
    {
        Fixture? fixtureReference = null;
        var mutationBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.MoveBeforeHandleRename)
            {
                return;
            }
            try
            {
                File.WriteAllText(
                    fixtureReference!.PhysicalPath("source/nested/value.txt"),
                    "move-interposition");
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                mutationBlocked = true;
            }
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/moved"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileMoveName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await fixture.Access.MoveAsync(
            "Workspace/source",
            "Workspace/moved",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(mutationBlocked);
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("moved/nested/value.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteBeforeHandleRename_CompleteSourceClosureBlocksNestedMutation()
    {
        Fixture? fixtureReference = null;
        var mutationBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.DeleteBeforeHandleRename)
            {
                return;
            }
            try
            {
                File.WriteAllText(
                    fixtureReference!.PhysicalPath("source/nested/value.txt"),
                    "delete-interposition");
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                mutationBlocked = true;
            }
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(("fileName", "Workspace/source"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileDeleteName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await fixture.Access.FrameworkStore.DeleteAsync(
            "Workspace/source",
            TestContext.Current.CancellationToken);
        Assert.True(result);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(mutationBlocked);
        Assert.False(Directory.Exists(fixture.PhysicalPath("source")));
    }

    [Fact]
    public async Task DirectoryCreateBeforeHandleRename_NewChildInjectionIsDetectedAndCanonicalPublicationFailsClosed()
    {
        string? staging = null;
        var injectionSucceeded = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.DirectoryCreateBeforeHandleRename)
            {
                return;
            }
            File.WriteAllText(
                Path.Combine(staging!, "middle", "leaf", "injected.txt"),
                "directory-interposition");
            injectionSucceeded = true;
        });
        var arguments = Arguments(("path", "Workspace/outer/middle/leaf"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCreateDirectoryName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access)
            .CreateDirectoryAsync(
                "Workspace/outer/middle/leaf",
                TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(injectionSucceeded);
        Assert.False(Directory.Exists(fixture.PhysicalPath("outer")));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task CopyTree_PostNativeRenameGapMutationCannotReportSuccessAndReconcilesUnknown()
    {
        Fixture? fixtureReference = null;
        var mutationSucceeded = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint
                != AliFileTreeExecutionCheckpoint.TestOnlyAfterNativeRootRenameBeforeDescendantReseal)
            {
                return;
            }
            File.WriteAllText(
                fixtureReference!.PhysicalPath("copied/nested/value.txt"),
                "same-user-gap-mutation");
            mutationSucceeded = true;
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source",
            "Workspace/copied",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(mutationSucceeded);
        Assert.True(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.False(Directory.Exists(staging));
        Assert.Equal(
            "same-user-gap-mutation",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/value.txt"),
                TestContext.Current.CancellationToken));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task CopyTree_PostNativeRenameGapInterruptionWithoutMutationReconcilesApplied()
    {
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint
                == AliFileTreeExecutionCheckpoint.TestOnlyAfterNativeRootRenameBeforeDescendantReseal)
            {
                throw new AliFileTreeSimulatedInterruptionException(checkpoint);
            }
        });
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
                "Workspace/source",
                "Workspace/copied",
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.False(Directory.Exists(staging));

        var restarted = fixture.CreateRestartCoordinator();
        var restartedAdapter = restarted.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCopyName);
        var reconciled = await restartedAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(restartedAdapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/value.txt"),
                TestContext.Current.CancellationToken));
        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task Copy_NativeRenameDoesNotReplaceDestinationCreatedAfterPreparation()
    {
        Fixture? fixtureReference = null;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyBeforeHandleRename)
            {
                File.WriteAllText(
                    fixtureReference!.PhysicalPath("copied.txt"),
                    "external-destination");
            }
        });
        fixtureReference = fixture;
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/copied.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source.txt",
            "Workspace/copied.txt",
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.Equal(
            "external-destination",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied.txt"),
                TestContext.Current.CancellationToken));
        Assert.True(File.Exists(staging));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(staging, TestContext.Current.CancellationToken));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task CopyTree_PostRenameClosureBlocksLateChildMutationAndKeepsAuthenticatedPublication()
    {
        Fixture? fixtureReference = null;
        var mutationBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyAfterHandleRename)
            {
                try
                {
                    File.WriteAllText(
                        fixtureReference!.PhysicalPath("copied/nested/value.txt"),
                        "late-mutation");
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException)
                {
                    mutationBlocked = true;
                }
            }
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();

        var activation = fixture.EnterGrant(adapter, arguments, prepared);
        var result = await new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
            "Workspace/source",
            "Workspace/copied",
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.True(mutationBlocked);
        Assert.True(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/value.txt"),
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(staging));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
    }

    [Fact]
    public async Task CopyTree_RestartRecognizesAuthenticatedPublicationWhenLateMutationWasBlocked()
    {
        Fixture? fixtureReference = null;
        var mutationBlocked = false;
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyAfterHandleRename)
            {
                try
                {
                    File.WriteAllText(
                        fixtureReference!.PhysicalPath("copied/nested/value.txt"),
                        "interrupted-late-mutation");
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException)
                {
                    mutationBlocked = true;
                }
                throw new AliFileTreeSimulatedInterruptionException(checkpoint);
            }
        });
        fixtureReference = fixture;
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
                "Workspace/source",
                "Workspace/copied",
                TestContext.Current.CancellationToken));
        Assert.True(mutationBlocked);
        Assert.True(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.False(Directory.Exists(staging));

        var restarted = fixture.CreateRestartCoordinator();
        var restartedAdapter = restarted.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCopyName);
        var reconciled = await restartedAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(restartedAdapter, arguments, prepared),
            TestContext.Current.CancellationToken);

        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
        Assert.True(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("copied/nested/value.txt"),
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(staging));
        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task CopyTree_RestartRollbackClosureBlocksNestedMutationAndNeverRestoresDrift()
    {
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyAfterHandleRename)
            {
                throw new AliFileTreeSimulatedInterruptionException(checkpoint);
            }
        });
        Directory.CreateDirectory(fixture.PhysicalPath("source/nested"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source"),
            ("destinationPath", "Workspace/copied"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
                "Workspace/source",
                "Workspace/copied",
                TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source/nested/value.txt"),
            "source-drift",
            TestContext.Current.CancellationToken);

        var nestedMutationBlocked = false;
        var restarted = fixture.CreateRestartCoordinator(checkpoint =>
        {
            if (checkpoint != AliFileTreeExecutionCheckpoint.RecoveryBeforeHandleRollback)
            {
                return;
            }
            try
            {
                File.WriteAllText(
                    fixture.PhysicalPath("copied/nested/value.txt"),
                    "recovery-interposition");
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                nestedMutationBlocked = true;
            }
        });
        var restartedAdapter = restarted.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCopyName);
        var reconciled = await restartedAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(restartedAdapter, arguments, prepared),
            TestContext.Current.CancellationToken);

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.True(nestedMutationBlocked);
        Assert.False(Directory.Exists(fixture.PhysicalPath("copied")));
        Assert.True(Directory.Exists(staging));
        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                Path.Combine(staging, "nested", "value.txt"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "source-drift",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("source/nested/value.txt"),
                TestContext.Current.CancellationToken));
        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    [Fact]
    public async Task CopyTree_RestartTreatsRenamedPublicationParentAsUnknown()
    {
        using var fixture = new Fixture(checkpoint =>
        {
            if (checkpoint == AliFileTreeExecutionCheckpoint.CopyExecutionBindingPersisted)
            {
                throw new AliFileTreeSimulatedInterruptionException(checkpoint);
            }
        });
        Directory.CreateDirectory(fixture.PhysicalPath("target"));
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("source.txt"),
            "authenticated",
            TestContext.Current.CancellationToken);
        var arguments = Arguments(
            ("sourcePath", "Workspace/source.txt"),
            ("destinationPath", "Workspace/target/copied.txt"));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileCopyName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        var staging = await fixture.SingleStagingPathAsync();
        var activation = fixture.EnterGrant(adapter, arguments, prepared);

        var interruption = await Assert.ThrowsAsync<AliFileTreeSimulatedInterruptionException>(() =>
            new AliWorkstationFileUtilities(fixture.Access).CopyAsync(
                "Workspace/source.txt",
                "Workspace/target/copied.txt",
                TestContext.Current.CancellationToken));
        var movedParent = Path.Combine(fixture.Root, "moved-target");
        Directory.Move(fixture.PhysicalPath("target"), movedParent);
        Directory.CreateDirectory(fixture.PhysicalPath("target"));

        var restarted = fixture.CreateRestartCoordinator();
        var restartedAdapter = restarted.ExecutionEffectAdapters.Single(candidate =>
            candidate.ToolName == AliCapabilityCatalog.FileCopyName);
        var reconciled = await restartedAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(restartedAdapter, arguments, prepared),
            TestContext.Current.CancellationToken);

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.True(File.Exists(Path.Combine(movedParent, Path.GetFileName(staging))));
        Assert.False(File.Exists(fixture.PhysicalPath("target/copied.txt")));
        await activation.FailAsync(interruption, CancellationToken.None);
        await activation.DisposeAsync();
    }

    private static AIFunctionArguments Arguments(params (string Name, object? Value)[] values)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in values)
        {
            arguments[name] = value;
        }
        return arguments;
    }

    private static string ArgumentsDigest(AIFunctionArguments arguments)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(
            JsonSerializer.SerializeToElement(arguments));
        try
        {
            return TurnStateIntegrity.Digest(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private sealed class Fixture : IDisposable
    {
        private readonly string _workspace;
        private readonly AliWorkstationFileStore _store;
        private readonly List<string> _links = [];

        internal Fixture(
            Action<AliFileTreeExecutionCheckpoint>? executionFaultHook = null,
            string? trashRootRelativePath = null)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "Ali-Cp7-FileTree-Tests",
                Guid.NewGuid().ToString("N"));
            _workspace = Directory.CreateDirectory(Path.Combine(Root, "Workspace")).FullName;
            var trashRoot = string.IsNullOrWhiteSpace(trashRootRelativePath)
                ? Path.Combine(Root, "Trash")
                : Path.GetFullPath(Path.Combine(
                    _workspace,
                    trashRootRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            _store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", _workspace)],
                trashRoot);
            Access = new AliWorkstationFileAccess(
                _store,
                new AgentFileActionAuditStore(Root, activeUsers: null),
                new AgentToolPermissionStore(Root),
                Path.Combine(Root, "OrchestrationV2"),
                "file-tree-test",
                treeExecutionFaultHook: executionFaultHook);
        }

        internal string Root { get; }

        internal TurnIdentity Identity { get; } =
            new("user", "file-tree-adapters", "assistant-message");

        internal AliWorkstationFileAccess Access { get; }

        internal string PhysicalPath(string relativePath) =>
            Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal async Task<string> SingleStagingPathAsync()
        {
            var planPath = Assert.Single(DomainPlanPaths());
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(
                planPath,
                TestContext.Current.CancellationToken));
            return plan.RootElement.GetProperty("StagingPhysicalPath").GetString()
                ?? throw new InvalidDataException(
                    "The exact directory plan did not bind its staging path.");
        }

        internal IReadOnlyList<string> DomainPlanPaths()
        {
            var domainRoot = Path.Combine(
                Root,
                "OrchestrationV2",
                "FileTreeInvocations",
                "Domain");
            return Directory.Exists(domainRoot)
                ? Directory.EnumerateFiles(
                        domainRoot,
                        "*.file-tree-plan.json",
                        SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
                : [];
        }

        internal void RegisterLink(string path) => _links.Add(path);

        internal AliFileTreeMutationCoordinator CreateCoordinator(
            Action<AliFileTreePreparationCheckpoint> preparationFaultHook) =>
            new(
                _store,
                Path.Combine(Root, "HookedOrchestrationV2"),
                "file-tree-hook-test",
                evidence: null,
                preparationFaultHook: preparationFaultHook);

        internal AliFileTreeMutationCoordinator CreateRestartCoordinator(
            Action<AliFileTreeExecutionCheckpoint>? executionFaultHook = null) =>
            new(
                _store,
                Path.Combine(Root, "OrchestrationV2"),
                "file-tree-test",
                evidence: null,
                executionFaultHook: executionFaultHook);

        internal IAliExecutionEffectAdapter Adapter(string toolName) =>
            Access.ExecutionEffectAdapters.Single(adapter =>
                string.Equals(adapter.ToolName, toolName, StringComparison.Ordinal));

        internal async Task<AliExecutionPreparation> PrepareAsync(
            IAliExecutionEffectAdapter adapter,
            AIFunctionArguments arguments,
            IActionTargetStateAdapter? exactTargetAdapter = null)
        {
            var element = JsonSerializer.SerializeToElement(arguments).Clone();
            var targetAdapter = exactTargetAdapter
                ?? Access.TargetStateAdapters.Single(candidate =>
                    candidate.ToolNames.Contains(adapter.ToolName, StringComparer.Ordinal));
            var snapshot = targetAdapter.Capture(adapter.ToolName, element);
            var targetDigest = WorkIdentityCanonicalizer.MapDigest(
                "action-target-versions-v1",
                snapshot.TargetVersions);
            return await adapter.PrepareAsync(
                new AliExecutionPreparationRequest(
                    Identity,
                    "call-file-tree",
                    "work-file-tree",
                    adapter.ToolName,
                    adapter.CapabilityId,
                    adapter.ReconcilerId,
                    element,
                    ArgumentsDigest(arguments),
                    Digest("action"),
                    targetDigest,
                    Digest("permission"),
                    Digest("registry"),
                    Digest("execution-registry")),
                TestContext.Current.CancellationToken);
        }

        internal AliExecutionInvocationActivation EnterGrant(
            IAliExecutionEffectAdapter adapter,
            AIFunctionArguments arguments,
            AliExecutionPreparation preparation)
        {
            var grant = new AliExecutionGrant(
                Digest("idempotency"),
                "call-file-tree",
                adapter.ToolName,
                adapter.CapabilityId,
                ArgumentsDigest(arguments),
                preparation.TargetVersionDigest,
                Digest("permission"),
                Digest("execution-registry"),
                adapter.ReconcilerId,
                preparation.PreparationIdentity,
                preparation.RootBinding);
            return new AliExecutionInvocationScope(grant).Enter(arguments);
        }

        internal PreparedActionIntent Intent(
            IAliExecutionEffectAdapter adapter,
            AIFunctionArguments arguments,
            AliExecutionPreparation preparation) =>
            new(
                Digest("idempotency"),
                "work-file-tree",
                adapter.ToolName,
                adapter.CapabilityId,
                ArgumentsDigest(arguments),
                preparation.TargetVersionDigest,
                Digest("permission"),
                Digest("registry"),
                Digest("execution-registry"),
                adapter.ReconcilerId,
                preparation.RootBinding,
                RequiresApproval: true,
                AcceptedCallId: "call-file-tree",
                PreparationIdentity: preparation.PreparationIdentity);

        public void Dispose()
        {
            foreach (var link in _links)
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link, recursive: false);
                }
            }
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
