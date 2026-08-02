using System.Collections.ObjectModel;
using Ali.Modules.Capabilities;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Ali.Modules.Mcp;

internal sealed record McpServerFunctionCatalog(
    IReadOnlyDictionary<string, AIFunction> Functions,
    IReadOnlyList<McpServerToolPolicy> EnabledPolicies,
    string BoundActiveUserId,
    Func<string> ActiveUserIdAccessor,
    string BoundActiveUserRevision,
    Func<string> ActiveUserRevisionAccessor);

internal enum McpCapabilityPublicationIssueCode
{
    MissingCanonicalDescriptor,
    CapabilityUnavailable,
    ApprovalUnavailable,
    MissingFunctionImplementation,
    SchemaIdentityMismatch
}

internal sealed record McpCapabilityPublicationIssue(
    string ToolName,
    McpCapabilityPublicationIssueCode Code,
    string Message);

internal sealed class McpCapabilityPublicationResult
{
    internal McpCapabilityPublicationResult(
        IEnumerable<McpServerTool> tools,
        IEnumerable<AIFunction> publishedFunctions,
        IEnumerable<McpCapabilityPublicationIssue> issues,
        string resolutionRevision)
    {
        Tools = Array.AsReadOnly(tools.ToArray());
        PublishedFunctions = Array.AsReadOnly(publishedFunctions.ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
        ResolutionRevision = resolutionRevision;
    }

    public IReadOnlyList<McpServerTool> Tools { get; }

    internal IReadOnlyList<AIFunction> PublishedFunctions { get; }

    public IReadOnlyList<McpCapabilityPublicationIssue> Issues { get; }

    public string ResolutionRevision { get; }
}

/// <summary>
/// The final outgoing MCP publication boundary. A server policy can request a tool,
/// but only the effective descriptor from the shared capability snapshot may be
/// converted into an MCP server tool. Every published function also retains the
/// planning lease so a later settings or runtime change blocks invocation safely.
/// </summary>
internal static class McpCapabilityPublicationGate
{
    internal static CapabilitySettingsSnapshotOwner CreateStandaloneOwner(
        string dataRoot,
        McpServerFunctionCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(catalog);

        var build = AliProductionCapabilityCatalog.Build(catalog.Functions.Values);
        var inventory = CapabilityTerminalToolInventory.Create(
            catalog.Functions.Values.Cast<AITool>(),
            build.Registry);
        var outgoingToolNames = EnabledToolNames(catalog);
        var runtimeState = new CapabilityRuntimeStateSnapshot(
            activeUserId: catalog.BoundActiveUserId,
            providerRevision: "ali-mcp-provider-v1",
            readyProviderIds: AliProductionCapabilityCatalog.RegisteredProviderIds,
            targetResolution: null,
            permissionRevision: "ali-mcp-permission-v1",
            allowedPermissionPolicyIds: build.Registry.Descriptors
                .Select(descriptor => descriptor.Permission.PolicyId)
                .Distinct(StringComparer.Ordinal),
            mcpRevision: CalculateMcpRevision([], outgoingToolNames),
            readyIncomingMcpToolNames: [],
            enabledOutgoingMcpToolNames: outgoingToolNames,
            reconcilerRevision: "ali-mcp-reconciler-v1",
            availableReconcilerIds: []);
        var runtime = CapabilityRuntimeAvailabilityFactory.Create(inventory, runtimeState);
        return CapabilitySettingsSnapshotOwner.Open(dataRoot, build.Registry, runtime);
    }

