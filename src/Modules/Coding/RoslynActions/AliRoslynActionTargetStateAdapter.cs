using System.Text.Json;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Exact-schema target versions for the five Action Deck tools. This adapter does not infer
/// targets from prose and never projects source paths or source content into planning state.
/// </summary>
internal sealed class AliRoslynActionTargetStateAdapter(
    AliCodingProjectResolver resolver,
    AliRoslynActionHandleStore handles,
    AliRoslynSemanticWorkspaceBinding semanticWorkspace) : IActionTargetStateAdapter
{
    private readonly AliCodingProjectResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly AliRoslynActionHandleStore _handles =
        handles ?? throw new ArgumentNullException(nameof(handles));
    private readonly AliRoslynSemanticWorkspaceBinding _semanticWorkspace =
        semanticWorkspace ?? throw new ArgumentNullException(nameof(semanticWorkspace));

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        AliCapabilityCatalog.RoslynInspectTargetName,
        AliCapabilityCatalog.RoslynListActionsName,
        AliCapabilityCatalog.RoslynPreviewActionName,
        AliCapabilityCatalog.RoslynVerifyChangesetName,
        AliCapabilityCatalog.RoslynApplyActionName
    ];

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The Roslyn Action Deck arguments must be a JSON object.");
        }

        var versions = toolName switch
        {
            AliCapabilityCatalog.RoslynInspectTargetName => CaptureTarget(arguments, includeHandleStore: false),
            AliCapabilityCatalog.RoslynListActionsName => CaptureTarget(arguments, includeHandleStore: false),
            AliCapabilityCatalog.RoslynPreviewActionName => CaptureTarget(arguments, includeHandleStore: true),
            AliCapabilityCatalog.RoslynVerifyChangesetName => CaptureHandle(arguments),
            AliCapabilityCatalog.RoslynApplyActionName => CaptureHandle(arguments),
            _ => throw new InvalidOperationException("The target-state adapter received an unregistered tool identity.")
        };
        return new TargetStateSnapshot(
            versions,
            new Dictionary<string, string>(versions, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private IReadOnlyDictionary<string, string> CaptureTarget(
        JsonElement arguments,
        bool includeHandleStore)
    {
        var target = _resolver.ResolveExistingTarget(RequireString(arguments, "targetPath"));
        string? document = null;
        if (TryGetString(arguments, "documentPath", out var documentPath))
        {
            document = _resolver.ResolveDocument(target, documentPath!);
        }
        return _semanticWorkspace.Capture(
            target,
            document,
            AdditionalVersions(includeHandleStore));
    }

    private IReadOnlyDictionary<string, string> CaptureHandle(JsonElement arguments) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["handle"] = _handles.CaptureProtectedArtifactDigest(RequireString(arguments, "handleId"))
        };

    internal AliRoslynStaticWorkspaceTarget ResolveTarget(
        string targetPath,
        string? documentPath)
    {
        var target = _resolver.ResolveExistingTarget(targetPath);
        var document = documentPath is null
            ? null
            : _resolver.ResolveDocument(target, documentPath);
        return new(target, document);
    }

    internal void RequireStaticGrantVersion(
        string toolName,
        AliExecutionGrant grant,
        AliRoslynStaticWorkspaceTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var includeHandleStore = toolName == AliCapabilityCatalog.RoslynPreviewActionName;
        _semanticWorkspace.RequireStaticGrantVersion(
            grant,
            target.Target,
            target.DocumentPhysicalPath,
            AdditionalVersions(includeHandleStore));
    }

    internal Task<AliRoslynSolutionFingerprintSnapshot> BindLoadedAsync(
        AliRoslynWorkspaceSession session,
        AliRoslynStaticWorkspaceTarget target,
        CancellationToken cancellationToken) =>
        _semanticWorkspace.BindLoadedAsync(
            session,
            target.Target,
            target.DocumentPhysicalPath,
            cancellationToken);

    internal Task RequireBoundSemanticFingerprintAsync(
        AliRoslynWorkspaceSession session,
        CancellationToken cancellationToken) =>
        _semanticWorkspace.RequireBoundSemanticFingerprintAsync(
            session,
            cancellationToken);

    private IReadOnlyDictionary<string, string>? AdditionalVersions(
        bool includeHandleStore) =>
        includeHandleStore
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["handleStore"] = _handles.CaptureStoreRevisionDigest()
            }
            : null;

    internal static string RequireString(JsonElement arguments, string propertyName)
    {
        if (!TryGetString(arguments, propertyName, out var value))
        {
            throw new InvalidDataException($"The exact '{propertyName}' argument is unavailable.");
        }

        return value!;
    }

    private static bool TryGetString(
        JsonElement arguments,
        string propertyName,
        out string? value)
    {
        if (arguments.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }
}
