using Ali.Modules.Capabilities;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Ali.Framework.Tests.Capabilities;

public sealed class IncomingMcpCapabilityCatalogTests
{
    [Fact]
    public async Task EquivalentFreshTransportSessions_HaveTheSameDurableCapabilityBinding()
    {
        var resolved = Resolved(
            Function("Files", "read_document", () => "document"),
            readOnly: true,
            requiresApproval: false);
        await using var first = new McpToolSession(
            [resolved],
            [],
            [],
            "settings-a",
            static () => "settings-a",
            sessionId: "transport-one");
        await using var second = new McpToolSession(
            [resolved],
            [],
            [],
            "settings-a",
            static () => "settings-a",
            sessionId: "transport-two");

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(first.SessionRevision, second.SessionRevision);
        Assert.Equal(
            first.CaptureSnapshot().BoundaryRevision,
            second.CaptureSnapshot().BoundaryRevision);
        Assert.Equal(
            IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), first).CatalogRevision,
            IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), second).CatalogRevision);
    }

    [Fact]
    public async Task ConfiguredReadOnlyTool_JoinsCanonicalTerminalAndRemainsCallable()
    {
        var invoked = 0;
        var settingsRevision = "settings-a";
        var function = Function(
            "Files",
            "read_document",
            () =>
            {
                invoked++;
                return "document";
            });
        await using var session = Session(
            [Resolved(function, readOnly: true, requiresApproval: false)],
            () => settingsRevision);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);

        var registered = Assert.Single(catalog.Tools);
        Assert.Equal(CapabilityRegistrationKind.Mcp, registered.Descriptor.RegistrationKind);
        Assert.Equal(CapabilityEffectKind.ExternalRead, registered.Descriptor.Effect.Kind);
        Assert.False(registered.Descriptor.Permission.RequiresApproval);
        Assert.Contains(registered.Descriptor.ProviderId, catalog.ProviderIds);

        var root = TemporaryRoot();
        try
        {
            var baseState = BaseState();
            var owner = OpenOwner(root, catalog, baseState, catalog.Tools.Select(tool => (AITool)tool.Function));
            var terminal = new TerminalCapabilityEnforcementProvider(
                owner,
                () => catalog.CreateRuntimeState(baseState));
            var projected = await terminal.ApplyTerminalContextAsync(new AIContext
            {
                Tools = catalog.Tools.Select(tool => (AITool)tool.Function).ToArray()
            }, TestContext.Current.CancellationToken);
            var guarded = Assert.Single(projected.Tools!.OfType<AIFunction>());

            Assert.Null(guarded.GetService<ApprovalRequiredAIFunction>());
            var result = await guarded.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken);
            Assert.Equal("document", result?.ToString());
            Assert.Equal(1, invoked);
            Assert.Contains(function.Name, owner.CapturePlanning().Runtime.ReadyIncomingMcpToolNames);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DisabledIncompleteCollidingAndSchemaDriftTools_AreIndividuallyQuarantined()
    {
        var safe = Function("Safe", "read", static () => "safe");
        var disabled = Function("Disabled", "write", static () => "disabled");
        var incomplete = Function("Incomplete", "run", static () => "incomplete");
        var firstCollision = Function("Collision", "same", static () => "first");
        var secondCollision = Function("Collision", "same", static () => "second");
        var drift = Function("Drift", "read", static () => "drift");
        var oversized = AIFunctionFactory.Create(
            (Func<string>)(static () => "oversized"),
            McpClientManager.BuildModelToolName(
                new McpServerProfile
                {
                    Id = "server-oversized",
                    Name = "Oversized"
                },
                "read",
                ConfiguredFingerprint("Oversized", "read")),
            new string('x', 2000));
        var oversizedResolved = new McpResolvedTool(
            oversized,
            "server-oversized",
            "Oversized",
            "read",
            ConfiguredEnabled: true,
            RequiresApproval: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            ConfiguredDeclarationFingerprint: ConfiguredFingerprint("Oversized", "read"),
            InvocationTimeoutSeconds: 30,
            SchemaFingerprint: CapabilitySchemaIdentity.Calculate(oversized));
        await using var session = Session(
        [
            Resolved(safe, readOnly: true),
            Resolved(disabled, enabled: false),
            Resolved(incomplete, serverId: ""),
            Resolved(firstCollision),
            Resolved(secondCollision),
            Resolved(drift) with { SchemaFingerprint = "stale-schema" },
            oversizedResolved
        ]);

        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);

        Assert.Equal(safe.Name, Assert.Single(catalog.Tools).Function.Name);
        Assert.Contains(catalog.Issues, issue => issue.Code == IncomingMcpCapabilityIssueCode.DisabledPolicy);
        Assert.Contains(catalog.Issues, issue => issue.Code == IncomingMcpCapabilityIssueCode.IncompleteConfiguration);
        Assert.Equal(
            2,
            catalog.Issues.Count(issue => issue.Code == IncomingMcpCapabilityIssueCode.ToolNameCollision));
        Assert.Contains(catalog.Issues, issue => issue.Code == IncomingMcpCapabilityIssueCode.SchemaIdentityMismatch);
        Assert.Contains(catalog.Issues, issue => issue.Code == IncomingMcpCapabilityIssueCode.DeclarationTooLarge);
        Assert.All(catalog.Issues, issue =>
        {
            Assert.DoesNotContain(issue.ToolIdentity, candidate => candidate is '\r' or '\n');
            Assert.DoesNotContain("_", issue.ToolIdentity, StringComparison.Ordinal);
            Assert.DoesNotContain(disabled.Name, issue.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(firstCollision.Name, issue.Message, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(
            catalog.Registry.Descriptors,
            descriptor => descriptor.ToolName == disabled.Name
                || descriptor.ToolName == incomplete.Name
                || descriptor.ToolName == firstCollision.Name
                || descriptor.ToolName == drift.Name);
    }

    [Fact]
    public async Task UnsupportedRemoteSchema_IsWithheldBeforeProjectionOrInvocation()
    {
        var template = Function("Unsupported", "read", static () => "must not run");
        using var schemaDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "value": { "type": "string" }
              },
              "not": { "required": ["value"] }
            }
            """);
        var unsupported = new SchemaOverrideAIFunction(
            template,
            schemaDocument.RootElement);
        var resolved = Resolved(unsupported, readOnly: true);
        await using var session = Session([resolved]);

        var discoveryAccepted = McpClientManager.TryValidateRemoteDeclaration(
            unsupported,
            out var discoveryReason);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);

        Assert.False(discoveryAccepted);
        Assert.Equal("unsupported schema dialect", discoveryReason);
        Assert.Empty(catalog.Tools);
        Assert.Contains(
            catalog.Issues,
            issue => issue.Code == IncomingMcpCapabilityIssueCode.UnsupportedSchemaDialect);
        Assert.DoesNotContain(
            catalog.Registry.Descriptors,
            descriptor => string.Equals(descriptor.ToolName, unsupported.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistedApprovalChoiceIsAuthoritativeWhileEffectRemainsConservative()
    {
        var read = Function("Read", "get", static () => "read");
        var mutation = Function("Write", "put", static () => "write");
        var destructive = Function("Delete", "remove", static () => "delete");
        await using var session = Session(
        [
            Resolved(read, readOnly: true, requiresApproval: false),
            Resolved(mutation, readOnly: false, requiresApproval: false),
            Resolved(destructive, readOnly: true, destructive: true, requiresApproval: false)
        ]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var byName = catalog.Tools.ToDictionary(tool => tool.Function.Name, StringComparer.Ordinal);

        Assert.False(byName[read.Name].Descriptor.Permission.RequiresApproval);
        Assert.Equal(CapabilityEffectKind.ExternalRead, byName[read.Name].Descriptor.Effect.Kind);
        Assert.False(byName[mutation.Name].Descriptor.Permission.RequiresApproval);
        Assert.Equal(CapabilityEffectKind.ExternalMutation, byName[mutation.Name].Descriptor.Effect.Kind);
        Assert.False(byName[destructive.Name].Descriptor.Permission.RequiresApproval);
        Assert.Equal(CapabilityEffectKind.Destructive, byName[destructive.Name].Descriptor.Effect.Kind);

        var policy = new AliToolPermissionPolicy(static () => null);
        Assert.Null(policy.Apply(read, byName[read.Name].Descriptor.Permission.RequiresApproval)
            .GetService<ApprovalRequiredAIFunction>());
        Assert.Null(policy.Apply(mutation, byName[mutation.Name].Descriptor.Permission.RequiresApproval)
            .GetService<ApprovalRequiredAIFunction>());
        Assert.Null(policy.Apply(destructive, byName[destructive.Name].Descriptor.Permission.RequiresApproval)
            .GetService<ApprovalRequiredAIFunction>());
    }

    [Fact]
    public async Task CatalogToolCountBoundary_AcceptsThirtyTwoAndWithholdsOnlyDeterministicOverflow()
    {
        var resolved = Enumerable.Range(0, IncomingMcpCapabilityCatalog.MaximumAcceptedToolCount + 1)
            .Select(index => Resolved(
                Function($"Server{index:D2}", "read", () => index.ToString()),
                readOnly: true))
            .ToArray();
        await using var atBoundarySession = Session(
            resolved.Take(IncomingMcpCapabilityCatalog.MaximumAcceptedToolCount).ToArray());
        await using var oneOverSession = Session(resolved);

        var atBoundary = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), atBoundarySession);
        var oneOver = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), oneOverSession);

        Assert.Equal(IncomingMcpCapabilityCatalog.MaximumAcceptedToolCount, atBoundary.Tools.Count);
        Assert.DoesNotContain(
            atBoundary.Issues,
            issue => issue.Code == IncomingMcpCapabilityIssueCode.CatalogLimitExceeded);
        Assert.Equal(IncomingMcpCapabilityCatalog.MaximumAcceptedToolCount, oneOver.Tools.Count);
        Assert.Single(
            oneOver.Issues,
            issue => issue.Code == IncomingMcpCapabilityIssueCode.CatalogLimitExceeded);
        var expectedAccepted = resolved
            .OrderBy(tool => tool.Function.Name, StringComparer.Ordinal)
            .ThenBy(tool => tool.ServerId, StringComparer.Ordinal)
            .ThenBy(tool => tool.OriginalName, StringComparer.Ordinal)
            .Take(IncomingMcpCapabilityCatalog.MaximumAcceptedToolCount)
            .Select(tool => tool.Function.Name);
        Assert.Equal(
            expectedAccepted,
            oneOver.Tools.Select(tool => tool.Function.Name));
    }

    [Fact]
    public void CumulativeDeclarationBoundary_AcceptsExactLimitAndRejectsOneCharacterOver()
    {
        Assert.True(IncomingMcpCapabilityCatalog.CanAcceptDeclaration(
            acceptedToolCount: 0,
            cumulativeDeclarationCharacters:
                IncomingMcpCapabilityCatalog.MaximumCumulativeDeclarationCharacters - 1,
            candidateDeclarationCharacters: 1));
        Assert.False(IncomingMcpCapabilityCatalog.CanAcceptDeclaration(
            acceptedToolCount: 0,
            cumulativeDeclarationCharacters:
                IncomingMcpCapabilityCatalog.MaximumCumulativeDeclarationCharacters,
            candidateDeclarationCharacters: 1));
    }

    [Fact]
    public async Task SettingsChangeAfterPlanning_BlocksWithoutCallingRemoteTool()
    {
        var invoked = 0;
        var settingsRevision = "settings-a";
        var function = Function("Settings", "read", () =>
        {
            invoked++;
            return "called";
        });
        await using var session = Session(
            [Resolved(function, readOnly: true)],
            () => settingsRevision);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var root = TemporaryRoot();
        try
        {
            var baseState = BaseState();
            var owner = OpenOwner(root, catalog, baseState, [catalog.Tools[0].Function]);
            var terminal = new TerminalCapabilityEnforcementProvider(
                owner,
                () => catalog.CreateRuntimeState(baseState));
            var projected = await terminal.ApplyTerminalContextAsync(new AIContext
            {
                Tools = [catalog.Tools[0].Function]
            }, TestContext.Current.CancellationToken);
            var guarded = Assert.Single(projected.Tools!.OfType<AIFunction>());

            settingsRevision = "settings-b";
            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(
                await guarded.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.DependencyId is "planning-publication" or "mcp");
            Assert.DoesNotContain(function.Name, owner.CapturePlanning().Runtime.ReadyIncomingMcpToolNames);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DisconnectAfterPlanning_BlocksWithoutCallingRemoteTool()
    {
        var invoked = 0;
        var function = Function("Disconnect", "read", () =>
        {
            invoked++;
            return "called";
        });
        await using var session = Session([Resolved(function, readOnly: true)]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var root = TemporaryRoot();
        try
        {
            var baseState = BaseState();
            var owner = OpenOwner(root, catalog, baseState, [catalog.Tools[0].Function]);
            var terminal = new TerminalCapabilityEnforcementProvider(
                owner,
                () => catalog.CreateRuntimeState(baseState));
            var projected = await terminal.ApplyTerminalContextAsync(new AIContext
            {
                Tools = [catalog.Tools[0].Function]
            }, TestContext.Current.CancellationToken);
            var guarded = Assert.Single(projected.Tools!.OfType<AIFunction>());

            session.MarkDisconnected();
            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(
                await guarded.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.DependencyId is "planning-publication" or "mcp");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ActiveUserAbaAfterPlanning_BlocksIncomingTool()
    {
        var invoked = 0;
        var activeUserRevision = "user-selection-1";
        var function = Function("Identity", "read", () =>
        {
            invoked++;
            return "called";
        });
        await using var session = Session([Resolved(function, readOnly: true)]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var root = TemporaryRoot();
        try
        {
            var baseState = BaseState(activeUserId: "user-a");
            var owner = OpenOwner(root, catalog, baseState, [catalog.Tools[0].Function]);
            var terminal = new TerminalCapabilityEnforcementProvider(
                owner,
                () => catalog.CreateRuntimeState(baseState),
                activeUserIdAccessor: static () => "user-a",
                activeUserRevisionAccessor: () => activeUserRevision);
            var projected = await terminal.ApplyTerminalContextAsync(new AIContext
            {
                Tools = [catalog.Tools[0].Function]
            }, TestContext.Current.CancellationToken);
            var guarded = Assert.Single(projected.Tools!.OfType<AIFunction>());

            activeUserRevision = "user-selection-3";
            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(
                await guarded.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, invoked);
            Assert.Contains(blocked.Reasons, reason => reason.DependencyId == "active-user-selection");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SharedCapabilitySettingsAbaAfterPlanning_BlocksIncomingTool()
    {
        var invoked = 0;
        var function = Function("Settings", "read", () =>
        {
            invoked++;
            return "called";
        });
        await using var session = Session([Resolved(function, readOnly: true)]);
        var baseRegistry = EmptyRegistry();
        var catalog = IncomingMcpCapabilityCatalog.Build(baseRegistry, session);
        var root = TemporaryRoot();
        try
        {
            var baseState = BaseState();
            var sharedInventory = CapabilityTerminalToolInventory.Create([], baseRegistry);
            var sharedRuntime = CapabilityRuntimeAvailabilityFactory.Create(
                sharedInventory,
                baseState);
            var sharedOwner = CapabilitySettingsSnapshotOwner.Open(
                root,
                baseRegistry,
                sharedRuntime);
            var turnOwner = OpenOwner(root, catalog, baseState, [catalog.Tools[0].Function]);
            var terminal = new TerminalCapabilityEnforcementProvider(
                turnOwner,
                () => catalog.CreateRuntimeState(baseState),
                invocationBoundaryRevisionAccessor: () =>
                    sharedOwner.CaptureSettings().Stamp.PublicationRevision,
                invocationBoundaryDependencyId: "capability-settings-publication",
                invocationBoundaryChangedMessage: "Capability settings changed.");
            var projected = await terminal.ApplyTerminalContextAsync(
                new AIContext { Tools = [catalog.Tools[0].Function] },
                TestContext.Current.CancellationToken);
            var guarded = Assert.Single(projected.Tools!.OfType<AIFunction>());

            var first = sharedOwner.CaptureSettings();
            var firstSelections = first.Rows.ToDictionary(
                row => row.GroupId,
                row => row.Enabled,
                StringComparer.Ordinal);
            firstSelections[CapabilityGroupIds.SpecialistsAndWorkflows] = false;
            Assert.Equal(
                CapabilitySettingsMutationStatus.Saved,
                sharedOwner.TrySaveRows(
                    first.Stamp,
                    firstSelections).Status);
            var second = sharedOwner.CaptureSettings();
            var secondSelections = second.Rows.ToDictionary(
                row => row.GroupId,
                row => row.Enabled,
                StringComparer.Ordinal);
            secondSelections[CapabilityGroupIds.SpecialistsAndWorkflows] = true;
            Assert.Equal(
                CapabilitySettingsMutationStatus.Saved,
                sharedOwner.TrySaveRows(
                    second.Stamp,
                    secondSelections).Status);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(
                await guarded.InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken));
            Assert.Equal(0, invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.DependencyId == "capability-settings-publication");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnreadableExternalPlanningBoundary_ReturnsBlockedToolWithoutCrashingOrSpinning()
    {
        var invoked = 0;
        var function = Function("Boundary", "read", () =>
        {
            invoked++;
            return "called";
        });
        await using var session = Session([Resolved(function, readOnly: true)]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var root = TemporaryRoot();
        try
        {
            var baseState = BaseState();
            var owner = OpenOwner(root, catalog, baseState, [catalog.Tools[0].Function]);
            var terminal = new TerminalCapabilityEnforcementProvider(
                owner,
                () => catalog.CreateRuntimeState(baseState),
                invocationBoundaryRevisionAccessor: static () =>
                    throw new IOException("locked"),
                invocationBoundaryDependencyId: "test-boundary",
                invocationBoundaryUnavailableMessage: "Test boundary unavailable");

            var projected = await terminal.ApplyTerminalContextAsync(
                new AIContext { Tools = [catalog.Tools[0].Function] },
                TestContext.Current.CancellationToken);
            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(
                await Assert.Single(projected.Tools!.OfType<AIFunction>()).InvokeAsync(
                    new AIFunctionArguments(),
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, invoked);
            Assert.Contains(blocked.Reasons, reason =>
                reason.DependencyId == "test-boundary"
                && reason.Message.Contains("IOException", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SessionDisposal_IsIdempotentDisposesOwnedResourcesAndInvalidatesReadiness()
    {
        var first = new RecordingAsyncDisposable();
        var second = new RecordingAsyncDisposable();
        var session = Session(
            [Resolved(Function("Dispose", "read", static () => "read"), readOnly: true)],
            resources: [first, second]);

        Assert.True(session.CaptureSnapshot().Ready);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.True(session.IsDisposed);
        Assert.False(session.CaptureSnapshot().Ready);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task NonCooperativeAsyncDisposal_IsBoundedAndStillRevokesSession()
    {
        var hanging = new HangingAsyncDisposable();
        var session = Session(
            [Resolved(Function("Dispose", "read", static () => "read"), readOnly: true)],
            resources: [hanging]);

        var started = DateTimeOffset.UtcNow;
        await session.DisposeAsync();
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(session.IsDisposed);
        Assert.False(session.CaptureSnapshot().Ready);
        Assert.Equal(1, hanging.DisposeCount);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Disposal took {elapsed}.");
    }

    [Fact]
    public async Task OversizedRemoteResponse_IsReplacedBeforeItCanGrowPlannerContext()
    {
        var function = Function(
            "Response",
            "read",
            () => new string('x', McpToolSessionAvailabilityAIFunction.MaximumSerializedResponseBytes));
        await using var session = Session([Resolved(function, readOnly: true)]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);

        var response = Assert.IsType<McpToolResponseWithheldResult>(
            await Assert.Single(catalog.Tools).Function.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.False(response.Success);
        Assert.True(response.Invoked);
        Assert.True(response.OutcomeUnknown);
        Assert.False(response.RetrySafe);
        Assert.Equal("response-withheld", response.Status);
        Assert.Contains("do not retry automatically", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Message.Length < 300);
        Assert.DoesNotContain("server-response", session.CaptureSnapshot().ReadyServerIds);
    }

    [Fact]
    public async Task UnexpectedRemoteCancellation_ReturnsFailureMarkerAndDisconnectsOnlyThatServer()
    {
        var cancelled = Function(
            "Cancelled",
            "read",
            static () => throw new OperationCanceledException("remote cancellation"));
        var healthy = Function("Healthy", "read", static () => "healthy");
        await using var session = Session(
        [
            Resolved(cancelled, serverId: "server-cancelled", readOnly: true),
            Resolved(healthy, serverId: "server-healthy", readOnly: true)
        ]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var byName = catalog.Tools.ToDictionary(tool => tool.Function.Name, StringComparer.Ordinal);

        var failed = Assert.IsType<McpToolInvocationFailedResult>(
            await byName[cancelled.Name].Function.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));

        Assert.False(failed.Success);
        Assert.True(failed.Invoked);
        Assert.True(failed.OutcomeUnknown);
        Assert.False(failed.RetrySafe);
        Assert.Equal("server-failed", failed.Status);
        var snapshot = session.CaptureSnapshot();
        Assert.DoesNotContain("server-cancelled", snapshot.ReadyServerIds);
        Assert.Contains("server-healthy", snapshot.ReadyServerIds);
        Assert.Equal(
            "healthy",
            (await byName[healthy.Name].Function.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken))?.ToString());
    }

    [Fact]
    public async Task NonCooperativeInvocationTimeout_DisconnectsOnlyThatServerAndReturnsPromptly()
    {
        var neverCompletes = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hung = AIFunctionFactory.Create(
            (Func<Task<string>>)(() => neverCompletes.Task),
            McpClientManager.BuildModelToolName(
                new McpServerProfile
                {
                    Id = "server-hung",
                    Name = "Hung"
                },
                "read",
                ConfiguredFingerprint("Hung", "read")),
            "Test tool read from Hung.");
        var healthy = Function("Healthy", "read", static () => "healthy");
        await using var session = Session(
        [
            Resolved(hung, serverId: "server-hung", readOnly: true, invocationTimeoutSeconds: 1),
            Resolved(healthy, serverId: "server-healthy", readOnly: true)
        ]);
        var catalog = IncomingMcpCapabilityCatalog.Build(EmptyRegistry(), session);
        var byName = catalog.Tools.ToDictionary(tool => tool.Function.Name, StringComparer.Ordinal);

        var started = DateTimeOffset.UtcNow;
        var timedOut = Assert.IsType<McpToolInvocationTimedOutResult>(
            await byName[hung.Name].Function.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(timedOut.Success);
        Assert.True(timedOut.Invoked);
        Assert.True(timedOut.OutcomeUnknown);
        Assert.False(timedOut.RetrySafe);
        Assert.Equal("timed-out", timedOut.Status);
        Assert.Contains("must not be retried", timedOut.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Invocation took {elapsed}.");
        var snapshot = session.CaptureSnapshot();
        Assert.DoesNotContain("server-hung", snapshot.ReadyServerIds);
        Assert.Contains("server-healthy", snapshot.ReadyServerIds);
        Assert.Equal(
            "healthy",
            (await byName[healthy.Name].Function.InvokeAsync(
                new AIFunctionArguments(),
                TestContext.Current.CancellationToken))?.ToString());
    }

    private static CapabilitySettingsSnapshotOwner OpenOwner(
        string root,
        IncomingMcpCapabilityCatalog catalog,
        CapabilityRuntimeStateSnapshot baseState,
        IEnumerable<AITool> tools)
    {
        var inventory = CapabilityTerminalToolInventory.Create(tools, catalog.Registry);
        var runtime = CapabilityRuntimeAvailabilityFactory.Create(
            inventory,
            catalog.CreateRuntimeState(baseState));
        return CapabilitySettingsSnapshotOwner.Open(root, catalog.Registry, runtime);
    }

    private static CapabilityRuntimeStateSnapshot BaseState(string activeUserId = "user-a") =>
        new(
            activeUserId,
            providerRevision: "base-provider-revision",
            readyProviderIds: [],
            targetResolution: null,
            permissionRevision: "permission-revision",
            allowedPermissionPolicyIds: ["ali-tool-permission-v1"],
            mcpRevision: "base-mcp-revision",
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: [],
            reconcilerRevision: "base-reconciler-revision",
            availableReconcilerIds: []);

    private static CanonicalCapabilityRegistry EmptyRegistry() => new([], []);

    private static AIFunction Function(
        string serverName,
        string originalName,
        Func<string> implementation) =>
        AIFunctionFactory.Create(
            implementation,
            McpClientManager.BuildModelToolName(
                new McpServerProfile
                {
                    Id = $"server-{serverName.ToLowerInvariant()}",
                    Name = serverName
                },
                originalName,
                ConfiguredFingerprint(serverName, originalName)),
            $"Test tool {originalName} from {serverName}.");

    private static McpResolvedTool Resolved(
        AIFunction function,
        string? serverId = null,
        bool enabled = true,
        bool requiresApproval = false,
        bool readOnly = false,
        bool destructive = false,
        int invocationTimeoutSeconds = 30)
    {
        var description = function.Description;
        var marker = description.IndexOf(" from ", StringComparison.Ordinal);
        var isTestDescription = description.StartsWith("Test tool ", StringComparison.Ordinal)
            && marker > "Test tool ".Length;
        var serverName = isTestDescription
            ? description[(marker + " from ".Length)..].TrimEnd('.')
            : function.Name.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .FirstOrDefault() ?? "Server";
        serverName = char.ToUpperInvariant(serverName[0]) + serverName[1..];
        var originalName = isTestDescription
            ? description["Test tool ".Length..marker]
            : function.Name.Split('_', StringSplitOptions.RemoveEmptyEntries) is { Length: > 4 } parts
                ? string.Join('_', parts.Skip(2).Take(parts.Length - 4))
                : "tool";
        var configuredFingerprint = ConfiguredFingerprint(serverName, originalName);
        return new McpResolvedTool(
            function,
            serverId ?? $"server-{serverName.ToLowerInvariant()}",
            serverName,
            originalName,
            enabled,
            requiresApproval,
            readOnly,
            destructive,
            configuredFingerprint,
            invocationTimeoutSeconds,
            CapabilitySchemaIdentity.Calculate(function));
    }

    private static string ConfiguredFingerprint(string serverName, string originalName)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("test-mcp-configured-declaration-v1");
        revision.Add(serverName);
        revision.Add(originalName);
        return revision.Finish();
    }

    private static McpToolSession Session(
        IReadOnlyList<McpResolvedTool> tools,
        Func<string>? settingsRevisionAccessor = null,
        IReadOnlyList<object>? resources = null)
    {
        settingsRevisionAccessor ??= static () => "settings-a";
        return new McpToolSession(
            tools,
            [],
            resources ?? [],
            "settings-a",
            settingsRevisionAccessor,
            "test-session");
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AliIncomingMcpCapabilityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HangingAsyncDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _neverCompletes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return new ValueTask(_neverCompletes.Task);
        }
    }

    private sealed class SchemaOverrideAIFunction(
        AIFunction inner,
        JsonElement schema) : DelegatingAIFunction(inner)
    {
        public override JsonElement JsonSchema { get; } = schema.Clone();
    }
}
