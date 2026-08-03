using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Dependencies;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Toolchains;
using Ali.Modules.Coding.Web;
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

public sealed class AliCodingProcessExecutionAdapterTests
{
    private static readonly string[] ExactToolNames =
    [
        AliCapabilityCatalog.CodingAnalyzeProjectName,
        AliCapabilityCatalog.CodingBuildProjectName,
        AliCapabilityCatalog.CodingTestProjectName,
        AliCapabilityCatalog.CodingRunProjectName,
        AliCapabilityCatalog.DotNetBuildName,
        AliCapabilityCatalog.DotNetTestName,
        AliCapabilityCatalog.DotNetVerifyName,
        AliCapabilityCatalog.DotNetRunName,
        AliCapabilityCatalog.DotNetStopProjectName,
        AliCapabilityCatalog.DotNetDependencyInspectName
    ];

    private static readonly string[] WithheldCanonicalMutationToolNames =
    [
        AliCapabilityCatalog.CodingFormatProjectName,
        AliCapabilityCatalog.DotNetCreateProjectName,
        AliCapabilityCatalog.RoslynFormatProjectName,
        AliCapabilityCatalog.DotNetDependencyApplyName
    ];

    [Fact]
    public async Task ModuleRegistersOnlyTransactionallySafeCodingProcessTuples()
    {
        await using var fixture = await Fixture.CreateAsync();

        var adapters = fixture.Coordinator.Adapters
            .Cast<AliCodingProcessExecutionAdapter>()
            .ToArray();
        Assert.Equal(ExactToolNames.Length, adapters.Length);
        Assert.Equal(
            ExactToolNames.Order(StringComparer.Ordinal),
            adapters.Select(adapter => adapter.ToolName).Order(StringComparer.Ordinal));
        Assert.Equal(
            ExactToolNames.Concat(WithheldCanonicalMutationToolNames).Order(StringComparer.Ordinal),
            fixture.Coordinator.TargetStates.ToolNames.Order(StringComparer.Ordinal));
        Assert.Equal(
            ExactToolNames.Length,
            adapters.Select(adapter => new
            {
                adapter.ToolName,
                adapter.CapabilityId,
                adapter.ReconcilerId
            }).Distinct().Count());

        foreach (var adapter in adapters)
        {
            Assert.Equal(
                "ali.tool." + adapter.ToolName,
                adapter.CapabilityId);
            Assert.Equal(
                "ali.reconcile." + adapter.ToolName,
                adapter.ReconcilerId);
            Assert.DoesNotContain('*', adapter.ToolName);
            Assert.DoesNotContain('*', adapter.CapabilityId);
            Assert.DoesNotContain('*', adapter.ReconcilerId);
        }

        Assert.All(
            WithheldCanonicalMutationToolNames,
            toolName => Assert.DoesNotContain(
                adapters,
                adapter => string.Equals(adapter.ToolName, toolName, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithheldCanonicalMutationsCannotReachTheirDelegateWithoutADurableGrant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectBefore = await File.ReadAllBytesAsync(
            fixture.ProjectPath,
            TestContext.Current.CancellationToken);
        var programBefore = await File.ReadAllBytesAsync(
            fixture.ProgramPath,
            TestContext.Current.CancellationToken);
        var delegateCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteProviderFormatAsync(
                fixture.ProjectVirtualPath,
                _ =>
                {
                    Interlocked.Increment(ref delegateCalls);
                    return Task.FromResult<AliLanguageOperationResult>(null!);
                },
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteDotNetCreateAsync(
                "Workspace/NewApp/NewApp.csproj",
                "console",
                _ =>
                {
                    Interlocked.Increment(ref delegateCalls);
                    return Task.FromResult<DotNetCreateProjectResult>(null!);
                },
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteRoslynFormatAsync(
                fixture.ProjectVirtualPath,
                _ =>
                {
                    Interlocked.Increment(ref delegateCalls);
                    return Task.FromResult<RoslynFormatResult>(null!);
                },
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteDependencyApplyAsync(
                fixture.ProjectVirtualPath,
                "add",
                "Example.Package",
                "1.2.3",
                _ =>
                {
                    Interlocked.Increment(ref delegateCalls);
                    return Task.FromResult<DependencyChangeResult>(null!);
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, delegateCalls);
        Assert.Equal(
            projectBefore,
            await File.ReadAllBytesAsync(
                fixture.ProjectPath,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            programBefore,
            await File.ReadAllBytesAsync(
                fixture.ProgramPath,
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, "NewApp")));
    }

    [Fact]
    public async Task EveryExactAdapterPreparesProtectedRootArgumentsCommandAndTargetIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preparationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var adapter in fixture.Coordinator.Adapters
                     .Cast<AliCodingProcessExecutionAdapter>())
        {
            var arguments = fixture.Arguments(adapter.ToolName);
            var target = fixture.Coordinator.TargetStates.Capture(
                adapter.ToolName,
                arguments);
            var targetDigest = AliCodingInvocationBindingResolver.TargetVersionDigest(target);
            var request = Request(adapter, arguments, targetDigest);
            var prepared = await adapter.PrepareAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.True(preparationIds.Add(prepared.PreparationIdentity));
            Assert.Equal(targetDigest, prepared.TargetVersionDigest);
            Assert.Matches("^[0-9a-f]{64}$", prepared.RootBinding);

            var plan = (await fixture.Store.LoadAsync(
                prepared.PreparationIdentity,
                TestContext.Current.CancellationToken)).Plan;
            Assert.Equal(adapter.ToolName, plan.ToolName);
            Assert.Equal(adapter.CapabilityId, plan.CapabilityId);
            Assert.Equal(adapter.ReconcilerId, plan.ReconcilerId);
            Assert.Equal(request.CanonicalArgumentsDigest, plan.CanonicalArgumentsDigest);
            Assert.Equal(targetDigest, plan.TargetVersionDigest);
            Assert.Equal(prepared.RootBinding, plan.RootBinding);
            Assert.Equal(
                AliCodingInvocationCatalog.CommandIdentity(adapter.Kind),
                plan.DomainPreparationIdentity);
            Assert.Matches("^[0-9A-Fa-f]{64}$", plan.DomainPreparationDigest);

            var intent = Intent(request, prepared);
            var recovered = await adapter.ReconcileAsync(
                request.TurnIdentity,
                intent,
                TestContext.Current.CancellationToken);
            Assert.Equal(ActionReconciliationDisposition.Absent, recovered.Disposition);
            Assert.Equal("invocation-prepared-not-started", recovered.OutcomeCode);
        }
    }

    [Fact]
    public async Task ExactGrantProducesAuthenticatedTerminalReceiptAndAppliedEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = Assert.Single(
            fixture.Coordinator.Adapters.Cast<AliCodingProcessExecutionAdapter>(),
            candidate => candidate.ToolName == AliCapabilityCatalog.CodingAnalyzeProjectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var targetDigest = AliCodingInvocationBindingResolver.TargetVersionDigest(
            fixture.Coordinator.TargetStates.Capture(
                adapter.ToolName,
                arguments));
        var request = Request(adapter, arguments, targetDigest);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var grant = Grant(intent);
        var expectedAuthorizationDigest = AliExecutionAuthorizationDigest.Compute(
            AliDurableInvocationStore.AuthorizationDomain,
            grant);
        var returned = new AliLanguageOperationResult(
            true,
            "dotnet-roslyn",
            "analyze",
            "Analysis completed.",
            0,
            1,
            string.Empty,
            []);

        await using (var activation = new AliExecutionInvocationScope(grant)
                         .Enter(functionArguments))
        {
            var result = await fixture.Coordinator.ExecuteProviderAnalyzeAsync(
                fixture.ProjectVirtualPath,
                _ => Task.FromResult(returned),
                TestContext.Current.CancellationToken);
            Assert.Same(returned, result);
            await activation.CompleteAsync(result, CancellationToken.None);
        }

        var completed = await fixture.Store.LoadAsync(
            prepared.PreparationIdentity,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Completed, completed.State);
        Assert.Equal(2, completed.Receipt!.Revision);
        Assert.Equal(
            "coding-analyze-project-returned-success",
            completed.Receipt.StableOutcomeCode);
        Assert.Equal(expectedAuthorizationDigest, completed.Receipt.AuthorizationDigest);
        Assert.Matches("^[0-9A-Fa-f]{64}$", completed.Receipt.ResultDigest!);

        var reconciled = await adapter.ReconcileAsync(
            request.TurnIdentity,
            intent,
            TestContext.Current.CancellationToken);
        Assert.True(
            reconciled.Disposition == ActionReconciliationDisposition.Applied,
            reconciled.OutcomeCode);
        Assert.Equal(
            completed.Receipt.StableOutcomeCode,
            reconciled.OutcomeCode);
        Assert.NotNull(reconciled.AppliedEvidence);
    }

    [Fact]
    public async Task Selected_source_root_substitution_is_blocked_or_detected_at_the_delegate_boundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var replacement = Path.Combine(fixture.Workspace, "ReplacementApp");
        var displaced = Path.Combine(fixture.Workspace, "DisplacedApp");
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "App.csproj"),
            "<Project Sdk=\"substituted\" />",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(replacement, "Program.cs"),
            "substituted-root",
            TestContext.Current.CancellationToken);
        var namespaceChanged = false;
        var replacementBlocked = false;
        var coordinator = new AliCodingProcessExecutionCoordinator(
            fixture.Bindings,
            fixture.Store,
            new EvidenceLedger(
                Path.Combine(fixture.Root, "delegate-boundary-evidence"),
                "coding-process-test-profile"),
            () =>
            {
                try
                {
                    Directory.Move(fixture.ProjectDirectory, displaced);
                    namespaceChanged = true;
                    Directory.Move(replacement, fixture.ProjectDirectory);
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
        var adapter = Assert.Single(
            coordinator.Adapters.Cast<AliCodingProcessExecutionAdapter>(),
            candidate => candidate.ToolName == AliCapabilityCatalog.CodingAnalyzeProjectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var targetDigest = AliCodingInvocationBindingResolver.TargetVersionDigest(
            coordinator.TargetStates.Capture(adapter.ToolName, arguments));
        var request = Request(adapter, arguments, targetDigest);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var delegateObservedOriginal = false;
        try
        {
            var executionException = await Record.ExceptionAsync(async () =>
            {
                await using var activation = new AliExecutionInvocationScope(Grant(intent))
                    .Enter(functionArguments);
                var result = await coordinator.ExecuteProviderAnalyzeAsync(
                    fixture.ProjectVirtualPath,
                    async _ =>
                    {
                        delegateObservedOriginal = (await File.ReadAllTextAsync(
                                fixture.ProgramPath,
                                TestContext.Current.CancellationToken))
                            .Contains("Console.WriteLine", StringComparison.Ordinal);
                        return new AliLanguageOperationResult(
                            true,
                            "dotnet-roslyn",
                            "analyze",
                            "Analysis completed.",
                            0,
                            1,
                            string.Empty,
                            []);
                    },
                    TestContext.Current.CancellationToken);
                await activation.CompleteAsync(result, CancellationToken.None);
            });

            if (namespaceChanged)
            {
                Assert.IsAssignableFrom<IOException>(executionException);
                Assert.False(delegateObservedOriginal);
            }
            else
            {
                Assert.True(replacementBlocked);
                Assert.Null(executionException);
                Assert.True(delegateObservedOriginal);
            }
        }
        finally
        {
            if (Directory.Exists(displaced))
            {
                if (Directory.Exists(fixture.ProjectDirectory)
                    && !Directory.Exists(replacement))
                {
                    Directory.Move(fixture.ProjectDirectory, replacement);
                }
                Directory.Move(displaced, fixture.ProjectDirectory);
            }
        }
    }

    [Fact]
    public async Task ReturnedFailureRemainsInDoubtAcrossRecovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = Assert.Single(
            fixture.Coordinator.Adapters.Cast<AliCodingProcessExecutionAdapter>(),
            candidate => candidate.ToolName == AliCapabilityCatalog.CodingAnalyzeProjectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var targetDigest = AliCodingInvocationBindingResolver.TargetVersionDigest(
            fixture.Coordinator.TargetStates.Capture(adapter.ToolName, arguments));
        var request = Request(adapter, arguments, targetDigest);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var returned = new AliLanguageOperationResult(
            false,
            "dotnet-roslyn",
            "analyze",
            "Analysis failed.",
            1,
            0,
            "compiler error",
            []);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var result = await fixture.Coordinator.ExecuteProviderAnalyzeAsync(
                fixture.ProjectVirtualPath,
                _ => Task.FromResult(returned),
                TestContext.Current.CancellationToken);
            await activation.CompleteAsync(result, CancellationToken.None);
        }

        var inDoubt = await fixture.Store.LoadAsync(
            prepared.PreparationIdentity,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.InDoubt, inDoubt.State);
        Assert.Equal(
            "coding-analyze-project-returned-failure",
            inDoubt.Receipt!.FailureCode);
        var reconciled = await adapter.ReconcileAsync(
            request.TurnIdentity,
            intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal(inDoubt.Receipt.FailureCode, reconciled.OutcomeCode);
        Assert.Null(reconciled.AppliedEvidence);
    }

    [Fact]
    public async Task SourceChangeAfterPrepareFailsBeforeTheExecutorCanRun()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = Assert.Single(
            fixture.Coordinator.Adapters.Cast<AliCodingProcessExecutionAdapter>(),
            candidate => candidate.ToolName == AliCapabilityCatalog.CodingAnalyzeProjectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var targetDigest = AliCodingInvocationBindingResolver.TargetVersionDigest(
            fixture.Coordinator.TargetStates.Capture(
                adapter.ToolName,
                arguments));
        var request = Request(adapter, arguments, targetDigest);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var executorCalls = 0;

        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// changed after durable prepare",
            TestContext.Current.CancellationToken);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.ExecuteProviderAnalyzeAsync(
                    fixture.ProjectVirtualPath,
                    _ =>
                    {
                        Interlocked.Increment(ref executorCalls);
                        return Task.FromResult(new AliLanguageOperationResult(
                            true,
                            "unexpected",
                            "analyze",
                            "unexpected",
                            0,
                            0,
                            string.Empty,
                            []));
                    },
                    TestContext.Current.CancellationToken));
            await activation.FailAsync(exception, CancellationToken.None);
        }

        Assert.Equal(0, executorCalls);
        var failed = await fixture.Store.LoadAsync(
            prepared.PreparationIdentity,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Failed, failed.State);
        Assert.Equal(
            "coding-binding-revalidation-failed",
            failed.Receipt!.FailureCode);
        var reconciled = await adapter.ReconcileAsync(
            request.TurnIdentity,
            intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal(
            "coding-invocation-failed-state-unproven",
            reconciled.OutcomeCode);
    }

    [Fact]
    public async Task DotNetBuildBindsTheExactDotNetHostForExecution()
    {
        await using var fixture = await Fixture.CreateAsync();

        var binding = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            fixture.Arguments(AliCapabilityCatalog.DotNetBuildName));

        var host = Assert.IsType<AliBoundExecutionFile>(
            binding.RuntimeBinding.DotNetHost);
        Assert.Equal(host.PhysicalPath, AliExactDotNetHost.Revalidate(host));
        Assert.Equal(host.Identity, binding.ExecutionAssets[host.PhysicalPath]);
    }

    [Fact]
    public async Task ExactDotNetHostBytesAreRevalidatedWithoutASecondPathLookup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hostPath = Path.Combine(fixture.Root, "fake-dotnet.exe");
        await File.WriteAllTextAsync(
            hostPath,
            "authorized-host-bytes",
            TestContext.Current.CancellationToken);
        var host = AliExactDotNetHost.Capture(hostPath);
        using var context = AliExactProcessExecutionContext.Enter(
            new AliExactProcessExecutionBinding(host, null));

        await File.WriteAllTextAsync(
            hostPath,
            "changed-host-bytes",
            TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => AliExactDotNetHost.Revalidate(host));
        Assert.Throws<InvalidOperationException>(() =>
            AliCodingInvocationExecutionContext.ResolveDotNetHostForExecution());
    }

    [Fact]
    public async Task DotNetRunRejectsBuiltArtifactByteDriftBeforeProcessLaunch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var artifactDirectory = Path.Combine(
            fixture.ProjectDirectory,
            "bin",
            "Release",
            "net10.0");
        Directory.CreateDirectory(artifactDirectory);
        var artifact = Path.Combine(artifactDirectory, "App.dll");
        await File.WriteAllTextAsync(
            artifact,
            "authorized-artifact",
            TestContext.Current.CancellationToken);
        var arguments = fixture.Arguments(AliCapabilityCatalog.DotNetRunName);
        var binding = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetRun,
            arguments);
        using var context = AliCodingInvocationExecutionContext.Enter(binding);

        await File.WriteAllTextAsync(
            artifact,
            "changed-artifact",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Tools.RunAsync(
                fixture.ProjectVirtualPath,
                "Release",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DotNetRunRejectsSidecarByteDriftBeforeProcessLaunch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var artifactDirectory = Path.Combine(
            fixture.ProjectDirectory,
            "bin",
            "Release",
            "net10.0");
        Directory.CreateDirectory(artifactDirectory);
        var artifact = Path.Combine(artifactDirectory, "App.dll");
        var sidecar = Path.Combine(artifactDirectory, "App.runtimeconfig.json");
        await File.WriteAllTextAsync(
            artifact,
            "authorized-artifact",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            sidecar,
            "authorized-sidecar",
            TestContext.Current.CancellationToken);
        var arguments = fixture.Arguments(AliCapabilityCatalog.DotNetRunName);
        var binding = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetRun,
            arguments);
        Assert.NotNull(binding.RuntimeBinding.DotNetRun?.LaunchClosure);
        using var context = AliCodingInvocationExecutionContext.Enter(binding);

        await File.WriteAllTextAsync(
            sidecar,
            "changed-sidecar",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Tools.RunAsync(
                fixture.ProjectVirtualPath,
                "Release",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplicationLaunchClosureRejectsSidecarAddChangeDeleteAndRename()
    {
        await using var fixture = await Fixture.CreateAsync();

        var added = await CaptureCaseAsync("add", includeSidecar: false);
        await File.WriteAllTextAsync(
            added.Sidecar,
            "added",
            TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => added.Closure.RequireStable());

        var changed = await CaptureCaseAsync("change", includeSidecar: true);
        await File.WriteAllTextAsync(
            changed.Sidecar,
            "changed",
            TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => changed.Closure.RequireStable());

        var deleted = await CaptureCaseAsync("delete", includeSidecar: true);
        File.Delete(deleted.Sidecar);
        Assert.Throws<InvalidOperationException>(() => deleted.Closure.RequireStable());

        var renamed = await CaptureCaseAsync("rename", includeSidecar: true);
        File.Move(
            renamed.Sidecar,
            Path.Combine(Path.GetDirectoryName(renamed.Sidecar)!, "renamed.json"));
        Assert.Throws<InvalidOperationException>(() => renamed.Closure.RequireStable());

        async Task<(AliApplicationLaunchClosure Closure, string Sidecar)> CaptureCaseAsync(
            string name,
            bool includeSidecar)
        {
            var directory = Path.Combine(fixture.Root, "launch-closure", name);
            Directory.CreateDirectory(directory);
            var artifact = Path.Combine(directory, "App.dll");
            var sidecar = Path.Combine(directory, "App.deps.json");
            await File.WriteAllTextAsync(
                artifact,
                "principal",
                TestContext.Current.CancellationToken);
            if (includeSidecar)
            {
                await File.WriteAllTextAsync(
                    sidecar,
                    "sidecar",
                    TestContext.Current.CancellationToken);
            }
            var principal = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
                artifact,
                "The test application principal");
            return (AliApplicationLaunchClosure.Capture(principal), sidecar);
        }
    }

    [Fact]
    public async Task Application_launch_lease_blocks_principal_and_sidecar_replacement_until_release()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var directory = Path.Combine(fixture.Root, "held-launch-closure");
        Directory.CreateDirectory(directory);
        var artifact = Path.Combine(directory, "App.exe");
        var sidecar = Path.Combine(directory, "App.runtimeconfig.json");
        var substitute = Path.Combine(fixture.Root, "substitute-sidecar.json");
        var displaced = Path.Combine(fixture.Root, "displaced-sidecar.json");
        await File.WriteAllTextAsync(
            artifact,
            "authorized-artifact",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            sidecar,
            "authorized-sidecar",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            substitute,
            "substituted-sidecar",
            TestContext.Current.CancellationToken);
        var principal = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            artifact,
            "The test application principal");
        var closure = AliApplicationLaunchClosure.Capture(principal);
        var replacementBlocked = false;

        using (var lease = AliApplicationLaunchLease.Acquire(principal, closure))
        {
            try
            {
                File.Move(sidecar, displaced);
                File.Move(substitute, sidecar);
            }
            catch (IOException)
            {
                replacementBlocked = true;
            }
            catch (UnauthorizedAccessException)
            {
                replacementBlocked = true;
            }
            lease.RequireStable();
        }

        Assert.True(replacementBlocked);
        Assert.Equal(
            "authorized-sidecar",
            await File.ReadAllTextAsync(sidecar, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(substitute));
        Assert.False(File.Exists(displaced));
    }

    [Fact]
    public async Task ApplicationLaunchClosureRejectsChildReparsePoint()
    {
        await using var fixture = await Fixture.CreateAsync();
        var directory = Path.Combine(fixture.Root, "launch-reparse");
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-launch")).FullName;
        Directory.CreateDirectory(directory);
        var artifact = Path.Combine(directory, "App.dll");
        await File.WriteAllTextAsync(
            artifact,
            "principal",
            TestContext.Current.CancellationToken);
        var principal = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            artifact,
            "The test application principal");
        var link = Path.Combine(directory, "linked-output");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Assert.Skip("Directory symbolic-link creation is unavailable: " + exception.Message);
        }

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                AliApplicationLaunchClosure.Capture(principal));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task ApplicationLaunchClosureRejectsUnapprovedHardLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var directory = Path.Combine(fixture.Root, "launch-hard-link");
        Directory.CreateDirectory(directory);
        var artifact = Path.Combine(directory, "App.dll");
        var sidecar = Path.Combine(directory, "App.deps.json");
        var alias = Path.Combine(directory, "App.alias.json");
        await File.WriteAllTextAsync(
            artifact,
            "principal",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            sidecar,
            "sidecar",
            TestContext.Current.CancellationToken);
        Assert.True(
            CreateHardLinkW(alias, sidecar, IntPtr.Zero),
            "The adversarial application-output hard link could not be created.");
        var principal = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            artifact,
            "The test application principal");

        var exception = Assert.Throws<InvalidDataException>(() =>
            AliApplicationLaunchClosure.Capture(principal));

        Assert.Contains("hard-link alias", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExactTrackedProcessStateRejectsStartTimeOrExecutableDrift()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var process = Process.GetCurrentProcess();
        var executablePath = process.MainModule?.FileName
            ?? throw new InvalidOperationException("The test process executable is unavailable.");
        var executable = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            executablePath,
            "The test process executable");
        var correct = new AliBoundProcessState(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            executable);
        correct.RequireStable(process);

        var wrongStart = correct with
        {
            StartTimeUtcTicks = correct.StartTimeUtcTicks + 1
        };
        Assert.Throws<InvalidOperationException>(() => wrongStart.RequireStable(process));
        var wrongExecutable = correct with
        {
            Executable = executable with { Identity = "file:sha256:" + new string('0', 64) }
        };
        Assert.Throws<InvalidOperationException>(() => wrongExecutable.RequireStable(process));
    }

    [Fact]
    public async Task ProviderProjectLocalToolBytesEnterTheExactBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = JsonSerializer.SerializeToElement(new
        {
            targetPath = fixture.WebVirtualPath,
            configuration = "Release"
        });
        var before = fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderBuild,
            arguments);

        await File.WriteAllTextAsync(
            fixture.WebToolPath,
            "changed local TypeScript tool bytes",
            TestContext.Current.CancellationToken);
        var after = fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderBuild,
            arguments);

        Assert.NotEqual(before.DomainPreparationDigest, after.DomainPreparationDigest);
        Assert.NotEqual(
            AliCodingInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(after.TargetState));
    }

    [Fact]
    public async Task ProviderPackageManifestIsBoundedBeforeNpmScriptParsing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(fixture.WebToolPath)!, "..", "..", "..", "package.json"),
            new string('x', checked(4 * 1024 * 1024 + 1)),
            TestContext.Current.CancellationToken);

        Assert.Throws<InvalidDataException>(() => fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderBuild,
            JsonSerializer.SerializeToElement(new
            {
                targetPath = fixture.WebVirtualPath,
                configuration = "Release"
            })));
    }

    [Fact]
    public async Task ExternalProviderExecutableByteDriftIsRejectedImmediatelyBeforeLaunch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executablePath = Path.Combine(fixture.Root, "provider-tool.exe");
        await File.WriteAllTextAsync(
            executablePath,
            "authorized-provider-executable",
            TestContext.Current.CancellationToken);
        var executable = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            executablePath,
            "The test provider executable");
        var original = fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderAnalyze,
            fixture.Arguments(AliCapabilityCatalog.CodingAnalyzeProjectName));
        var assets = original.ExecutionAssets.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        assets[executable.PhysicalPath] = executable.Identity;
        var binding = original with { ExecutionAssets = assets };
        using var context = AliCodingInvocationExecutionContext.Enter(binding);

        await File.WriteAllTextAsync(
            executablePath,
            "changed-provider-executable",
            TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() =>
            AliCodingInvocationExecutionContext.ValidateProcessLaunch(
                executablePath,
                []));
    }

    [Fact]
    public async Task ExecutedProviderToolIsLeasedAndUnboundGeneratedCppExecutablesFailClosed()
    {
        Assert.Throws<NotSupportedException>(() =>
            AliCodingInvocationBindingResolver.RequireProviderExecutionCanBeBound(
                AliCodingInvocationKind.ProviderTest,
                "cpp-msvc"));
        Assert.Throws<NotSupportedException>(() =>
            AliCodingInvocationBindingResolver.RequireProviderExecutionCanBeBound(
                AliCodingInvocationKind.ProviderRun,
                "cpp-msvc"));
        AliCodingInvocationBindingResolver.RequireProviderExecutionCanBeBound(
            AliCodingInvocationKind.ProviderBuild,
            "cpp-msvc");

        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderBuild,
            JsonSerializer.SerializeToElement(new
            {
                targetPath = fixture.WebVirtualPath,
                configuration = "Release"
            }));
        using var context = AliCodingInvocationExecutionContext.Enter(binding);
        using var leases = AliCodingInvocationExecutionContext.AcquireExecutedAssetLeases(
            [fixture.WebToolPath]);
        var displaced = fixture.WebToolPath + ".displaced";

        Assert.ThrowsAny<IOException>(() => File.Move(fixture.WebToolPath, displaced));
        leases.RequireStable();
        Assert.True(File.Exists(fixture.WebToolPath));
        Assert.False(File.Exists(displaced));
    }

    [Fact]
    public async Task GeneratedAliOutputLayoutDriftEntersTargetBindingWithoutContentHashing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = fixture.Arguments(AliCapabilityCatalog.CodingAnalyzeProjectName);
        var before = fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderAnalyze,
            arguments);
        var output = Path.Combine(fixture.ProjectDirectory, ".ali", "receipts", "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(
            output,
            new string('x', 1024),
            TestContext.Current.CancellationToken);
        var after = fixture.Bindings.Resolve(
            AliCodingInvocationKind.ProviderAnalyze,
            arguments);

        Assert.Equal(before.DomainPreparationDigest, after.DomainPreparationDigest);
        Assert.NotEqual(
            AliCodingInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(after.TargetState));
    }

    [Fact]
    public async Task GeneratedAliChildReparseIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var aliDirectory = Path.Combine(fixture.ProjectDirectory, ".ali");
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-output")).FullName;
        Directory.CreateDirectory(aliDirectory);
        var link = Path.Combine(aliDirectory, "linked-output");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Assert.Skip("Directory symbolic-link creation is unavailable: " + exception.Message);
        }

        try
        {
            Assert.Throws<InvalidDataException>(() => fixture.Bindings.Resolve(
                AliCodingInvocationKind.ProviderAnalyze,
                fixture.Arguments(AliCapabilityCatalog.CodingAnalyzeProjectName)));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task ReferencedProjectOutsideSelectedRootIsNotEvaluatedBeforeIntent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var libraryDirectory = Path.Combine(fixture.Workspace, "Library");
        Directory.CreateDirectory(libraryDirectory);
        var libraryProject = Path.Combine(libraryDirectory, "Library.csproj");
        var librarySource = Path.Combine(libraryDirectory, "Library.cs");
        await File.WriteAllTextAsync(
            libraryProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            librarySource,
            "public sealed class LibraryType { }",
            TestContext.Current.CancellationToken);
        await fixture.WriteProjectAsync(
            $"<ItemGroup><ProjectReference Include=\"{Path.GetRelativePath(fixture.ProjectDirectory, libraryProject)}\" /></ItemGroup>");
        var arguments = fixture.Arguments(AliCapabilityCatalog.DotNetBuildName);
        var before = fixture.Bindings.Resolve(AliCodingInvocationKind.DotNetBuild, arguments);

        await File.AppendAllTextAsync(
            librarySource,
            Environment.NewLine + "// referenced project drift",
            TestContext.Current.CancellationToken);
        var after = fixture.Bindings.Resolve(AliCodingInvocationKind.DotNetBuild, arguments);

        Assert.Equal(before.DomainPreparationDigest, after.DomainPreparationDigest);
        Assert.Equal(before.RootBinding, after.RootBinding);
        Assert.Equal(
            AliCodingInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(after.TargetState));
        Assert.DoesNotContain(
            after.TargetState.TargetVersions,
            item => item.Key.StartsWith(
                "coding-execution:dotnet.graph.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferencedProjectOutsideMountDoesNotExpandPreIntentRootBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var outsideDirectory = Path.Combine(fixture.Root, "outside-project");
        Directory.CreateDirectory(outsideDirectory);
        var outsideProject = Path.Combine(outsideDirectory, "Outside.csproj");
        await File.WriteAllTextAsync(
            outsideProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await fixture.WriteProjectAsync(
            $"<ItemGroup><ProjectReference Include=\"{Path.GetRelativePath(fixture.ProjectDirectory, outsideProject)}\" /></ItemGroup>");

        var before = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            fixture.Arguments(AliCapabilityCatalog.DotNetBuildName));
        await File.AppendAllTextAsync(
            outsideProject,
            Environment.NewLine + "<!-- outside drift -->",
            TestContext.Current.CancellationToken);
        var after = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            fixture.Arguments(AliCapabilityCatalog.DotNetBuildName));

        Assert.Equal(before.DomainPreparationDigest, after.DomainPreparationDigest);
        Assert.Equal(before.RootBinding, after.RootBinding);
    }

    [Fact]
    public async Task ParentAndExternalImportsAreNotEvaluatedBeforeIntent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = fixture.Arguments(AliCapabilityCatalog.DotNetBuildName);
        var before = fixture.Bindings.Resolve(AliCodingInvocationKind.DotNetBuild, arguments);
        await File.WriteAllTextAsync(
            fixture.WorkspacePropsPath,
            "<Project><PropertyGroup><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets><BoundValue>changed</BoundValue></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        var after = fixture.Bindings.Resolve(AliCodingInvocationKind.DotNetBuild, arguments);
        Assert.Equal(before.DomainPreparationDigest, after.DomainPreparationDigest);
        Assert.Equal(
            AliCodingInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(after.TargetState));

        var outsideImport = Path.Combine(fixture.Root, "outside.targets");
        await File.WriteAllTextAsync(
            outsideImport,
            "<Project><PropertyGroup><Outside>true</Outside></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await fixture.WriteProjectAsync(
            $"<Import Project=\"{Path.GetRelativePath(fixture.ProjectDirectory, outsideImport)}\" />");
        var withExternalImport = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            arguments);

        Assert.Equal(after.DomainPreparationDigest, withExternalImport.DomainPreparationDigest);
        Assert.NotEqual(
            AliCodingInvocationBindingResolver.TargetVersionDigest(after.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(withExternalImport.TargetState));
        await File.AppendAllTextAsync(
            outsideImport,
            Environment.NewLine + "<!-- outside import drift -->",
            TestContext.Current.CancellationToken);
        var afterExternalDrift = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            arguments);
        Assert.Equal(
            withExternalImport.DomainPreparationDigest,
            afterExternalDrift.DomainPreparationDigest);
        Assert.Equal(
            AliCodingInvocationBindingResolver.TargetVersionDigest(withExternalImport.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(afterExternalDrift.TargetState));
    }

    [Fact]
    public async Task DeclaredOutputRootDoesNotExpandPreIntentRootBinding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var arguments = fixture.Arguments(AliCapabilityCatalog.DotNetBuildName);
        var before = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            arguments);
        var outsideOutput = Path.Combine(fixture.Root, "outside-build-output");
        await fixture.WriteProjectAsync(
            $"<PropertyGroup><BaseOutputPath>{Path.GetRelativePath(fixture.ProjectDirectory, outsideOutput)}\\</BaseOutputPath></PropertyGroup>");

        var after = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            arguments);

        Assert.Equal(before.DomainPreparationDigest, after.DomainPreparationDigest);
        Assert.NotEqual(
            AliCodingInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliCodingInvocationBindingResolver.TargetVersionDigest(after.TargetState));
        Assert.Equal(before.RootBinding, after.RootBinding);
    }

    [Fact]
    public async Task MissingSdkIsCapturedWithoutPreIntentProjectEvaluation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.ProjectPath,
            "<Project Sdk=\"Ali.Intentionally.Missing.Sdk/1.0.0\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);

        var binding = fixture.Bindings.Resolve(
            AliCodingInvocationKind.DotNetBuild,
            fixture.Arguments(AliCapabilityCatalog.DotNetBuildName));

        Assert.Matches("^[0-9a-f]{64}$", binding.DomainPreparationDigest);
        Assert.DoesNotContain(
            binding.TargetState.TargetVersions,
            item => item.Key.StartsWith(
                "coding-execution:dotnet.graph.",
                StringComparison.Ordinal));
    }

    private static AliExecutionPreparationRequest Request(
        AliCodingProcessExecutionAdapter adapter,
        JsonElement arguments,
        string targetVersionDigest)
    {
        var suffix = adapter.ToolName;
        return new AliExecutionPreparationRequest(
            new TurnIdentity(
                "test-user",
                "coding-process-adapter-" + suffix,
                "assistant-message"),
            "call-" + suffix,
            "work-" + suffix,
            adapter.ToolName,
            adapter.CapabilityId,
            adapter.ReconcilerId,
            arguments.Clone(),
            ArgumentsDigest(arguments),
            Digest("action-" + suffix),
            targetVersionDigest,
            Digest("permission-" + suffix),
            Digest("registry-revision-" + suffix),
            Digest("registry-identity-" + suffix));
    }

    private static PreparedActionIntent Intent(
        AliExecutionPreparationRequest request,
        AliExecutionPreparation prepared) =>
        new(
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
            prepared.RootBinding,
            RequiresApproval: true,
            request.CallId,
            prepared.PreparationIdentity);

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

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            string durableRoot,
            string workspace,
            string projectPath,
            string programPath,
            string workspacePropsPath,
            string webToolPath,
            AliRoslynCodingTools tools,
            AliCodingInvocationBindingResolver bindings,
            AliCodingProcessExecutionCoordinator coordinator,
            AliLanguageProviderRegistry providers)
        {
            Root = root;
            Workspace = workspace;
            ProjectPath = projectPath;
            ProgramPath = programPath;
            WorkspacePropsPath = workspacePropsPath;
            WebToolPath = webToolPath;
            Tools = tools;
            Bindings = bindings;
            Coordinator = coordinator;
            Providers = providers;
            Store = new AliDurableInvocationStore(
                Path.Combine(durableRoot, "Coding", "Invocations"),
                "coding-process-test-profile");
        }

        internal string Root { get; }

        internal string Workspace { get; }

        internal string ProjectVirtualPath => "Workspace/App/App.csproj";

        internal string WebVirtualPath => "Workspace/Web/package.json";

        internal string ProjectPath { get; }

        internal string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

        internal string ProgramPath { get; }

        internal string WorkspacePropsPath { get; }

        internal string WebToolPath { get; }

        internal AliRoslynCodingTools Tools { get; }

        internal AliCodingInvocationBindingResolver Bindings { get; }

        internal AliCodingProcessExecutionCoordinator Coordinator { get; }

        internal AliLanguageProviderRegistry Providers { get; }

        internal AliDurableInvocationStore Store { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                TestRepository.Root,
                "bin",
                "AliCodingProcessExecutionAdapterTests",
                Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var projectDirectory = Path.Combine(workspace, "App");
            Directory.CreateDirectory(projectDirectory);
            var workspacePropsPath = Path.Combine(workspace, "Directory.Build.props");
            await File.WriteAllTextAsync(
                workspacePropsPath,
                "<Project><PropertyGroup><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            var programPath = Path.Combine(projectDirectory, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                programPath,
                "Console.WriteLine(\"Ali\");",
                TestContext.Current.CancellationToken);
            var webDirectory = Path.Combine(workspace, "Web");
            var webToolPath = Path.Combine(
                webDirectory,
                "node_modules",
                "typescript",
                "bin",
                "tsc");
            Directory.CreateDirectory(Path.GetDirectoryName(webToolPath)!);
            await File.WriteAllTextAsync(
                Path.Combine(webDirectory, "package.json"),
                "{\"scripts\":{\"build\":\"tsc\",\"test\":\"node --test\",\"start\":\"node index.js\"}}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                webToolPath,
                "authorized local TypeScript tool bytes",
                TestContext.Current.CancellationToken);

            var permissions = new AgentToolPermissionStore(root);
            var fileStore = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", workspace)],
                Path.Combine(root, "trash"));
            var access = new AliWorkstationFileAccess(
                fileStore,
                new AgentFileActionAuditStore(root, activeUsers: null),
                permissions);
            var durableRoot = Path.Combine(root, "durable");
            var auditPath = Path.Combine(root, "dotnet-actions.jsonl");
            var tracker = new AliCodingProjectTracker();
            var dotNetResolver = new AliCodingProjectResolver(access);
            var tools = new AliRoslynCodingTools(dotNetResolver, tracker, auditPath);
            var engineering = new AliDotNetEngineeringLoop(dotNetResolver);
            var languageResolver = new AliLanguageProjectResolver(access);
            var providers = new AliLanguageProviderRegistry();
            providers.Register(new AliDotNetLanguageProvider(
                tools,
                engineering,
                new AliToolchainLocator()));
            providers.Register(new AliWebLanguageProvider(new AliToolchainLocator()));
            var bindings = new AliCodingInvocationBindingResolver(
                access,
                dotNetResolver,
                languageResolver,
                providers,
                tools);
            var store = new AliDurableInvocationStore(
                Path.Combine(durableRoot, "Coding", "Invocations"),
                "coding-process-test-profile");
            var coordinator = new AliCodingProcessExecutionCoordinator(
                bindings,
                store,
                new EvidenceLedger(durableRoot, "coding-process-test-profile"));
            return new Fixture(
                root,
                durableRoot,
                workspace,
                projectPath,
                programPath,
                workspacePropsPath,
                webToolPath,
                tools,
                bindings,
                coordinator,
                providers);
        }

        internal Task WriteProjectAsync(string additionalXml) =>
            File.WriteAllTextAsync(
                ProjectPath,
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup>{additionalXml}</Project>",
                TestContext.Current.CancellationToken);

        internal JsonElement Arguments(string toolName) => toolName switch
        {
            AliCapabilityCatalog.CodingAnalyzeProjectName
                or AliCapabilityCatalog.CodingFormatProjectName =>
                JsonSerializer.SerializeToElement(new { targetPath = ProjectVirtualPath }),
            AliCapabilityCatalog.CodingBuildProjectName
                or AliCapabilityCatalog.CodingTestProjectName
                or AliCapabilityCatalog.CodingRunProjectName =>
                JsonSerializer.SerializeToElement(new
                {
                    targetPath = ProjectVirtualPath,
                    configuration = "Release"
                }),
            AliCapabilityCatalog.DotNetCreateProjectName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = "Workspace/NewApp/NewApp.csproj",
                    template = "console"
                }),
            AliCapabilityCatalog.RoslynFormatProjectName
                or AliCapabilityCatalog.DotNetDependencyInspectName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = ProjectVirtualPath
                }),
            AliCapabilityCatalog.DotNetBuildName
                or AliCapabilityCatalog.DotNetRunName
                or AliCapabilityCatalog.DotNetStopProjectName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = ProjectVirtualPath,
                    configuration = "Release"
                }),
            AliCapabilityCatalog.DotNetTestName
                or AliCapabilityCatalog.DotNetVerifyName =>
                JsonSerializer.SerializeToElement(new
                {
                    targetPath = ProjectVirtualPath,
                    configuration = "Release"
                }),
            AliCapabilityCatalog.DotNetDependencyApplyName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = ProjectVirtualPath,
                    action = "add",
                    packageId = "Example.Package",
                    version = "1.2.3"
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName))
        };

        public async ValueTask DisposeAsync()
        {
            foreach (var provider in Providers.Providers)
            {
                await provider.DisposeAsync();
            }
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A failed cleanup must not obscure the adapter assertion that already ran.
            }
        }
    }
}
