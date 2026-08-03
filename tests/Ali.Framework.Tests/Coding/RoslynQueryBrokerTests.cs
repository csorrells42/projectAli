using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coding;
using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coding.RoslynQueries;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.State;
using Ali.Modules.Orchestration.Work;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class RoslynQueryBrokerTests
{
    private static readonly string[] QueryToolNames =
    [
        AliCapabilityCatalog.RoslynAnalyzeProjectName,
        AliCapabilityCatalog.RoslynFindSymbolName,
        AliCapabilityCatalog.RoslynGetCompletionsName,
        AliCapabilityCatalog.RoslynInspectSolutionName,
        AliCapabilityCatalog.RoslynInspectDocumentName,
        AliCapabilityCatalog.RoslynInspectPositionName,
        AliCapabilityCatalog.RoslynFindReferencesName
    ];

    [Fact]
    public async Task TargetStateAdapter_UsesOnlyTheSevenExactQuerySchemas()
    {
        await using var fixture = await QueryFixture.CreateAsync();

        Assert.Equal(
            QueryToolNames.OrderBy(name => name, StringComparer.Ordinal),
            fixture.TargetStates.ToolNames.OrderBy(name => name, StringComparer.Ordinal));

        foreach (var toolName in QueryToolNames)
        {
            var snapshot = fixture.TargetStates.Capture(toolName, fixture.Arguments(toolName));
            Assert.Single(snapshot.TargetVersions);
            Assert.Contains(
                AliRoslynSemanticWorkspaceBinding.StaticSourceRevisionVersionKey,
                snapshot.TargetVersions.Keys);
            Assert.Equal(snapshot.TargetVersions.Count, snapshot.ArtifactVersions.Count);
            Assert.All(
                snapshot.TargetVersions,
                pair => Assert.Equal(pair.Value, snapshot.ArtifactVersions[pair.Key]));
            Assert.All(
                snapshot.TargetVersions.Values,
                value => Assert.Matches("^[0-9a-f]{64}$", value));
            Assert.DoesNotContain(
                snapshot.TargetVersions.Values,
                value => value.Contains(fixture.PhysicalProjectRoot, StringComparison.OrdinalIgnoreCase));
        }

        var before = fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynInspectDocumentName,
            fixture.Arguments(AliCapabilityCatalog.RoslynInspectDocumentName));
        await File.AppendAllTextAsync(
            fixture.PhysicalDocumentPath,
            Environment.NewLine + "// changed",
            TestContext.Current.CancellationToken);
        var after = fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynInspectDocumentName,
            fixture.Arguments(AliCapabilityCatalog.RoslynInspectDocumentName));
        Assert.NotEqual(
            before.TargetVersions[AliRoslynSemanticWorkspaceBinding.StaticSourceRevisionVersionKey],
            after.TargetVersions[AliRoslynSemanticWorkspaceBinding.StaticSourceRevisionVersionKey]);

        Assert.Throws<InvalidDataException>(() => fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynFindSymbolName,
            JsonSerializer.SerializeToElement(new
            {
                targetPath = fixture.TargetVirtualPath,
                query = "Calculator"
            })));
        Assert.Throws<InvalidDataException>(() => fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynInspectPositionName,
            JsonSerializer.SerializeToElement(new
            {
                targetPath = fixture.TargetVirtualPath,
                documentPath = fixture.DocumentVirtualPath,
                line = 0,
                column = 1
            })));
        Assert.Throws<InvalidOperationException>(() => fixture.TargetStates.Capture(
            "roslyn_find_something_from_a_title",
            JsonSerializer.SerializeToElement(new
            {
                targetPath = fixture.TargetVirtualPath
            })));
    }

    [Fact]
    public async Task TargetStateCapture_DoesNotMaterializeDesignTimeOutputsBeforeDurableIntent()
    {
        await using var fixture = await QueryFixture.CreateAsync();
        var before = Directory.EnumerateFiles(
                fixture.PhysicalProjectRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.PhysicalProjectRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _ = fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynInspectSolutionName,
            fixture.Arguments(AliCapabilityCatalog.RoslynInspectSolutionName));

        var after = Directory.EnumerateFiles(
                fixture.PhysicalProjectRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.PhysicalProjectRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(before, after, StringComparer.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.PhysicalProjectRoot, "obj")));
    }

    [Fact]
    public async Task TargetStateCapture_DoesNotParseOrEvaluateProjectBeforeDurableIntent()
    {
        await using var fixture = await QueryFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Resolver.ResolveExistingTarget(fixture.TargetVirtualPath).PhysicalPath,
            "<Project",
            TestContext.Current.CancellationToken);

        var snapshot = fixture.TargetStates.Capture(
            AliCapabilityCatalog.RoslynInspectSolutionName,
            fixture.Arguments(AliCapabilityCatalog.RoslynInspectSolutionName));

        Assert.Single(snapshot.TargetVersions);
        Assert.Contains(
            AliRoslynSemanticWorkspaceBinding.StaticSourceRevisionVersionKey,
            snapshot.TargetVersions.Keys);
        Assert.False(Directory.Exists(Path.Combine(fixture.PhysicalProjectRoot, "obj")));
    }

    [Fact]
    public async Task ExecutionAdapters_PrepareEveryExactQueryAndRejectStaleTargets()
    {
        await using var fixture = await QueryFixture.CreateAsync();
        var adapters = AliRoslynQueryExecutionAdapter.CreateAll(fixture.TargetStates);

        Assert.Equal(QueryToolNames.Length, adapters.Count);
        Assert.Equal(
            QueryToolNames.OrderBy(name => name, StringComparer.Ordinal),
            adapters.Select(adapter => adapter.ToolName).OrderBy(name => name, StringComparer.Ordinal));

        var preparationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            var arguments = fixture.Arguments(adapter.ToolName);
            var targetVersionDigest = TargetVersionDigest(
                fixture.TargetStates.Capture(adapter.ToolName, arguments));
            var prepared = await adapter.PrepareAsync(
                Request(adapter, arguments, targetVersionDigest),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                AliRoslynQueryExecutionAdapter.CapabilityIdFor(adapter.ToolName),
                adapter.CapabilityId);
            Assert.Equal(
                AliRoslynQueryExecutionAdapter.ReconcilerIdFor(adapter.ToolName),
                adapter.ReconcilerId);
            Assert.Equal(targetVersionDigest, prepared.TargetVersionDigest);
            Assert.Equal(
                AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot),
                prepared.RootBinding);
            Assert.True(Guid.TryParseExact(prepared.PreparationIdentity, "N", out _));
            Assert.True(preparationIds.Add(prepared.PreparationIdentity));

            var intent = Intent(adapter, targetVersionDigest, prepared.PreparationIdentity);
            var reconciled = await adapter.ReconcileAsync(
                Identity(),
                intent,
                TestContext.Current.CancellationToken);
            Assert.Equal(ActionReconciliationDisposition.Absent, reconciled.Disposition);
            Assert.Equal("roslyn-query-safe-to-repeat", reconciled.OutcomeCode);
        }

        var analyze = Assert.Single(
            adapters,
            adapter => adapter.ToolName == AliCapabilityCatalog.RoslynAnalyzeProjectName);
        var analyzeArguments = fixture.Arguments(analyze.ToolName);
        await Assert.ThrowsAsync<AliExecutionPreparationException>(async () =>
            await analyze.PrepareAsync(
                Request(analyze, analyzeArguments, Digest("stale-target")),
                TestContext.Current.CancellationToken));

        var exactDigest = TargetVersionDigest(
            fixture.TargetStates.Capture(analyze.ToolName, analyzeArguments));
        var mismatchedIntent = Intent(analyze, exactDigest, Guid.NewGuid().ToString("N")) with
        {
            ToolName = AliCapabilityCatalog.RoslynFindSymbolName
        };
        var mismatch = await analyze.ReconcileAsync(
            Identity(),
            mismatchedIntent,
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionReconciliationDisposition.Unknown, mismatch.Disposition);
        Assert.Equal("roslyn-query-adapter-identity-mismatch", mismatch.OutcomeCode);
    }

    [Fact]
    public async Task Facade_RequiresAnExactOneUseGrantAndValidatesItsRoot()
    {
        await using var fixture = await QueryFixture.CreateAsync();

        Func<Task>[] unauthorizedCalls =
        [
            async () =>
            {
                _ = await fixture.Facade.AnalyzeAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken);
            },
            async () =>
            {
                _ = await fixture.Facade.FindSymbolAsync(
                    fixture.TargetVirtualPath,
                    "Calculator",
                    TestContext.Current.CancellationToken);
            },
            async () =>
            {
                _ = await fixture.Facade.GetCompletionsAsync(
                    fixture.TargetVirtualPath,
                    fixture.DocumentVirtualPath,
                    5,
                    20,
                    TestContext.Current.CancellationToken);
            },
            async () =>
            {
                _ = await fixture.Facade.InspectSolutionAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken);
            },
            async () =>
            {
                _ = await fixture.Facade.InspectDocumentAsync(
                    fixture.TargetVirtualPath,
                    fixture.DocumentVirtualPath,
                    TestContext.Current.CancellationToken);
            },
            async () =>
            {
                _ = await fixture.Facade.InspectPositionAsync(
                    fixture.TargetVirtualPath,
                    fixture.DocumentVirtualPath,
                    5,
                    16,
                    TestContext.Current.CancellationToken);
            },
            async () =>
            {
                _ = await fixture.Facade.FindReferencesAsync(
                    fixture.TargetVirtualPath,
                    fixture.DocumentVirtualPath,
                    5,
                    16,
                    TestContext.Current.CancellationToken);
            }
        ];
        foreach (var call in unauthorizedCalls)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(call);
        }

        var arguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.TargetVirtualPath
        };
        var argumentsDigest = ArgumentsDigest(arguments);
        var wrongToolGrant = Grant(
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            argumentsDigest,
            AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot));
        using (new AliExecutionInvocationScope(wrongToolGrant).Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectSolutionAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken));
        }

        var wrongRootGrant = Grant(
            AliCapabilityCatalog.RoslynInspectSolutionName,
            argumentsDigest,
            Digest("wrong-root"));
        using (new AliExecutionInvocationScope(wrongRootGrant).Enter(arguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectSolutionAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken));
        }

        var exactGrant = Grant(
            AliCapabilityCatalog.RoslynInspectSolutionName,
            argumentsDigest,
            AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot),
            TargetVersionDigest(fixture.TargetStates.Capture(
                AliCapabilityCatalog.RoslynInspectSolutionName,
                fixture.Arguments(AliCapabilityCatalog.RoslynInspectSolutionName))));
        using (new AliExecutionInvocationScope(exactGrant).Enter(arguments))
        {
            var result = await fixture.Facade.InspectSolutionAsync(
                fixture.TargetVirtualPath,
                TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
            Assert.Equal(2, result.Projects.Count);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectSolutionAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Facade_RejectsSelectedDocumentDriftAfterPreparation()
    {
        await using var fixture = await QueryFixture.CreateAsync();
        var toolName = AliCapabilityCatalog.RoslynInspectDocumentName;
        var jsonArguments = fixture.Arguments(toolName);
        var invocationArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.TargetVirtualPath,
            ["documentPath"] = fixture.DocumentVirtualPath
        };
        var grant = Grant(
            toolName,
            ArgumentsDigest(invocationArguments),
            AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot),
            TargetVersionDigest(fixture.TargetStates.Capture(toolName, jsonArguments)));
        await File.AppendAllTextAsync(
            fixture.PhysicalDocumentPath,
            Environment.NewLine + "// drift after broker preparation",
            TestContext.Current.CancellationToken);

        using (new AliExecutionInvocationScope(grant).Enter(invocationArguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectDocumentAsync(
                    fixture.TargetVirtualPath,
                    fixture.DocumentVirtualPath,
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Facade_RejectsAPhysicalDocumentSharedByMultipleProjects()
    {
        await using var fixture = await QueryFixture.CreateAsync();
        var sharedVirtualPath = "Workspace/RoslynQuery/Shared.cs";
        var sharedPhysicalPath = Path.Combine(fixture.PhysicalProjectRoot, "Shared.cs");
        await File.WriteAllTextAsync(
            sharedPhysicalPath,
            "namespace SharedInput; public sealed class SharedType { }",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.PhysicalProjectRoot, "Helper", "Helper.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\Shared.cs" Link="Shared.cs" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        var toolName = AliCapabilityCatalog.RoslynInspectDocumentName;
        var jsonArguments = JsonSerializer.SerializeToElement(new
        {
            targetPath = fixture.TargetVirtualPath,
            documentPath = sharedVirtualPath
        });
        var invocationArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.TargetVirtualPath,
            ["documentPath"] = sharedVirtualPath
        };
        var grant = Grant(
            toolName,
            ArgumentsDigest(invocationArguments),
            AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot),
            TargetVersionDigest(fixture.TargetStates.Capture(toolName, jsonArguments)));

        using (new AliExecutionInvocationScope(grant).Enter(invocationArguments))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectDocumentAsync(
                    fixture.TargetVirtualPath,
                    sharedVirtualPath,
                    TestContext.Current.CancellationToken));
            Assert.Contains("shared", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("project identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Facade_RejectsWorkspaceWarningsInsteadOfReturningPartialSemantics()
    {
        await using var fixture = await QueryFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Resolver.ResolveExistingTarget(fixture.TargetVirtualPath).PhysicalPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="Helper\Helper.csproj" />
                <ProjectReference Include="Missing\Missing.csproj" />
              </ItemGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        var toolName = AliCapabilityCatalog.RoslynInspectSolutionName;
        var jsonArguments = fixture.Arguments(toolName);
        var invocationArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.TargetVirtualPath
        };
        var grant = Grant(
            toolName,
            ArgumentsDigest(invocationArguments),
            AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot),
            TargetVersionDigest(fixture.TargetStates.Capture(toolName, jsonArguments)));

        using (new AliExecutionInvocationScope(grant).Enter(invocationArguments))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectSolutionAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken));
            Assert.Contains(
                "MSBuildWorkspace reported",
                exception.Message,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("unrelated-project")]
    [InlineData("imported-props")]
    [InlineData("project-reference")]
    public async Task Facade_RejectsStaticSourceDriftAfterPreparation(
        string driftKind)
    {
        await using var fixture = await QueryFixture.CreateAsync();
        var toolName = AliCapabilityCatalog.RoslynInspectSolutionName;
        var jsonArguments = fixture.Arguments(toolName);
        var invocationArguments = new AIFunctionArguments
        {
            ["targetPath"] = fixture.TargetVirtualPath
        };
        var grant = Grant(
            toolName,
            ArgumentsDigest(invocationArguments),
            AliRoslynActionExecutionAdapter.RootBinding(fixture.PhysicalProjectRoot),
            TargetVersionDigest(fixture.TargetStates.Capture(toolName, jsonArguments)));
        await fixture.ApplySemanticDriftAsync(
            driftKind,
            TestContext.Current.CancellationToken);

        using (new AliExecutionInvocationScope(grant).Enter(invocationArguments))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Facade.InspectSolutionAsync(
                    fixture.TargetVirtualPath,
                    TestContext.Current.CancellationToken));
        }
    }

    private static AliExecutionPreparationRequest Request(
        IAliExecutionEffectAdapter adapter,
        JsonElement arguments,
        string targetVersionDigest) =>
        new(
            TurnIdentity: Identity(),
            CallId: "call-1",
            WorkItemId: "work-1",
            ToolName: adapter.ToolName,
            CapabilityId: adapter.CapabilityId,
            ReconcilerId: adapter.ReconcilerId,
            Arguments: arguments,
            CanonicalArgumentsDigest: Digest("arguments"),
            ActionIdentityFingerprint: Digest("action"),
            TargetVersionDigest: targetVersionDigest,
            PermissionReceiptDigest: Digest("permission"),
            RegistryRevisionDigest: Digest("registry"),
            ExecutionRegistryIdentityDigest: Digest("execution-registry"));

    private static PreparedActionIntent Intent(
        IAliExecutionEffectAdapter adapter,
        string targetVersionDigest,
        string preparationIdentity) =>
        new(
            Digest("idempotency"),
            "work-1",
            adapter.ToolName,
            adapter.CapabilityId,
            Digest("arguments"),
            targetVersionDigest,
            Digest("permission"),
            Digest("registry"),
            Digest("execution-registry"),
            adapter.ReconcilerId,
            "root-binding",
            RequiresApproval: false,
            AcceptedCallId: "call-1",
            PreparationIdentity: preparationIdentity);

    private static AliExecutionGrant Grant(
        string toolName,
        string argumentsDigest,
        string rootBinding,
        string? targetVersionDigest = null) =>
        new(
            Digest("idempotency"),
            "call-1",
            toolName,
            AliRoslynQueryExecutionAdapter.CapabilityIdFor(toolName),
            argumentsDigest,
            targetVersionDigest ?? Digest("target-version"),
            Digest("permission"),
            Digest("registry"),
            AliRoslynQueryExecutionAdapter.ReconcilerIdFor(toolName),
            Guid.NewGuid().ToString("N"),
            rootBinding);

    private static TurnIdentity Identity() =>
        new("user", "roslyn-query-broker", "assistant-message");

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

    private static string TargetVersionDigest(TargetStateSnapshot snapshot) =>
        WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            snapshot.TargetVersions);

    private static string Digest(string value) =>
        TurnStateIntegrity.Digest(Encoding.UTF8.GetBytes(value));

    private static bool RequiresDocument(string toolName) =>
        toolName is AliCapabilityCatalog.RoslynGetCompletionsName
            or AliCapabilityCatalog.RoslynInspectDocumentName
            or AliCapabilityCatalog.RoslynInspectPositionName
            or AliCapabilityCatalog.RoslynFindReferencesName;

    private sealed class QueryFixture : IAsyncDisposable
    {
        private QueryFixture(
            string root,
            AliCodingProjectResolver resolver,
            AliRoslynCodingTools tools)
        {
            Root = root;
            Resolver = resolver;
            var loader = new AliRoslynWorkspaceLoader(resolver);
            var fingerprint = new AliRoslynSolutionFingerprint(
                new AliRoslynTargetReferenceResolver());
            var semanticWorkspace = new AliRoslynSemanticWorkspaceBinding(
                loader,
                fingerprint);
            TargetStates = new AliRoslynQueryTargetStateAdapter(
                resolver,
                semanticWorkspace);
            Facade = new AliRoslynQueryFacade(
                tools,
                resolver,
                loader,
                TargetStates);
        }

        internal string Root { get; }

        internal string TargetVirtualPath { get; } =
            "Workspace/RoslynQuery/RoslynQuery.csproj";

        internal string DocumentVirtualPath { get; } =
            "Workspace/RoslynQuery/Calculator.cs";

        internal AliCodingProjectResolver Resolver { get; }

        internal AliRoslynQueryTargetStateAdapter TargetStates { get; }

        internal AliRoslynQueryFacade Facade { get; }

        internal string PhysicalProjectRoot =>
            Resolver.ResolveExistingTarget(TargetVirtualPath).RootDirectory;

        internal string PhysicalDocumentPath =>
            Resolver.ResolveDocument(
                Resolver.ResolveExistingTarget(TargetVirtualPath),
                DocumentVirtualPath);

        internal static async Task<QueryFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "AliRoslynQueryTests",
                Guid.NewGuid().ToString("N"));
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            await access.Store.WriteAsync(
                "Workspace/RoslynQuery/RoslynQuery.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Helper\Helper.csproj" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/RoslynQuery/Directory.Build.props",
                """
                <Project>
                  <PropertyGroup>
                    <DefineConstants>INITIAL_BINDING</DefineConstants>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/RoslynQuery/Calculator.cs",
                """
                namespace RoslynQuery;

                public sealed class Calculator
                {
                    public int Add(int left, int right) => left + right;
                }
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/RoslynQuery/Helper/Helper.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/RoslynQuery/Helper/Helper.cs",
                "namespace RoslynQuery.Helper; public static class HelperValue { public const int Value = 1; }",
                TestContext.Current.CancellationToken);
            var resolver = new AliCodingProjectResolver(access);
            var tools = new AliRoslynCodingTools(
                resolver,
                new AliCodingProjectTracker(),
                Path.Combine(root, "roslyn-query-audit.jsonl"));
            return new QueryFixture(root, resolver, tools);
        }

        internal async Task ApplySemanticDriftAsync(
            string driftKind,
            CancellationToken cancellationToken)
        {
            var path = driftKind switch
            {
                "unrelated-project" => Path.Combine(
                    PhysicalProjectRoot,
                    "Helper",
                    "Helper.cs"),
                "imported-props" => Path.Combine(
                    PhysicalProjectRoot,
                    "Directory.Build.props"),
                "project-reference" => Path.Combine(
                    PhysicalProjectRoot,
                    "Helper",
                    "Helper.csproj"),
                _ => throw new InvalidOperationException("Unknown semantic drift fixture.")
            };
            var replacement = driftKind switch
            {
                "unrelated-project" =>
                    "namespace RoslynQuery.Helper; public static class HelperValue { public const int Value = 2; }",
                "imported-props" =>
                    "<Project><PropertyGroup><DefineConstants>CHANGED_BINDING</DefineConstants></PropertyGroup></Project>",
                "project-reference" =>
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Changed.Helper</AssemblyName></PropertyGroup></Project>",
                _ => throw new InvalidOperationException("Unknown semantic drift fixture.")
            };
            await File.WriteAllTextAsync(path, replacement, cancellationToken);
        }

        internal JsonElement Arguments(string toolName) => toolName switch
        {
            AliCapabilityCatalog.RoslynAnalyzeProjectName =>
                JsonSerializer.SerializeToElement(new { projectPath = TargetVirtualPath }),
            AliCapabilityCatalog.RoslynFindSymbolName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = TargetVirtualPath,
                    query = "Calculator"
                }),
            AliCapabilityCatalog.RoslynGetCompletionsName =>
                JsonSerializer.SerializeToElement(new
                {
                    projectPath = TargetVirtualPath,
                    documentPath = DocumentVirtualPath,
                    line = 5,
                    column = 20
                }),
            AliCapabilityCatalog.RoslynInspectSolutionName =>
                JsonSerializer.SerializeToElement(new { targetPath = TargetVirtualPath }),
            AliCapabilityCatalog.RoslynInspectDocumentName =>
                JsonSerializer.SerializeToElement(new
                {
                    targetPath = TargetVirtualPath,
                    documentPath = DocumentVirtualPath
                }),
            AliCapabilityCatalog.RoslynInspectPositionName
                or AliCapabilityCatalog.RoslynFindReferencesName =>
                JsonSerializer.SerializeToElement(new
                {
                    targetPath = TargetVirtualPath,
                    documentPath = DocumentVirtualPath,
                    line = 5,
                    column = 16
                }),
            _ => throw new InvalidOperationException("Unknown Roslyn query test tool.")
        };

        public async ValueTask DisposeAsync()
        {
            for (var attempt = 1; Directory.Exists(Root); attempt++)
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
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
}
