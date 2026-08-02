using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Ali.Modules.UserMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class TerminalCapabilityEnforcementProviderTests
{
    [Fact]
    public async Task EffectiveInventory_ReportsOnlyToolsCallableInThePublishedSnapshot()
    {
        const string description = "Return the effective capability inventory.";
        var staticResult = new CoordinatorCapabilityResult(
            "stale static inventory",
            [
                new CoordinatorCapability(AliCapabilityCatalog.ListAvailableToolsName, description),
                new CoordinatorCapability(AliCapabilityCatalog.GitStatusName, "Git status")
            ]);
        var canonicalList = AIFunctionFactory.Create(
            (Func<CoordinatorCapabilityResult>)(() => staticResult),
            AliCapabilityCatalog.ListAvailableToolsName,
            description);
        var canonicalGit = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonicalList, canonicalGit]);
        ToggleGroup(fixture.Owner, CapabilityGroupIds.DevOpsArchitectureQuality);
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => fixture.State,
            functionProjector: AliAgentHarnessRunner.ProjectEffectiveInventory);

        var output = await provider.ApplyTerminalContextAsync(
            new AIContext { Tools = new AITool[] { canonicalList, canonicalGit } },
            TestContext.Current.CancellationToken);

        var enforced = Assert.Single(output.Tools!.OfType<AIFunction>());
        var encoded = Assert.IsType<JsonElement>(await enforced.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken));
        var result = Assert.IsType<CoordinatorCapabilityResult>(
            encoded.Deserialize<CoordinatorCapabilityResult>(JsonSerializerOptions.Web));
        Assert.Equal(
            [AliCapabilityCatalog.ListAvailableToolsName],
            result.Tools.Select(tool => tool.Name));
        Assert.Contains("1 effective model-callable tools", result.Status, StringComparison.Ordinal);
        Assert.Contains("1 registered tool(s) are currently unavailable", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalProvider_PreservesInstructionsAndMessagesAndReplacesAccumulatedTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonicalGit = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var canonicalList = Function(AliCapabilityCatalog.ListAvailableToolsName, "list schema");
        var fixture = new Fixture([canonicalGit, canonicalList]);
        var invoked = 0;
        var actualGit = Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++);
        var mismatchedList = Function(AliCapabilityCatalog.ListAvailableToolsName, "changed list schema");
        var unknown = Function("mcp_unassigned_execute", "unknown schema");
        CapabilityTerminalIssueReport? issueReport = null;
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => fixture.State,
            issueCallback: report => issueReport = report);
        var messages = new[] { new ChatMessage(ChatRole.User, "keep this exact message") };
        var input = new AIContext
        {
            Instructions = "keep these exact instructions",
            Messages = messages,
            Tools = new AITool[] { actualGit, mismatchedList, unknown }
        };

        var output = await provider.ApplyTerminalContextAsync(input, cancellationToken);

        Assert.Equal(input.Instructions, output.Instructions);
        Assert.Same(messages, output.Messages);
        var enforced = Assert.Single(output.Tools!);
        Assert.Equal(AliCapabilityCatalog.GitStatusName, Assert.IsAssignableFrom<AIFunction>(enforced).Name);
        await ((AIFunction)enforced).InvokeAsync(new AIFunctionArguments(), cancellationToken);
        Assert.Equal(1, invoked);

        Assert.NotNull(issueReport);
        Assert.Equal(2, issueReport.Issues.Count);
        Assert.Contains(issueReport.Issues, issue =>
            issue.Code == CapabilityTerminalToolIssueCode.UnknownTool
            && issue.ToolIdentity == unknown.Name);
        Assert.Contains(issueReport.Issues, issue =>
            issue.Code == CapabilityTerminalToolIssueCode.SchemaIdentityMismatch
            && issue.ToolIdentity == mismatchedList.Name);
        Assert.Equal(2, issueReport.QuarantinedCapabilities.Count);
        Assert.Equal(2, fixture.Owner.CapturePlanning().Resolution.QuarantinedCapabilities.Count);
        Assert.Equal(3, fixture.Owner.CapturePlanning().Runtime.RegisteredToolsByName.Count);
    }

    [Fact]
    public async Task ReadyIncomingMcpTool_WithoutCanonicalMetadata_IsStillQuarantinedFailClosed()
    {
        const string incomingName = "mcp_external_ready_tool";
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var incoming = Function(incomingName, "dynamic incoming MCP schema");
        var fixture = new Fixture([canonical]);
        var initialState = fixture.State;
        var stateWithReadyIncoming = new CapabilityRuntimeStateSnapshot(
            initialState.ActiveUserId,
            initialState.ProviderRevision,
            initialState.ReadyProviderIds,
            initialState.TargetResolution,
            initialState.PermissionRevision,
            initialState.AllowedPermissionPolicyIds,
            "mcp-with-ready-incoming",
            readyIncomingMcpToolNames: [incomingName],
            enabledOutgoingMcpToolNames: initialState.EnabledOutgoingMcpToolNames,
            reconcilerRevision: initialState.ReconcilerRevision,
            availableReconcilerIds: initialState.AvailableReconcilerIds);
        CapabilityTerminalIssueReport? issueReport = null;
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => stateWithReadyIncoming,
            issueCallback: report => issueReport = report);

        var output = await provider.ApplyTerminalContextAsync(
            new AIContext { Tools = new AITool[] { canonical, incoming } },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [AliCapabilityCatalog.GitStatusName],
            output.Tools!.OfType<AIFunction>().Select(tool => tool.Name));
        Assert.False(fixture.Owner.CapturePlanning().Resolution.TryGetTool(incomingName, out _));
        Assert.Contains(fixture.Owner.CapturePlanning().Resolution.QuarantinedCapabilities, item =>
            item.ToolName == incomingName
            && item.Code == CapabilityQuarantineReasonCode.MissingCanonicalDescriptor);
        Assert.NotNull(issueReport);
        Assert.Contains(issueReport.Issues, issue =>
            issue.ToolIdentity == incomingName
            && issue.Code == CapabilityTerminalToolIssueCode.UnknownTool);
    }

    [Fact]
    public async Task DuplicateActualNames_AreEncodedInRuntimeAndQuarantinedTogether()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var first = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var second = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var inventory = CapabilityTerminalToolInventory.Create(
            new AITool[] { first, second },
            fixture.Registry);
        var duplicate = Assert.Single(inventory.Issues);
        Assert.Equal(CapabilityTerminalToolIssueCode.DuplicateToolName, duplicate.Code);
        Assert.StartsWith("ali.runtime.duplicate-schema.",
            Assert.Single(inventory.Registrations).SchemaFactoryId,
            StringComparison.Ordinal);
        var provider = new TerminalCapabilityEnforcementProvider(fixture.Owner, () => fixture.State);

        var output = await provider.ApplyTerminalContextAsync(new AIContext
        {
            Tools = new AITool[] { first, second }
        }, cancellationToken);

        Assert.Empty(output.Tools!);
        var quarantine = Assert.Single(fixture.Owner.CapturePlanning().Resolution.QuarantinedCapabilities);
        Assert.Equal(AliCapabilityCatalog.GitStatusName, quarantine.ToolName);
        Assert.Equal(CapabilityQuarantineReasonCode.SchemaIdentityMismatch, quarantine.Code);
    }

    [Fact]
    public async Task ApprovalMarker_IsPreservedAndDescriptorApprovalIsAdded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonicalGit = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var canonicalDelete = Function(AliCapabilityCatalog.FileDeleteName, "delete schema");
        var fixture = new Fixture([canonicalGit, canonicalDelete]);
        var markedGit = new TestDelegatingAIFunction(new ApprovalRequiredAIFunction(
            Function(AliCapabilityCatalog.GitStatusName, "git schema")));
        var unmarkedDelete = Function(AliCapabilityCatalog.FileDeleteName, "delete schema");
        var provider = new TerminalCapabilityEnforcementProvider(fixture.Owner, () => fixture.State);

        var output = await provider.ApplyTerminalContextAsync(new AIContext
        {
            Tools = new AITool[] { markedGit, unmarkedDelete }
        }, cancellationToken);

        var byName = output.Tools!.Cast<AIFunction>().ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        Assert.NotNull(byName[AliCapabilityCatalog.GitStatusName].GetService<ApprovalRequiredAIFunction>());
        Assert.NotNull(byName[AliCapabilityCatalog.FileDeleteName].GetService<ApprovalRequiredAIFunction>());
    }

    [Fact]
    public async Task PermissionProjection_RetriesAfterLockedTrustedLockedAba()
    {
        var root = NewPermissionRoot();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var store = new AgentToolPermissionStore(root);
            store.SetProfile(AgentPermissionProfile.LockedDown);
            var raw = Function(AliCapabilityCatalog.SearchCurrentWebName, "web schema");
            var policy = new AliToolPermissionPolicy(() => null, () => store.CurrentProfile);
            var profileAware = policy.ApplyProfileAware(raw);
            var fixture = new Fixture([profileAware]);
            var projectionCalls = 0;
            var provider = new TerminalCapabilityEnforcementProvider(
                fixture.Owner,
                () => fixture.CreateState(
                    "profile-aba",
                    store.CaptureSnapshot().Revision),
                functionProjector: (function, resolution) =>
                {
                    var snapshot = store.CaptureSnapshot();
                    if (Interlocked.Increment(ref projectionCalls) == 1)
                    {
                        store.SetProfile(AgentPermissionProfile.TrustedWorkstation);
                        store.SetProfile(AgentPermissionProfile.LockedDown);
                    }

                    var profile = string.Equals(
                            snapshot.Revision,
                            resolution.PermissionRevision,
                            StringComparison.Ordinal)
                        ? snapshot.Profile
                        : AgentPermissionProfile.LockedDown;
                    return Assert.IsType<CapabilityPermissionProjectionAIFunction>(function)
                        .Project(profile);
                },
                permissionRevisionAccessor: () => store.CaptureSnapshot().Revision);

            var output = await provider.ApplyTerminalContextAsync(
                new AIContext { Tools = new AITool[] { profileAware } },
                cancellationToken);

            Assert.Equal(2, projectionCalls);
            Assert.Equal(AgentPermissionProfile.LockedDown, store.CurrentProfile);
            var enforced = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));
            Assert.NotNull(enforced.GetService<ApprovalRequiredAIFunction>());
            Assert.Equal(
                store.CaptureSnapshot().Revision,
                fixture.Owner.CapturePlanning().Resolution.PermissionRevision);
        }
        finally
        {
            DeletePermissionRoot(root);
        }
    }

    [Fact]
    public async Task DeferredStandingGrant_CurrentCallExecutesThenRevokeStalesNextLease()
    {
        var root = NewPermissionRoot();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var store = new AgentToolPermissionStore(root);
            var user = new ActiveUser("user-a", "Alice", false, "test");
            var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
            var fixture = new Fixture([canonical]);
            var invoked = 0;
            var actual = Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++);
            var provider = new TerminalCapabilityEnforcementProvider(
                fixture.Owner,
                () => fixture.CreateState(
                    "standing-grant",
                    store.CaptureSnapshot().Revision),
                permissionRevisionAccessor: () => store.CaptureSnapshot().Revision);
            var firstOutput = await provider.ApplyTerminalContextAsync(
                new AIContext { Tools = new AITool[] { actual } },
                cancellationToken);
            var firstGuarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(firstOutput.Tools!));
            var plannedRevision = store.CaptureSnapshot().Revision;
            var tracker = new PendingStandingPermissionTracker();
            var call = new FunctionCallContent(
                "call-standing",
                AliCapabilityCatalog.GitStatusName,
                new Dictionary<string, object?> { ["path"] = "." });

            Assert.True(tracker.TryQueue(
                user,
                AgentToolApprovalChoice.AlwaysAllowTool,
                call,
                out var queueReason), queueReason);
            Assert.Empty(store.ListForUser(user.StableId));
            Assert.Equal(plannedRevision, store.CaptureSnapshot().Revision);

            var currentResult = await firstGuarded.InvokeAsync(
                new AIFunctionArguments(),
                cancellationToken);
            var completion = tracker.Complete(
                new FunctionResultContent(call.CallId, currentResult));
            var pending = Assert.IsType<PendingStandingPermission>(completion.Permission);
            Assert.Equal(PendingStandingPermissionCompletionStatus.ReadyToSave, completion.Status);
            var saved = store.Save(
                pending.ActiveUser,
                pending.ToolName,
                pending.Scope,
                pending.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

            Assert.Equal(1, invoked);
            Assert.NotEqual(plannedRevision, store.CaptureSnapshot().Revision);

            var secondOutput = await provider.ApplyTerminalContextAsync(
                new AIContext { Tools = new AITool[] { actual } },
                cancellationToken);
            var secondGuarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(secondOutput.Tools!));
            Assert.True(store.Revoke(user.StableId, saved.Id));

            var revokedResult = await secondGuarded.InvokeAsync(
                new AIFunctionArguments(),
                cancellationToken);

            var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(revokedResult);
            Assert.Contains(blocked.Reasons, reason =>
                reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
                && reason.DependencyId == "permission-profile");
            Assert.Equal(1, invoked);
        }
        finally
        {
            DeletePermissionRoot(root);
        }
    }

    [Fact]
    public async Task DeferredStandingGrant_IsDiscardedWhenMatchingInvocationIsBlocked()
    {
        var root = NewPermissionRoot();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var store = new AgentToolPermissionStore(root);
            var user = new ActiveUser("user-a", "Alice", false, "test");
            var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
            var fixture = new Fixture([canonical]);
            var provider = new TerminalCapabilityEnforcementProvider(
                fixture.Owner,
                () => fixture.CreateState(
                    "blocked-standing-grant",
                    store.CaptureSnapshot().Revision),
                permissionRevisionAccessor: () => store.CaptureSnapshot().Revision);
            var output = await provider.ApplyTerminalContextAsync(
                new AIContext { Tools = new AITool[] { canonical } },
                cancellationToken);
            var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));
            var tracker = new PendingStandingPermissionTracker();
            var call = new FunctionCallContent(
                "call-blocked-standing",
                AliCapabilityCatalog.GitStatusName);
            Assert.True(tracker.TryQueue(
                user,
                AgentToolApprovalChoice.AlwaysAllowTool,
                call,
                out var queueReason), queueReason);

            store.SetProfile(AgentPermissionProfile.LockedDown);
            var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);
            var completion = tracker.Complete(new FunctionResultContent(call.CallId, result));

            Assert.IsType<CapabilityInvocationBlockedResult>(result);
            Assert.Equal(PendingStandingPermissionCompletionStatus.CapabilityBlocked, completion.Status);
            Assert.Empty(store.ListForUser(user.StableId));
            Assert.Equal(0, tracker.Count);
        }
        finally
        {
            DeletePermissionRoot(root);
        }
    }

    [Fact]
    public void DeferredStandingGrant_IsDiscardedForEveryUncertainMcpMarkerShape()
    {
        object?[] results =
        [
            new McpToolResponseWithheldResult(
                "mcp_test_tool",
                "response-withheld",
                "The target-side outcome is unknown."),
            JsonSerializer.SerializeToElement(new
            {
                success = false,
                invoked = true,
                outcomeUnknown = true,
                retrySafe = false
            }),
            "{\"success\":false,\"invoked\":true,\"outcomeUnknown\":true,\"retrySafe\":false}",
            new Dictionary<string, object?>
            {
                ["success"] = false,
                ["invoked"] = true,
                ["outcomeUnknown"] = true,
                ["retrySafe"] = false
            }
        ];
        var user = new ActiveUser("user-a", "Alice", false, "test");

        for (var index = 0; index < results.Length; index++)
        {
            var tracker = new PendingStandingPermissionTracker();
            var call = new FunctionCallContent(
                $"call-uncertain-{index}",
                "mcp_test_tool");
            Assert.True(tracker.TryQueue(
                user,
                AgentToolApprovalChoice.AlwaysAllowTool,
                call,
                out var reason), reason);

            var completion = tracker.Complete(
                new FunctionResultContent(call.CallId, results[index]));

            Assert.Equal(PendingStandingPermissionCompletionStatus.ToolFailed, completion.Status);
            Assert.Null(completion.Permission);
            Assert.Equal(0, tracker.Count);
        }
    }

    [Fact]
    public async Task InvocationLease_RevalidatesImmediatelyAndReturnsDataOnlyBlockedResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var invoked = 0;
        var actual = Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++);
        var provider = new TerminalCapabilityEnforcementProvider(fixture.Owner, () => fixture.State);
        var output = await provider.ApplyTerminalContextAsync(new AIContext
        {
            Tools = new AITool[] { actual }
        }, cancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));

        DisableGroup(fixture.Owner, CapabilityGroupIds.DevOpsArchitectureQuality);
        var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.False(blocked.Success);
        Assert.False(blocked.Invoked);
        Assert.Equal("blocked", blocked.Status);
        Assert.Equal(AliCapabilityCatalog.GitStatusName, blocked.ToolName);
        Assert.Contains(blocked.Reasons, reason => reason.Code == nameof(CapabilityAvailabilityReasonCode.GroupDisabled));
        Assert.Contains(blocked.Reasons, reason => reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale));
        Assert.Equal(0, invoked);
        var json = JsonSerializer.Serialize(blocked);
        Assert.Contains("\"status\":\"blocked\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delegate", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvocationLease_BlocksAfterDisableReenableAbaEvenWhenContentRevisionsReturn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var invoked = 0;
        var provider = new TerminalCapabilityEnforcementProvider(fixture.Owner, () => fixture.State);
        var output = await provider.ApplyTerminalContextAsync(
            new AIContext
            {
                Tools = new AITool[]
                {
                    Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++)
                }
            },
            cancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));
        var planned = fixture.Owner.CapturePlanning();

        ToggleGroup(fixture.Owner, CapabilityGroupIds.DevOpsArchitectureQuality);
        ToggleGroup(fixture.Owner, CapabilityGroupIds.DevOpsArchitectureQuality);
        var restored = fixture.Owner.CapturePlanning();

        Assert.Equal(planned.Settings.Revision, restored.Settings.Revision);
        Assert.Equal(planned.Resolution.ResolutionRevision, restored.Resolution.ResolutionRevision);
        Assert.NotEqual(planned.PublicationRevision, restored.PublicationRevision);
        Assert.True(restored.Resolution.TryGetTool(AliCapabilityCatalog.GitStatusName, out _));
        var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            && reason.DependencyId == "planning-publication");
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task InvocationLease_BlocksLiveActiveUserChangeBeforeInnerInvocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var invoked = 0;
        var activeUserId = fixture.State.ActiveUserId;
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => fixture.State,
            activeUserIdAccessor: () => activeUserId);
        var output = await provider.ApplyTerminalContextAsync(
            new AIContext
            {
                Tools = new AITool[]
                {
                    Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++)
                }
            },
            cancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));

        activeUserId = "user-b";
        var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.False(blocked.Invoked);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            && reason.DependencyId == "active-user");
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task InvocationLease_BlocksActiveUserAbaWhenStableIdReturnsToPlannedUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var invoked = 0;
        var activeUserId = fixture.State.ActiveUserId;
        var activeUserRevision = "active-user-selection-v1:1";
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => fixture.State,
            activeUserIdAccessor: () => activeUserId,
            activeUserRevisionAccessor: () => activeUserRevision);
        var output = await provider.ApplyTerminalContextAsync(
            new AIContext
            {
                Tools = new AITool[]
                {
                    Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++)
                }
            },
            cancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));

        activeUserId = "user-b";
        activeUserRevision = "active-user-selection-v1:2";
        activeUserId = fixture.State.ActiveUserId;
        activeUserRevision = "active-user-selection-v1:3";
        var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            && reason.DependencyId == "active-user-selection");
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task InvocationLease_BlocksLivePermissionRevisionChangeBeforeInnerInvocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var invoked = 0;
        var permissionRevision = fixture.State.PermissionRevision;
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => fixture.State,
            permissionRevisionAccessor: () => permissionRevision);
        var output = await provider.ApplyTerminalContextAsync(
            new AIContext
            {
                Tools = new AITool[]
                {
                    Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++)
                }
            },
            cancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));

        permissionRevision = "permissions-changed";
        var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.False(blocked.Invoked);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            && reason.DependencyId == "permission-profile");
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task MatchingRuntimeRevision_UsesNoPublicationHotPath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var before = fixture.Owner.CaptureSettings();
        var provider = new TerminalCapabilityEnforcementProvider(fixture.Owner, () => fixture.State);

        var output = await provider.ApplyTerminalContextAsync(new AIContext
        {
            Tools = new AITool[] { Function(AliCapabilityCatalog.GitStatusName, "git schema") }
        }, cancellationToken);

        Assert.Single(output.Tools!);
        var after = fixture.Owner.CaptureSettings();
        Assert.Equal(before.PublicationRevision, after.PublicationRevision);
        Assert.Equal(before.RuntimeRevision, after.RuntimeRevision);
    }

    [Fact]
    public async Task ChangedRuntimeState_IsPublishedWithExactTerminalInventory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var changedState = fixture.CreateState("changed");
        var before = fixture.Owner.CaptureSettings();
        var provider = new TerminalCapabilityEnforcementProvider(fixture.Owner, () => changedState);
        var exact = Function(AliCapabilityCatalog.GitStatusName, "git schema");

        await provider.ApplyTerminalContextAsync(
            new AIContext { Tools = new AITool[] { exact } },
            cancellationToken);

        var after = fixture.Owner.CaptureSettings();
        Assert.NotEqual(before.PublicationRevision, after.PublicationRevision);
        Assert.NotEqual(before.RuntimeRevision, after.RuntimeRevision);
        Assert.Equal(changedState.ProviderRevision, after.ProviderRevision);
        var registration = Assert.Single(fixture.Owner.CapturePlanning().Runtime.RegisteredToolsByName).Value;
        Assert.Equal(AliProductionCapabilityCatalog.GetSchemaFactoryId(exact.Name), registration.SchemaFactoryId);
        Assert.Equal(CapabilitySchemaIdentity.Calculate(exact), registration.SchemaFingerprint);
    }

    [Fact]
    public async Task InvocationLease_ResamplesLiveRuntimeAndBlocksProviderLossBeforeInvocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var invoked = 0;
        var liveState = fixture.State;
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () => liveState);
        var output = await provider.ApplyTerminalContextAsync(
            new AIContext
            {
                Tools = new AITool[]
                {
                    Function(AliCapabilityCatalog.GitStatusName, "git schema", () => invoked++)
                }
            },
            cancellationToken);
        var guarded = Assert.IsAssignableFrom<AIFunction>(Assert.Single(output.Tools!));

        liveState = fixture.CreateState("provider-down", readyProviderIds: []);
        var result = await guarded.InvokeAsync(new AIFunctionArguments(), cancellationToken);

        var blocked = Assert.IsType<CapabilityInvocationBlockedResult>(result);
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.ProviderUnavailable));
        Assert.Contains(blocked.Reasons, reason =>
            reason.Code == nameof(CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            && reason.DependencyId == "planning-publication");
        Assert.Equal(liveState.ProviderRevision, fixture.Owner.CapturePlanning().Runtime.ProviderRevision);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public async Task RuntimePublication_RetriesPastRepeatedConflictsWithoutAnArbitraryCap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var canonical = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var fixture = new Fixture([canonical]);
        var changedState = fixture.CreateState("after-conflicts");
        var accessorCalls = 0;
        var provider = new TerminalCapabilityEnforcementProvider(
            fixture.Owner,
            () =>
            {
                accessorCalls++;
                if (accessorCalls <= 12)
                {
                    ToggleGroup(fixture.Owner, CapabilityGroupIds.DevOpsArchitectureQuality);
                }
                return changedState;
            });

        var output = await provider.ApplyTerminalContextAsync(new AIContext
        {
            Tools = new AITool[] { Function(AliCapabilityCatalog.GitStatusName, "git schema") }
        }, cancellationToken);

        Assert.Equal(13, accessorCalls);
        Assert.Single(output.Tools!);
        Assert.Equal(changedState.ProviderRevision, fixture.Owner.CaptureSettings().ProviderRevision);
    }

    [Fact]
    public void InventoryAndRuntimeFactories_AreStableAndOrderIndependent()
    {
        var git = Function(AliCapabilityCatalog.GitStatusName, "git schema");
        var list = Function(AliCapabilityCatalog.ListAvailableToolsName, "list schema");
        var fixture = new Fixture([git, list]);
        var forward = CapabilityTerminalToolInventory.Create(new AITool[] { git, list }, fixture.Registry);
        var reverse = CapabilityTerminalToolInventory.Create(new AITool[] { list, git }, fixture.Registry);

        Assert.Equal(forward.Revision, reverse.Revision);
        Assert.Equal(
            CapabilityRuntimeAvailabilityFactory.Create(forward, fixture.State).RuntimeRevision,
            CapabilityRuntimeAvailabilityFactory.Create(reverse, fixture.State).RuntimeRevision);
        Assert.Equal(64, forward.Revision.Length);
        Assert.Equal(64, CapabilityRuntimeAvailabilityFactory.Create(forward, fixture.State).RuntimeRevision.Length);
    }

    private static AIFunction Function(
        string name,
        string description,
        Action? invoked = null) =>
        AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                invoked?.Invoke();
                return "ok";
            }),
            name,
            description);

    private static void DisableGroup(CapabilitySettingsSnapshotOwner owner, string groupId)
    {
        var before = owner.CaptureSettings();
        var selections = before.Rows.ToDictionary(row => row.GroupId, row => row.Enabled, StringComparer.Ordinal);
        selections[groupId] = false;
        var result = owner.TrySaveRows(before.Stamp, selections);
        Assert.Equal(CapabilitySettingsMutationStatus.Saved, result.Status);
    }

    private static void ToggleGroup(CapabilitySettingsSnapshotOwner owner, string groupId)
    {
        var before = owner.CaptureSettings();
        var selections = before.Rows.ToDictionary(row => row.GroupId, row => row.Enabled, StringComparer.Ordinal);
        selections[groupId] = !selections[groupId];
        var result = owner.TrySaveRows(before.Stamp, selections);
        Assert.Equal(CapabilitySettingsMutationStatus.Saved, result.Status);
    }

    private sealed class Fixture
    {
        public Fixture(IReadOnlyList<AIFunction> canonicalFunctions)
        {
            Registry = AliProductionCapabilityCatalog.CreateRegistry(canonicalFunctions);
            State = CreateState("initial");
            var inventory = CapabilityTerminalToolInventory.Create(canonicalFunctions, Registry);
            var runtime = CapabilityRuntimeAvailabilityFactory.Create(inventory, State);
            Owner = new CapabilitySettingsSnapshotOwner(
                Registry,
                new CapabilityResolver(),
                runtime,
                new MemoryPersistence());
        }

        public CanonicalCapabilityRegistry Registry { get; }

        public CapabilityRuntimeStateSnapshot State { get; }

        public CapabilitySettingsSnapshotOwner Owner { get; }

        public CapabilityRuntimeStateSnapshot CreateState(
            string suffix,
            string? permissionRevision = null,
            IEnumerable<string>? readyProviderIds = null) => new(
            "user-a",
            $"providers-{suffix}",
            readyProviderIds ?? [AliProductionCapabilityCatalog.ProviderId],
            targetResolution: null,
            permissionRevision ?? $"permissions-{suffix}",
            Registry.Descriptors.Select(descriptor => descriptor.Permission.PolicyId).Distinct(StringComparer.Ordinal),
            $"mcp-{suffix}",
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: Registry.Descriptors
                .Where(descriptor => descriptor.McpExposure.Exposed)
                .Select(descriptor => descriptor.ToolName),
            $"reconcilers-{suffix}",
            Registry.Descriptors
                .Where(descriptor => descriptor.Effect.ReconcilerId is not null)
                .Select(descriptor => descriptor.Effect.ReconcilerId!)
                .Distinct(StringComparer.Ordinal));
    }

    private sealed class MemoryPersistence : ICapabilityAvailabilitySettingsPersistence
    {
        private CapabilityAvailabilitySettings _settings = CapabilityAvailabilitySettings.CreateDefault();

        public CapabilityAvailabilityLoadResult Load() =>
            CapabilityAvailabilityLoadResult.Loaded(_settings);

        public CapabilityAvailabilitySaveResult Save(
            string expectedRevision,
            CapabilityAvailabilitySettings settings)
        {
            if (!string.Equals(expectedRevision, _settings.Revision, StringComparison.Ordinal))
            {
                return CapabilityAvailabilitySaveResult.Conflict(_settings);
            }

            _settings = new CapabilityAvailabilitySettings(settings.GroupSelections);
            return CapabilityAvailabilitySaveResult.Saved(_settings);
        }
    }

    private sealed class TestDelegatingAIFunction(AIFunction inner) : DelegatingAIFunction(inner);

    private static string NewPermissionRoot() => Path.Combine(
        Path.GetTempPath(),
        "AliTerminalPermissionTests",
        Guid.NewGuid().ToString("N"));

    private static void DeletePermissionRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
