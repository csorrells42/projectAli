using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Delivery;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Quality;
using Ali.Modules.Coding.Release;
using Ali.Modules.Coding.Verification;
using Ali.Modules.Coordinator;
using Ali.Modules.DevOpsExecution;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Execution;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class AliDevOpsExecutionAdapterTests
{
    private static readonly string[] ExactToolNames =
    [
        AliCapabilityCatalog.ArchitectureInspectName,
        AliCapabilityCatalog.ArchitectureCheckName,
        AliCapabilityCatalog.DotNetQualityScanName,
        AliCapabilityCatalog.DotNetApplicationVerifyName,
        AliCapabilityCatalog.DotNetReleasePublishName,
        AliCapabilityCatalog.DotNetDeliveryVerifyName
    ];

    [Fact]
    public async Task SelectedDevOpsTargetRootReplacementIsBlockedOrDetectedByItsExecutionLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        await using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.QualityScan,
            fixture.Arguments(AliCapabilityCatalog.DotNetQualityScanName));
        using var leases = AliDevOpsTargetRootLeaseGroup.Acquire(
            binding.TargetRootIdentities);
        var targetRoot = Assert.Single(binding.TargetRootIdentities).TargetPath;
        var displaced = targetRoot + ".displaced";

        try
        {
            Directory.Move(targetRoot, displaced);
            Assert.ThrowsAny<IOException>(() => leases.RequireStable());
            Assert.False(Directory.Exists(targetRoot));
            Assert.True(Directory.Exists(displaced));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Physical blocking is acceptable; otherwise the lease must detect the moved root.
        }
        finally
        {
            if (Directory.Exists(displaced) && !Directory.Exists(targetRoot))
            {
                Directory.Move(displaced, targetRoot);
            }
        }

        Assert.True(Directory.Exists(targetRoot));
        Assert.False(Directory.Exists(displaced));
        leases.RequireStable();
    }

    [Fact]
    public async Task CoordinatorRegistersOnlyTheSixExactProductionTuples()
    {
        await using var fixture = await Fixture.CreateAsync();

        var adapters = fixture.Coordinator.Adapters
            .Cast<AliDevOpsExecutionAdapter>()
            .ToArray();
        Assert.Equal(ExactToolNames.Length, adapters.Length);
        Assert.Equal(
            ExactToolNames.Order(StringComparer.Ordinal),
            adapters.Select(adapter => adapter.ToolName).Order(StringComparer.Ordinal));
        Assert.Equal(
            ExactToolNames.Order(StringComparer.Ordinal),
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
            Assert.Equal("ali.tool." + adapter.ToolName, adapter.CapabilityId);
            Assert.Equal("ali.reconcile." + adapter.ToolName, adapter.ReconcilerId);
            Assert.DoesNotContain('*', adapter.ToolName);
            Assert.DoesNotContain('*', adapter.CapabilityId);
            Assert.DoesNotContain('*', adapter.ReconcilerId);
        }
    }

    [Fact]
    public async Task EveryExactAdapterPreparesBoundRootArgumentsOperationAndTargetIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preparationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var adapter in fixture.Coordinator.Adapters
                     .Cast<AliDevOpsExecutionAdapter>())
        {
            var arguments = fixture.Arguments(adapter.ToolName);
            var target = fixture.Coordinator.TargetStates.Capture(
                adapter.ToolName,
                arguments);
            var targetDigest = AliDevOpsInvocationBindingResolver.TargetVersionDigest(target);
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
                AliDevOpsInvocationCatalog.OperationIdentity(adapter.Kind),
                plan.DomainPreparationIdentity);
            Assert.Matches("^[0-9A-Fa-f]{64}$", plan.DomainPreparationDigest);

            var recovered = await adapter.ReconcileAsync(
                request.TurnIdentity,
                Intent(request, prepared),
                TestContext.Current.CancellationToken);
            Assert.Equal(ActionReconciliationDisposition.Absent, recovered.Disposition);
            Assert.Equal("invocation-prepared-not-started", recovered.OutcomeCode);
        }
    }

    [Fact]
    public async Task ExactGrantProducesTerminalReceiptAndAppliedRecoveryEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = fixture.Adapter(AliCapabilityCatalog.ArchitectureInspectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var grant = Grant(intent);
        var expectedAuthorizationDigest = AliExecutionAuthorizationDigest.Compute(
            AliDurableInvocationStore.AuthorizationDomain,
            grant);
        var returned = new ArchitectureInspectionResult(
            true,
            "Architecture inspected.",
            [],
            [],
            [],
            []);

        await using (var activation = new AliExecutionInvocationScope(grant)
                         .Enter(functionArguments))
        {
            var result = await fixture.Coordinator.ExecuteArchitectureInspectAsync(
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
            "architecture-inspect-returned-success",
            completed.Receipt.StableOutcomeCode);
        Assert.Equal(expectedAuthorizationDigest, completed.Receipt.AuthorizationDigest);
        Assert.Matches("^[0-9a-f]{64}$", completed.Receipt.ResultDigest!);

        var reconciled = await adapter.ReconcileAsync(
            request.TurnIdentity,
            intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
        Assert.Equal(completed.Receipt.StableOutcomeCode, reconciled.OutcomeCode);
        Assert.NotNull(reconciled.AppliedEvidence);
    }

    [Fact]
    public async Task ReturnedFailureRemainsInDoubtAcrossRecovery()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = fixture.Adapter(AliCapabilityCatalog.ArchitectureInspectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var returned = new ArchitectureInspectionResult(
            false,
            "Architecture inspection failed.",
            [],
            [],
            [],
            []);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var result = await fixture.Coordinator.ExecuteArchitectureInspectAsync(
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
            "architecture-inspect-returned-failure",
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
    public async Task SourceChangeAfterPrepareFailsBeforeExecutorRuns()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = fixture.Adapter(AliCapabilityCatalog.ArchitectureInspectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var executorCalls = 0;

        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// changed after DevOps prepare",
            TestContext.Current.CancellationToken);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.ExecuteArchitectureInspectAsync(
                    fixture.ProjectVirtualPath,
                    _ =>
                    {
                        Interlocked.Increment(ref executorCalls);
                        return Task.FromResult(new ArchitectureInspectionResult(
                            true,
                            "unexpected",
                            [],
                            [],
                            [],
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
            "devops-binding-revalidation-failed",
            failed.Receipt!.FailureCode);
    }

    [Fact]
    public async Task ApplicationArtifactChangeAfterPrepareFailsBeforeExecutorRuns()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = fixture.Adapter(AliCapabilityCatalog.DotNetApplicationVerifyName);
        var functionArguments = new AIFunctionArguments
        {
            ["projectPath"] = fixture.ProjectVirtualPath,
            ["configuration"] = "Release",
            ["healthUrl"] = null
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var executorCalls = 0;
        var artifact = Path.Combine(
            Path.GetDirectoryName(fixture.ProgramPath)!,
            "bin",
            "Release",
            "net10.0",
            "App.dll");
        await File.WriteAllBytesAsync(
            artifact,
            Encoding.UTF8.GetBytes("changed-after-prepare"),
            TestContext.Current.CancellationToken);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.ExecuteApplicationVerifyAsync(
                    fixture.ProjectVirtualPath,
                    "Release",
                    null,
                    _ =>
                    {
                        Interlocked.Increment(ref executorCalls);
                        return Task.FromResult(new ApplicationVerificationResult(
                            true,
                            "unexpected",
                            fixture.ProjectVirtualPath,
                            "console",
                            0,
                            null,
                            string.Empty,
                            null,
                            false,
                            0));
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
            "devops-binding-revalidation-failed",
            failed.Receipt!.FailureCode);
    }

    [Fact]
    public async Task ApplicationSidecarChangeAfterPrepareFailsBeforeExecutorRuns()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sidecar = Path.Combine(
            Path.GetDirectoryName(fixture.ApplicationArtifactPath)!,
            "App.runtimeconfig.json");
        await File.WriteAllTextAsync(
            sidecar,
            "authorized-sidecar",
            TestContext.Current.CancellationToken);
        var adapter = fixture.Adapter(AliCapabilityCatalog.DotNetApplicationVerifyName);
        var functionArguments = new AIFunctionArguments
        {
            ["projectPath"] = fixture.ProjectVirtualPath,
            ["configuration"] = "Release",
            ["healthUrl"] = null
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var executorCalls = 0;

        await File.WriteAllTextAsync(
            sidecar,
            "changed-after-prepare",
            TestContext.Current.CancellationToken);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Coordinator.ExecuteApplicationVerifyAsync(
                    fixture.ProjectVirtualPath,
                    "Release",
                    null,
                    _ =>
                    {
                        Interlocked.Increment(ref executorCalls);
                        return Task.FromResult(new ApplicationVerificationResult(
                            true,
                            "unexpected",
                            fixture.ProjectVirtualPath,
                            "console",
                            0,
                            null,
                            string.Empty,
                            null,
                            false,
                            0));
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
            "devops-binding-revalidation-failed",
            failed.Receipt!.FailureCode);
    }

    [Fact]
    public async Task DeliveryApplicationVerificationPreparesWithoutAPreexistingArtifact()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.Delete(fixture.ApplicationArtifactPath);
        var arguments = fixture.DeliveryArguments(verifyApplication: true);

        var binding = fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.DeliveryVerify,
            arguments);

        Assert.Null(binding.ProcessBinding.ApplicationArtifact);
        var policy = Assert.IsType<AliPostBuildApplicationArtifactPolicy>(
            binding.ProcessBinding.PostBuildApplicationArtifact);
        Assert.Equal(
            Path.GetDirectoryName(fixture.ApplicationArtifactPath),
            policy.AuthorizedOutputRoot,
            PathComparer);
        Assert.Contains(fixture.ApplicationArtifactPath, policy.CandidateArtifactPaths, PathComparer);

        var adapter = fixture.Adapter(AliCapabilityCatalog.DotNetDeliveryVerifyName);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(prepared.PreparationIdentity));

        Assert.Throws<FileNotFoundException>(() => fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.ApplicationVerify,
            JsonSerializer.SerializeToElement(new
            {
                projectPath = fixture.ProjectVirtualPath,
                configuration = "Release",
                healthUrl = (string?)null
            })));
    }

    [Fact]
    public async Task DeliveryCapturesTheAuthorizedPostBuildArtifactForApplicationVerification()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.Delete(fixture.ApplicationArtifactPath);
        var binding = fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.DeliveryVerify,
            fixture.DeliveryArguments(verifyApplication: true));
        await File.WriteAllBytesAsync(
            fixture.ApplicationArtifactPath,
            Encoding.UTF8.GetBytes("post-build-artifact"),
            TestContext.Current.CancellationToken);

        var derived = binding.ProcessBinding.BindPostBuildApplicationArtifact(
            fixture.ProjectVirtualPath,
            "Release");
        Assert.NotNull(derived.ApplicationLaunchClosure);
        var project = fixture.Resolver.ResolveExistingProject(fixture.ProjectVirtualPath);
        var selected = AliApplicationVerification.ResolveApplicationArtifactForLaunch(
            project,
            "Release",
            derived);

        Assert.Equal(fixture.ApplicationArtifactPath, selected, PathComparer);
        var sidecar = Path.Combine(
            Path.GetDirectoryName(fixture.ApplicationArtifactPath)!,
            "App.runtimeconfig.json");
        await File.WriteAllTextAsync(
            sidecar,
            "appeared-after-post-build-capture",
            TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() =>
            AliApplicationVerification.ResolveApplicationArtifactForLaunch(
                project,
                "Release",
                derived));
        File.Delete(sidecar);
        Assert.Equal(
            fixture.ApplicationArtifactPath,
            AliApplicationVerification.ResolveApplicationArtifactForLaunch(
                project,
                "Release",
                derived),
            PathComparer);
        await File.WriteAllBytesAsync(
            fixture.ApplicationArtifactPath,
            Encoding.UTF8.GetBytes("drifted-after-post-build-capture"),
            TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() =>
            AliApplicationVerification.ResolveApplicationArtifactForLaunch(
                project,
                "Release",
                derived));
    }

    [Fact]
    public async Task PostBuildArtifactPolicyRejectsAnArtifactOutsideItsAuthorizedOutputRoot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var outputRoot = Path.GetDirectoryName(fixture.ApplicationArtifactPath)!;
        var escapedArtifact = Path.Combine(fixture.Root, "escaped", "App.dll");

        var exception = Assert.Throws<InvalidDataException>(() =>
            AliPostBuildApplicationArtifactPolicy.Create(
                fixture.ProjectVirtualPath,
                fixture.ProjectPhysicalPath,
                "Release",
                outputRoot,
                [escapedArtifact]));

        Assert.Contains("escapes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QualityScanReadsOnlyTheSameBoundedSourceTreeAsItsTargetFingerprint()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectDirectory = Path.GetDirectoryName(fixture.ProgramPath)!;
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, ".editorconfig"),
            "root = true",
            TestContext.Current.CancellationToken);

        foreach (var excluded in new[]
                 {
                     ".ali",
                     "node_modules",
                     "artifacts",
                     "release",
                     "TestResults"
                 })
        {
            var directory = Path.Combine(projectDirectory, excluded);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Excluded.cs"),
                "internal static class Excluded { }",
                TestContext.Current.CancellationToken);
        }

        var withoutProjectConfig = AliQualityEngineering.CaptureSourceInputs(projectDirectory);
        Assert.False(withoutProjectConfig.EditorConfigPresent);
        Assert.DoesNotContain(
            withoutProjectConfig.Files,
            path => new[]
            {
                ".ali",
                "node_modules",
                "artifacts",
                "release",
                "TestResults"
            }.Any(directory => path.Contains(
                Path.DirectorySeparatorChar + directory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)));

        var projectEditorConfig = Path.Combine(projectDirectory, ".editorconfig");
        var includedProps = Path.Combine(projectDirectory, "Directory.Build.props");
        await File.WriteAllTextAsync(
            projectEditorConfig,
            "root = true",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            includedProps,
            "<Project />",
            TestContext.Current.CancellationToken);

        var withProjectConfig = AliQualityEngineering.CaptureSourceInputs(projectDirectory);
        Assert.True(withProjectConfig.EditorConfigPresent);
        Assert.Contains(includedProps, withProjectConfig.Files, PathComparer);
        Assert.DoesNotContain(projectEditorConfig, withProjectConfig.Files, PathComparer);
    }

    [Fact]
    public async Task PreIntentCaptureDoesNotEvaluateOrBindDeclaredExternalProjectInputs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectDirectory = Path.GetDirectoryName(fixture.ProgramPath)!;
        var workspace = Directory.GetParent(projectDirectory)!.FullName;
        var importDirectory = Path.Combine(workspace, "Imported");
        var libraryDirectory = Path.Combine(workspace, "Library");
        var declaredOutputDirectory = Path.Combine(workspace, "DeclaredOutput");
        Directory.CreateDirectory(importDirectory);
        Directory.CreateDirectory(libraryDirectory);
        Directory.CreateDirectory(declaredOutputDirectory);
        var importedProps = Path.Combine(importDirectory, "Imported.props");
        var libraryProject = Path.Combine(libraryDirectory, "Library.csproj");
        var librarySource = Path.Combine(libraryDirectory, "Library.cs");
        var declaredOutput = Path.Combine(declaredOutputDirectory, "App.dll");
        await File.WriteAllTextAsync(
            importedProps,
            "<Project><PropertyGroup><ImportedValue>one</ImportedValue></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            libraryProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            librarySource,
            "namespace Library; public sealed class Helper { }",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Ali.Definitely.Missing.Sdk/999.0.0\"><Import Project=\"..\\Imported\\Imported.props\" /><PropertyGroup><TargetFramework>net10.0</TargetFramework><BaseOutputPath>..\\DeclaredOutput\\</BaseOutputPath></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Library\\Library.csproj\" /></ItemGroup></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            declaredOutput,
            Encoding.UTF8.GetBytes("declared-output-one"),
            TestContext.Current.CancellationToken);
        var arguments = fixture.Arguments(AliCapabilityCatalog.DotNetQualityScanName);

        var before = fixture.Bindings.Resolve(AliDevOpsInvocationKind.QualityScan, arguments);
        Assert.DoesNotContain(
            before.TargetState.TargetVersions.Keys,
            key => key.Contains("msbuild", StringComparison.OrdinalIgnoreCase));
        await File.AppendAllTextAsync(
            importedProps,
            Environment.NewLine + "<!-- imported drift -->",
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            librarySource,
            Environment.NewLine + "// referenced-project drift",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            declaredOutput,
            Encoding.UTF8.GetBytes("declared-output-two"),
            TestContext.Current.CancellationToken);
        var afterExternalDrift = fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.QualityScan,
            arguments);

        Assert.Equal(before.RootBinding, afterExternalDrift.RootBinding);
        Assert.Equal(
            AliDevOpsInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliDevOpsInvocationBindingResolver.TargetVersionDigest(
                afterExternalDrift.TargetState));

        await File.AppendAllTextAsync(
            fixture.ProgramPath,
            Environment.NewLine + "// selected-root drift",
            TestContext.Current.CancellationToken);
        var afterSelectedRootDrift = fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.QualityScan,
            arguments);
        Assert.NotEqual(
            AliDevOpsInvocationBindingResolver.TargetVersionDigest(before.TargetState),
            AliDevOpsInvocationBindingResolver.TargetVersionDigest(
                afterSelectedRootDrift.TargetState));
    }

    [Fact]
    public async Task DeliveryPreIntentPolicyDoesNotEvaluateOrAuthorizeDeclaredOutputExpansion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectDirectory = Path.GetDirectoryName(fixture.ProgramPath)!;
        var declaredOutputRoot = Path.Combine(fixture.Root, "declared-output");
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Ali.Definitely.Missing.Sdk/999.0.0\"><Import Project=\"..\\missing\\Never.props\" /><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>App</AssemblyName><BaseOutputPath>"
            + declaredOutputRoot
            + "</BaseOutputPath><TargetPath>"
            + Path.Combine(declaredOutputRoot, "Escaped.dll")
            + "</TargetPath></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);

        var binding = fixture.Bindings.Resolve(
            AliDevOpsInvocationKind.DeliveryVerify,
            fixture.DeliveryArguments(verifyApplication: true));
        var policy = Assert.IsType<AliPostBuildApplicationArtifactPolicy>(
            binding.ProcessBinding.PostBuildApplicationArtifact);

        Assert.Equal(
            Path.GetDirectoryName(fixture.ApplicationArtifactPath),
            policy.AuthorizedOutputRoot,
            PathComparer);
        Assert.Contains(fixture.ApplicationArtifactPath, policy.CandidateArtifactPaths, PathComparer);
        Assert.DoesNotContain(
            policy.CandidateArtifactPaths,
            candidate => candidate.StartsWith(
                declaredOutputRoot,
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            binding.TargetState.TargetVersions.Keys,
            key => key.Contains("msbuild", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedAliChildReparseIsRejectedBeforeDevOpsGrantCreation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var projectDirectory = Path.GetDirectoryName(fixture.ProgramPath)!;
        var aliDirectory = Path.Combine(projectDirectory, ".ali");
        var outside = Directory.CreateDirectory(
            Path.Combine(fixture.Root, "outside-generated-output")).FullName;
        Directory.CreateDirectory(aliDirectory);
        var link = Path.Combine(aliDirectory, "quality");
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
                fixture.Coordinator.TargetStates.Capture(
                    AliCapabilityCatalog.DotNetQualityScanName,
                    fixture.Arguments(AliCapabilityCatalog.DotNetQualityScanName)));
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
    public async Task InvocationWithoutExactGrantFailsClosedBeforeExecutorRuns()
    {
        await using var fixture = await Fixture.CreateAsync();
        var executorCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.ExecuteArchitectureInspectAsync(
                fixture.ProjectVirtualPath,
                _ =>
                {
                    Interlocked.Increment(ref executorCalls);
                    return Task.FromResult(new ArchitectureInspectionResult(
                        true,
                        "unexpected",
                        [],
                        [],
                        [],
                        []));
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(0, executorCalls);
    }

    [Fact]
    public async Task CallerCancellationFlowsToExecutorAndRecordsCanceledReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = fixture.Adapter(AliCapabilityCatalog.ArchitectureInspectName);
        var functionArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.ProjectVirtualPath
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        using var callerCancellation = new CancellationTokenSource();

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Coordinator.ExecuteArchitectureInspectAsync(
                    fixture.ProjectVirtualPath,
                    async token =>
                    {
                        callerCancellation.Cancel();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return new ArchitectureInspectionResult(
                            true,
                            "unreachable",
                            [],
                            [],
                            [],
                            []);
                    },
                    callerCancellation.Token));
            await activation.FailAsync(exception, CancellationToken.None);
        }

        var failed = await fixture.Store.LoadAsync(
            prepared.PreparationIdentity,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.Failed, failed.State);
        Assert.Equal("devops-invocation-canceled", failed.Receipt!.FailureCode);
    }

    [Fact]
    public async Task OversizedTypedOutputIsMarkedInDoubtInsteadOfCompleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var adapter = fixture.Adapter(AliCapabilityCatalog.DotNetApplicationVerifyName);
        var functionArguments = new AIFunctionArguments
        {
            ["projectPath"] = fixture.ProjectVirtualPath,
            ["configuration"] = "Release",
            ["healthUrl"] = null
        };
        var arguments = JsonSerializer.SerializeToElement(functionArguments);
        var request = fixture.Request(adapter, arguments);
        var prepared = await adapter.PrepareAsync(
            request,
            TestContext.Current.CancellationToken);
        var intent = Intent(request, prepared);
        var returned = new ApplicationVerificationResult(
            true,
            "Smoke passed.",
            fixture.ProjectVirtualPath,
            "console-or-service",
            0,
            123,
            new string('x', 1_000_001),
            null,
            false,
            10);

        await using (var activation = new AliExecutionInvocationScope(Grant(intent))
                         .Enter(functionArguments))
        {
            var result = await fixture.Coordinator.ExecuteApplicationVerifyAsync(
                fixture.ProjectVirtualPath,
                "Release",
                null,
                _ => Task.FromResult(returned),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<AliDevOpsResultContractException>(async () =>
                await activation.CompleteAsync(result, CancellationToken.None));
        }

        var inDoubt = await fixture.Store.LoadAsync(
            prepared.PreparationIdentity,
            TestContext.Current.CancellationToken);
        Assert.Equal(AliDurableInvocationState.InDoubt, inDoubt.State);
        Assert.Equal(
            "devops-result-contract-unproven",
            inDoubt.Receipt!.FailureCode);
        var reconciled = await adapter.ReconcileAsync(
            request.TurnIdentity,
            intent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
    }

    private static AliExecutionPreparationRequest Request(
        AliDevOpsExecutionAdapter adapter,
        JsonElement arguments,
        string targetVersionDigest)
    {
        var suffix = adapter.ToolName;
        return new AliExecutionPreparationRequest(
            new TurnIdentity(
                "test-user",
                "devops-execution-adapter-" + suffix,
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

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            string programPath,
            string projectVirtualPath,
            AliCodingProjectResolver resolver,
            AliDevOpsInvocationBindingResolver bindings,
            AliDurableInvocationStore store,
            AliDevOpsExecutionCoordinator coordinator)
        {
            Root = root;
            ProgramPath = programPath;
            ProjectVirtualPath = projectVirtualPath;
            Resolver = resolver;
            Bindings = bindings;
            Store = store;
            Coordinator = coordinator;
        }

        internal string Root { get; }

        internal string ProgramPath { get; }

        internal string ProjectVirtualPath { get; }

        internal string ProjectPhysicalPath =>
            Path.Combine(Path.GetDirectoryName(ProgramPath)!, "App.csproj");

        internal string ApplicationArtifactPath => Path.Combine(
            Path.GetDirectoryName(ProgramPath)!,
            "bin",
            "Release",
            "net10.0",
            "App.dll");

        internal AliCodingProjectResolver Resolver { get; }

        internal AliDevOpsInvocationBindingResolver Bindings { get; }

        internal AliDurableInvocationStore Store { get; }

        internal AliDevOpsExecutionCoordinator Coordinator { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                TestRepository.Root,
                "bin",
                "AliDevOpsExecutionAdapterTests",
                Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var projectDirectory = Path.Combine(workspace, "App");
            var outputDirectory = Path.Combine(
                projectDirectory,
                "bin",
                "Release",
                "net10.0");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Directory.Build.props"),
                "<Project />",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "Directory.Build.targets"),
                "<Project />",
                TestContext.Current.CancellationToken);
            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            var programPath = Path.Combine(projectDirectory, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                programPath,
                "Console.WriteLine(\"Ali\");",
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(outputDirectory, "App.dll"),
                Encoding.UTF8.GetBytes("bounded-test-artifact"),
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
            var resolver = new AliCodingProjectResolver(access);
            var bindings = new AliDevOpsInvocationBindingResolver(resolver);
            var store = new AliDurableInvocationStore(
                Path.Combine(durableRoot, "Coding", "Invocations"),
                "devops-execution-test-profile");
            var coordinator = new AliDevOpsExecutionCoordinator(
                bindings,
                store,
                new EvidenceLedger(durableRoot, "devops-execution-test-profile"));
            return new Fixture(
                root,
                programPath,
                "Workspace/App/App.csproj",
                resolver,
                bindings,
                store,
                coordinator);
        }

        internal AliDevOpsExecutionAdapter Adapter(string toolName) =>
            Assert.Single(
                Coordinator.Adapters.Cast<AliDevOpsExecutionAdapter>(),
                adapter => adapter.ToolName == toolName);

        internal AliExecutionPreparationRequest Request(
            AliDevOpsExecutionAdapter adapter,
            JsonElement arguments)
        {
            var target = Coordinator.TargetStates.Capture(adapter.ToolName, arguments);
            return AliDevOpsExecutionAdapterTests.Request(
                adapter,
                arguments,
                AliDevOpsInvocationBindingResolver.TargetVersionDigest(target));
        }

        internal JsonElement Arguments(string toolName) => toolName switch
        {
            AliCapabilityCatalog.ArchitectureInspectName =>
                JsonSerializer.SerializeToElement(new { targetPath = ProjectVirtualPath }),
            AliCapabilityCatalog.ArchitectureCheckName =>
                JsonSerializer.SerializeToElement(new
                {
                    targetPath = ProjectVirtualPath,
                    rules = new[]
                    {
                        new ArchitectureBoundaryRule("App.Domain", "App.Infrastructure")
                    }
                }),
            AliCapabilityCatalog.DotNetQualityScanName =>
                JsonSerializer.SerializeToElement(new { projectPath = ProjectVirtualPath }),
            AliCapabilityCatalog.DotNetApplicationVerifyName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = ProjectVirtualPath,
                    configuration = "Release",
                    healthUrl = (string?)null
                }),
            AliCapabilityCatalog.DotNetReleasePublishName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = ProjectVirtualPath,
                    runtimeIdentifier = "win-x64",
                    selfContained = true
                }),
            AliCapabilityCatalog.DotNetDeliveryVerifyName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = ProjectVirtualPath,
                    testTargetPath = ProjectVirtualPath,
                    configuration = "Release",
                    verifyApplication = false,
                    publishRelease = false
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName))
        };

        internal JsonElement DeliveryArguments(bool verifyApplication) =>
            JsonSerializer.SerializeToElement(new
            {
                projectPath = ProjectVirtualPath,
                testTargetPath = ProjectVirtualPath,
                configuration = "Release",
                verifyApplication,
                publishRelease = false
            });

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Cleanup must not obscure the focused adapter assertion.
            }
            return ValueTask.CompletedTask;
        }
    }
}
