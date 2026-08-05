using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding.Changesets;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class AliFrameworkFileMutationBrokerTests
{
    [Fact]
    public async Task CoreAssistant_ReplacePublishesDirectlyWithoutDurableGrant()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("core.txt"),
            "before",
            TestContext.Current.CancellationToken);

        using (AliCoreAssistantExecutionContext.Enter())
        {
            _ = await InvokeProviderAsync<string>(
                fixture.Provider,
                "ReplaceAsync",
                "Workspace/core.txt",
                "before",
                "after",
                false,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            "after",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("core.txt"),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Access.FrameworkStore.WriteAsync(
                "Workspace/core.txt",
                "outside-core-path",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CoreAssistant_ReplaceAllowsAValidFullSourceRewrite()
    {
        using var fixture = new Fixture();
        var original = "namespace Demo; public static class Program { "
            + string.Concat(Enumerable.Repeat("private static int Value() => 1; ", 20))
            + "public static void Main() { } }";
        const string replacement =
            "namespace Demo; public static class Program { public static void Main() { } }";
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("Program.cs"),
            original,
            TestContext.Current.CancellationToken);

        using (AliCoreAssistantExecutionContext.Enter())
        {
            _ = await InvokeProviderAsync<string>(
                fixture.Provider,
                "ReplaceAsync",
                "Workspace/Program.cs",
                original,
                replacement,
                false,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            replacement,
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("Program.cs"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_PreparesAnExactAbsentPreimage_ThenFrameworkPublishesOnlyWithOneUseGrant()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "Workspace/new.txt"),
            ("content", "authenticated content"),
            ("overwrite", false));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileWriteName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);

        Assert.False(File.Exists(fixture.PhysicalPath("new.txt")));
        using (fixture.EnterGrant(adapter, arguments, prepared))
        {
            _ = await InvokeProviderAsync<string>(
                fixture.Provider,
                "WriteAsync",
                "Workspace/new.txt",
                "authenticated content",
                false,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            "authenticated content",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("new.txt"),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Access.FrameworkStore.WriteAsync(
                "Workspace/new.txt",
                "replayed",
                TestContext.Current.CancellationToken));

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
        Assert.NotNull(reconciled.AppliedEvidence);
    }

    [Fact]
    public async Task ReplaceAndReplaceLines_AuthenticateTheFrameworksExactLiteralPostimages()
    {
        using var replaceFixture = new Fixture();
        await File.WriteAllTextAsync(
            replaceFixture.PhysicalPath("replace.txt"),
            "alpha\r\nbeta\nomega\r",
            TestContext.Current.CancellationToken);
        var replaceArguments = Arguments(
            ("fileName", "Workspace/replace.txt"),
            ("oldString", "beta"),
            ("newString", "gamma"),
            ("replaceAll", false));
        var replace = replaceFixture.Adapter(AliCapabilityCatalog.FileReplaceName);
        var replacePreparation = await replaceFixture.PrepareAsync(replace, replaceArguments);
        using (replaceFixture.EnterGrant(replace, replaceArguments, replacePreparation))
        {
            _ = await InvokeProviderAsync<string>(
                replaceFixture.Provider,
                "ReplaceAsync",
                "Workspace/replace.txt",
                "beta",
                "gamma",
                false,
                TestContext.Current.CancellationToken);
        }
        Assert.Equal(
            "alpha\r\ngamma\nomega\r",
            await File.ReadAllTextAsync(
                replaceFixture.PhysicalPath("replace.txt"),
                TestContext.Current.CancellationToken));

        using var linesFixture = new Fixture();
        await File.WriteAllTextAsync(
            linesFixture.PhysicalPath("lines.txt"),
            "one\r\ntwo\nthree\rfour",
            TestContext.Current.CancellationToken);
        var editsJson = JsonSerializer.SerializeToElement(new object[]
        {
            new Dictionary<string, object?>
            {
                ["line_number"] = 1,
                ["new_line"] = "ONE\r\n"
            },
            new Dictionary<string, object?>
            {
                ["line_number"] = 2,
                ["new_line"] = string.Empty
            },
            new Dictionary<string, object?>
            {
                ["line_number"] = 3,
                ["new_line"] = "THREE\r"
            },
            new Dictionary<string, object?>
            {
                ["line_number"] = 4,
                ["new_line"] = "FOUR"
            }
        });
        var lineArguments = Arguments(
            ("fileName", "Workspace/lines.txt"),
            ("edits", editsJson));
        var replaceLines = linesFixture.Adapter(AliCapabilityCatalog.FileReplaceLinesName);
        var linePreparation = await linesFixture.PrepareAsync(replaceLines, lineArguments);
        using (linesFixture.EnterGrant(replaceLines, lineArguments, linePreparation))
        {
            _ = await InvokeProviderAsync<string>(
                linesFixture.Provider,
                "ReplaceLinesAsync",
                "Workspace/lines.txt",
                new List<FileLineEdit>
                {
                    new() { LineNumber = 1, NewLine = "ONE\r\n" },
                    new() { LineNumber = 2, NewLine = string.Empty },
                    new() { LineNumber = 3, NewLine = "THREE\r" },
                    new() { LineNumber = 4, NewLine = "FOUR" }
                },
                TestContext.Current.CancellationToken);
        }
        Assert.Equal(
            "ONE\r\nTHREE\rFOUR",
            await File.ReadAllTextAsync(
                linesFixture.PhysicalPath("lines.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FrameworkStore_ConsumesGrantBeforeRejectingContentThatDiffersFromAuthenticatedPostimage()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "Workspace/exact.txt"),
            ("content", "authenticated"),
            ("overwrite", false));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileWriteName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);

        using (fixture.EnterGrant(adapter, arguments, prepared))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Access.FrameworkStore.WriteAsync(
                    "Workspace/exact.txt",
                    "different",
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Access.FrameworkStore.WriteAsync(
                    "Workspace/exact.txt",
                    "authenticated",
                    TestContext.Current.CancellationToken));
        }

        Assert.False(File.Exists(fixture.PhysicalPath("exact.txt")));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommittedReceiptWithChangedDurableAuthorization_RemainsUnknownWithoutEvidence(
        bool changeExecutionRegistryIdentity)
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "Workspace/authorization.txt"),
            ("content", "committed content"),
            ("overwrite", false));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileWriteName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        using (fixture.EnterGrant(adapter, arguments, prepared))
        {
            _ = await InvokeProviderAsync<string>(
                fixture.Provider,
                "WriteAsync",
                "Workspace/authorization.txt",
                "committed content",
                false,
                TestContext.Current.CancellationToken);
        }
        var exactIntent = Intent(adapter, arguments, prepared);
        var changedIntent = changeExecutionRegistryIdentity
            ? exactIntent with { ExecutionRegistryIdentityDigest = Digest("changed-execution-registry") }
            : exactIntent with { RootBinding = "changed-root-binding" };

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            changedIntent,
            TestContext.Current.CancellationToken);

        Assert.Equal(ActionReconciliationDisposition.Unknown, reconciled.Disposition);
        Assert.Equal("file-mutation-authorization-mismatch", reconciled.OutcomeCode);
        Assert.Null(reconciled.AppliedEvidence);
        Assert.Equal(
            "committed content",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("authorization.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrashReconciliation_ClassifiesCommittedRolledBackAndInDoubt_WithoutReplaying()
    {
        await AssertCrashClassificationAsync(
            AliSourceTransactionBoundary.OperationAppliedPersisted,
            tamperAfterCrash: false,
            ActionReconciliationDisposition.Applied,
            expectedContent: "postimage");
        await AssertCrashClassificationAsync(
            AliSourceTransactionBoundary.PreparedReceiptPersisted,
            tamperAfterCrash: false,
            ActionReconciliationDisposition.Absent,
            expectedContent: null);
        await AssertCrashClassificationAsync(
            AliSourceTransactionBoundary.OperationMutationCompleted,
            tamperAfterCrash: false,
            ActionReconciliationDisposition.Unknown,
            expectedContent: "postimage");
        await AssertCrashClassificationAsync(
            AliSourceTransactionBoundary.OperationAppliedPersisted,
            tamperAfterCrash: true,
            ActionReconciliationDisposition.Unknown,
            expectedContent: "unrecognized external content");
    }

    [Fact]
    public void Parser_RejectsAliasesOutsideTheExactLiveFrameworkSchemas()
    {
        var replaceAlias = JsonSerializer.SerializeToElement(new
        {
            fileName = "Workspace/file.txt",
            oldText = "old",
            newText = "new"
        });
        var lineAlias = JsonSerializer.SerializeToElement(new
        {
            fileName = "Workspace/file.txt",
            edits = new[] { new { lineNumber = 1, newLine = "new\n" } }
        });

        Assert.Throws<InvalidDataException>(() =>
            AliFrameworkFileMutationPlan.Create(
                AliCapabilityCatalog.FileReplaceName,
                replaceAlias,
                "old"));
        Assert.Throws<InvalidDataException>(() =>
            AliFrameworkFileMutationPlan.Create(
                AliCapabilityCatalog.FileReplaceLinesName,
                lineAlias,
                "old\n"));
    }

    [Fact]
    public async Task ProductionReachableStore_RejectsEveryMutationWithoutAnExactGrant()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            fixture.PhysicalPath("preserved.txt"),
            "preserved",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Access.Store.WriteAsync(
                "Workspace/ungranted.txt",
                "blocked",
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Access.Store.DeleteAsync(
                "Workspace/preserved.txt",
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Access.Store.CreateDirectoryAsync(
                "Workspace/ungranted-directory",
                TestContext.Current.CancellationToken));

        Assert.Same(fixture.Access.Store, fixture.Access.FrameworkStore);
        Assert.Equal(
            "preserved",
            await fixture.Access.Store.ReadAsync(
                "Workspace/preserved.txt",
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.PhysicalPath("ungranted.txt")));
        Assert.False(Directory.Exists(fixture.PhysicalPath("ungranted-directory")));
    }

    [Fact]
    public async Task ProductionReachableStore_PublishesOnlyTheAuthenticatedBrokeredMutation()
    {
        using var fixture = new Fixture();
        var arguments = Arguments(
            ("fileName", "Workspace/brokered.txt"),
            ("content", "authenticated"),
            ("overwrite", false));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileWriteName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);

        using (fixture.EnterGrant(adapter, arguments, prepared))
        {
            await fixture.Access.Store.WriteAsync(
                "Workspace/brokered.txt",
                "authenticated",
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            "authenticated",
            await File.ReadAllTextAsync(
                fixture.PhysicalPath("brokered.txt"),
                TestContext.Current.CancellationToken));
        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Applied, reconciled.Disposition);
    }

    [Fact]
    public async Task Prepare_RejectsAReparseTargetBeforeCapturingItsTextPreimage()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(
            Path.GetDirectoryName(fixture.PhysicalPath("unused"))!,
            "..",
            "outside-secret.txt");
        outside = Path.GetFullPath(outside);
        await File.WriteAllTextAsync(
            outside,
            "must-not-be-read-through-link",
            TestContext.Current.CancellationToken);
        var link = fixture.PhysicalPath("linked.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or PlatformNotSupportedException
                                           or IOException)
        {
            return;
        }

        var arguments = Arguments(
            ("fileName", "Workspace/linked.txt"),
            ("oldString", "must-not-be-read-through-link"),
            ("newString", "changed"),
            ("replaceAll", false));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileReplaceName);

        await Assert.ThrowsAsync<AliExecutionPreparationException>(() =>
            adapter.PrepareAsync(
                new AliExecutionPreparationRequest(
                    fixture.Identity,
                    "call-file-1",
                    "work-file-1",
                    adapter.ToolName,
                    adapter.CapabilityId,
                    adapter.ReconcilerId,
                    JsonSerializer.SerializeToElement(arguments),
                    ArgumentsDigest(arguments),
                    Digest("action"),
                    Digest("untrusted-reparse-target"),
                    Digest("permission"),
                    Digest("registry"),
                    Digest("execution-registry")),
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(
            "must-not-be-read-through-link",
            await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TargetVersionCapture_HashesOrdinaryFiles_AndRejectsFileAndParentReparsePoints()
    {
        using var fixture = new Fixture();
        var ordinaryPath = fixture.PhysicalPath("ordinary.txt");
        await File.WriteAllTextAsync(
            ordinaryPath,
            "ordinary",
            TestContext.Current.CancellationToken);
        var registry = AliProductionTargetStateAdapters.Create(fixture.Access);
        var ordinary = CaptureTargetVersion(
            registry,
            AliCapabilityCatalog.FileWriteName,
            "Workspace/ordinary.txt");
        var expectedHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(
                    ordinaryPath,
                    TestContext.Current.CancellationToken)))
            .ToLowerInvariant();
        Assert.Equal("sha256:" + expectedHash, ordinary);

        var outsideFile = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(fixture.PhysicalPath("unused"))!,
            "..",
            "outside-target.txt"));
        await File.WriteAllTextAsync(
            outsideFile,
            "outside",
            TestContext.Current.CancellationToken);
        var fileLink = fixture.PhysicalPath("file-link.txt");
        try
        {
            File.CreateSymbolicLink(fileLink, outsideFile);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or PlatformNotSupportedException
                                           or IOException)
        {
            return;
        }
        Assert.Equal(
            "unavailable:InvalidDataException",
            CaptureTargetVersion(
                registry,
                AliCapabilityCatalog.FileReplaceName,
                "Workspace/file-link.txt"));

        var outsideDirectory = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(fixture.PhysicalPath("unused"))!,
            "..",
            "outside-directory"))).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(outsideDirectory, "nested.txt"),
            "outside nested",
            TestContext.Current.CancellationToken);
        var directoryLink = fixture.PhysicalPath("directory-link");
        try
        {
            Directory.CreateSymbolicLink(directoryLink, outsideDirectory);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or PlatformNotSupportedException
                                           or IOException)
        {
            return;
        }
        Assert.Equal(
            "unavailable:InvalidDataException",
            CaptureTargetVersion(
                registry,
                AliCapabilityCatalog.FileReplaceLinesName,
                "Workspace/directory-link/nested.txt"));
    }

    private static async Task AssertCrashClassificationAsync(
        AliSourceTransactionBoundary boundary,
        bool tamperAfterCrash,
        ActionReconciliationDisposition expectedDisposition,
        string? expectedContent)
    {
        var injected = 0;
        using var fixture = new Fixture(fault =>
        {
            if (fault.Boundary == boundary
                && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                throw new AliSourceSimulatedInterruptionException(
                    fault.Boundary,
                    fault.OperationSequence);
            }
        });
        var arguments = Arguments(
            ("fileName", "Workspace/crash.txt"),
            ("content", "postimage"),
            ("overwrite", false));
        var adapter = fixture.Adapter(AliCapabilityCatalog.FileWriteName);
        var prepared = await fixture.PrepareAsync(adapter, arguments);
        using (fixture.EnterGrant(adapter, arguments, prepared))
        {
            await Assert.ThrowsAsync<AliSourceSimulatedInterruptionException>(() =>
                fixture.Access.FrameworkStore.WriteAsync(
                    "Workspace/crash.txt",
                    "postimage",
                    TestContext.Current.CancellationToken));
        }
        if (tamperAfterCrash)
        {
            await File.WriteAllTextAsync(
                fixture.PhysicalPath("crash.txt"),
                "unrecognized external content",
                TestContext.Current.CancellationToken);
        }

        var reconciled = await adapter.ReconcileAsync(
            fixture.Identity,
            Intent(adapter, arguments, prepared),
            TestContext.Current.CancellationToken);

        Assert.True(
            reconciled.Disposition == expectedDisposition,
            $"Expected {expectedDisposition}, received {reconciled.Disposition}: {reconciled.OutcomeCode}");
        if (expectedContent is null)
        {
            Assert.False(File.Exists(fixture.PhysicalPath("crash.txt")));
        }
        else
        {
            Assert.Equal(
                expectedContent,
                await File.ReadAllTextAsync(
                    fixture.PhysicalPath("crash.txt"),
                    TestContext.Current.CancellationToken));
        }
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

    private static string CaptureTargetVersion(
        Ali.Modules.Orchestration.Work.TargetStateRegistry registry,
        string toolName,
        string fileName)
    {
        var snapshot = registry.Capture(registry.Prepare(
            toolName,
            JsonSerializer.SerializeToElement(new { fileName })));
        return Assert.Single(snapshot.TargetVersions).Value;
    }

    private static PreparedActionIntent Intent(
        IAliExecutionEffectAdapter adapter,
        AIFunctionArguments arguments,
        AliExecutionPreparation prepared) =>
        new(
            Digest("idempotency"),
            "work-file-1",
            adapter.ToolName,
            adapter.CapabilityId,
            ArgumentsDigest(arguments),
            prepared.TargetVersionDigest,
            Digest("permission"),
            Digest("registry"),
            Digest("execution-registry"),
            adapter.ReconcilerId,
            prepared.RootBinding,
            RequiresApproval: true,
            AcceptedCallId: "call-file-1",
            PreparationIdentity: prepared.PreparationIdentity);

    private static async Task<T> InvokeProviderAsync<T>(
        FileAccessProvider provider,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(FileAccessProvider).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(FileAccessProvider).FullName, methodName);
        var task = method.Invoke(provider, arguments) as Task
            ?? throw new InvalidOperationException($"{methodName} did not return a Task.");
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        return result is T typed
            ? typed
            : throw new InvalidOperationException(
                $"{methodName} returned {result?.GetType().FullName ?? "null"}, not {typeof(T).FullName}.");
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
        private readonly string _root;
        private readonly string _workspace;

        internal Fixture(Action<AliSourceTransactionFault>? faultInjector = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "ProjectAli.FrameworkFileMutationBrokerTests",
                Guid.NewGuid().ToString("N"));
            _workspace = Directory.CreateDirectory(Path.Combine(_root, "Workspace")).FullName;
            var permissions = new AgentToolPermissionStore(_root);
            var store = new AliWorkstationFileStore(
                [new AliWorkstationFileMount("Workspace", _workspace)],
                Path.Combine(_root, "Trash"));
            Access = new AliWorkstationFileAccess(
                store,
                new AgentFileActionAuditStore(_root, activeUsers: null),
                permissions,
                Path.Combine(_root, "OrchestrationV2"),
                "framework-file-test",
                evidence: null,
                faultInjector: faultInjector);
            Provider = new FileAccessProvider(
                Access.FrameworkStore,
                new FileAccessProviderOptions
                {
                    DisableReadOnlyToolApproval = true,
                    DisableWriteToolApproval = true
                });
        }

        internal TurnIdentity Identity { get; } =
            new("user", "framework-file-mutation", "assistant-message");

        internal AliWorkstationFileAccess Access { get; }

        internal FileAccessProvider Provider { get; }

        internal IAliExecutionEffectAdapter Adapter(string toolName) =>
            Access.ExecutionEffectAdapters.Single(adapter =>
                string.Equals(adapter.ToolName, toolName, StringComparison.Ordinal));

        internal string PhysicalPath(string relativePath) =>
            Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal async Task<AliExecutionPreparation> PrepareAsync(
            IAliExecutionEffectAdapter adapter,
            AIFunctionArguments arguments)
        {
            var element = JsonSerializer.SerializeToElement(arguments).Clone();
            var fileName = element.GetProperty("fileName").GetString()!;
            var physicalPath = Access.ResolvePhysicalFilePath(fileName).PhysicalPath;
            var image = File.Exists(physicalPath)
                ? AliSourceFileImage.FromBytes(
                    await File.ReadAllBytesAsync(
                        physicalPath,
                        TestContext.Current.CancellationToken))
                : AliSourceFileImage.Absent;
            var targetDigest = AliFrameworkFileMutationTransaction.TargetVersionDigest(
                fileName,
                image);
            return await adapter.PrepareAsync(
                new AliExecutionPreparationRequest(
                    Identity,
                    "call-file-1",
                    "work-file-1",
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

        internal IDisposable EnterGrant(
            IAliExecutionEffectAdapter adapter,
            AIFunctionArguments arguments,
            AliExecutionPreparation prepared)
        {
            var grant = new AliExecutionGrant(
                Digest("idempotency"),
                "call-file-1",
                adapter.ToolName,
                adapter.CapabilityId,
                ArgumentsDigest(arguments),
                prepared.TargetVersionDigest,
                Digest("permission"),
                Digest("execution-registry"),
                adapter.ReconcilerId,
                prepared.PreparationIdentity,
                prepared.RootBinding);
            return new AliExecutionInvocationScope(grant).Enter(arguments);
        }

        public void Dispose()
        {
            Provider.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
