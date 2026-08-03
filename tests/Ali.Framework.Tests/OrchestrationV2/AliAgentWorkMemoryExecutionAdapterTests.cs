using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001 // The production Framework provider is the subject of these tests.

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliAgentWorkMemoryExecutionAdapterTests
{
    [Fact]
    public void Registration_ContainsOnlyFourExactMutationIdentities()
    {
        using var fixture = new Fixture();
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            AliCapabilityCatalog.WorkMemoryWriteName,
            AliCapabilityCatalog.WorkMemoryReplaceName,
            AliCapabilityCatalog.WorkMemoryReplaceLinesName,
            AliCapabilityCatalog.WorkMemoryDeleteName
        };
        var actual = fixture.Memory.ExecutionEffectAdapters;

        Assert.Equal(4, actual.Count);
        Assert.True(expected.SetEquals(actual.Select(adapter => adapter.ToolName)));
        Assert.All(actual, adapter =>
        {
            Assert.Equal("ali.tool." + adapter.ToolName, adapter.CapabilityId);
            Assert.Equal("ali.reconcile." + adapter.ToolName, adapter.ReconcilerId);
            Assert.DoesNotContain('*', adapter.ToolName);
            Assert.DoesNotContain('*', adapter.CapabilityId);
            Assert.DoesNotContain('*', adapter.ReconcilerId);
        });
        Assert.Single(fixture.Memory.TargetStateAdapters);
        Assert.True(expected.SetEquals(fixture.Memory.TargetStateAdapters[0].ToolNames));
    }

    [Fact]
    public async Task FrameworkWrite_MultiCallFacadePublishesOnlyOnCompletion_AndGrantIsOneUse()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "notes.md"),
            ("content", "alpha\nbeta\n"),
            ("description", "Two lines used by the durable integration test."));
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            arguments);

        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "alpha\nbeta\n",
            "Two lines used by the durable integration test.",
            TestContext.Current.CancellationToken);

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        var stagedMain = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "notes.md",
            SearchOption.AllDirectories));
        Assert.Equal(
            "alpha\nbeta\n",
            await File.ReadAllTextAsync(
                stagedMain,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Memory.Store.WriteAsync(
                "unauthorized.md",
                "must not bypass the exact invocation",
                TestContext.Current.CancellationToken));

        await activation.CompleteAsync(result, CancellationToken.None);
        AssertNoStagingEntries(fixture.StagingRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Memory.Store.WriteAsync(
                "replay.md",
                "must not execute",
                TestContext.Current.CancellationToken));
        await activation.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Memory.Store.WriteAsync(
                "no-grant-bypass.md",
                "must not execute without a durable grant",
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.WorkspacePath("no-grant-bypass.md")));

        Assert.Equal(
            "alpha\nbeta\n",
            await File.ReadAllTextAsync(
                fixture.WorkspacePath("notes.md"),
                TestContext.Current.CancellationToken));
        Assert.Contains(
            Directory.EnumerateFiles(fixture.Workspace, "*", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).EndsWith(
                "_description.md",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(fixture.WorkspacePath("memories.md")));

        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.True(
            reconciled.Disposition == ActionReconciliationDisposition.Applied,
            $"Expected Applied but received {reconciled.Disposition}: {reconciled.OutcomeCode}");
        Assert.NotNull(reconciled.AppliedEvidence);
    }

    [Fact]
    public async Task AllFourFrameworkMutations_PreserveExactProviderSemantics()
    {
        using var fixture = new Fixture();

        await fixture.RunProviderMutationAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "alpha\nbeta\n"),
                ("description", null)),
            "WriteAsync",
            "notes.md",
            "alpha\nbeta\n",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal("alpha\nbeta\n", await fixture.ReadCanonicalAsync("notes.md"));

        await fixture.RunProviderMutationAsync(
            AliCapabilityCatalog.WorkMemoryReplaceName,
            Arguments(
                ("fileName", "notes.md"),
                ("oldString", "beta"),
                ("newString", "gamma"),
                ("replaceAll", false)),
            "ReplaceAsync",
            "notes.md",
            "beta",
            "gamma",
            false,
            TestContext.Current.CancellationToken);
        Assert.Equal("alpha\ngamma\n", await fixture.ReadCanonicalAsync("notes.md"));

        var edits = new List<FileLineEdit>
        {
            new() { LineNumber = 1, NewLine = "delta\n" }
        };
        await fixture.RunProviderMutationAsync(
            AliCapabilityCatalog.WorkMemoryReplaceLinesName,
            Arguments(
                ("fileName", "notes.md"),
                ("edits", new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["line_number"] = 1,
                        ["new_line"] = "delta\n"
                    }
                })),
            "ReplaceLinesAsync",
            "notes.md",
            edits,
            TestContext.Current.CancellationToken);
        Assert.Equal("delta\ngamma\n", await fixture.ReadCanonicalAsync("notes.md"));

        await fixture.RunProviderMutationAsync(
            AliCapabilityCatalog.WorkMemoryDeleteName,
            Arguments(("fileName", "notes.md")),
            "DeleteAsync",
            "notes.md",
            TestContext.Current.CancellationToken);
        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        Assert.True(Directory.EnumerateDirectories(
            fixture.Memory.RecoverableTrashPath,
            "workspace",
            SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task ActiveMismatchedGrant_CannotFallThroughToCanonicalStore()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "notes.md"),
            ("content", "approved"),
            ("description", null));
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            arguments);
        var mismatched = fixture.EnterGrant(
            prepared,
            capabilityId: prepared.Adapter.CapabilityId + ".wrong");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Memory.Store.WriteAsync(
                "notes.md",
                "bypass",
                TestContext.Current.CancellationToken));
        await mismatched.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
    }

    [Fact]
    public async Task MissingReplaceTarget_FailsBeforeAProtectedInvocationCanStart()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "missing.md"),
            ("oldString", "before"),
            ("newString", "after"),
            ("replaceAll", false));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.PrepareAsync(
                AliCapabilityCatalog.WorkMemoryReplaceName,
                arguments));
        Assert.False(File.Exists(fixture.WorkspacePath("missing.md")));
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task MissingDeleteTarget_FailsBeforeStagingOrProtectedInvocationCreation()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.PrepareAsync(
                AliCapabilityCatalog.WorkMemoryDeleteName,
                Arguments(("fileName", "missing.md"))));

        Assert.False(File.Exists(fixture.WorkspacePath("missing.md")));
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task InvalidReplaceLines_FailsBeforeStagingPlaintext()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "one\n");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.PrepareAsync(
                AliCapabilityCatalog.WorkMemoryReplaceLinesName,
                Arguments(
                    ("fileName", "notes.md"),
                    ("edits", new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["line_number"] = 2,
                            ["new_line"] = "outside\n"
                        }
                    }))));

        Assert.Equal("one\n", await fixture.ReadCanonicalAsync("notes.md"));
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task WorkspaceDriftDuringPreparation_IsRejectedWithoutLeavingStagedPlaintext()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "accepted");
        var hooked = fixture.CreateHookedCoordinator(checkpoint =>
        {
            if (checkpoint == AliWorkMemoryPreparationCheckpoint.ExactTargetCaptured)
            {
                File.WriteAllText(fixture.WorkspacePath("external.md"), "drift");
            }
        });

        await Assert.ThrowsAsync<AliExecutionPreparationException>(() =>
            fixture.PrepareAsync(
                hooked,
                AliCapabilityCatalog.WorkMemoryWriteName,
                Arguments(
                    ("fileName", "notes.md"),
                    ("content", "approved"),
                    ("description", null))));

        Assert.Equal("accepted", await fixture.ReadCanonicalAsync("notes.md"));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Fact]
    public async Task ScopeDriftAfterAcceptedCapture_IsRejectedWithoutStaging()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "accepted");
        var hooked = fixture.CreateHookedCoordinator(preparationFaultHook: null);

        await Assert.ThrowsAsync<AliExecutionPreparationException>(() =>
            fixture.PrepareAsync(
                hooked,
                AliCapabilityCatalog.WorkMemoryWriteName,
                Arguments(
                    ("fileName", "notes.md"),
                    ("content", "approved"),
                    ("description", null)),
                afterAcceptedCapture: () => hooked.ChangeConversation("other-conversation")));

        Assert.Equal("accepted", await fixture.ReadCanonicalAsync("notes.md"));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Fact]
    public async Task MidCopyPreparationFailure_RemovesPartialStagingTreeNoFollow()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "plaintext-that-must-not-remain-staged");
        var injected = 0;
        var hooked = fixture.CreateHookedCoordinator(checkpoint =>
        {
            if (checkpoint == AliWorkMemoryPreparationCheckpoint.StagingEntryCopied
                && Interlocked.Exchange(ref injected, 1) == 0)
            {
                throw new IOException("injected-staging-copy-failure");
            }
        });

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            fixture.PrepareAsync(
                hooked,
                AliCapabilityCatalog.WorkMemoryWriteName,
                Arguments(
                    ("fileName", "notes.md"),
                    ("content", "approved"),
                    ("description", null))));

        Assert.Equal("injected-staging-copy-failure", exception.Message);
        Assert.Equal("plaintext-that-must-not-remain-staged", await fixture.ReadCanonicalAsync("notes.md"));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Fact]
    public async Task PreparationCleanup_HardLinkInterpositionRetainsResidueAndExternalIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "plaintext-that-must-not-escape");
        var outside = Path.Combine(fixture.Root, "cleanup-hard-link-source.md");
        await File.WriteAllTextAsync(
            outside,
            "external-protected",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        string? stagingRoot = null;
        var injected = 0;
        var hardLinkError = 0;
        var hooked = fixture.CreateHookedCoordinator(checkpoint =>
        {
            if (checkpoint != AliWorkMemoryPreparationCheckpoint.StagingEntryCopied
                || Interlocked.Exchange(ref injected, 1) != 0)
            {
                return;
            }
            var transaction = Assert.Single(Directory.EnumerateDirectories(stagingRoot!));
            var interposed = Path.Combine(transaction, "workspace", "unknown.md");
            if (!CreateHardLinkW(interposed, outside, IntPtr.Zero))
            {
                hardLinkError = Marshal.GetLastWin32Error();
            }
            throw new IOException("injected-cleanup-interposition");
        });
        stagingRoot = hooked.StagingRoot;

        _ = await Assert.ThrowsAsync<IOException>(() => fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null))));
        if (hardLinkError != 0)
        {
            Assert.Skip(
                "Hard-link creation is unavailable: Win32 error "
                + hardLinkError.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(
            "external-protected",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        Assert.True(Directory.EnumerateDirectories(hooked.StagingRoot).Any());
    }

    [Fact]
    public async Task CanonicalSourceLateReplacementAfterIdentityBinding_IsRejectedBeforeDurablePlan()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "accepted");
        var hooked = fixture.CreateHookedCoordinator(checkpoint =>
        {
            if (checkpoint == AliWorkMemoryPreparationCheckpoint.CanonicalSourceIdentityBound)
            {
                Directory.Move(fixture.Workspace, fixture.Workspace + "-authenticated");
                Directory.CreateDirectory(fixture.Workspace);
                File.WriteAllText(fixture.WorkspacePath("notes.md"), "late-swap", Encoding.UTF8);
            }
        });

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null))));

        Assert.Equal("late-swap", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal(
            "accepted",
            await File.ReadAllTextAsync(
                Path.Combine(fixture.Workspace + "-authenticated", "notes.md"),
                TestContext.Current.CancellationToken));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Fact]
    public async Task StagingSourceReplacementAfterIdentityBinding_IsRejectedAndNeverPublished()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        string? stagingRoot = null;
        var hooked = fixture.CreateHookedCoordinator(checkpoint =>
        {
            if (checkpoint != AliWorkMemoryPreparationCheckpoint.StagingSourceIdentityBound)
            {
                return;
            }
            var transaction = Assert.Single(Directory.EnumerateDirectories(stagingRoot!));
            var staging = Path.Combine(transaction, "workspace");
            Directory.Move(staging, Path.Combine(transaction, "replaced-workspace"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "notes.md"), "replacement", Encoding.UTF8);
        });
        stagingRoot = hooked.StagingRoot;

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null))));

        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Fact]
    public async Task UnresolvedStagingAdmissionCap_AllowsLimitAndRejectsLimitPlusOne()
    {
        using var fixture = new Fixture();
        var hooked = fixture.CreateHookedCoordinator(preparationFaultHook: null);
        Directory.CreateDirectory(hooked.StagingRoot);
        for (var index = 0; index < 64; index++)
        {
            Directory.CreateDirectory(Path.Combine(hooked.StagingRoot, index.ToString("D2")));
        }

        _ = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));

        Assert.Equal(65, Directory.EnumerateDirectories(hooked.StagingRoot).Count());
        var exception = await Assert.ThrowsAsync<IOException>(() => fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "second.md"),
                ("content", "approved"),
                ("description", null))));

        Assert.Contains("transactions=65", exception.Message, StringComparison.Ordinal);
        Assert.Equal(65, Directory.EnumerateDirectories(hooked.StagingRoot).Count());
        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
    }

    [Fact]
    public async Task DeleteReportedFalse_NeverPublishesOrCompletesSuccessfully()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "must-remain-canonical");
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryDeleteName,
            Arguments(("fileName", "notes.md")));
        var activation = fixture.EnterGrant(prepared);
        Assert.True(await fixture.Memory.Store.DeleteAsync(
            "notes.md",
            TestContext.Current.CancellationToken));
        var reportedResult = await fixture.Memory.Store.DeleteAsync(
            "notes.md",
            TestContext.Current.CancellationToken);
        Assert.False(reportedResult);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(reportedResult, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Equal("must-remain-canonical", await fixture.ReadCanonicalAsync("notes.md"));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.NotEqual(ActionReconciliationDisposition.Applied, reconciled.Disposition);
    }

    [Fact]
    public async Task CanonicalDriftBeforePublication_PreservesExternalStateAndReconcilesUnknown()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var arguments = Arguments(
            ("fileName", "notes.md"),
            ("content", "approved"),
            ("description", null));
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            arguments);
        await File.WriteAllTextAsync(
            fixture.WorkspacePath("notes.md"),
            "external",
            TestContext.Current.CancellationToken);

        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Equal("external", await fixture.ReadCanonicalAsync("notes.md"));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task CanonicalChildWriteAtFirstMoveBoundary_IsBlockedByTreeClosure()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.BeforeCanonicalToBackup)
                {
                    try
                    {
                        File.WriteAllText(
                            fixture.WorkspacePath("notes.md"),
                            "external",
                            Encoding.UTF8);
                    }
                    catch (Exception exception)
                    {
                        mutationFailure = exception;
                    }
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.True(Directory.Exists(backup));
        Assert.False(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task PublicationFaultAtEverySwapBoundary_RestoresOrProvesTheExactState(
        int faultBoundary,
        bool expectedApplied)
    {
        var faultAt = (AliWorkMemoryPublicationCheckpoint)faultBoundary;
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == faultAt)
                {
                    throw new IOException("injected-publication-boundary-failure");
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Equal("injected-publication-boundary-failure", exception.Message);
        Assert.Equal(
            expectedApplied ? "approved" : "before",
            await fixture.ReadCanonicalAsync("notes.md"));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            expectedApplied
                ? ActionReconciliationDisposition.Applied
                : ActionReconciliationDisposition.Absent,
            reconciled.Disposition);
    }

    [Fact]
    public async Task CompensationQuarantinesAnUnrecognizedCanonicalWorkspaceBeforeExactRestore()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup)
                {
                    return;
                }
                Directory.CreateDirectory(fixture.Workspace);
                File.WriteAllText(fixture.WorkspacePath("notes.md"), "external", Encoding.UTF8);
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        var quarantinedFile = Assert.Single(Directory.EnumerateFiles(
            fixture.Memory.RecoverableTrashPath,
            "notes.md",
            SearchOption.AllDirectories));
        Assert.Equal(
            "external",
            await File.ReadAllTextAsync(
                quarantinedFile,
                TestContext.Current.CancellationToken));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
    }

    [Fact]
    public async Task BackupChildWriteAfterFirstMove_IsBlockedByTreeClosure()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup)
                {
                    return;
                }
                var racedBackupFile = Assert.Single(Directory.EnumerateFiles(
                    fixture.Memory.RecoverableTrashPath,
                    "notes.md",
                    SearchOption.AllDirectories));
                try
                {
                    File.WriteAllText(racedBackupFile, "unrecognized-backup", Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    mutationFailure = exception;
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal(
            "before",
            await File.ReadAllTextAsync(
                Path.Combine(backup, "notes.md"),
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(backup));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task StagedPostimageDivergence_NeverPublishesCanonicalState()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "notes.md"),
            ("content", "approved"),
            ("description", null));
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            arguments);

        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var stagedFile = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "notes.md",
            SearchOption.AllDirectories));
        await File.WriteAllTextAsync(
            stagedFile,
            "tampered",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task StagedWorkspaceOmission_NeverPublishesAnIncompleteWorkspace()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("kept.md", "must-survive");
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var stagedKept = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "kept.md",
            SearchOption.AllDirectories));
        File.Delete(stagedKept);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Equal("must-survive", await fixture.ReadCanonicalAsync("kept.md"));
        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
    }

    [Fact]
    public async Task ExtraStagedFile_NeverPublishesOutsideThePreparedWorkspaceManifest()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var stagedNotes = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "notes.md",
            SearchOption.AllDirectories));
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(stagedNotes)!, "extra.md"),
            "unprepared",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        Assert.False(File.Exists(fixture.WorkspacePath("extra.md")));
    }

    [Fact]
    public async Task StagedChildDeleteImmediatelyBeforeSecondMove_IsBlockedByTreeClosure()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        await fixture.SeedAsync("kept.md", "must-survive");
        string? hookedStagingRoot = null;
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != AliWorkMemoryPublicationCheckpoint.BeforeStagingToCanonical)
                {
                    return;
                }
                var stagedKept = Assert.Single(Directory.EnumerateFiles(
                    hookedStagingRoot!,
                    "kept.md",
                    SearchOption.AllDirectories));
                try
                {
                    File.Delete(stagedKept);
                }
                catch (Exception exception)
                {
                    mutationFailure = exception;
                }
            });
        hookedStagingRoot = hooked.StagingRoot;
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal("must-survive", await fixture.ReadCanonicalAsync("kept.md"));
        Assert.True(Directory.Exists(backup));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task StagedChildWriteAfterFinalCheck_IsBlockedByTheHeldTreeClosure()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        string? staging = null;
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterFinalStagingCheckBeforeRename)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(staging!, "notes.md"),
                            "late-staged-child-write",
                            Encoding.UTF8);
                    }
                    catch (Exception exception)
                    {
                        mutationFailure = exception;
                    }
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        (staging, _) = PublicationPaths(fixture, hooked);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task StagedRootChildCreationAfterFinalCheck_IsBlockedBySealedRoot()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        string? staging = null;
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterFinalStagingCheckBeforeRename)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(staging!, "late-root-child.md"),
                            "unprepared",
                            Encoding.UTF8);
                    }
                    catch (Exception exception)
                    {
                        mutationFailure = exception;
                    }
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        (staging, _) = PublicationPaths(fixture, hooked);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.False(File.Exists(fixture.WorkspacePath("late-root-child.md")));
    }

    [Theory]
    [InlineData("canonical-parent")]
    [InlineData("canonical-ancestor")]
    [InlineData("backup-parent")]
    [InlineData("backup-ancestor")]
    public async Task HeldNamespaceSpines_BlockLateParentReplacement(string target)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        string? backup = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != AliWorkMemoryPublicationCheckpoint.BeforeCanonicalToBackup)
                {
                    return;
                }
                var parent = target switch
                {
                    "canonical-parent" => Path.GetDirectoryName(fixture.Workspace)!,
                    "canonical-ancestor" => Path.GetDirectoryName(
                        Path.GetDirectoryName(fixture.Workspace)!)!,
                    "backup-parent" => Path.GetDirectoryName(backup!)!,
                    "backup-ancestor" => Path.GetDirectoryName(
                        Path.GetDirectoryName(backup!)!)!,
                    _ => throw new InvalidOperationException("Unknown namespace replacement target.")
                };
                Directory.Move(parent, parent + "-replacement");
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        (_, backup) = PublicationPaths(fixture, hooked);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.True(Directory.Exists(Path.GetDirectoryName(backup)!));
    }

    [Theory]
    [InlineData((int)AliWorkMemoryPublicationCheckpoint.BeforeCanonicalToBackup)]
    [InlineData((int)AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup)]
    public async Task CanonicalAndBackupChildWritesAtTransitionSeams_AreBlockedByTreeClosure(
        int seamValue)
    {
        var seam = (AliWorkMemoryPublicationCheckpoint)seamValue;
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        string? backup = null;
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != seam)
                {
                    return;
                }
                var target = seam == AliWorkMemoryPublicationCheckpoint.BeforeCanonicalToBackup
                    ? fixture.WorkspacePath("notes.md")
                    : Path.Combine(backup!, "notes.md");
                try
                {
                    File.WriteAllText(target, "transition-seam-mutation", Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    mutationFailure = exception;
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        (_, backup) = PublicationPaths(fixture, hooked);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal(
            "before",
            await File.ReadAllTextAsync(
                Path.Combine(backup, "notes.md"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishedChildWriteAtPostRenameSeam_IsBlockedByHeldTreeClosure()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical)
                {
                    try
                    {
                        File.WriteAllText(
                            fixture.WorkspacePath("notes.md"),
                            "post-rename-mutation",
                            Encoding.UTF8);
                    }
                    catch (Exception exception)
                    {
                        mutationFailure = exception;
                    }
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
    }

    [Theory]
    [InlineData((int)AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical)]
    [InlineData((int)AliWorkMemoryPublicationCheckpoint.BeforeDurableCompletion)]
    public async Task PublishedRootChildCreationAtTerminalSeams_IsBlockedBySealedRoot(
        int seamValue)
    {
        var seam = (AliWorkMemoryPublicationCheckpoint)seamValue;
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        Exception? mutationFailure = null;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != seam)
                {
                    return;
                }
                try
                {
                    File.WriteAllText(
                        fixture.WorkspacePath("terminal-root-child.md"),
                        "unprepared",
                        Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    mutationFailure = exception;
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);

        await activation.CompleteAsync(result, CancellationToken.None);
        await activation.DisposeAsync();

        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.False(File.Exists(fixture.WorkspacePath("terminal-root-child.md")));
    }

    [Fact]
    public async Task MissingStagedMemoryIndex_NeverPublishesTheWorkspace()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var stagedIndex = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "memories.md",
            SearchOption.AllDirectories));
        File.Delete(stagedIndex);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        Assert.False(File.Exists(fixture.WorkspacePath("memories.md")));
    }

    [Fact]
    public async Task DriftedStagedMemoryIndex_NeverPublishesTheWorkspace()
    {
        using var fixture = new Fixture();
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", "bound description")));
        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            "bound description",
            TestContext.Current.CancellationToken);
        var stagedIndex = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "memories.md",
            SearchOption.AllDirectories));
        await File.WriteAllTextAsync(
            stagedIndex,
            "# Memory Index\n\n- **stale.md**\n",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        Assert.False(File.Exists(fixture.WorkspacePath("memories.md")));
    }

    [Fact]
    public async Task RestartRecoveryAfterFirstMove_RestoresTheAuthenticatedPreimage()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());

        var restarted = hooked.RestartCoordinator();
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(staging));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Fact]
    public async Task RestartWithMissingStartedBindingAndSurvivingStaging_IsAlwaysUnknown()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.BeforeCanonicalToBackup)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());
        var bindingRoot = Path.Combine(
            Path.GetDirectoryName(hooked.StagingRoot)!,
            "ExecutionBindings");
        var binding = Assert.Single(Directory.EnumerateFiles(bindingRoot));
        File.Delete(binding);

        var restarted = hooked.RestartCoordinator();
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.True(Directory.EnumerateDirectories(hooked.StagingRoot).Any());
    }

    [Fact]
    public async Task RestartRecoveryAfterFirstMove_PreservesMismatchedBackupAsUnknown()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());
        await File.WriteAllTextAsync(
            Path.Combine(backup, "notes.md"),
            "unrecognized-backup",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        var restarted = hooked.RestartCoordinator();
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Equal(
            "unrecognized-backup",
            await File.ReadAllTextAsync(
                Path.Combine(backup, "notes.md"),
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(backup));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public async Task RestartRecoveryAfterFirstMove_DoesNotPromoteReplacementBackupDirectory()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterCanonicalToBackup)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());
        var authenticatedBackup = backup + "-authenticated";
        Directory.Move(backup, authenticatedBackup);
        Directory.CreateDirectory(backup);
        await File.WriteAllTextAsync(
            Path.Combine(backup, "notes.md"),
            "replacement-backup",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        var restarted = hooked.RestartCoordinator();
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.False(Directory.Exists(fixture.Workspace));
        Assert.Equal(
            "replacement-backup",
            await File.ReadAllTextAsync(
                Path.Combine(backup, "notes.md"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            "before",
            await File.ReadAllTextAsync(
                Path.Combine(authenticatedBackup, "notes.md"),
                TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public async Task RecoveryRollback_CanonicalAndBackupChildWritesAreBlockedByTreeClosures()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (_, backup) = PublicationPaths(fixture, hooked);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());
        await File.WriteAllTextAsync(
            fixture.WorkspacePath("notes.md"),
            "tampered-after-crash",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        Exception? canonicalMutationFailure = null;
        Exception? backupMutationFailure = null;
        var restarted = hooked.RestartCoordinator(checkpoint =>
        {
            if (checkpoint == AliWorkMemoryPublicationCheckpoint.BeforeRecoveryQuarantine)
            {
                try
                {
                    File.WriteAllText(
                        fixture.WorkspacePath("notes.md"),
                        "recovery-canonical-mutation",
                        Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    canonicalMutationFailure = exception;
                }
            }
            else if (checkpoint == AliWorkMemoryPublicationCheckpoint.BeforeRecoveryRestore)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(backup, "notes.md"),
                        "recovery-backup-mutation",
                        Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    backupMutationFailure = exception;
                }
            }
        });
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
        Assert.IsAssignableFrom<IOException>(canonicalMutationFailure);
        Assert.IsAssignableFrom<IOException>(backupMutationFailure);
        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        var quarantined = Assert.Single(Directory.EnumerateFiles(
            fixture.Memory.RecoverableTrashPath,
            "notes.md",
            SearchOption.AllDirectories));
        Assert.Equal(
            "tampered-after-crash",
            await File.ReadAllTextAsync(
                quarantined,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoveryClassificationRootChildCreation_IsBlockedByObservedSealedRoot()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());

        Exception? mutationFailure = null;
        var restarted = hooked.RestartCoordinator(checkpoint =>
        {
            if (checkpoint != AliWorkMemoryPublicationCheckpoint.AfterClassificationSnapshot)
            {
                return;
            }
            try
            {
                File.WriteAllText(
                    fixture.WorkspacePath("classification-root-child.md"),
                    "unprepared",
                    Encoding.UTF8);
            }
            catch (Exception exception)
            {
                mutationFailure = exception;
            }
        });
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
        Assert.IsAssignableFrom<IOException>(mutationFailure);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.False(File.Exists(fixture.WorkspacePath("classification-root-child.md")));
    }

    [Fact]
    public async Task RestartRecoveryAfterSecondMove_ProvesTheCompleteAuthenticatedPoststate()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        await fixture.SeedAsync("kept.md", "must-survive");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());

        var restarted = hooked.RestartCoordinator();
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
        Assert.Equal("approved", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal("must-survive", await fixture.ReadCanonicalAsync("kept.md"));
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "# Memory Index",
                string.Empty,
                "- **kept.md**",
                "- **notes.md**",
                string.Empty),
            await fixture.ReadCanonicalAsync("memories.md"));
        Assert.Equal(
            new[] { "kept.md", "memories.md", "notes.md" },
            Directory.EnumerateFiles(fixture.Workspace, "*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path)!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.True(Directory.Exists(backup));
        Assert.False(Directory.Exists(staging));
        AssertNoStagingEntries(hooked.StagingRoot);
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("omit")]
    [InlineData("extra")]
    public async Task RestartRecoveryAfterSecondMove_QuarantinesIncompleteOrExtraPoststate(
        string corruption)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        await fixture.SeedAsync("kept.md", "must-survive");
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint == AliWorkMemoryPublicationCheckpoint.AfterStagingToCanonical)
                {
                    throw new AliWorkMemorySimulatedInterruptionException(checkpoint);
                }
            });
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await hooked.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var (staging, backup) = PublicationPaths(fixture, hooked);
        _ = await Assert.ThrowsAsync<AliWorkMemorySimulatedInterruptionException>(() =>
            activation.CompleteAsync(result, CancellationToken.None).AsTask());
        switch (corruption)
        {
            case "tamper":
                await File.WriteAllTextAsync(
                    fixture.WorkspacePath("notes.md"),
                    "tampered",
                    Encoding.UTF8,
                    TestContext.Current.CancellationToken);
                break;
            case "omit":
                File.Delete(fixture.WorkspacePath("kept.md"));
                break;
            case "extra":
                await File.WriteAllTextAsync(
                    fixture.WorkspacePath("extra.md"),
                    "unprepared",
                    Encoding.UTF8,
                    TestContext.Current.CancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unknown test corruption.");
        }

        var restarted = hooked.RestartCoordinator();
        var recoveryAdapter = restarted.ExecutionEffectAdapters.Single(adapter =>
            string.Equals(
                adapter.ToolName,
                AliCapabilityCatalog.WorkMemoryWriteName,
                StringComparison.Ordinal));
        var reconciled = await recoveryAdapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal("must-survive", await fixture.ReadCanonicalAsync("kept.md"));
        Assert.False(Directory.Exists(backup));
        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.EnumerateDirectories(
            fixture.Memory.RecoverableTrashPath,
            "quarantine-workspace",
            SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task AbandonedStartedMutation_IsInDoubtAndNeverPublishes()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "notes.md"),
            ("content", "approved"),
            ("description", null));
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            arguments);

        var activation = fixture.EnterGrant(prepared);
        await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    [Fact]
    public async Task CanceledBeforeFirstStoreCall_RemainsProvedAbsent()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "notes.md"),
            ("content", "approved"),
            ("description", null));
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            arguments);
        var activation = fixture.EnterGrant(prepared);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Memory.Store.WriteAsync(
                "notes.md",
                "approved",
                cancellation.Token));
        await activation.DisposeAsync();

        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
        var reconciled = await prepared.Adapter.ReconcileAsync(
            fixture.Identity,
            fixture.Intent(prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task ExistingDescriptionShortNameAlias_IsRejectedBeforeStaging()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new Fixture();
        const string descriptionFileName = "participant_memory_description.md";
        await fixture.SeedAsync(descriptionFileName, "protected-description");
        var shortName = TryGetDistinctShortFileName(fixture.WorkspacePath(descriptionFileName));
        if (shortName is null)
        {
            Assert.Skip("This filesystem does not expose a distinct NTFS 8.3 short name.");
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.PrepareAsync(
                AliCapabilityCatalog.WorkMemoryReplaceName,
                Arguments(
                    ("fileName", shortName),
                    ("oldString", "protected-description"),
                    ("newString", "tampered-description"),
                    ("replaceAll", false))));

        Assert.Contains("alias", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "protected-description",
            await fixture.ReadCanonicalAsync(descriptionFileName));
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task MultiplyLinkedCanonicalMemoryFile_IsRejectedBeforeStaging()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Workspace);
        var outside = Path.Combine(fixture.Root, "hard-link-source.md");
        await File.WriteAllTextAsync(
            outside,
            "protected-content",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        if (!CreateHardLinkW(fixture.WorkspacePath("notes.md"), outside, IntPtr.Zero))
        {
            Assert.Skip(
                "Hard-link creation is unavailable: Win32 error "
                + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.PrepareAsync(
                AliCapabilityCatalog.WorkMemoryReplaceName,
                Arguments(
                    ("fileName", "notes.md"),
                    ("oldString", "protected-content"),
                    ("newString", "tampered-content"),
                    ("replaceAll", false))));

        Assert.Contains("hard-link", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("protected-content", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal(
            "protected-content",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task StagedWriteSwap_HardLinkInterpositionNeverTouchesExternalTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new Fixture();
        await fixture.SeedAsync("notes.md", "before");
        var outside = Path.Combine(fixture.Root, "write-swap-hard-link-source.md");
        await File.WriteAllTextAsync(
            outside,
            "external-protected",
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        string? stagingRoot = null;
        var hardLinkError = 0;
        var hooked = fixture.CreateHookedCoordinator(
            preparationFaultHook: null,
            publicationFaultHook: checkpoint =>
            {
                if (checkpoint != AliWorkMemoryPublicationCheckpoint.BeforeStagedFileWriteSwap)
                {
                    return;
                }
                var transaction = Assert.Single(Directory.EnumerateDirectories(stagingRoot!));
                var interposed = Path.Combine(transaction, "workspace", "notes.md");
                if (!CreateHardLinkW(interposed, outside, IntPtr.Zero))
                {
                    hardLinkError = Marshal.GetLastWin32Error();
                }
            });
        stagingRoot = hooked.StagingRoot;
        var prepared = await fixture.PrepareAsync(
            hooked,
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        Exception? failure = null;
        try
        {
            _ = await hooked.InvokeProviderAsync(
                "WriteAsync",
                "notes.md",
                "approved",
                null,
                TestContext.Current.CancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        await activation.DisposeAsync();
        if (hardLinkError != 0)
        {
            Assert.Skip(
                "Hard-link creation is unavailable: Win32 error "
                + hardLinkError.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.IsAssignableFrom<IOException>(failure);
        Assert.Equal("before", await fixture.ReadCanonicalAsync("notes.md"));
        Assert.Equal(
            "external-protected",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiplyLinkedStagedMemoryFile_IsRejectedBeforePublication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var fixture = new Fixture();
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null)));
        var activation = fixture.EnterGrant(prepared);
        var result = await fixture.InvokeProviderAsync(
            "WriteAsync",
            "notes.md",
            "approved",
            null,
            TestContext.Current.CancellationToken);
        var stagedFile = Assert.Single(Directory.EnumerateFiles(
            fixture.StagingRoot,
            "notes.md",
            SearchOption.AllDirectories));
        var outsideAlias = Path.Combine(fixture.Root, "staged-hard-link-alias.md");
        if (!CreateHardLinkW(outsideAlias, stagedFile, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            await activation.DisposeAsync();
            Assert.Skip(
                "Hard-link creation is unavailable: Win32 error "
                + error.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await activation.CompleteAsync(result, CancellationToken.None));
        await activation.DisposeAsync();

        Assert.Contains("hard-link", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData(" notes.md")]
    [InlineData("notes.md ")]
    [InlineData("memories.md.")]
    [InlineData("memories.md ")]
    [InlineData("notes.md:stream")]
    [InlineData("foo_description.md.")]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("CONIN$")]
    [InlineData("COM\u00B9.txt")]
    [InlineData("LPT\u00B2.log")]
    public async Task UnsafeFlatNameBoundaryRejectsAliasesBeforeMutation(string fileName)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync("memories.md", "protected-index");
        await fixture.SeedAsync("foo_description.md", "protected-description");
        var escape = Arguments(
            ("fileName", fileName),
            ("content", "escape"),
            ("description", null));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            escape));
        Assert.Equal("protected-index", await fixture.ReadCanonicalAsync("memories.md"));
        Assert.Equal(
            "protected-description",
            await fixture.ReadCanonicalAsync("foo_description.md"));
        AssertNoStagingEntries(fixture.StagingRoot);
    }

    [Fact]
    public async Task WorkspaceReparseBoundaryRejectsMutation()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Workspace);
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        var link = fixture.WorkspacePath("linked");
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

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.PrepareAsync(
            AliCapabilityCatalog.WorkMemoryWriteName,
            Arguments(
                ("fileName", "notes.md"),
                ("content", "approved"),
                ("description", null))));
        Assert.False(File.Exists(fixture.WorkspacePath("notes.md")));
    }

    private static string? TryGetDistinctShortFileName(string path)
    {
        var required = GetShortPathNameW(path, null, 0);
        if (required == 0 || required > 64 * 1024)
        {
            return null;
        }
        var buffer = new StringBuilder(checked((int)required));
        var written = GetShortPathNameW(path, buffer, checked((uint)buffer.Capacity));
        if (written == 0 || written >= buffer.Capacity)
        {
            return null;
        }
        var shortFileName = Path.GetFileName(buffer.ToString());
        return string.Equals(
            shortFileName,
            Path.GetFileName(path),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : shortFileName;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetShortPathNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetShortPathNameW(
        string longPath,
        StringBuilder? shortPath,
        uint shortPathCharacters);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

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

    private static void AssertNoStagingEntries(string stagingRoot)
    {
        Assert.False(
            Directory.Exists(stagingRoot)
            && Directory.EnumerateFileSystemEntries(stagingRoot).Any(),
            $"Expected no retained staging entries beneath '{stagingRoot}'.");
    }

    private static (string Staging, string Backup) PublicationPaths(
        Fixture fixture,
        Fixture.HookedWorkMemoryCoordinator hooked)
    {
        var transaction = Assert.Single(Directory.EnumerateDirectories(hooked.StagingRoot));
        var domainId = Path.GetFileName(transaction);
        return (
            Path.Combine(transaction, "workspace"),
            Path.Combine(
                fixture.Memory.RecoverableTrashPath,
                "DurableTransactions",
                domainId,
                "workspace"));
    }

    private sealed record PreparedMutation(
        IAliExecutionEffectAdapter Adapter,
        AIFunctionArguments Arguments,
        AliExecutionPreparation Preparation,
        string CallId,
        string WorkItemId);

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly List<string> _links = [];
        private readonly List<HookedWorkMemoryCoordinator> _hookedCoordinators = [];
        private readonly FileMemoryProvider _provider;

        internal Fixture()
        {
            Root = Path.Combine(
                Directory.GetCurrentDirectory(),
                "artifacts",
                "cp7-work-memory-tests",
                Guid.NewGuid().ToString("N"));
            var userData = Path.Combine(Root, "UserData");
            DurableRoot = Path.Combine(Root, "OrchestrationV2");
            Memory = new AliAgentWorkMemory(
                userData,
                DurableRoot,
                "work-memory-test");
            _scope = Memory.EnterScope("conversation", activeUser: null);
            Workspace = Memory.GetWorkspacePath("unselected", "conversation");
            StagingRoot = Path.Combine(
                DurableRoot,
                "AgentWorkMemoryInvocations",
                "Staging");
            _provider = new FileMemoryProvider(Memory.Store, null, null);
        }

        internal string Root { get; }

        internal string DurableRoot { get; }

        internal string Workspace { get; }

        internal string StagingRoot { get; }

        internal TurnIdentity Identity { get; } =
            new("user", "work-memory-adapters", "assistant-message");

        internal AliAgentWorkMemory Memory { get; }

        internal string WorkspacePath(string fileName) => Path.Combine(Workspace, fileName);

        internal void RegisterLink(string path) => _links.Add(path);

        internal HookedWorkMemoryCoordinator CreateHookedCoordinator(
            Action<AliWorkMemoryPreparationCheckpoint>? preparationFaultHook,
            Action<AliWorkMemoryPublicationCheckpoint>? publicationFaultHook = null)
        {
            var hooked = new HookedWorkMemoryCoordinator(
                this,
                preparationFaultHook,
                publicationFaultHook);
            _hookedCoordinators.Add(hooked);
            return hooked;
        }

        internal async Task SeedAsync(string fileName, string content)
        {
            Directory.CreateDirectory(Workspace);
            await File.WriteAllTextAsync(
                WorkspacePath(fileName),
                content,
                Encoding.UTF8,
                TestContext.Current.CancellationToken);
        }

        internal async Task<string> ReadCanonicalAsync(string fileName) =>
            await File.ReadAllTextAsync(
                WorkspacePath(fileName),
                TestContext.Current.CancellationToken);

        internal async Task<PreparedMutation> PrepareAsync(
            string toolName,
            AIFunctionArguments arguments) =>
            await PrepareUsingAsync(
                    Memory.ExecutionEffectAdapters,
                    Memory.TargetStateAdapters,
                    toolName,
                    arguments,
                    afterAcceptedCapture: null)
                .ConfigureAwait(false);

        internal async Task<PreparedMutation> PrepareAsync(
            HookedWorkMemoryCoordinator hooked,
            string toolName,
            AIFunctionArguments arguments,
            Action? afterAcceptedCapture = null)
        {
            ArgumentNullException.ThrowIfNull(hooked);
            return await PrepareUsingAsync(
                    hooked.Coordinator.ExecutionEffectAdapters,
                    hooked.Coordinator.TargetStateAdapters,
                    toolName,
                    arguments,
                    afterAcceptedCapture)
                .ConfigureAwait(false);
        }

        private async Task<PreparedMutation> PrepareUsingAsync(
            IReadOnlyList<IAliExecutionEffectAdapter> executionAdapters,
            IReadOnlyList<IActionTargetStateAdapter> targetStateAdapters,
            string toolName,
            AIFunctionArguments arguments,
            Action? afterAcceptedCapture)
        {
            var adapter = executionAdapters.Single(candidate =>
                string.Equals(candidate.ToolName, toolName, StringComparison.Ordinal));
            var element = JsonSerializer.SerializeToElement(arguments).Clone();
            var targetAdapter = targetStateAdapters.Single(candidate =>
                candidate.ToolNames.Contains(toolName, StringComparer.Ordinal));
            var snapshot = targetAdapter.Capture(toolName, element);
            var targetDigest = WorkIdentityCanonicalizer.MapDigest(
                "action-target-versions-v1",
                snapshot.TargetVersions);
            afterAcceptedCapture?.Invoke();
            var callId = "call-" + Guid.NewGuid().ToString("N");
            var workItemId = "work-" + Guid.NewGuid().ToString("N");
            var preparation = await adapter.PrepareAsync(
                new AliExecutionPreparationRequest(
                    Identity,
                    callId,
                    workItemId,
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
            return new PreparedMutation(
                adapter,
                arguments,
                preparation,
                callId,
                workItemId);
        }

        internal sealed class HookedWorkMemoryCoordinator
            : IDisposable
        {
            private readonly Fixture _fixture;
            private readonly string _durableRoot;
            private readonly FileMemoryProvider _provider;
            private AgentWorkMemoryScope _scope;

            internal HookedWorkMemoryCoordinator(
                Fixture fixture,
                Action<AliWorkMemoryPreparationCheckpoint>? preparationFaultHook,
                Action<AliWorkMemoryPublicationCheckpoint>? publicationFaultHook)
            {
                _fixture = fixture;
                _scope = new AgentWorkMemoryScope(
                    "unselected",
                    "conversation",
                    Path.GetRelativePath(fixture.Memory.RootPath, fixture.Workspace));
                _durableRoot = Path.Combine(
                    fixture.DurableRoot,
                    "Hooked-" + Guid.NewGuid().ToString("N"));
                var canonical = new ScopedAgentWorkMemoryStore(
                    fixture.Memory.RootPath,
                    fixture.Memory.RecoverableTrashPath,
                    Path.Combine(fixture.Root, "hooked-work-memory-audit.jsonl"),
                    () => _scope);
                Coordinator = new AliAgentWorkMemoryExecutionCoordinator(
                    canonical,
                    fixture.Memory.RootPath,
                    fixture.Memory.RecoverableTrashPath,
                    () => _scope,
                    _durableRoot,
                    "work-memory-hook-test",
                    evidence: null,
                    preparationFaultHook: preparationFaultHook,
                    publicationFaultHook: publicationFaultHook);
                Store = new AliBrokeredAgentWorkMemoryStore(canonical, Coordinator);
                _provider = new FileMemoryProvider(Store, null, null);
                StagingRoot = Path.Combine(
                    _durableRoot,
                    "AgentWorkMemoryInvocations",
                    "Staging");
            }

            internal AliAgentWorkMemoryExecutionCoordinator Coordinator { get; }

            internal string StagingRoot { get; }

            internal AgentFileStore Store { get; }

            internal AliAgentWorkMemoryExecutionCoordinator RestartCoordinator(
                Action<AliWorkMemoryPublicationCheckpoint>? publicationFaultHook = null)
            {
                var canonical = new ScopedAgentWorkMemoryStore(
                    _fixture.Memory.RootPath,
                    _fixture.Memory.RecoverableTrashPath,
                    Path.Combine(_fixture.Root, "hooked-work-memory-recovery-audit.jsonl"),
                    () => _scope);
                return new AliAgentWorkMemoryExecutionCoordinator(
                    canonical,
                    _fixture.Memory.RootPath,
                    _fixture.Memory.RecoverableTrashPath,
                    () => _scope,
                    _durableRoot,
                    "work-memory-hook-test",
                    evidence: null,
                    preparationFaultHook: null,
                    publicationFaultHook: publicationFaultHook);
            }

            internal Task<object?> InvokeProviderAsync(
                string methodName,
                params object?[] arguments) =>
                Fixture.InvokeProviderAsync(_provider, methodName, arguments);

            internal void ChangeConversation(string conversationId)
            {
                _scope = new AgentWorkMemoryScope(
                    _scope.UserStableId,
                    conversationId,
                    Path.Combine(
                        "Users",
                        _scope.UserStableId,
                        "Conversations",
                        conversationId));
            }

            public void Dispose() => _provider.Dispose();
        }

        internal AliExecutionInvocationActivation EnterGrant(
            PreparedMutation prepared,
            string? capabilityId = null)
        {
            var grant = new AliExecutionGrant(
                Digest("idempotency"),
                prepared.CallId,
                prepared.Adapter.ToolName,
                capabilityId ?? prepared.Adapter.CapabilityId,
                ArgumentsDigest(prepared.Arguments),
                prepared.Preparation.TargetVersionDigest,
                Digest("permission"),
                Digest("execution-registry"),
                prepared.Adapter.ReconcilerId,
                prepared.Preparation.PreparationIdentity,
                prepared.Preparation.RootBinding);
            return new AliExecutionInvocationScope(grant).Enter(prepared.Arguments);
        }

        internal PreparedActionIntent Intent(PreparedMutation prepared) =>
            new(
                Digest("idempotency"),
                prepared.WorkItemId,
                prepared.Adapter.ToolName,
                prepared.Adapter.CapabilityId,
                ArgumentsDigest(prepared.Arguments),
                prepared.Preparation.TargetVersionDigest,
                Digest("permission"),
                Digest("registry"),
                Digest("execution-registry"),
                prepared.Adapter.ReconcilerId,
                prepared.Preparation.RootBinding,
                RequiresApproval: true,
                AcceptedCallId: prepared.CallId,
                PreparationIdentity: prepared.Preparation.PreparationIdentity);

        internal async Task<object?> RunProviderMutationAsync(
            string toolName,
            AIFunctionArguments arguments,
            string methodName,
            params object?[] providerArguments)
        {
            var prepared = await PrepareAsync(toolName, arguments);
            var activation = EnterGrant(prepared);
            var result = await InvokeProviderAsync(methodName, providerArguments);
            await activation.CompleteAsync(result, CancellationToken.None);
            await activation.DisposeAsync();
            return result;
        }

        internal async Task<object?> InvokeProviderAsync(
            string methodName,
            params object?[] arguments) =>
            await InvokeProviderAsync(_provider, methodName, arguments).ConfigureAwait(false);

        private static async Task<object?> InvokeProviderAsync(
            FileMemoryProvider provider,
            string methodName,
            params object?[] arguments)
        {
            var method = typeof(FileMemoryProvider)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    && candidate.GetParameters().Length == arguments.Length);
            object? invocation;
            try
            {
                invocation = method.Invoke(provider, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }

            if (invocation is not Task task)
            {
                throw new InvalidOperationException(
                    "The Framework work-memory method did not return Task.");
            }
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        public void Dispose()
        {
            foreach (var hooked in _hookedCoordinators)
            {
                hooked.Dispose();
            }
            _provider.Dispose();
            _scope.Dispose();
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
