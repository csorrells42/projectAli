using Ali.Modules.Capabilities;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using System.Text.Json;

namespace Ali.Modules.Mcp;

internal enum IncomingMcpCapabilityIssueCode
{
    DisabledPolicy,
    IncompleteConfiguration,
    ToolNameCollision,
    ProviderIdentityCollision,
    SchemaIdentityMismatch,
    DeclarationTooLarge,
    SchemaTooComplex,
    CatalogLimitExceeded
}

internal sealed record IncomingMcpCapabilityIssue(
    string ToolIdentity,
    IncomingMcpCapabilityIssueCode Code,
    string Message);

internal sealed record IncomingMcpRegisteredTool(
    McpResolvedTool Source,
    CapabilityDescriptor Descriptor,
    AIFunction Function);

/// <summary>
/// Immutable turn-scoped canonical registration for configured incoming MCP tools.
/// A remote declaration is never callable merely because it was discovered: its
/// exact saved policy, stable server identity, and schema must all join the same
/// canonical registry and terminal capability boundary as Ali's native tools.
/// </summary>
internal sealed class IncomingMcpCapabilityCatalog
{
    private const string PermissionPolicyId = "ali-tool-permission-v1";
    private const int MaximumDescriptionCharacters = 1600;
    private const int MaximumSchemaCharacters = 64 * 1024;
    private const int MaximumSchemaDepth = 32;
    internal const int MaximumAcceptedToolCount = 32;
    internal const int MaximumCumulativeDeclarationCharacters = 256 * 1024;
    private readonly McpToolSession _session;

    private IncomingMcpCapabilityCatalog(
        McpToolSession session,
        CanonicalCapabilityRegistry registry,
        IEnumerable<IncomingMcpRegisteredTool> tools,
        IEnumerable<string> providerIds,
        IEnumerable<IncomingMcpCapabilityIssue> issues)
    {
        _session = session;
        Registry = registry;
        Tools = Array.AsReadOnly(tools.ToArray());
        ProviderIds = providerIds.ToHashSet(StringComparer.Ordinal);
        ReadyToolNames = Tools
            .Select(tool => tool.Function.Name)
            .ToHashSet(StringComparer.Ordinal);
        Issues = Array.AsReadOnly(issues
            .OrderBy(issue => issue.ToolIdentity, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code)
            .ToArray());
        CatalogRevision = CalculateCatalogRevision();
    }

    public CanonicalCapabilityRegistry Registry { get; }

    public IReadOnlyList<IncomingMcpRegisteredTool> Tools { get; }

    public IReadOnlySet<string> ProviderIds { get; }

    public IReadOnlySet<string> ReadyToolNames { get; }

    public IReadOnlyList<IncomingMcpCapabilityIssue> Issues { get; }

    public string CatalogRevision { get; }

    public static IncomingMcpCapabilityCatalog Build(
        CanonicalCapabilityRegistry baseRegistry,
        McpToolSession session)
    {
        ArgumentNullException.ThrowIfNull(baseRegistry);
        ArgumentNullException.ThrowIfNull(session);

        var issues = new List<IncomingMcpCapabilityIssue>();
        var candidates = session.Tools
            .Select((tool, index) => new Candidate(tool, index))
            .ToArray();
        var invalidIndexes = new HashSet<int>();

        foreach (var candidate in candidates)
        {
            var tool = candidate.Tool;
            if (!tool.ConfiguredEnabled)
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.DisabledPolicy,
                    "The saved MCP tool policy is disabled.",
                    invalidIndexes,
                    issues);
                continue;
            }

