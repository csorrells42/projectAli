using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class McpCapabilityPublicationGateTests
{
    [Fact]
    public async Task SharedOwner_PublishesOnlyEffectiveCanonicalSchema_AndLeaseBlocksAfterDisable()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var canonical = AIFunctionFactory.Create(
                (Func<string>)(() => "canonical"),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Canonical local-time description.");
            var actual = AIFunctionFactory.Create(
                (Func<string>)(() => "actual"),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "MCP-specific local-time description.");
            var owner = CreateOwner(root, [canonical], [canonical.Name]);
            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([actual], [actual.Name]),
                owner,
                TestContext.Current.CancellationToken);

            var published = Assert.Single(publication.PublishedFunctions);
            Assert.Empty(publication.Issues);
            Assert.Single(publication.Tools);
            Assert.Equal(
                "actual",
                (await published.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken))?.ToString());
            Assert.Equal(
                CapabilitySchemaIdentity.Calculate(canonical),
                CapabilitySchemaIdentity.Calculate(published));

            var envelope = owner.CaptureSettings();
            var selections = envelope.Rows.ToDictionary(row => row.GroupId, row => row.Enabled, StringComparer.Ordinal);
            selections[CapabilityGroupIds.PersonalContextAndMemory] = false;
            var save = owner.TrySaveRows(envelope.Stamp, selections);
            Assert.Equal(CapabilitySettingsMutationStatus.Saved, save.Status);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(
                await published.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken));
            Assert.False(blocked.Success);
            Assert.False(blocked.Invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.Code == CapabilityAvailabilityReasonCode.InvocationLeaseStale.ToString());
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task EquivalentMcpRefreshThenDesktopPlanning_PreservesExistingAndCurrentMcpLeases()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var invocations = 0;
            var function = AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invocations++;
                    return "now";
                }),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var owner = CreateOwner(root, [function], []);
            var catalog = Catalog([function], [function.Name]);

            var originalPublication = McpCapabilityPublicationGate.Publish(
                catalog,
                owner,
                TestContext.Current.CancellationToken);
            var originalMcpFunction = Assert.Single(originalPublication.PublishedFunctions);
            var afterOriginal = owner.CaptureSettings();

            var refreshedPublication = McpCapabilityPublicationGate.Publish(
                catalog,
                owner,
                TestContext.Current.CancellationToken);
            var currentMcpFunction = Assert.Single(refreshedPublication.PublishedFunctions);
            var afterRefresh = owner.CaptureSettings();
            Assert.Equal(afterOriginal.RuntimeRevision, afterRefresh.RuntimeRevision);
            Assert.Equal(afterOriginal.PublicationRevision, afterRefresh.PublicationRevision);

            var equivalentState = StateFrom(owner.CapturePlanning().Runtime);
            var desktopTerminal = new TerminalCapabilityEnforcementProvider(
                owner,
                () => equivalentState);
            var desktopContext = await desktopTerminal.ApplyTerminalContextAsync(
                new AIContext { Tools = new AITool[] { function } },
                TestContext.Current.CancellationToken);
            Assert.Single(desktopContext.Tools!);

            var afterDesktopPlanning = owner.CaptureSettings();
            Assert.Equal(afterRefresh.RuntimeRevision, afterDesktopPlanning.RuntimeRevision);
            Assert.Equal(afterRefresh.PublicationRevision, afterDesktopPlanning.PublicationRevision);
            Assert.Equal(
                "now",
                (await originalMcpFunction.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken))?.ToString());
            Assert.Equal(
                "now",
                (await currentMcpFunction.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken))?.ToString());
            Assert.Equal(2, invocations);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PublishedFunction_BlocksWhenActiveUserChangesAfterPublication()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var invoked = 0;
            var activeUserId = "test-user";
            var function = AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invoked++;
                    return "now";
                }),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var owner = CreateOwner(root, [function], [function.Name]);
            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([function], [function.Name], () => activeUserId),
                owner,
                TestContext.Current.CancellationToken);
            var published = Assert.Single(publication.PublishedFunctions);

            var before = owner.CapturePlanning().Runtime;
            activeUserId = "different-user";
            var changedRuntime = CapabilityRuntimeAvailabilityFactory.Create(
                before.RegisteredToolsByName.Values,
                StateFrom(before, activeUserId: activeUserId));
            Assert.NotEqual(before.RuntimeRevision, changedRuntime.RuntimeRevision);
            Assert.True(owner.TryPublishRuntime(
                owner.CaptureSettings().Stamp,
                changedRuntime,
                out _));
            var result = await published.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
            Assert.False(blocked.Invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
                && reason.DependencyId == "active-user");
            Assert.Equal(0, invoked);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PublishedFunction_BlocksActiveUserAbaWhenStableIdReturns()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var invoked = 0;
            var activeUserId = "test-user";
            var activeUserRevision = "test-user-selection-v1";
            var function = AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invoked++;
                    return "now";
                }),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var owner = CreateOwner(root, [function], [function.Name]);
            var publication = McpCapabilityPublicationGate.Publish(
                Catalog(
                    [function],
                    [function.Name],
                    () => activeUserId,
                    () => activeUserRevision),
                owner,
                TestContext.Current.CancellationToken);
            var published = Assert.Single(publication.PublishedFunctions);

            activeUserId = "different-user";
            activeUserRevision = "test-user-selection-v2";
            activeUserId = "test-user";
            activeUserRevision = "test-user-selection-v3";
            var result = await published.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
            Assert.Contains(blocked.Reasons, reason =>
                reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
                && reason.DependencyId == "active-user-selection");
            Assert.Equal(0, invoked);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PublishedFunction_BlocksAfterCanonicalPermissionRuntimeChange()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var invoked = 0;
            var function = AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invoked++;
                    return "now";
                }),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var owner = CreateOwner(root, [function], [function.Name]);
            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([function], [function.Name]),
                owner,
                TestContext.Current.CancellationToken);
            var published = Assert.Single(publication.PublishedFunctions);
            var before = owner.CapturePlanning().Runtime;
            var changedRuntime = CapabilityRuntimeAvailabilityFactory.Create(
                before.RegisteredToolsByName.Values,
                StateFrom(before, permissionRevision: "permissions-changed"));
            Assert.NotEqual(before.RuntimeRevision, changedRuntime.RuntimeRevision);
            Assert.True(owner.TryPublishRuntime(
                owner.CaptureSettings().Stamp,
                changedRuntime,
                out _));

            var result = await published.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
            Assert.False(blocked.Invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
                && reason.DependencyId == "permissions");
            Assert.Equal(0, invoked);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Theory]
    [InlineData("capability-settings")]
    [InlineData("active-user")]
    [InlineData("mcp-policy")]
    [InlineData("tool-permissions")]
    [InlineData("host-configuration")]
    public async Task HeadlessPublishedFunction_BlocksAfterPersistedSecurityBoundaryWrite(
        string boundaryFile)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var invoked = 0;
            var function = AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invoked++;
                    return "now";
                }),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var owner = CreateOwner(root, [function], [function.Name]);
            var hostConfigurationPath = boundaryFile == "host-configuration"
                ? Path.Combine(root, "Ali.McpHost.xml")
                : null;
            Func<string> boundaryRevision = () =>
                McpPersistedSecurityBoundaryRevision.Capture(root, hostConfigurationPath);
            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([function], [function.Name]),
                owner,
                TestContext.Current.CancellationToken,
                boundaryRevision);
            var published = Assert.Single(publication.PublishedFunctions);
            var path = boundaryFile switch
            {
                "capability-settings" => CapabilityAvailabilitySettingsStore.GetSettingsPath(root),
                "active-user" => Path.Combine(root, "active-user-session.json"),
                "mcp-policy" => McpServerSettingsStore.GetSettingsPath(root),
                "tool-permissions" => Path.Combine(root, "Permissions", "agent-tool-permissions.json"),
                "host-configuration" => hostConfigurationPath!,
                _ => throw new InvalidOperationException($"Unknown test boundary '{boundaryFile}'.")
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                $"{{\"boundary\":\"{boundaryFile}\"}}",
                TestContext.Current.CancellationToken);

            var result = await published.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
            Assert.Contains(blocked.Reasons, reason =>
                reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
                && reason.DependencyId == "persisted-security-boundary");
            Assert.Equal(0, invoked);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void SchemaMismatchAndUnknownTool_AreWithheldBeforeMcpServerToolCreation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var canonical = AIFunctionFactory.Create(
                (Func<string>)(() => "canonical"),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Canonical description.");
            var mismatched = AIFunctionFactory.Create(
                (Func<int>)(() => 42),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Canonical description.");
            var unknown = AIFunctionFactory.Create(
                (Func<string>)(() => "unknown"),
                "unregistered_outgoing_tool",
                "Unknown tool.");
            var owner = CreateOwner(root, [canonical], [canonical.Name]);

            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([mismatched, unknown], [mismatched.Name, unknown.Name]),
                owner,
                TestContext.Current.CancellationToken);

            Assert.Empty(publication.Tools);
            Assert.Empty(publication.PublishedFunctions);
            Assert.Contains(publication.Issues, issue =>
                issue.ToolName == canonical.Name
                && issue.Code == McpCapabilityPublicationIssueCode.SchemaIdentityMismatch);
            Assert.Contains(publication.Issues, issue =>
                issue.ToolName == unknown.Name
                && issue.Code == McpCapabilityPublicationIssueCode.MissingCanonicalDescriptor);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void DisabledGroupAndUnavailableReconciler_AreWithheld()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var localTime = AIFunctionFactory.Create(
                (Func<string>)(() => "now"),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var reminder = AIFunctionFactory.Create(
                (Func<string, string, string>)((title, dueAtLocal) => $"{title}:{dueAtLocal}"),
                AliCapabilityCatalog.CreateCalendarEventName,
                "Create reminder.");
            var owner = CreateOwner(
                root,
                [localTime, reminder],
                [localTime.Name, reminder.Name]);
            var envelope = owner.CaptureSettings();
            var selections = envelope.Rows.ToDictionary(row => row.GroupId, row => row.Enabled, StringComparer.Ordinal);
            selections[CapabilityGroupIds.PersonalContextAndMemory] = false;
            Assert.Equal(
                CapabilitySettingsMutationStatus.Saved,
                owner.TrySaveRows(envelope.Stamp, selections).Status);

            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([localTime, reminder], [localTime.Name, reminder.Name]),
                owner,
                TestContext.Current.CancellationToken);

            Assert.Empty(publication.Tools);
            Assert.All(publication.Issues, issue =>
                Assert.Equal(McpCapabilityPublicationIssueCode.CapabilityUnavailable, issue.Code));
            Assert.Contains(publication.Issues, issue => issue.ToolName == localTime.Name);
            Assert.Contains(publication.Issues, issue =>
                issue.ToolName == reminder.Name
                && issue.Message.Contains("reconciler", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void EveryOutgoingDurableEffectCapability_IsWithheldByMutationBoundary()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var names = McpServerToolCatalog.CreateDefaultPolicies()
                .Select(policy => policy.Name)
                .Where(AliProductionCapabilityCatalog.KnownToolNames.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var functions = names
                .Select(name => AIFunctionFactory.Create(
                    (Func<string>)(() => name),
                    name,
                    $"Canonical schema for {name}."))
                .ToArray();
            var owner = CreateOwner(
                root,
                functions,
                names,
                includeReconcilers: true);
            var planning = owner.CapturePlanning();
            var effectCapable = planning.Resolution.OutgoingMcpDescriptors
                .Where(descriptor => descriptor.Effect.RequiresDurableEffectAdapter
                    || descriptor.Effect.MutationBoundary
                        == CapabilityMutationBoundary.StagedWorkspace)
                .ToArray();
            Assert.NotEmpty(effectCapable);
            Assert.Contains(effectCapable, descriptor =>
                !descriptor.Permission.RequiresApproval);
            Assert.All(
                effectCapable.Where(descriptor => descriptor.Effect.ReconcilerId is not null),
                descriptor => Assert.Contains(
                    descriptor.Effect.ReconcilerId!,
                    planning.Runtime.AvailableReconcilerIds));

            var publication = McpCapabilityPublicationGate.Publish(
                Catalog(functions, names),
                owner,
                TestContext.Current.CancellationToken);
            var issueByName = publication.Issues.ToDictionary(
                issue => issue.ToolName,
                StringComparer.Ordinal);
            var publishedNames = publication.PublishedFunctions
                .Select(function => function.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(effectCapable, descriptor =>
            {
                Assert.DoesNotContain(descriptor.ToolName, publishedNames);
                Assert.True(issueByName.TryGetValue(descriptor.ToolName, out var issue));
                Assert.NotNull(issue);
                Assert.Equal(
                    McpCapabilityPublicationIssueCode.MutationBoundaryUnavailable,
                    issue.Code);
            });
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void NonIdempotentNetworkOperationWithoutApproval_IsStillWithheldByMutationBoundary()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var search = AIFunctionFactory.Create(
                (Func<string, string>)(topic => topic),
                AliCapabilityCatalog.SearchCurrentWebName,
                "Search current web sources.");
            var owner = CreateOwner(
                root,
                [search],
                [search.Name],
                includeReconcilers: true);
            var descriptor = Assert.Single(owner.CapturePlanning().Registry.Descriptors);
            Assert.Equal(CapabilityEffectKind.LocalMutation, descriptor.Effect.Kind);
            Assert.True(descriptor.Effect.UsesNetwork);
            Assert.False(descriptor.Effect.SupportsIdempotency);
            Assert.True(descriptor.Effect.RequiresDurableEffectAdapter);
            Assert.False(descriptor.Permission.RequiresApproval);

            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([search], [search.Name]),
                owner,
                TestContext.Current.CancellationToken);

            Assert.Empty(publication.PublishedFunctions);
            var issue = Assert.Single(publication.Issues);
            Assert.Equal(search.Name, issue.ToolName);
            Assert.Equal(
                McpCapabilityPublicationIssueCode.MutationBoundaryUnavailable,
                issue.Code);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void ApprovalBearingPureRead_IsWithheldWithoutAnOutgoingApprovalBridge()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var read = AIFunctionFactory.Create(
                (Func<string, string>)(path => path),
                AliCapabilityCatalog.FileReadName,
                "Read one exact local file.");
            var owner = CreateOwner(
                root,
                [read],
                [read.Name]);
            var descriptor = Assert.Single(owner.CapturePlanning().Registry.Descriptors);
            Assert.False(descriptor.Effect.IsMutation);
            Assert.False(descriptor.Effect.WritesLocalData);
            Assert.False(descriptor.Effect.StartsProcesses);
            Assert.False(descriptor.Effect.ChangesSystemState);

            var publication = McpCapabilityPublicationGate.Publish(
                Catalog([read], [read.Name]),
                owner,
                TestContext.Current.CancellationToken);

            Assert.Empty(publication.Tools);
            Assert.Empty(publication.PublishedFunctions);
            var issue = Assert.Single(publication.Issues);
            Assert.Equal(read.Name, issue.ToolName);
            Assert.Equal(McpCapabilityPublicationIssueCode.ApprovalUnavailable, issue.Code);
            Assert.Contains("approval", issue.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void StandaloneHeadlessOwner_UsesSameFailClosedPublicationBoundary()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var known = AIFunctionFactory.Create(
                (Func<string>)(() => "now"),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var unknown = AIFunctionFactory.Create(
                (Func<string>)(() => "unknown"),
                "headless_unknown_tool",
                "Unknown.");
            var catalog = Catalog([known, unknown], [known.Name, unknown.Name]);
            var owner = McpCapabilityPublicationGate.CreateStandaloneOwner(root, catalog);

            var publication = McpCapabilityPublicationGate.Publish(
                catalog,
                owner,
                TestContext.Current.CancellationToken);

            Assert.Single(publication.Tools);
            Assert.Equal(known.Name, Assert.Single(publication.PublishedFunctions).Name);
            Assert.Contains(publication.Issues, issue =>
                issue.ToolName == unknown.Name
                && issue.Code == McpCapabilityPublicationIssueCode.MissingCanonicalDescriptor);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PublishedCapabilityInventory_ReportsOnlyEffectiveOutgoingTools()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var inventory = AIFunctionFactory.Create(
                (Func<CoordinatorCapabilityResult>)(() => new CoordinatorCapabilityResult(
                    "stale",
                    [])),
                AliCapabilityCatalog.ListAvailableToolsName,
                "List tools.");
            var localTime = AIFunctionFactory.Create(
                (Func<string>)(() => "now"),
                AliCapabilityCatalog.GetCurrentLocalTimeName,
                "Local time.");
            var reminder = AIFunctionFactory.Create(
                (Func<string, string, string>)((title, dueAtLocal) => $"{title}:{dueAtLocal}"),
                AliCapabilityCatalog.CreateCalendarEventName,
                "Create reminder.");
            var catalog = Catalog(
                [inventory, localTime, reminder],
                [inventory.Name, localTime.Name, reminder.Name]);
            var owner = McpCapabilityPublicationGate.CreateStandaloneOwner(root, catalog);

            var publication = McpCapabilityPublicationGate.Publish(
                catalog,
                owner,
                TestContext.Current.CancellationToken);
            var publishedInventory = Assert.Single(
                publication.PublishedFunctions,
                function => function.Name == AliCapabilityCatalog.ListAvailableToolsName);
            var result = await publishedInventory.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);
            var serialized = result?.ToString() ?? string.Empty;

            Assert.Contains("2 effective tool(s)", serialized, StringComparison.Ordinal);
            Assert.Contains(localTime.Name, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(reminder.Name, serialized, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static CapabilitySettingsSnapshotOwner CreateOwner(
        string root,
        IReadOnlyList<AIFunction> functions,
        IReadOnlyList<string> enabledOutgoingNames,
        bool includeReconcilers = false)
    {
        var registry = AliProductionCapabilityCatalog.CreateRegistry(functions);
        var runtime = new CapabilityRuntimeAvailability(
            "test-user",
            "test-runtime-v1",
            functions.Select(function => CapabilityRuntimeToolRegistration.Create(
                function,
                AliProductionCapabilityCatalog.GetSchemaFactoryId(function.Name))),
            "test-provider-v1",
            [AliProductionCapabilityCatalog.ProviderId],
            null,
            "test-permission-v1",
            ["ali-tool-permission-v1"],
            "test-mcp-v1",
            [],
            enabledOutgoingNames,
            "test-reconciler-v1",
            includeReconcilers
                ? registry.Descriptors
                    .Where(descriptor => descriptor.Effect.ReconcilerId is not null)
                    .Select(descriptor => descriptor.Effect.ReconcilerId!)
                : [],
            enforceReconcilerAvailability: true);
        return CapabilitySettingsSnapshotOwner.Open(root, registry, runtime);
    }

    private static McpServerFunctionCatalog Catalog(
        IReadOnlyList<AIFunction> functions,
        IReadOnlyList<string> enabledNames,
        Func<string>? activeUserIdAccessor = null,
        Func<string>? activeUserRevisionAccessor = null) =>
        new(
            functions.ToDictionary(function => function.Name, StringComparer.Ordinal),
            enabledNames.Select(name => new McpServerToolPolicy
            {
                Name = name,
                Description = $"Policy for {name}",
                Enabled = true
            }).ToArray(),
            "test-user",
            activeUserIdAccessor ?? (() => "test-user"),
            "test-user-selection-v1",
            activeUserRevisionAccessor ?? (static () => "test-user-selection-v1"));

    private static CapabilityRuntimeStateSnapshot StateFrom(
        CapabilityRuntimeAvailability runtime,
        string? activeUserId = null,
        string? permissionRevision = null) =>
        new(
            activeUserId ?? runtime.ActiveUserId,
            runtime.ProviderRevision,
            runtime.ReadyProviderIds,
            runtime.TargetResolution,
            permissionRevision ?? runtime.PermissionRevision,
            runtime.AllowedPermissionPolicyIds,
            runtime.McpRevision,
            runtime.ReadyIncomingMcpToolNames,
            runtime.EnabledOutgoingMcpToolNames,
            runtime.ReconcilerRevision,
            runtime.AvailableReconcilerIds,
            runtime.EnforceReconcilerAvailability,
            runtime.EnforceResolvedTargetBinding);

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "AliMcpCapabilityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