    internal static McpCapabilityPublicationResult Publish(
        McpServerFunctionCatalog catalog,
        CapabilitySettingsSnapshotOwner owner,
        CancellationToken cancellationToken = default,
        Func<string>? invocationBoundaryRevisionAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(owner);

        var plannedInvocationBoundaryRevision = invocationBoundaryRevisionAccessor?.Invoke();
        var planning = PublishOutgoingAvailability(
            owner,
            EnabledToolNames(catalog),
            catalog.BoundActiveUserId,
            cancellationToken);
        var enabledNames = catalog.EnabledPolicies
            .Select(policy => policy.Name)
            .ToHashSet(StringComparer.Ordinal);
        var outgoingByName = planning.Resolution.OutgoingMcpDescriptors
            .ToDictionary(descriptor => descriptor.ToolName, StringComparer.Ordinal);
        var issues = new List<McpCapabilityPublicationIssue>();
        var candidates = new List<PublishedCandidate>();

        foreach (var toolName in enabledNames.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!catalog.Functions.TryGetValue(toolName, out var actualFunction))
            {
                issues.Add(new McpCapabilityPublicationIssue(
                    toolName,
                    McpCapabilityPublicationIssueCode.MissingFunctionImplementation,
                    $"Enabled MCP tool '{toolName}' has no invokable implementation and was not published."));
                continue;
            }

            if (!outgoingByName.TryGetValue(toolName, out var descriptor))
            {
                issues.Add(CreateUnavailableIssue(toolName, planning));
                continue;
            }

            if (descriptor.Permission.RequiresApproval
                || AliToolPermissionPolicy.RequiresApproval(
                    toolName,
                    AgentPermissionProfile.LockedDown))
            {
                issues.Add(new McpCapabilityPublicationIssue(
                    toolName,
                    McpCapabilityPublicationIssueCode.ApprovalUnavailable,
                    $"Enabled MCP tool '{toolName}' requires an approval path that the non-interactive outgoing server does not provide and was not published."));
                continue;
            }

            if (!TryCanonicalize(actualFunction, descriptor, out var canonicalFunction))
            {
                issues.Add(new McpCapabilityPublicationIssue(
                    toolName,
                    McpCapabilityPublicationIssueCode.SchemaIdentityMismatch,
                    $"Enabled MCP tool '{toolName}' does not match canonical schema '{descriptor.SchemaFactoryId}' and was not published."));
                continue;
            }

            candidates.Add(new PublishedCandidate(descriptor, canonicalFunction));
        }

        ReplaceCapabilityInventoryFunction(candidates, issues);

        var publishedFunctions = new List<AIFunction>(candidates.Count);
        var tools = new List<McpServerTool>(candidates.Count);
        var resolver = new CapabilityResolver();
        foreach (var candidate in candidates.OrderBy(
                     item => item.Descriptor.McpExposure.PublishedName,
                     StringComparer.Ordinal))
        {
            var lease = planning.CreateInvocationLease(candidate.Descriptor.ToolName);
            AIFunction publishedFunction = new CapabilityInvocationLeaseAIFunction(
                candidate.Function,
                owner,
                resolver,
                lease,
                targetBindingIdAccessor: null,
                activeUserIdAccessor: catalog.ActiveUserIdAccessor,
                invocationBoundaryRevisionAccessor: invocationBoundaryRevisionAccessor,
                plannedInvocationBoundaryRevision: plannedInvocationBoundaryRevision,
                activeUserRevisionAccessor: catalog.ActiveUserRevisionAccessor,
                plannedActiveUserRevision: catalog.BoundActiveUserRevision);
            var publishedName = candidate.Descriptor.McpExposure.PublishedName!;
            if (!string.Equals(publishedFunction.Name, publishedName, StringComparison.Ordinal))
            {
                publishedFunction = new McpFunctionMetadataAIFunction(
                    publishedFunction,
                    publishedName,
                    publishedFunction.Description);
            }

            publishedFunctions.Add(publishedFunction);
            tools.Add(McpServerTool.Create(publishedFunction));
        }

        return new McpCapabilityPublicationResult(
            tools,
            publishedFunctions,
            issues
                .OrderBy(issue => issue.ToolName, StringComparer.Ordinal)
                .ThenBy(issue => issue.Code),
            planning.Resolution.ResolutionRevision);
    }