            if (string.IsNullOrWhiteSpace(tool.ServerId)
                || string.IsNullOrWhiteSpace(tool.ServerName)
                || string.IsNullOrWhiteSpace(tool.OriginalName)
                || string.IsNullOrWhiteSpace(tool.Function.Name)
                || string.IsNullOrWhiteSpace(tool.Function.Description)
                || string.IsNullOrWhiteSpace(tool.SchemaFingerprint)
                || string.IsNullOrWhiteSpace(tool.ConfiguredDeclarationFingerprint)
                || tool.ConfiguredDeclarationFingerprint.Length
                    > McpClientSettingsStore.MaximumSchemaFingerprintCharacters
                || tool.InvocationTimeoutSeconds is < 1 or > 300)
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.IncompleteConfiguration,
                    "The configured MCP tool is missing its stable server, policy, or schema identity.",
                    invalidIndexes,
                    issues);
                continue;
            }

            var expectedName = McpClientManager.BuildModelToolName(
                new McpServerProfile
                {
                    Id = tool.ServerId,
                    Name = tool.ServerName
                },
                tool.OriginalName,
                tool.ConfiguredDeclarationFingerprint);
            if (!string.Equals(tool.Function.Name, expectedName, StringComparison.Ordinal))
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.IncompleteConfiguration,
                    "The MCP function name does not match its exact configured server and tool identity.",
                    invalidIndexes,
                    issues);
                continue;
            }

            if (!string.Equals(
                    CapabilitySchemaIdentity.Calculate(tool.Function),
                    tool.SchemaFingerprint,
                    StringComparison.Ordinal))
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.SchemaIdentityMismatch,
                    "The MCP function schema changed after it was resolved and was withheld.",
                    invalidIndexes,
                    issues);
                continue;
            }

            if (tool.Function.Description.Length > MaximumDescriptionCharacters
                || !McpClientManager.IsBoundedJson(
                    tool.Function.JsonSchema,
                    MaximumSchemaCharacters)
                || (tool.Function.ReturnJsonSchema is { } boundedReturnSchema
                    && !McpClientManager.IsBoundedJson(
                        boundedReturnSchema,
                        MaximumSchemaCharacters)))
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.DeclarationTooLarge,
                    "The MCP declaration exceeds Ali's bounded per-tool context limit and was withheld.",
                    invalidIndexes,
                    issues);
                continue;
            }

            if (ExceedsMaximumDepth(tool.Function.JsonSchema, MaximumSchemaDepth)
                || (tool.Function.ReturnJsonSchema is { } returnSchema
                    && ExceedsMaximumDepth(returnSchema, MaximumSchemaDepth)))
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.SchemaTooComplex,
                    "The MCP schema exceeds Ali's bounded nesting limit and was withheld.",
                    invalidIndexes,
                    issues);
            }
        }

        foreach (var group in candidates
                     .Where(candidate => !invalidIndexes.Contains(candidate.Index))
                     .GroupBy(candidate => candidate.Tool.Function.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var candidate in group)
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.ToolNameCollision,
                    "The incoming MCP name is ambiguous and was withheld.",
                    invalidIndexes,
                    issues);
            }
        }

        var baseToolNames = baseRegistry.Descriptors
            .Select(descriptor => descriptor.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(candidate =>
                     !invalidIndexes.Contains(candidate.Index)
                     && baseToolNames.Contains(candidate.Tool.Function.Name)))
        {
            Reject(
                candidate,
                IncomingMcpCapabilityIssueCode.ToolNameCollision,
                "The incoming MCP name collides with a canonical Ali tool and was withheld.",
                invalidIndexes,
                issues);
        }

        var ambiguousServerIds = candidates
            .Where(candidate => !invalidIndexes.Contains(candidate.Index))
            .GroupBy(candidate => candidate.Tool.ServerId, StringComparer.Ordinal)
            .Where(group => group
                .Select(candidate => candidate.Tool.ServerName)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var baseProviderIds = baseRegistry.ProviderBindings
            .Select(binding => binding.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(candidate =>
                     !invalidIndexes.Contains(candidate.Index)))
        {
            var providerId = BuildProviderId(candidate.Tool.ServerId);
            if (!ambiguousServerIds.Contains(candidate.Tool.ServerId)
                && !baseProviderIds.Contains(providerId))
            {
                continue;
            }

            Reject(
                candidate,
                IncomingMcpCapabilityIssueCode.ProviderIdentityCollision,
                ambiguousServerIds.Contains(candidate.Tool.ServerId)
                    ? "A stable MCP server identity refers to more than one configured server name."
                    : "An incoming MCP provider identity collides with an existing canonical provider.",
                invalidIndexes,
                issues);
        }

        var accepted = new List<IncomingMcpRegisteredTool>();
        var cumulativeDeclarationCharacters = 0;
        foreach (var candidate in candidates
                     .Where(candidate => !invalidIndexes.Contains(candidate.Index))
                     .OrderBy(candidate => candidate.Tool.Function.Name, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Tool.ServerId, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Tool.OriginalName, StringComparer.Ordinal))
        {
            var declarationCharacters = CalculateDeclarationCharacters(candidate.Tool);
            if (!CanAcceptDeclaration(
                    accepted.Count,
                    cumulativeDeclarationCharacters,
                    declarationCharacters))
            {
                Reject(
                    candidate,
                    IncomingMcpCapabilityIssueCode.CatalogLimitExceeded,
                    $"The turn-scoped MCP catalog is limited to {MaximumAcceptedToolCount} tools and {MaximumCumulativeDeclarationCharacters} cumulative declaration characters. Disable unused MCP tools in Settings.",
                    invalidIndexes,
                    issues);
                continue;
            }

            var descriptor = CreateDescriptor(candidate.Tool);
            var function = new McpToolSessionAvailabilityAIFunction(
                candidate.Tool.Function,
                session,
                candidate.Tool.ServerId,
                candidate.Tool.InvocationTimeoutSeconds);
            accepted.Add(new IncomingMcpRegisteredTool(candidate.Tool, descriptor, function));
            cumulativeDeclarationCharacters += declarationCharacters;
        }

        var providerIds = accepted
            .Select(tool => tool.Descriptor.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var registry = new CanonicalCapabilityRegistry(
            baseRegistry.Descriptors.Concat(accepted.Select(tool => tool.Descriptor)),
            baseRegistry.ProviderBindings.Concat(providerIds.Select(providerId =>
                new CapabilityProviderBinding(
                    providerId,
                    CapabilityGroupIds.ExternalMcp))));
        var frozenDescriptors = registry.Descriptors.ToDictionary(
            descriptor => descriptor.ToolName,
            StringComparer.Ordinal);
        var frozenAccepted = accepted
            .Select(tool => tool with
            {
                Descriptor = frozenDescriptors[tool.Function.Name]
            })
            .ToArray();
        return new IncomingMcpCapabilityCatalog(
            session,
            registry,
            frozenAccepted,
            providerIds,
            issues);
    }

    public CapabilityRuntimeStateSnapshot CreateRuntimeState(
        CapabilityRuntimeStateSnapshot baseState)
    {
        ArgumentNullException.ThrowIfNull(baseState);
        var sessionSnapshot = _session.CaptureSnapshot();
        var readyCatalogTools = sessionSnapshot.Ready
            ? Tools
                .Where(tool => sessionSnapshot.ReadyServerIds.Contains(tool.Source.ServerId))
                .ToArray()
            : [];
        var readyIncoming = baseState.ReadyIncomingMcpToolNames
            .Concat(readyCatalogTools.Select(tool => tool.Function.Name))
            .ToHashSet(StringComparer.Ordinal);
        var readyProviders = baseState.ReadyProviderIds
            .Concat(readyCatalogTools.Select(tool => tool.Descriptor.ProviderId))
            .ToHashSet(StringComparer.Ordinal);

        using var providerRevision = new CapabilityRevisionBuilder();
        providerRevision.Add("ali-provider-availability-with-incoming-mcp-v1");
        providerRevision.Add(baseState.ProviderRevision);
        providerRevision.Add(sessionSnapshot.BoundaryRevision);
        providerRevision.Add(readyProviders.Order(StringComparer.Ordinal).ToArray());

        using var mcpRevision = new CapabilityRevisionBuilder();
        mcpRevision.Add("ali-incoming-mcp-availability-v1");
        mcpRevision.Add(baseState.McpRevision);
        mcpRevision.Add(CatalogRevision);
        mcpRevision.Add(sessionSnapshot.BoundaryRevision);
        mcpRevision.Add(readyIncoming.Order(StringComparer.Ordinal).ToArray());
        mcpRevision.Add(baseState.EnabledOutgoingMcpToolNames.Order(StringComparer.Ordinal).ToArray());

        return new CapabilityRuntimeStateSnapshot(
            baseState.ActiveUserId,
            providerRevision.Finish(),
            readyProviders,
            baseState.TargetResolution,
            baseState.PermissionRevision,
            baseState.AllowedPermissionPolicyIds,
            mcpRevision.Finish(),
            readyIncoming,
            baseState.EnabledOutgoingMcpToolNames,
            baseState.ReconcilerRevision,
            baseState.AvailableReconcilerIds,
            baseState.EnforceReconcilerAvailability,
            baseState.EnforceResolvedTargetBinding);
    }

    private string CalculateCatalogRevision()
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-incoming-mcp-canonical-catalog-v1");
        revision.Add(_session.SessionRevision);
        revision.Add(Tools.Count);
        foreach (var tool in Tools.OrderBy(tool => tool.Function.Name, StringComparer.Ordinal))
        {
            revision.Add(tool.Function.Name);
            revision.Add(tool.Descriptor.ProviderId);
            revision.Add(tool.Descriptor.SchemaFingerprint);
            revision.Add(tool.Source.ConfiguredEnabled);
            revision.Add(tool.Source.RequiresApproval);
            revision.Add(tool.Source.ReadOnlyHint);
            revision.Add(tool.Source.DestructiveHint);
            revision.Add(tool.Source.ConfiguredDeclarationFingerprint);
            revision.Add(tool.Source.InvocationTimeoutSeconds);
        }
        revision.Add(Issues.Count);
        foreach (var issue in Issues)
        {
            revision.Add(issue.ToolIdentity);
            revision.Add((int)issue.Code);
            revision.Add(issue.Message);
        }
        return revision.Finish();
    }

    private static CapabilityDescriptor CreateDescriptor(McpResolvedTool tool)
    {
        var effectKind = tool.DestructiveHint
            ? CapabilityEffectKind.Destructive
            : tool.ReadOnlyHint
                ? CapabilityEffectKind.ExternalRead
                : CapabilityEffectKind.ExternalMutation;
        var mutation = effectKind is CapabilityEffectKind.Destructive
            or CapabilityEffectKind.ExternalMutation;
        var requiresApproval = tool.RequiresApproval;
        var providerId = BuildProviderId(tool.ServerId);
        var stableSuffix = BuildStableToolKey(tool.ServerId, tool.OriginalName);
        return CapabilityDescriptor.Create(
            id: $"incoming-mcp:{stableSuffix}",
            toolName: tool.Function.Name,
            displayName: $"{tool.ServerName}: {tool.OriginalName}",
            description: tool.Function.Description,
            tier: CapabilityTier.Task,
            groupId: CapabilityGroupIds.ExternalMcp,
            providerId: providerId,
            registrationKind: CapabilityRegistrationKind.Mcp,
            schemaFactoryId: $"ali.incoming-mcp.schema:{stableSuffix}",
            schemaFactory: () => tool.Function,
            providerGate: new CapabilityProviderGate(
                CapabilityProviderGateKind.OwnerOnly,
                Array.Empty<string>()),
            prerequisiteGroupIds: Array.Empty<string>(),
            presetIds: Array.Empty<string>(),
            permission: new CapabilityPermissionDescriptor(
                PermissionPolicyId,
                requiresApproval,
                effectKind switch
                {
                    CapabilityEffectKind.Destructive => CapabilityRiskLevel.High,
                    CapabilityEffectKind.ExternalMutation => CapabilityRiskLevel.Medium,
                    _ when requiresApproval => CapabilityRiskLevel.Medium,
                    _ => CapabilityRiskLevel.Low
                }),
            semanticSearchText:
                $"External MCP tool {tool.OriginalName} from configured server {tool.ServerName}. {tool.Function.Description}",
            semanticMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "incoming-mcp",
                ["server-id"] = tool.ServerId,
                ["server-name"] = tool.ServerName,
                ["original-tool-name"] = tool.OriginalName,
                ["configured-enabled"] = tool.ConfiguredEnabled.ToString(),
                ["configured-requires-approval"] = tool.RequiresApproval.ToString(),
                ["configured-read-only"] = tool.ReadOnlyHint.ToString(),
                ["configured-destructive"] = tool.DestructiveHint.ToString(),
                ["configured-declaration-fingerprint"] = tool.ConfiguredDeclarationFingerprint,
                ["invocation-timeout-seconds"] = tool.InvocationTimeoutSeconds.ToString()
            },
            visibleInCapabilityReport: true,
            visibleInCriticInventory: true,
            mcpExposure: new CapabilityMcpExposure(false, null),
            effect: new CapabilityEffectDescriptor(
                effectKind,
                effectKind switch
                {
                    CapabilityEffectKind.Destructive => "May destructively change data or state through a configured external MCP server.",
                    CapabilityEffectKind.ExternalMutation => "May change data or state through a configured external MCP server.",
                    _ => "Reads potentially private data through a configured external MCP server."
                },
                mutation
                    ? CapabilityMutationBoundary.PermissionGuarded
                    : CapabilityMutationBoundary.None,
                SupportsIdempotency: false,
                ReconcilerId: mutation
                    ? $"incoming-mcp-reconciler:{stableSuffix}"
                    : null,
                ReadsLocalData: true,
                WritesLocalData: effectKind == CapabilityEffectKind.Destructive,
                UsesNetwork: true,
                StartsProcesses: false,
                ChangesSystemState: mutation));
    }

    private static string BuildProviderId(string serverId)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-incoming-mcp-provider-v1");
        revision.Add(serverId);
        return $"incoming-mcp:{revision.Finish()}";
    }

    private static string BuildStableToolKey(string serverId, string originalName)
    {
        using var revision = new CapabilityRevisionBuilder();
        revision.Add("ali-incoming-mcp-tool-v1");
        revision.Add(serverId);
        revision.Add(originalName);
        return revision.Finish();
    }

    private static void Reject(
        Candidate candidate,
        IncomingMcpCapabilityIssueCode code,
        string message,
        ISet<int> invalidIndexes,
        ICollection<IncomingMcpCapabilityIssue> issues)
    {
        if (!invalidIndexes.Add(candidate.Index))
        {
            return;
        }

        var serverName = CompactIdentityPart(
            candidate.Tool.ServerName,
            McpClientSettingsStore.MaximumServerNameCharacters,
            "MCP server");
        var originalName = CompactIdentityPart(
            candidate.Tool.OriginalName,
            McpClientSettingsStore.MaximumToolNameCharacters,
            $"tool {candidate.Index + 1}");
        var identity = $"{serverName}: {originalName}";
        issues.Add(new IncomingMcpCapabilityIssue(identity, code, message));
    }

    private static string CompactIdentityPart(
        string? value,
        int maximumCharacters,
        string fallback)
    {
        var compact = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maximumCharacters
            ? compact
            : compact[..maximumCharacters];
    }

    private static bool ExceedsMaximumDepth(
        System.Text.Json.JsonElement element,
        int maximumDepth)
    {
        var pending = new Stack<(System.Text.Json.JsonElement Element, int Depth)>();
        pending.Push((element, 1));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (depth > maximumDepth)
            {
                return true;
            }

            switch (current.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                    {
                        pending.Push((property.Value, depth + 1));
                    }
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray())
                    {
                        pending.Push((item, depth + 1));
                    }
                    break;
            }
        }

        return false;
    }

    internal static int CalculateDeclarationCharacters(McpResolvedTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var function = tool.Function;
        return checked(
            function.Name.Length
            + function.Description.Length
            + tool.ServerId.Length
            + tool.ServerName.Length
            + tool.OriginalName.Length
            + tool.ConfiguredDeclarationFingerprint.Length
            + function.JsonSchema.GetRawText().Length
            + (function.ReturnJsonSchema?.GetRawText().Length ?? 0));
    }

    internal static bool CanAcceptDeclaration(
        int acceptedToolCount,
        int cumulativeDeclarationCharacters,
        int candidateDeclarationCharacters) =>
        acceptedToolCount >= 0
        && acceptedToolCount < MaximumAcceptedToolCount
        && cumulativeDeclarationCharacters >= 0
        && candidateDeclarationCharacters >= 0
        && cumulativeDeclarationCharacters <= MaximumCumulativeDeclarationCharacters
        && candidateDeclarationCharacters
            <= MaximumCumulativeDeclarationCharacters - cumulativeDeclarationCharacters;

    private sealed record Candidate(McpResolvedTool Tool, int Index);
}

