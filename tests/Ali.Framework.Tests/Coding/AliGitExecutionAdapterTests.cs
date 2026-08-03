using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.SourceControl;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class AliGitExecutionAdapterTests
{
    private static readonly string[] ExactToolNames =
    [
        AliCapabilityCatalog.GitStatusName,
        AliCapabilityCatalog.GitDiffName,
        AliCapabilityCatalog.GitCreateBranchName,
        AliCapabilityCatalog.GitCommitName,
        AliCapabilityCatalog.GitPushName
    ];

    [Fact]
    public async Task RegistersAndPreparesOnlyFiveExactGitOperationTuples()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapters = fixture.Coordinator.Adapters
            .Cast<AliGitExecutionAdapter>()
            .ToArray();
        Assert.Equal(ExactToolNames.Length, adapters.Length);
        Assert.Equal(
            ExactToolNames.Order(StringComparer.Ordinal),
            adapters.Select(adapter => adapter.ToolName).Order(StringComparer.Ordinal));
        Assert.Equal(
            ExactToolNames.Order(StringComparer.Ordinal),
            fixture.Coordinator.TargetStates.ToolNames.Order(StringComparer.Ordinal));

        var preparationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            var prepared = await fixture.PrepareAsync(
                adapter.ToolName,
                fixture.DefaultArguments(adapter.ToolName));
            Assert.True(preparationIds.Add(prepared.Preparation.PreparationIdentity));
            Assert.Equal("ali.tool." + adapter.ToolName, adapter.CapabilityId);
            Assert.Equal("ali.reconcile." + adapter.ToolName, adapter.ReconcilerId);
            Assert.DoesNotContain('*', adapter.ToolName);
            Assert.DoesNotContain('*', adapter.CapabilityId);
            Assert.DoesNotContain('*', adapter.ReconcilerId);
            Assert.Matches("^[0-9a-f]{64}$", prepared.Preparation.RootBinding);

            var snapshot = await fixture.Store.LoadAsync(
                prepared.Preparation.PreparationIdentity,
                TestContext.Current.CancellationToken);
            Assert.Equal(adapter.ToolName, snapshot.Plan.ToolName);
            Assert.Equal(adapter.CapabilityId, snapshot.Plan.CapabilityId);
            Assert.Equal(adapter.ReconcilerId, snapshot.Plan.ReconcilerId);
            Assert.Equal(
                AliGitInvocationCatalog.CommandIdentity(adapter.Kind),
                snapshot.Plan.DomainPreparationIdentity);
            Assert.Matches("^[0-9A-Fa-f]{64}$", snapshot.Plan.DomainPreparationDigest);

            var reconciled = await adapter.ReconcileAsync(
                prepared.Request.TurnIdentity,
                prepared.Intent,
                TestContext.Current.CancellationToken);
            Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
            Assert.Equal("invocation-prepared-not-started", reconciled.OutcomeCode);
        }
    }

    [Fact]
    public async Task StatusAndDiffUseExactGrantsAndCommitAuthenticatedReadEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();

        var statusArguments = fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName);
        var status = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitStatusName,
            statusArguments);
        SourceControlResult statusResult;
        await using (var activation = new AliExecutionInvocationScope(Grant(status.Intent))
                         .Enter(statusArguments))
        {
            statusResult = await fixture.Coordinator.ExecuteStatusAsync(
                fixture.ProjectVirtualPath,
                token => fixture.SourceControl.StatusAsync(
                    fixture.ProjectVirtualPath,
                    token),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(statusResult, CancellationToken.None);
        }
        Assert.True(statusResult.Success, statusResult.Output);
        Assert.Equal("status", statusResult.Operation);
        Assert.Contains("##", statusResult.Output, StringComparison.Ordinal);
        var statusReconciled = await status.Adapter.ReconcileAsync(
            status.Request.TurnIdentity,
            status.Intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, statusReconciled.Disposition);
        Assert.Equal("git-status-returned-success", statusReconciled.OutcomeCode);
        Assert.NotNull(statusReconciled.AppliedEvidence);

        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// diff evidence",
            TestContext.Current.CancellationToken);
        var diffArguments = fixture.DefaultArguments(AliCapabilityCatalog.GitDiffName);
        var diff = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitDiffName,
            diffArguments);
        SourceControlResult diffResult;
        await using (var activation = new AliExecutionInvocationScope(Grant(diff.Intent))
                         .Enter(diffArguments))
        {
            diffResult = await fixture.Coordinator.ExecuteDiffAsync(
                fixture.ProjectVirtualPath,
                staged: false,
                token => fixture.SourceControl.DiffAsync(
                    fixture.ProjectVirtualPath,
                    staged: false,
                    token),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(diffResult, CancellationToken.None);
        }
        Assert.True(diffResult.Success, diffResult.Output);
        Assert.Equal("diff", diffResult.Operation);
        Assert.Contains("diff evidence", diffResult.Output, StringComparison.Ordinal);
        var diffReconciled = await diff.Adapter.ReconcileAsync(
            diff.Request.TurnIdentity,
            diff.Intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, diffReconciled.Disposition);
        Assert.Equal("git-diff-returned-success", diffReconciled.OutcomeCode);
        Assert.NotNull(diffReconciled.AppliedEvidence);
    }

    [Fact]
    public async Task RepositoryChangeAfterPrepareFailsBeforeProviderExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName);
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitStatusName,
            arguments);
        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// changed after authorization",
            TestContext.Current.CancellationToken);
        var executed = 0;

        await using (var activation = new AliExecutionInvocationScope(Grant(prepared.Intent))
                         .Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.ExecuteStatusAsync(
                    fixture.ProjectVirtualPath,
                    _ =>
                    {
                        executed++;
                        return Task.FromResult(new SourceControlResult(
                            true,
                            "status",
                            fixture.RepositoryRoot,
                            "unexpected",
                            string.Empty,
                            0));
                    },
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, executed);
        var failed = await fixture.Store.LoadAsync(
            prepared.Preparation.PreparationIdentity,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Failed, failed.State);
        Assert.Equal("git-binding-revalidation-failed", failed.Receipt!.FailureCode);
        var reconciled = await prepared.Adapter.ReconcileAsync(
            prepared.Request.TurnIdentity,
            prepared.Intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("git-invocation-failed-state-unproven", reconciled.OutcomeCode);
    }

    [Fact]
    public async Task StartedBranchCommitAndPushRemainUnknownWithoutExactPoststateProof()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var toolName in new[]
                 {
                     AliCapabilityCatalog.GitCreateBranchName,
                     AliCapabilityCatalog.GitCommitName,
                     AliCapabilityCatalog.GitPushName
                 })
        {
            var arguments = fixture.DefaultArguments(toolName);
            var prepared = await fixture.PrepareAsync(toolName, arguments);
            await using (var activation = new AliExecutionInvocationScope(Grant(prepared.Intent))
                             .Enter(arguments))
            {
                await prepared.Adapter.BeginInvocationAsync(
                    prepared.Arguments,
                    TestContext.Current.CancellationToken);
                var started = await prepared.Adapter.ReconcileAsync(
                    prepared.Request.TurnIdentity,
                    prepared.Intent,
                    TestContext.Current.CancellationToken);
                Assert.Equal(ActionReconciliationDisposition.Unknown, started.Disposition);
                Assert.Equal("invocation-started-no-terminal-receipt", started.OutcomeCode);
            }

            var abandoned = await fixture.Store.LoadAsync(
                prepared.Preparation.PreparationIdentity,
                TestContext.Current.CancellationToken);
            Assert.Equal(AliDurableInvocationState.InDoubt, abandoned.State);
        }
    }

    [Fact]
    public async Task BranchCommitAndPushUseOnlyTheIsolatedLocalBareRemote()
    {
        await using var fixture = await Fixture.CreateAsync();
        const string branchName = "feature/cp7-git-adapter";

        var branchArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath,
            ["branchName"] = branchName
        };
        var branch = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitCreateBranchName,
            branchArguments);
        SourceControlResult branchResult;
        await using (var activation = new AliExecutionInvocationScope(Grant(branch.Intent))
                         .Enter(branchArguments))
        {
            branchResult = await fixture.Coordinator.ExecuteCreateBranchAsync(
                fixture.ProjectVirtualPath,
                branchName,
                token => fixture.SourceControl.CreateBranchAsync(
                    fixture.ProjectVirtualPath,
                    branchName,
                    token),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(branchResult, CancellationToken.None);
        }
        Assert.True(branchResult.Success, branchResult.Output);
        Assert.Equal("create-branch", branchResult.Operation);
        Assert.Equal(
            ActionReconciliationDisposition.Applied,
            (await branch.Adapter.ReconcileAsync(
                branch.Request.TurnIdentity,
                branch.Intent,
                TestContext.Current.CancellationToken)).Disposition);

        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// committed through exact Git adapter",
            TestContext.Current.CancellationToken);
        await fixture.RunGitAsync("add", "App/Program.cs");
        var commitArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath,
            ["message"] = "Exercise exact durable Git adapter"
        };
        var commit = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitCommitName,
            commitArguments);
        SourceControlResult commitResult;
        await using (var activation = new AliExecutionInvocationScope(Grant(commit.Intent))
                         .Enter(commitArguments))
        {
            commitResult = await fixture.Coordinator.ExecuteCommitAsync(
                fixture.ProjectVirtualPath,
                "Exercise exact durable Git adapter",
                token => fixture.SourceControl.CommitAsync(
                    fixture.ProjectVirtualPath,
                    "Exercise exact durable Git adapter",
                    token),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(commitResult, CancellationToken.None);
        }
        Assert.True(commitResult.Success, commitResult.Output);
        Assert.Equal("commit", commitResult.Operation);
        Assert.Equal(
            ActionReconciliationDisposition.Applied,
            (await commit.Adapter.ReconcileAsync(
                commit.Request.TurnIdentity,
                commit.Intent,
                TestContext.Current.CancellationToken)).Disposition);

        var pushArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath,
            ["remote"] = "origin",
            ["branchName"] = branchName
        };
        var push = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitPushName,
            pushArguments);
        SourceControlResult pushResult;
        await using (var activation = new AliExecutionInvocationScope(Grant(push.Intent))
                         .Enter(pushArguments))
        {
            pushResult = await fixture.Coordinator.ExecutePushAsync(
                fixture.ProjectVirtualPath,
                "origin",
                branchName,
                token => fixture.SourceControl.PushAsync(
                    fixture.ProjectVirtualPath,
                    "origin",
                    branchName,
                    token),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(pushResult, CancellationToken.None);
        }
        Assert.True(pushResult.Success, pushResult.Output);
        Assert.Equal("push", pushResult.Operation);
        Assert.Equal(
            ActionReconciliationDisposition.Applied,
            (await push.Adapter.ReconcileAsync(
                push.Request.TurnIdentity,
                push.Intent,
                TestContext.Current.CancellationToken)).Disposition);

        var localHead = (await fixture.RunGitAsync("rev-parse", "HEAD")).Trim();
        var remoteHead = (await fixture.RunBareGitAsync(
            "rev-parse",
            "refs/heads/" + branchName)).Trim();
        Assert.Matches("^[0-9a-f]{40,64}$", localHead);
        Assert.Equal(localHead, remoteHead);
    }

    [Fact]
    public async Task CommitDisablesExternalRepositoryHooks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var markerPath = Path.Combine(fixture.RepositoryRoot, "hook-invoked.txt");
        var hookPath = Path.Combine(
            fixture.RepositoryRoot,
            ".git",
            "hooks",
            "pre-commit");
        await File.WriteAllTextAsync(
            hookPath,
            "#!/bin/sh\nprintf invoked > hook-invoked.txt\nexit 73\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// hook suppression proof",
            TestContext.Current.CancellationToken);
        await fixture.RunGitAsync("add", "App/Program.cs");

        var arguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath,
            ["message"] = "Prove hooks are disabled"
        };
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitCommitName,
            arguments);
        SourceControlResult result;
        await using (var activation = new AliExecutionInvocationScope(Grant(prepared.Intent))
                         .Enter(arguments))
        {
            result = await fixture.Coordinator.ExecuteCommitAsync(
                fixture.ProjectVirtualPath,
                "Prove hooks are disabled",
                token => fixture.SourceControl.CommitAsync(
                    fixture.ProjectVirtualPath,
                    "Prove hooks are disabled",
                    token),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(result, CancellationToken.None);
        }

        Assert.True(result.Success, result.Output);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task IncludedConfigurationChangeInvalidatesPreparedGrant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var includePath = Path.Combine(fixture.WorkspaceRoot, "included.gitconfig");
        await File.WriteAllTextAsync(
            includePath,
            "[alias]\n\tcp7 = status\n",
            TestContext.Current.CancellationToken);
        await fixture.RunGitAsync("config", "include.path", includePath);
        var arguments = fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName);
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitStatusName,
            arguments);
        await File.AppendAllTextAsync(
            includePath,
            "[color]\n\tui = false\n",
            TestContext.Current.CancellationToken);
        var executed = 0;

        await using (var activation = new AliExecutionInvocationScope(Grant(prepared.Intent))
                         .Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.ExecuteStatusAsync(
                    fixture.ProjectVirtualPath,
                    _ =>
                    {
                        executed++;
                        return Task.FromResult(new SourceControlResult(
                            true,
                            "status",
                            fixture.RepositoryRoot,
                            "unexpected",
                            string.Empty,
                            0));
                    },
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, executed);
    }

    [Fact]
    public async Task IncludedConfigurationCannotBeReplacedWhileGitExecutionLeaseIsHeld()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var includePath = Path.Combine(fixture.WorkspaceRoot, "leased.gitconfig");
        await File.WriteAllTextAsync(
            includePath,
            "[alias]\n\tcp7 = status\n",
            TestContext.Current.CancellationToken);
        await fixture.RunGitAsync("config", "include.path", includePath);
        var binding = fixture.Bindings.Resolve(
            AliGitInvocationKind.Status,
            JsonSerializer.SerializeToElement(
                fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName)));
        using var leases = AliGitExecutionFileLeaseGroup.Acquire(binding.ExecutionFiles);
        var displaced = includePath + ".displaced";

        Assert.ThrowsAny<IOException>(() => File.Move(includePath, displaced));
        leases.RequireStable();
        Assert.True(File.Exists(includePath));
        Assert.False(File.Exists(displaced));
    }

    [Fact]
    public async Task Repository_root_cannot_be_substituted_at_the_delegate_boundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var replacement = Path.Combine(fixture.WorkspaceRoot, "ReplacementRepo");
        var displaced = Path.Combine(fixture.WorkspaceRoot, "DisplacedRepo");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "substituted.txt"),
            "substituted-root",
            TestContext.Current.CancellationToken);
        var replacementBlocked = false;
        var coordinator = new AliGitExecutionCoordinator(
            fixture.Bindings,
            fixture.Store,
            new EvidenceLedger(
                Path.Combine(fixture.Root, "delegate-boundary-evidence"),
                "git-adapter-test-profile"),
            () =>
            {
                try
                {
                    Directory.Move(fixture.RepositoryRoot, displaced);
                    Directory.Move(replacement, fixture.RepositoryRoot);
                }
                catch (IOException)
                {
                    replacementBlocked = true;
                }
                catch (UnauthorizedAccessException)
                {
                    replacementBlocked = true;
                }
            });
        var arguments = fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName);
        var prepared = await fixture.PrepareAsync(
            coordinator,
            AliCapabilityCatalog.GitStatusName,
            arguments);
        var delegateObservedOriginal = false;
        try
        {
            await using var activation = new AliExecutionInvocationScope(Grant(prepared.Intent))
                .Enter(arguments);
            var result = await coordinator.ExecuteStatusAsync(
                fixture.ProjectVirtualPath,
                async _ =>
                {
                    delegateObservedOriginal = (await File.ReadAllTextAsync(
                            fixture.ProgramPath,
                            TestContext.Current.CancellationToken))
                        .Contains("Console.WriteLine", StringComparison.Ordinal);
                    return new SourceControlResult(
                        true,
                        "status",
                        fixture.RepositoryRoot,
                        "Status completed.",
                        string.Empty,
                        0);
                },
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(result, CancellationToken.None);

            Assert.True(replacementBlocked);
            Assert.True(delegateObservedOriginal);
        }
        finally
        {
            if (Directory.Exists(displaced) && !Directory.Exists(fixture.RepositoryRoot))
            {
                Directory.Move(displaced, fixture.RepositoryRoot);
            }
        }
    }

    [Fact]
    public async Task PathReplacementCannotRedirectPreparedGitExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName);
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitStatusName,
            arguments);
        var fakePath = Path.Combine(fixture.Root, "fake-path");
        Directory.CreateDirectory(fakePath);
        await File.WriteAllTextAsync(
            Path.Combine(fakePath, "git.exe"),
            "This is deliberately not an executable.",
            TestContext.Current.CancellationToken);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", fakePath);
            SourceControlResult result;
            await using (var activation = new AliExecutionInvocationScope(Grant(prepared.Intent))
                             .Enter(arguments))
            {
                result = await fixture.Coordinator.ExecuteStatusAsync(
                    fixture.ProjectVirtualPath,
                    token => fixture.SourceControl.StatusAsync(
                        fixture.ProjectVirtualPath,
                        token),
                    TestContext.Current.CancellationToken);
                await activation.CompleteAsync(result, CancellationToken.None);
            }
            Assert.True(result.Success, result.Output);
            Assert.Contains("##", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task LinkedWorktreeMetadataOutsideApprovedMountIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var externalRepository = Path.Combine(fixture.Root, "outside-repository");
        var linkedWorktree = Path.Combine(fixture.WorkspaceRoot, "Linked");
        var externalApp = Path.Combine(externalRepository, "App");
        Directory.CreateDirectory(externalApp);
        await Fixture.RunGitAtAsync(externalRepository, ["init", "-b", "main"]);
        await Fixture.RunGitAtAsync(
            externalRepository,
            ["config", "user.name", "Ali Test"]);
        await Fixture.RunGitAtAsync(
            externalRepository,
            ["config", "user.email", "ali@example.invalid"]);
        await File.WriteAllTextAsync(
            Path.Combine(externalApp, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(externalApp, "Program.cs"),
            "Console.WriteLine(\"Linked\");",
            TestContext.Current.CancellationToken);
        await Fixture.RunGitAtAsync(externalRepository, ["add", "."]);
        await Fixture.RunGitAtAsync(
            externalRepository,
            ["commit", "-m", "External initial commit"]);
        await Fixture.RunGitAtAsync(
            externalRepository,
            ["worktree", "add", linkedWorktree, "-b", "cp7-linked"]);
        var arguments = JsonSerializer.SerializeToElement(new AIFunctionArguments
        {
            ["targetPath"] = "Workspace/Linked/App/App.csproj"
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.Coordinator.TargetStates.Capture(
                AliCapabilityCatalog.GitStatusName,
                arguments));
        Assert.Contains("approved mount", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpsPushRemoteIsBoundWithoutContactingTheNetwork()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunGitAsync(
            "remote",
            "set-url",
            "--push",
            "origin",
            "https://example.invalid/project-ali.git");
        var prepared = await fixture.PrepareAsync(
            AliCapabilityCatalog.GitPushName,
            fixture.DefaultArguments(AliCapabilityCatalog.GitPushName));

        Assert.Matches(
            "^[0-9a-f]{32}$",
            prepared.Preparation.PreparationIdentity);
        var reconciled = await prepared.Adapter.ReconcileAsync(
            prepared.Request.TurnIdentity,
            prepared.Intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
    }

    [Fact]
    public async Task ConditionalConfigurationIncludeIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(
            Path.Combine(fixture.RepositoryRoot, ".git", "config"),
            "\n[includeIf \"onbranch:main\"]\n\tpath = ../conditional.gitconfig\n",
            TestContext.Current.CancellationToken);
        var arguments = JsonSerializer.SerializeToElement(
            fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName));

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.Coordinator.TargetStates.Capture(
                AliCapabilityCatalog.GitStatusName,
                arguments));
        Assert.Contains("Conditional Git includes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicCredentialHelperIsRejectedForPush()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunGitAsync(
            "config",
            "--local",
            "--add",
            "credential.helper",
            "!echo unsafe");
        var arguments = JsonSerializer.SerializeToElement(
            fixture.DefaultArguments(AliCapabilityCatalog.GitPushName));

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.Coordinator.TargetStates.Capture(
                AliCapabilityCatalog.GitPushName,
                arguments));
        Assert.Contains("credential helper", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilterHelperWithExternalScriptInputIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunGitAsync(
            "config",
            "--local",
            "filter.unsafe.clean",
            "python C:/outside/filter.py");
        var arguments = JsonSerializer.SerializeToElement(
            fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName));

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.Coordinator.TargetStates.Capture(
                AliCapabilityCatalog.GitStatusName,
                arguments));
        Assert.Contains("filter helper", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("sh")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SingleTokenFilterShellIsRejectedDuringPreauthorization(
        string hostileCommand)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.RunGitAsync(
            "config",
            "--local",
            "filter.unsafe.clean",
            hostileCommand);
        var arguments = JsonSerializer.SerializeToElement(
            fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName));

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.Coordinator.TargetStates.Capture(
                AliCapabilityCatalog.GitStatusName,
                arguments));

        Assert.Contains("git-lfs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepositoryFileHardLinkedToOutsideApprovedMountIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var outside = Path.Combine(fixture.Root, "outside-hard-link-source.cs");
        var inside = Path.Combine(fixture.RepositoryRoot, "App", "HardLinked.cs");
        await File.WriteAllTextAsync(
            outside,
            "Console.WriteLine(\"outside alias\");",
            TestContext.Current.CancellationToken);
        Assert.True(
            CreateHardLinkW(inside, outside, IntPtr.Zero),
            "The adversarial hard-link fixture could not be created.");
        var arguments = JsonSerializer.SerializeToElement(
            fixture.DefaultArguments(AliCapabilityCatalog.GitStatusName));

        var exception = Assert.Throws<InvalidDataException>(() =>
            fixture.Coordinator.TargetStates.Capture(
                AliCapabilityCatalog.GitStatusName,
                arguments));

        Assert.Contains("hard-link alias", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaticPreauthorizationCaptureDoesNotExecuteInvalidProviderBinary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var installationRoot = Path.Combine(fixture.Root, "static-provider");
        var execPath = Path.Combine(installationRoot, "git-core");
        Directory.CreateDirectory(execPath);
        var executable = Path.Combine(
            execPath,
            OperatingSystem.IsWindows() ? "git.exe" : "git");
        await File.WriteAllTextAsync(
            executable,
            "This deliberately is not an executable image.",
            TestContext.Current.CancellationToken);
        var provider = new AliGitProviderPin(
            executable,
            execPath,
            installationRoot,
            [execPath],
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var metadata = Path.Combine(fixture.RepositoryRoot, ".git");
        var repository = new AliGitRepositoryLayout(
            fixture.WorkspaceRoot,
            fixture.RepositoryRoot,
            metadata,
            metadata);

        var captured = AliGitEffectiveInputCapture.Capture(
            repository,
            provider,
            AliGitInvocationKind.Status,
            remote: null);

        Assert.Matches("^[0-9a-f]{64}$", captured.CombinedDigest);
    }

    [Fact]
    public async Task Pinned_provider_executable_cannot_be_substituted_before_process_start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Path.Combine(
            TestRepository.Root,
            "bin",
            nameof(AliGitExecutionAdapterTests),
            Guid.NewGuid().ToString("N"));
        var execPath = Path.Combine(root, "provider-tools");
        Directory.CreateDirectory(execPath);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = Path.Combine(execPath, "provider.exe");
        var substitute = Path.Combine(root, "substitute.exe");
        var displaced = Path.Combine(root, "displaced.exe");
        File.Copy(Path.Combine(system, "cmd.exe"), executable);
        File.Copy(Path.Combine(system, "where.exe"), substitute);
        var provider = new AliGitProviderPin(
            executable,
            execPath,
            root,
            [execPath],
            AliGitProviderPin.CaptureEnvironment());
        var replacementBlocked = false;
        try
        {
            _ = await AliGitFixedProcess.RunAsync(
                provider,
                root,
                AliGitInvocationKind.Status,
                [],
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken,
                () =>
                {
                    try
                    {
                        File.Move(executable, displaced);
                        File.Move(substitute, executable);
                    }
                    catch (IOException)
                    {
                        replacementBlocked = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        replacementBlocked = true;
                    }
                });

            Assert.True(replacementBlocked);
            Assert.True(File.Exists(substitute));
            Assert.False(File.Exists(displaced));
        }
        finally
        {
            if (File.Exists(displaced) && !File.Exists(executable))
            {
                File.Move(displaced, executable);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderPinDiscoversStaticLayoutWithoutStartingInvalidBinary()
    {
        var root = Path.Combine(
            TestRepository.Root,
            "bin",
            "AliGitExecutionAdapterTests",
            Guid.NewGuid().ToString("N"));
        var cmd = Path.Combine(root, "cmd");
        var execPath = Path.Combine(root, "mingw64", "libexec", "git-core");
        Directory.CreateDirectory(cmd);
        Directory.CreateDirectory(execPath);
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        await File.WriteAllTextAsync(
            Path.Combine(cmd, name),
            "invalid executable anchor",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(execPath, name),
            "invalid canonical executable",
            TestContext.Current.CancellationToken);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", cmd);
            var provider = AliGitProviderIdentity.Pin();
            Assert.Equal(Path.GetFullPath(execPath), provider.ExecPath);
            Assert.Equal(Path.GetFullPath(root), provider.InstallationRoot);
            Assert.Equal(Path.GetFullPath(Path.Combine(execPath, name)), provider.ExecutablePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(root, recursive: true);
        }
    }

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

    private static AliExecutionGrant Grant(PreparedActionIntent intent) =>
        new(
            intent.IdempotencyKey,
            intent.AcceptedCallId!,
            intent.ToolName,
            intent.CapabilityId,
            intent.CanonicalArgumentsDigest,
            intent.TargetVersionDigest,
            intent.PermissionReceiptDigest,
            intent.ExecutionRegistryIdentityDigest,
            intent.ReconcilerId,
            intent.PreparationIdentity!,
            intent.RootBinding);

    private static string ArgumentsDigest(JsonElement arguments)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(arguments);
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

    private sealed record PreparedInvocation(
        AliGitExecutionAdapter Adapter,
        AIFunctionArguments FunctionArguments,
        JsonElement Arguments,
        AliExecutionPreparationRequest Request,
        AliExecutionPreparation Preparation,
        PreparedActionIntent Intent);

    private sealed class Fixture : IAsyncDisposable
    {
        private const string ProfileBinding = "git-adapter-test-profile";

        private Fixture(
            string root,
            string workspaceRoot,
            string repositoryRoot,
            string bareRemoteRoot,
            string programPath,
            string durableRoot,
            AliGitInvocationBindingResolver bindings,
            AliGitExecutionCoordinator coordinator,
            AliSourceControlEngineering sourceControl)
        {
            Root = root;
            WorkspaceRoot = workspaceRoot;
            RepositoryRoot = repositoryRoot;
            BareRemoteRoot = bareRemoteRoot;
            ProgramPath = programPath;
            Bindings = bindings;
            Coordinator = coordinator;
            SourceControl = sourceControl;
            Store = new AliDurableInvocationStore(
                Path.Combine(durableRoot, "Git", "Invocations"),
                ProfileBinding);
        }

        internal string Root { get; }

        internal string WorkspaceRoot { get; }

        internal string RepositoryRoot { get; }

        internal string BareRemoteRoot { get; }

        internal string ProgramPath { get; }

        internal string ProjectVirtualPath => "Workspace/Repo/App/App.csproj";

        internal AliGitInvocationBindingResolver Bindings { get; }

        internal AliGitExecutionCoordinator Coordinator { get; }

        internal AliSourceControlEngineering SourceControl { get; }

        internal AliDurableInvocationStore Store { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                TestRepository.Root,
                "bin",
                "AliGitExecutionAdapterTests",
                Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var repository = Path.Combine(workspace, "Repo");
            var bareRemote = Path.Combine(workspace, "Remote.git");
            var app = Path.Combine(repository, "App");
            Directory.CreateDirectory(workspace);
            await RunGitAtAsync(workspace, ["init", "--bare", bareRemote]);
            Directory.CreateDirectory(repository);
            await RunGitAtAsync(repository, ["init", "-b", "main"]);
            await RunGitAtAsync(repository, ["config", "user.name", "Ali Test"]);
            await RunGitAtAsync(repository, ["config", "user.email", "ali@example.invalid"]);
            await RunGitAtAsync(repository, ["remote", "add", "origin", bareRemote]);
            Directory.CreateDirectory(app);
            var projectPath = Path.Combine(app, "App.csproj");
            var programPath = Path.Combine(app, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                programPath,
                "Console.WriteLine(\"Ali\");",
                TestContext.Current.CancellationToken);
            await RunGitAtAsync(repository, ["add", "."]);
            await RunGitAtAsync(repository, ["commit", "-m", "Initial commit"]);
            await RunGitAtAsync(repository, ["push", "-u", "origin", "main"]);

            var permissions = new AgentToolPermissionStore(root);
            var fileStore = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(
                fileStore,
                new AgentFileActionAuditStore(root, activeUsers: null),
                permissions);
            var resolver = new AliCodingProjectResolver(access);
            var sourceControl = new AliSourceControlEngineering(resolver);
            var bindings = new AliGitInvocationBindingResolver(
                resolver,
                sourceControl.ProviderPin);
            var durableRoot = Path.Combine(root, "durable");
            var store = new AliDurableInvocationStore(
                Path.Combine(durableRoot, "Git", "Invocations"),
                ProfileBinding);
            var coordinator = new AliGitExecutionCoordinator(
                bindings,
                store,
                new EvidenceLedger(durableRoot, ProfileBinding));
            return new Fixture(
                root,
                workspace,
                repository,
                bareRemote,
                programPath,
                durableRoot,
                bindings,
                coordinator,
                sourceControl);
        }

        internal AIFunctionArguments DefaultArguments(string toolName) => toolName switch
        {
            AliCapabilityCatalog.GitStatusName => new AIFunctionArguments
            {
                ["targetPath"] = ProjectVirtualPath
            },
            AliCapabilityCatalog.GitDiffName => new AIFunctionArguments
            {
                ["targetPath"] = ProjectVirtualPath,
                ["staged"] = false
            },
            AliCapabilityCatalog.GitCreateBranchName => new AIFunctionArguments
            {
                ["targetPath"] = ProjectVirtualPath,
                ["branchName"] = "feature/prepared-only"
            },
            AliCapabilityCatalog.GitCommitName => new AIFunctionArguments
            {
                ["targetPath"] = ProjectVirtualPath,
                ["message"] = "Prepared exact commit"
            },
            AliCapabilityCatalog.GitPushName => new AIFunctionArguments
            {
                ["targetPath"] = ProjectVirtualPath,
                ["remote"] = "origin",
                ["branchName"] = "main"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(toolName))
        };

        internal async Task<PreparedInvocation> PrepareAsync(
            string toolName,
            AIFunctionArguments functionArguments) =>
            await PrepareAsync(Coordinator, toolName, functionArguments);

        internal async Task<PreparedInvocation> PrepareAsync(
            AliGitExecutionCoordinator coordinator,
            string toolName,
            AIFunctionArguments functionArguments)
        {
            var adapter = Assert.Single(
                coordinator.Adapters.Cast<AliGitExecutionAdapter>(),
                candidate => candidate.ToolName == toolName);
            var arguments = JsonSerializer.SerializeToElement(functionArguments);
            var target = coordinator.TargetStates.Capture(toolName, arguments);
            var targetDigest = AliGitInvocationBindingResolver.TargetVersionDigest(target);
            var request = new AliExecutionPreparationRequest(
                new TurnIdentity(
                    "test-user",
                    "git-adapter-" + toolName,
                    "assistant-message"),
                "call-" + toolName,
                "work-" + toolName,
                toolName,
                adapter.CapabilityId,
                adapter.ReconcilerId,
                arguments.Clone(),
                ArgumentsDigest(arguments),
                Digest("action-" + toolName),
                targetDigest,
                Digest("permission-" + toolName),
                Digest("registry-revision-" + toolName),
                Digest("registry-identity-" + toolName));
            var preparation = await adapter.PrepareAsync(
                request,
                TestContext.Current.CancellationToken);
            var intent = new PreparedActionIntent(
                request.ActionIdentityFingerprint,
                request.WorkItemId,
                request.ToolName,
                request.CapabilityId,
                request.CanonicalArgumentsDigest,
                request.TargetVersionDigest,
                request.PermissionReceiptDigest,
                request.RegistryRevisionDigest,
                request.ExecutionRegistryIdentityDigest,
                request.ReconcilerId,
                preparation.RootBinding,
                RequiresApproval: true,
                request.CallId,
                preparation.PreparationIdentity);
            return new PreparedInvocation(
                adapter,
                functionArguments,
                arguments,
                request,
                preparation,
                intent);
        }

        internal Task<string> RunGitAsync(params string[] arguments) =>
            RunGitAtAsync(RepositoryRoot, arguments);

        internal Task<string> RunBareGitAsync(params string[] arguments) =>
            RunGitAtAsync(BareRemoteRoot, arguments);

        internal static async Task<string> RunGitAtAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            var result = await AliBoundedProcessRunner.RunAsync(
                "git",
                workingDirectory,
                arguments,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Output);
            return result.Output;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(
                             Root,
                             "*",
                             SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            "The isolated Git fixture unexpectedly contains a reparse point.");
                    }
                    if ((attributes & FileAttributes.Directory) == 0
                        && (attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