    internal static string CalculateMcpRevision(
        IEnumerable<string> readyIncomingToolNames,
        IEnumerable<string> enabledOutgoingToolNames)
    {
        ArgumentNullException.ThrowIfNull(readyIncomingToolNames);
        ArgumentNullException.ThrowIfNull(enabledOutgoingToolNames);
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-mcp-availability-v1");
        revision.Add(readyIncomingToolNames
            .Order(StringComparer.Ordinal)
            .ToArray());
        revision.Add(enabledOutgoingToolNames
            .Order(StringComparer.Ordinal)
            .ToArray());
        return revision.Finish();
    }

    private static CapabilityPlanningPublication PublishOutgoingAvailability(
        CapabilitySettingsSnapshotOwner owner,
        IReadOnlySet<string> enabledOutgoingToolNames,
        string boundActiveUserId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = owner.CaptureSettings();
            var planning = owner.CapturePlanning();
            if (!string.Equals(
                    envelope.RegistryRevision,
                    planning.Registry.RegistryRevision,
                    StringComparison.Ordinal)
                || !string.Equals(
                    envelope.SettingsRevision,
                    planning.Settings.Revision,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var current = planning.Runtime;
            var mcpRevision = CalculateMcpRevision(
                current.ReadyIncomingMcpToolNames,
                enabledOutgoingToolNames);
            if (string.Equals(current.ActiveUserId, boundActiveUserId, StringComparison.Ordinal)
                && string.Equals(current.McpRevision, mcpRevision, StringComparison.Ordinal)
                && current.EnabledOutgoingMcpToolNames.SetEquals(enabledOutgoingToolNames))
            {
                return planning;
            }

            var runtime = CloneWithOutgoingAvailability(
                current,
                boundActiveUserId,
                mcpRevision,
                enabledOutgoingToolNames);
            if (!owner.TryPublishRuntime(envelope.Stamp, runtime, out _))
            {
                continue;
            }

            var published = owner.CapturePlanning();
            if (string.Equals(published.Runtime.ActiveUserId, boundActiveUserId, StringComparison.Ordinal)
                && string.Equals(published.Runtime.McpRevision, mcpRevision, StringComparison.Ordinal)
                && published.Runtime.EnabledOutgoingMcpToolNames.SetEquals(enabledOutgoingToolNames))
            {
                return published;
            }
        }
    }

    private static CapabilityRuntimeAvailability CloneWithOutgoingAvailability(
        CapabilityRuntimeAvailability current,
        string boundActiveUserId,
        string mcpRevision,
        IReadOnlySet<string> enabledOutgoingToolNames)
    {
        var state = new CapabilityRuntimeStateSnapshot(
            boundActiveUserId,
            current.ProviderRevision,
            current.ReadyProviderIds,
            current.TargetResolution,
            current.PermissionRevision,
            current.AllowedPermissionPolicyIds,
            mcpRevision,
            current.ReadyIncomingMcpToolNames,
            enabledOutgoingToolNames,
            current.ReconcilerRevision,
            current.AvailableReconcilerIds,
            current.EnforceReconcilerAvailability,
            current.EnforceResolvedTargetBinding);
        return CapabilityRuntimeAvailabilityFactory.Create(
            current.RegisteredToolsByName.Values,
            state);
    }

    private static McpCapabilityPublicationIssue CreateUnavailableIssue(
        string toolName,
        CapabilityPlanningPublication planning)
    {
        var quarantine = planning.Resolution.QuarantinedCapabilities.FirstOrDefault(
            item => string.Equals(item.ToolName, toolName, StringComparison.Ordinal));
        if (quarantine is not null)
        {
            return new McpCapabilityPublicationIssue(
                toolName,
                McpCapabilityPublicationIssueCode.MissingCanonicalDescriptor,
                quarantine.Message);
        }

        var unavailable = planning.Resolution.UnavailableDescriptors.FirstOrDefault(
            item => string.Equals(item.Descriptor.ToolName, toolName, StringComparison.Ordinal));
        if (unavailable is not null)
        {
            return new McpCapabilityPublicationIssue(
                toolName,
                McpCapabilityPublicationIssueCode.CapabilityUnavailable,
                string.Join(" ", unavailable.Reasons.Select(reason => reason.Message)));
        }

        return new McpCapabilityPublicationIssue(
            toolName,
            McpCapabilityPublicationIssueCode.MissingCanonicalDescriptor,
            $"Enabled MCP tool '{toolName}' has no canonical outgoing capability descriptor and was not published.");
    }

    private static bool TryCanonicalize(
        AIFunction actualFunction,
        CapabilityDescriptor descriptor,
        out AIFunction canonicalFunction)
    {
        if (string.Equals(
                CapabilitySchemaIdentity.Calculate(actualFunction),
                descriptor.SchemaFingerprint,
                StringComparison.Ordinal))
        {
            canonicalFunction = actualFunction;
            return true;
        }

        if (descriptor.SchemaFactory() is not AIFunctionDeclaration canonicalDeclaration
            || !HasSameInvocationSchema(actualFunction, canonicalDeclaration))
        {
            canonicalFunction = null!;
            return false;
        }

        var normalized = new McpFunctionMetadataAIFunction(
            actualFunction,
            actualFunction.Name,
            canonicalDeclaration.Description);
        if (!string.Equals(
                CapabilitySchemaIdentity.Calculate(normalized),
                descriptor.SchemaFingerprint,
                StringComparison.Ordinal))
        {
            canonicalFunction = null!;
            return false;
        }

        canonicalFunction = normalized;
        return true;
    }

    private static bool HasSameInvocationSchema(
        AIFunctionDeclaration actual,
        AIFunctionDeclaration canonical) =>
        string.Equals(actual.Name, canonical.Name, StringComparison.Ordinal)
        && string.Equals(actual.JsonSchema.GetRawText(), canonical.JsonSchema.GetRawText(), StringComparison.Ordinal)
        && string.Equals(
            actual.ReturnJsonSchema?.GetRawText(),
            canonical.ReturnJsonSchema?.GetRawText(),
            StringComparison.Ordinal);

    private static void ReplaceCapabilityInventoryFunction(
        List<PublishedCandidate> candidates,
        ICollection<McpCapabilityPublicationIssue> issues)
    {
        var index = candidates.FindIndex(candidate => string.Equals(
            candidate.Descriptor.ToolName,
            AliCapabilityCatalog.ListAvailableToolsName,
            StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var descriptor = candidates[index].Descriptor;
        var capabilities = candidates
            .Select(candidate => new CoordinatorCapability(
                candidate.Descriptor.McpExposure.PublishedName!,
                candidate.Descriptor.Description,
                "Ali capability registry"))
            .OrderBy(capability => capability.Name, StringComparer.Ordinal)
            .ToArray();
        var exactInventory = AIFunctionFactory.Create(
            (Func<CoordinatorCapabilityResult>)(() => new CoordinatorCapabilityResult(
                $"This MCP server currently exposes {capabilities.Length} effective tool(s).",
                capabilities)),
            descriptor.ToolName,
            descriptor.SchemaFactory() is AIFunctionDeclaration declaration
                ? declaration.Description
                : descriptor.Description);
        if (string.Equals(
                CapabilitySchemaIdentity.Calculate(exactInventory),
                descriptor.SchemaFingerprint,
                StringComparison.Ordinal))
        {
            candidates[index] = new PublishedCandidate(descriptor, exactInventory);
            return;
        }

        candidates.RemoveAt(index);
        issues.Add(new McpCapabilityPublicationIssue(
            descriptor.ToolName,
            McpCapabilityPublicationIssueCode.SchemaIdentityMismatch,
            "The exact outgoing capability inventory did not match its canonical return schema and was not published."));
    }

    private static IReadOnlySet<string> EnabledToolNames(McpServerFunctionCatalog catalog) =>
        catalog.EnabledPolicies
            .Select(policy => policy.Name)
            .ToHashSet(StringComparer.Ordinal);

    private sealed record PublishedCandidate(
        CapabilityDescriptor Descriptor,
        AIFunction Function);
}

internal sealed class McpFunctionMetadataAIFunction(
    AIFunction innerFunction,
    string name,
    string description) : DelegatingAIFunction(innerFunction)
{
    public override string Name { get; } = name;

    public override string Description { get; } = description;
}