internal sealed class McpToolSessionAvailabilityAIFunction(
    AIFunction innerFunction,
    McpToolSession session,
    string serverId,
    int invocationTimeoutSeconds) : DelegatingAIFunction(innerFunction)
{
    internal const int MaximumSerializedResponseBytes = 64 * 1024;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = session.CaptureSnapshot();
        if (!snapshot.Ready || !snapshot.ReadyServerIds.Contains(serverId))
        {
            return new CapabilityInvocationBlockedResult(
                Name,
                snapshot.BoundaryRevision,
                [new CapabilityAvailabilityReason(
                    CapabilityAvailabilityReasonCode.McpUnavailable,
                    $"incoming-mcp-server:{serverId}",
                    snapshot.UnavailableReason
                    ?? "The configured MCP server connection is no longer available.")]);
        }

        using var invocationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        invocationTimeout.CancelAfter(TimeSpan.FromSeconds(invocationTimeoutSeconds));
        try
        {
            var invocation = InnerFunction.InvokeAsync(arguments, invocationTimeout.Token);
            var result = await invocation
                .AsTask()
                .WaitAsync(invocationTimeout.Token)
                .ConfigureAwait(false);
            if (IsBoundedResponse(result, out var failure))
            {
                return result;
            }

            session.MarkDisconnected(serverId);
            return new McpToolResponseWithheldResult(
                    Name,
                    "response-withheld",
                    failure
                    ?? $"The external MCP response exceeded {MaximumSerializedResponseBytes} serialized bytes.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (invocationTimeout.IsCancellationRequested)
        {
            return BlockTimedOutInvocation();
        }
        catch (TimeoutException)
        {
            return BlockTimedOutInvocation();
        }
        catch (OperationCanceledException ex)
        {
            session.MarkDisconnected(serverId);
            return new McpToolInvocationFailedResult(
                Name,
                "server-failed",
                $"The external MCP invocation ended unexpectedly ({ex.GetType().Name}). Its target-side outcome is unknown; this server is disconnected for the rest of the turn and must not be retried automatically.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && IsExternalOperationalFailure(ex))
        {
            session.MarkDisconnected(serverId);
            return new McpToolInvocationFailedResult(
                Name,
                "server-failed",
                $"The external MCP invocation failed safely ({ex.GetType().Name}). Its target-side outcome is unknown; this server is disconnected for the rest of the turn and must not be retried automatically.");
        }

        McpToolInvocationTimedOutResult BlockTimedOutInvocation()
        {
            session.MarkDisconnected(serverId);
            return new McpToolInvocationTimedOutResult(
                Name,
                "timed-out",
                $"The external MCP tool timed out after {invocationTimeoutSeconds} second(s). Its target-side outcome is unknown; this server is disconnected for the rest of the turn and must not be retried automatically.");
        }
    }

    private static bool IsExternalOperationalFailure(Exception exception) =>
        exception is not OperationCanceledException
        and not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private static bool IsBoundedResponse(object? result, out string? failure)
    {
        failure = null;
        if (result is null)
        {
            return true;
        }

        try
        {
            using var stream = new BoundedWriteStream(MaximumSerializedResponseBytes);
            JsonSerializer.Serialize(stream, result, result.GetType());
            return true;
        }
        catch (McpResponseLimitExceededException)
        {
            failure = $"The external MCP response exceeded {MaximumSerializedResponseBytes} serialized bytes. Its target-side outcome may be unknown; do not retry automatically.";
            return false;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            failure = $"The external MCP response could not be boundedly serialized ({ex.GetType().Name}). Its target-side outcome may be unknown; do not retry automatically.";
            return false;
        }
    }
}

internal interface IMcpPostDispatchFailureResult
{
    string ToolName { get; }

    string Status { get; }

    string Message { get; }

    bool Success { get; }

    bool Invoked { get; }

    bool OutcomeUnknown { get; }

    bool RetrySafe { get; }
}

internal sealed record McpToolResponseWithheldResult(
    string ToolName,
    string Status,
    string Message) : IMcpPostDispatchFailureResult
{
    public bool Success => false;

    public bool Invoked => true;

    public bool OutcomeUnknown => true;

    public bool RetrySafe => false;
}

internal sealed record McpToolInvocationTimedOutResult(
    string ToolName,
    string Status,
    string Message) : IMcpPostDispatchFailureResult
{
    public bool Success => false;

    public bool Invoked => true;

    public bool OutcomeUnknown => true;

    public bool RetrySafe => false;
}

internal sealed record McpToolInvocationFailedResult(
    string ToolName,
    string Status,
    string Message) : IMcpPostDispatchFailureResult
{
    public bool Success => false;

    public bool Invoked => true;

    public bool OutcomeUnknown => true;

    public bool RetrySafe => false;
}

internal sealed class BoundedWriteStream(int maximumBytes) : Stream
{
    private int _written;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Advance(count);

    public override void Write(ReadOnlySpan<byte> buffer) =>
        Advance(buffer.Length);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Advance(buffer.Length);
        return ValueTask.CompletedTask;
    }

    private void Advance(int count)
    {
        if (count < 0 || count > maximumBytes - _written)
        {
            throw new McpResponseLimitExceededException();
        }
        _written += count;
    }
}

internal sealed class McpResponseLimitExceededException : Exception;
