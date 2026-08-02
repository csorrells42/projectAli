using Ali.Modules.Capabilities;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.Capabilities;

public sealed class CapabilityResolverTests
{
    [Fact]
    public void DisabledTaskGroups_LeaveOnlyReadyReservedProtocolCapabilities()
    {
        var protocol = CanonicalCapabilityRegistryTests.Descriptor(
            "protocol.list",
            "submit_orchestration_decision",
            groupId: null,
            tier: CapabilityTier.Protocol);
        var task = CanonicalCapabilityRegistryTests.Descriptor(
            "files.read",
            "file_access_read",
            CapabilityGroupIds.FilesAndArchives);
        var registry = CanonicalCapabilityRegistryTests.Registry([protocol, task]);

        var resolution = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(CapabilityAvailabilitySettings.CreateFailClosed()),
            Runtime([protocol, task]));

        Assert.Equal(new[] { protocol.Id }, resolution.EffectiveDescriptors.Select(item => item.Id));
        var unavailable = Assert.Single(resolution.UnavailableDescriptors);
        Assert.Equal(task.Id, unavailable.Descriptor.Id);
        Assert.Contains(unavailable.Reasons, reason => reason.Code == CapabilityAvailabilityReasonCode.GroupDisabled);
        Assert.Empty(resolution.EnabledGroups);
    }

    [Fact]
    public void Resolver_IntersectsEveryReadinessDimensionAndReturnsAllReasons()
    {
        var mutation = CanonicalCapabilityRegistryTests.Descriptor(
            "dotnet.apply",
            "dotnet_roslyn_apply",
            CapabilityGroupIds.CSharpDotNetRoslyn,
            providerId: "dotnet-roslyn",
            prerequisiteGroupIds: [CapabilityGroupIds.ProgrammingCore],
            effect: new CapabilityEffectDescriptor(
                CapabilityEffectKind.SourceMutation,
                "Applies a staged source changeset.",
                CapabilityMutationBoundary.StagedWorkspace,
                false,
                "source-changeset",
                true,
                true,
                false,
                false,
                false));
        var registry = CanonicalCapabilityRegistryTests.Registry([mutation]);
        var settings = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection(CapabilityGroupIds.CSharpDotNetRoslyn, false)
            .WithGroupSelection(CapabilityGroupIds.ProgrammingCore, false);

        var resolution = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(settings),
            EmptyRuntime());

        var unavailable = Assert.Single(resolution.UnavailableDescriptors);
        Assert.Equal(
            new[]
            {
                CapabilityAvailabilityReasonCode.GroupDisabled,
                CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled,
                CapabilityAvailabilityReasonCode.RuntimeToolMissing,
                CapabilityAvailabilityReasonCode.ProviderUnavailable,
                CapabilityAvailabilityReasonCode.PermissionUnavailable,
                CapabilityAvailabilityReasonCode.ReconcilerUnavailable
            },
            unavailable.Reasons.Select(reason => reason.Code));
    }

    [Fact]
    public void NativeMcpExportIsSeparateFromIncomingMcpReadiness()
    {
        var native = CanonicalCapabilityRegistryTests.Descriptor(
            "native.read",
            "native_read",
            CapabilityGroupIds.FilesAndArchives,
            mcpExposure: new CapabilityMcpExposure(true, "native_read"));
        var incoming = CanonicalCapabilityRegistryTests.Descriptor(
            "incoming.read",
            "mcp_server_read",
            CapabilityGroupIds.FilesAndArchives,
            registrationKind: CapabilityRegistrationKind.Mcp,
            providerId: "mcp:server",
            mcpExposure: new CapabilityMcpExposure(true, "incoming_public_read"));
        var registry = CanonicalCapabilityRegistryTests.Registry([native, incoming]);

        var resolution = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(CapabilityAvailabilitySettings.CreateDefault()),
            Runtime(
                [native, incoming],
                readyIncomingMcpToolNames: Array.Empty<string>(),
                enabledOutgoingMcpToolNames: [native.ToolName]));

        Assert.True(resolution.TryGetTool(native.ToolName, out _));
        Assert.Equal(native.Id, Assert.Single(resolution.OutgoingMcpDescriptors).Id);
        Assert.False(resolution.TryGetTool(incoming.ToolName, out _));
        Assert.Equal(
            CapabilityAvailabilityReasonCode.McpUnavailable,
            Assert.Single(Assert.Single(resolution.UnavailableDescriptors).Reasons).Code);

        var exportDisabled = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(CapabilityAvailabilitySettings.CreateDefault()),
            Runtime(
                [native, incoming],
                readyIncomingMcpToolNames: [incoming.ToolName],
                enabledOutgoingMcpToolNames: Array.Empty<string>()));
        Assert.True(exportDisabled.TryGetTool(native.ToolName, out _));
        Assert.True(exportDisabled.TryGetTool(incoming.ToolName, out _));
        Assert.DoesNotContain(
            exportDisabled.OutgoingMcpDescriptors,
            descriptor => descriptor.Id == incoming.Id);
        Assert.Empty(exportDisabled.OutgoingMcpDescriptors);
    }

    [Fact]
    public void OutgoingMcpAlias_IsEnabledByCanonicalToolNameAndPublishesAlias()
    {
        var descriptor = CanonicalCapabilityRegistryTests.Descriptor(
            "native.read",
            "native_internal_read",
            CapabilityGroupIds.FilesAndArchives,
            mcpExposure: new CapabilityMcpExposure(true, "public_read"));
        var registry = CanonicalCapabilityRegistryTests.Registry([descriptor]);
        var frozen = registry.Freeze(CapabilityAvailabilitySettings.CreateDefault());

        var canonicalEnabled = new CapabilityResolver().ResolvePlanning(
            frozen,
            Runtime(
                [descriptor],
                enabledOutgoingMcpToolNames: [descriptor.ToolName]));
        var outgoing = Assert.Single(canonicalEnabled.OutgoingMcpDescriptors);
        Assert.Equal(descriptor.ToolName, outgoing.ToolName);
        Assert.Equal("public_read", outgoing.McpExposure.PublishedName);

        var aliasOnlyEnabled = new CapabilityResolver().ResolvePlanning(
            frozen,
            Runtime(
                [descriptor],
                enabledOutgoingMcpToolNames: ["public_read"]));
        Assert.True(aliasOnlyEnabled.TryGetTool(descriptor.ToolName, out _));
        Assert.Empty(aliasOnlyEnabled.OutgoingMcpDescriptors);
    }

    [Fact]
    public void UnknownAndSchemaMismatchedRuntimeTools_AreQuarantined()
    {
        var known = CanonicalCapabilityRegistryTests.Descriptor(
            "known.read",
            "known_read",
            CapabilityGroupIds.FilesAndArchives);
        var registry = CanonicalCapabilityRegistryTests.Registry([known]);
        var wrongSchema = AIFunctionFactory.Create(
            (string value) => value,
            known.ToolName,
            "A different callable schema.");
        var unknown = AIFunctionFactory.Create(
            () => "unknown",
            "mcp_unknown_tool",
            "Unknown MCP tool.");

        var resolution = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(CapabilityAvailabilitySettings.CreateDefault()),
            Runtime(
                [known],
                registrations:
                [
                    CapabilityRuntimeToolRegistration.Create(wrongSchema, known.SchemaFactoryId),
                    CapabilityRuntimeToolRegistration.Create(unknown, "unknown.schema")
                ]));

        Assert.Equal(
            new[]
            {
                CapabilityQuarantineReasonCode.MissingCanonicalDescriptor,
                CapabilityQuarantineReasonCode.SchemaIdentityMismatch
            },
            resolution.QuarantinedCapabilities.Select(item => item.Code).Order());
        Assert.Contains(
            Assert.Single(resolution.UnavailableDescriptors).Reasons,
            reason => reason.Code == CapabilityAvailabilityReasonCode.RuntimeToolIdentityMismatch);
        Assert.False(resolution.TryGetTool(known.ToolName, out _));
    }

    [Fact]
    public void ProtocolCapabilities_StillRequireRuntimeProviderAndPermissionReadiness()
    {
        var protocol = CanonicalCapabilityRegistryTests.Descriptor(
            "protocol.list",
            "submit_orchestration_decision",
            groupId: null,
            tier: CapabilityTier.Protocol);
        var registry = CanonicalCapabilityRegistryTests.Registry([protocol]);

        var resolution = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(CapabilityAvailabilitySettings.CreateFailClosed()),
            EmptyRuntime());

        Assert.Equal(
            new[]
            {
                CapabilityAvailabilityReasonCode.RuntimeToolMissing,
                CapabilityAvailabilityReasonCode.ProviderUnavailable,
                CapabilityAvailabilityReasonCode.PermissionUnavailable
            },
            Assert.Single(resolution.UnavailableDescriptors).Reasons.Select(reason => reason.Code));
    }

    [Fact]
    public void ResolvedTargetLease_UsesOrPlanningAndExactTargetInvocation()
    {
        var generic = GenericResolvedTargetDescriptor();
        var registry = GenericRegistry(generic);
        var settings = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection(CapabilityGroupIds.Python, false);
        var planning = new CapabilityResolver().ResolvePlanning(
            registry.Freeze(settings),
            Runtime([generic]));
        var lease = planning.CreateInvocationLease(generic.ToolName, "test-publication-1");

        var pythonValidation = new CapabilityResolver().ValidateInvocation(
            registry.Freeze(settings),
            Runtime(
                [generic],
                targetResolution: new CapabilityTargetResolution(
                    "target-python",
                    "python-cpython",
                    "target-revision-1",
                    "providers-1")),
            lease,
            "target-python");
        var dotnetValidation = new CapabilityResolver().ValidateInvocation(
            registry.Freeze(settings),
            Runtime(
                [generic],
                targetResolution: new CapabilityTargetResolution(
                    "target-dotnet",
                    "dotnet-roslyn",
                    "target-revision-2",
                    "providers-1")),
            lease,
            "target-dotnet");

        Assert.Equal(new[] { "dotnet-roslyn" }, planning.EligibleProviderIdsByToolName[generic.ToolName]);
        Assert.True(lease.RequiresTargetResolution);
        Assert.False(pythonValidation.Success);
        Assert.Contains(
            pythonValidation.Reasons,
            reason => reason.Code == CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled
                      && reason.DependencyId == CapabilityGroupIds.Python);
        Assert.True(dotnetValidation.Success);
    }

    [Fact]
    public void AnySupportedGate_UsesOrSemanticsWithoutTargetResolution()
    {
        var generic = GenericAnySupportedDescriptor();
        var registry = GenericRegistry(generic);
        var settings = CapabilityAvailabilitySettings.CreateDefault()
            .WithGroupSelection(CapabilityGroupIds.Python, false);
        var runtime = Runtime(
            [generic],
            readyProviderIds: [generic.ProviderId, "dotnet-roslyn"]);
        var resolver = new CapabilityResolver();
        var planning = resolver.ResolvePlanning(registry.Freeze(settings), runtime);

        Assert.True(planning.TryGetTool(generic.ToolName, out _));
        Assert.Equal(
            new[] { "dotnet-roslyn" },
            planning.EligibleProviderIdsByToolName[generic.ToolName]);
        Assert.DoesNotContain(generic.ToolName, planning.RequiresTargetResolutionToolNames);

        var lease = planning.CreateInvocationLease(generic.ToolName, "test-publication-1");
        var validation = resolver.ValidateInvocation(
            registry.Freeze(settings),
            runtime,
            lease,
            boundTargetBindingId: null);

        Assert.False(lease.RequiresTargetResolution);
        Assert.True(validation.Success);
        Assert.NotNull(validation.Descriptor);
        Assert.Empty(validation.Reasons);

        var noProvidersSettings = settings.WithGroupSelection(
            CapabilityGroupIds.CSharpDotNetRoslyn,
            false);
        var noProviders = resolver.ResolvePlanning(
            registry.Freeze(noProvidersSettings),
            Runtime(
                [generic],
                readyProviderIds: [generic.ProviderId]));

        Assert.False(noProviders.TryGetTool(generic.ToolName, out _));
        Assert.Empty(noProviders.EligibleProviderIdsByToolName[generic.ToolName]);
        var unavailable = Assert.Single(noProviders.UnavailableDescriptors);
        Assert.Equal(generic.Id, unavailable.Descriptor.Id);
        Assert.Contains(
            unavailable.Reasons,
            reason => reason.Code == CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled
                      && reason.DependencyId == CapabilityGroupIds.CSharpDotNetRoslyn);
        Assert.Contains(
            unavailable.Reasons,
            reason => reason.Code == CapabilityAvailabilityReasonCode.PrerequisiteGroupDisabled
                      && reason.DependencyId == CapabilityGroupIds.Python);
    }

    [Fact]
    public void ResolvedTargetMetadata_PreservesLegacyInvocationUntilExactBindingCutover()
    {
        var generic = GenericResolvedTargetDescriptor();
        var registry = GenericRegistry(generic);
        var frozen = registry.Freeze(CapabilityAvailabilitySettings.CreateDefault());
        var runtime = Runtime(
            [generic],
            enforceResolvedTargetBinding: false);
        var resolver = new CapabilityResolver();
        var planning = resolver.ResolvePlanning(frozen, runtime);
        var lease = planning.CreateInvocationLease(generic.ToolName, "compatibility-publication");

        var validation = resolver.ValidateInvocation(
            frozen,
            runtime,
            lease,
            boundTargetBindingId: null);

        Assert.True(planning.TryGetTool(generic.ToolName, out _));
        Assert.True(lease.RequiresTargetResolution);
        Assert.True(validation.Success);
        Assert.Empty(validation.Reasons);
    }

    [Fact]
    public void ResolvedTargetLease_RejectsMissingMismatchedUnsupportedAndStaleTargets()
    {
        var generic = GenericResolvedTargetDescriptor();
        var registry = GenericRegistry(generic);
        var frozen = registry.Freeze(CapabilityAvailabilitySettings.CreateDefault());
        var lease = new CapabilityResolver()
            .ResolvePlanning(frozen, Runtime([generic]))
            .CreateInvocationLease(generic.ToolName, "test-publication-1");

        var missing = new CapabilityResolver().ValidateInvocation(
            frozen,
            Runtime([generic]),
            lease,
            "target-dotnet");
        var mismatch = new CapabilityResolver().ValidateInvocation(
            frozen,
            Runtime(
                [generic],
                targetResolution: new CapabilityTargetResolution(
                    "target-a",
                    "dotnet-roslyn",
                    "target-revision-1",
                    "providers-1")),
            lease,
            "target-b");
        var unsupported = new CapabilityResolver().ValidateInvocation(
            frozen,
            Runtime(
                [generic],
                targetResolution: new CapabilityTargetResolution(
                    "target-java",
                    "java-temurin",
                    "target-revision-2",
                    "providers-1")),
            lease,
            "target-java");
        var stale = new CapabilityResolver().ValidateInvocation(
            frozen,
            Runtime(
                [generic],
                targetResolution: new CapabilityTargetResolution(
                    "target-dotnet",
                    "dotnet-roslyn",
                    "target-revision-3",
                    "providers-old")),
            lease,
            "target-dotnet");

        Assert.Contains(missing.Reasons, reason => reason.Code == CapabilityAvailabilityReasonCode.TargetProviderUnresolved);
        Assert.Contains(mismatch.Reasons, reason => reason.Code == CapabilityAvailabilityReasonCode.TargetBindingMismatch);
        Assert.Contains(unsupported.Reasons, reason => reason.Code == CapabilityAvailabilityReasonCode.TargetProviderUnsupported);
        Assert.Contains(stale.Reasons, reason => reason.Code == CapabilityAvailabilityReasonCode.TargetResolutionStale);
    }

    [Theory]
    [InlineData("registry", "registry")]
    [InlineData("settings", "settings")]
    [InlineData("active-user", "active-user")]
    [InlineData("runtime", "runtime")]
    [InlineData("providers", "providers")]
    [InlineData("permissions", "permissions")]
    [InlineData("mcp", "mcp")]
    [InlineData("reconcilers", "reconcilers")]
    [InlineData("planning-resolution", "planning-resolution")]
    public void InvocationLease_RejectsEveryRevisionAndPlanningFingerprintChange(
        string changedDimension,
        string expectedDependencyId)
    {
        var descriptor = CanonicalCapabilityRegistryTests.Descriptor(
            "files.read",
            "file_access_read",
            CapabilityGroupIds.FilesAndArchives);
        var registry = CanonicalCapabilityRegistryTests.Registry([descriptor]);
        var settings = CapabilityAvailabilitySettings.CreateDefault();
        var resolver = new CapabilityResolver();
        var planning = resolver.ResolvePlanning(
            registry.Freeze(settings),
            Runtime([descriptor]));
        var lease = planning.CreateInvocationLease(descriptor.ToolName, "test-publication-1");
        var currentRegistry = registry.Freeze(settings);
        var currentRuntime = Runtime([descriptor]);

        switch (changedDimension)
        {
            case "registry":
                var changedDescriptor = descriptor with
                {
                    SemanticSearchText = descriptor.SemanticSearchText + " changed"
                };
                currentRegistry = CanonicalCapabilityRegistryTests.Registry([changedDescriptor]).Freeze(settings);
                currentRuntime = Runtime([changedDescriptor]);
                break;
            case "settings":
                currentRegistry = registry.Freeze(
                    settings.WithGroupSelection(CapabilityGroupIds.Python, false));
                break;
            case "active-user":
                currentRuntime = Runtime([descriptor], activeUserId: "user-b");
                break;
            case "runtime":
                currentRuntime = Runtime([descriptor], runtimeRevision: "runtime-2");
                break;
            case "providers":
                currentRuntime = Runtime([descriptor], providerRevision: "providers-2");
                break;
            case "permissions":
                currentRuntime = Runtime([descriptor], permissionRevision: "permissions-2");
                break;
            case "mcp":
                currentRuntime = Runtime([descriptor], mcpRevision: "mcp-2");
                break;
            case "reconcilers":
                currentRuntime = Runtime([descriptor], reconcilerRevision: "reconcilers-2");
                break;
            case "planning-resolution":
                currentRuntime = Runtime(
                    [descriptor],
                    readyProviderIds: [descriptor.ProviderId, "unused-ready-provider"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedDimension));
        }

        var validation = resolver.ValidateInvocation(
            currentRegistry,
            currentRuntime,
            lease,
            boundTargetBindingId: null);

        Assert.False(validation.Success);
        Assert.Null(validation.Descriptor);
        var staleDependencies = validation.Reasons
            .Where(reason => reason.Code == CapabilityAvailabilityReasonCode.InvocationLeaseStale)
            .Select(reason => reason.DependencyId)
            .ToArray();
        Assert.Equal(
            changedDimension == "planning-resolution"
                ? new[] { "planning-resolution" }
                : new[] { expectedDependencyId, "planning-resolution" },
            staleDependencies);
    }

    [Fact]
    public void RuntimeInputsAndPlanningResolution_AreImmutableAndScopedPerUser()
    {
        var descriptor = CanonicalCapabilityRegistryTests.Descriptor(
            "files.read",
            "file_access_read",
            CapabilityGroupIds.FilesAndArchives);
        var registrations = new List<CapabilityRuntimeToolRegistration>
        {
            CapabilityRuntimeToolRegistration.Create(descriptor.SchemaFactory(), descriptor.SchemaFactoryId)
        };
        var runtime = Runtime([descriptor], registrations: registrations, activeUserId: "user-a");
        var registry = CanonicalCapabilityRegistryTests.Registry([descriptor]);
        var frozen = registry.Freeze(CapabilityAvailabilitySettings.CreateDefault());

        var first = new CapabilityResolver().ResolvePlanning(frozen, runtime);
        registrations.Clear();
        var second = new CapabilityResolver().ResolvePlanning(
            frozen,
            Runtime([descriptor], activeUserId: "user-b"));

        Assert.True(runtime.RegisteredToolsByName.ContainsKey(descriptor.ToolName));
        Assert.True(first.TryGetTool(descriptor.ToolName, out _));
        Assert.NotEqual(first.ResolutionRevision, second.ResolutionRevision);
        Assert.Equal("user-a", first.ActiveUserId);
        Assert.Equal("user-b", second.ActiveUserId);
    }

    private static CapabilityDescriptor GenericResolvedTargetDescriptor() =>
        CanonicalCapabilityRegistryTests.Descriptor(
            "coding.build",
            "coding_build_project",
            CapabilityGroupIds.ProgrammingCore,
            providerId: "ali.coding.dispatch",
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.ResolvedTarget,
                ["dotnet-roslyn", "python-cpython"]));

    private static CapabilityDescriptor GenericAnySupportedDescriptor() =>
        CanonicalCapabilityRegistryTests.Descriptor(
            "coding.inspect",
            "coding_inspect_project",
            CapabilityGroupIds.ProgrammingCore,
            providerId: "ali.coding.dispatch",
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.AnySupported,
                ["dotnet-roslyn", "python-cpython"]));

    private static CanonicalCapabilityRegistry GenericRegistry(CapabilityDescriptor descriptor) =>
        CanonicalCapabilityRegistryTests.Registry(
            [descriptor],
            [
                new CapabilityProviderBinding("dotnet-roslyn", CapabilityGroupIds.CSharpDotNetRoslyn),
                new CapabilityProviderBinding("python-cpython", CapabilityGroupIds.Python)
            ]);

    private static CapabilityRuntimeAvailability EmptyRuntime() =>
        new(
            "user-a",
            "runtime-1",
            Array.Empty<CapabilityRuntimeToolRegistration>(),
            "providers-1",
            Array.Empty<string>(),
            null,
            "permissions-1",
            Array.Empty<string>(),
            "mcp-1",
            Array.Empty<string>(),
            Array.Empty<string>(),
            "reconcilers-1",
            Array.Empty<string>(),
            enforceReconcilerAvailability: true,
            enforceResolvedTargetBinding: true);

    private static CapabilityRuntimeAvailability Runtime(
        IReadOnlyList<CapabilityDescriptor> descriptors,
        IReadOnlyList<CapabilityRuntimeToolRegistration>? registrations = null,
        IReadOnlyList<string>? readyIncomingMcpToolNames = null,
        IReadOnlyList<string>? enabledOutgoingMcpToolNames = null,
        CapabilityTargetResolution? targetResolution = null,
        string activeUserId = "user-a",
        string runtimeRevision = "runtime-1",
        string providerRevision = "providers-1",
        IReadOnlyList<string>? readyProviderIds = null,
        string permissionRevision = "permissions-1",
        IReadOnlyList<string>? allowedPermissionPolicyIds = null,
        string mcpRevision = "mcp-1",
        string reconcilerRevision = "reconcilers-1",
        IReadOnlyList<string>? availableReconcilerIds = null,
        bool enforceReconcilerAvailability = true,
        bool enforceResolvedTargetBinding = true) =>
        new(
            activeUserId,
            runtimeRevision,
            registrations
            ?? descriptors
                .Select(item => CapabilityRuntimeToolRegistration.Create(
                    item.SchemaFactory(),
                    item.SchemaFactoryId))
                .ToArray(),
            providerRevision,
            readyProviderIds
            ?? descriptors
                .Select(item => item.ProviderId)
                .Concat(descriptors.SelectMany(item => item.ProviderGate.SupportedProviderIds))
                .Distinct(StringComparer.Ordinal),
            targetResolution,
            permissionRevision,
            allowedPermissionPolicyIds
            ?? descriptors.Select(item => item.Permission.PolicyId).Distinct(StringComparer.Ordinal),
            mcpRevision,
            readyIncomingMcpToolNames
            ?? descriptors
                .Where(item => item.RegistrationKind == CapabilityRegistrationKind.Mcp)
                .Select(item => item.ToolName)
                .ToArray(),
            enabledOutgoingMcpToolNames
            ?? descriptors
                .Where(item => item.McpExposure.Exposed)
                .Select(item => item.ToolName)
                .ToArray(),
            reconcilerRevision,
            availableReconcilerIds
            ?? descriptors
                .Where(item => item.Effect.ReconcilerId is not null)
                .Select(item => item.Effect.ReconcilerId!)
                .Distinct(StringComparer.Ordinal),
            enforceReconcilerAvailability,
            enforceResolvedTargetBinding);
}
