using System.Text.Json;
using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.RoslynQueries;

/// <summary>
/// Captures target versions from the declared arguments of the seven Roslyn semantic-query
/// schemas. It does not infer a target from prose and never publishes source paths or content
/// into orchestration state.
/// </summary>
internal sealed class AliRoslynQueryTargetStateAdapter(
    AliCodingProjectResolver resolver,
    AliRoslynSemanticWorkspaceBinding semanticWorkspace) : IActionTargetStateAdapter
{
    private readonly AliCodingProjectResolver _resolver =
        resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly AliRoslynSemanticWorkspaceBinding _semanticWorkspace =
        semanticWorkspace ?? throw new ArgumentNullException(nameof(semanticWorkspace));

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        AliCapabilityCatalog.RoslynAnalyzeProjectName,
        AliCapabilityCatalog.RoslynFindSymbolName,
        AliCapabilityCatalog.RoslynGetCompletionsName,
        AliCapabilityCatalog.RoslynInspectSolutionName,
        AliCapabilityCatalog.RoslynInspectDocumentName,
        AliCapabilityCatalog.RoslynInspectPositionName,
        AliCapabilityCatalog.RoslynFindReferencesName
    ];

    public TargetStateSnapshot Capture(string toolName, JsonElement arguments)
    {
        var resolved = Resolve(toolName, arguments);
        var versions = _semanticWorkspace.Capture(
            resolved.Target,
            resolved.DocumentPhysicalPath);

        return new TargetStateSnapshot(
            versions,
            new Dictionary<string, string>(versions, StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    internal AliResolvedCodingTarget ResolveTarget(
        string toolName,
        JsonElement arguments) => Resolve(toolName, arguments).Target;

    internal void RequireStaticGrantVersion(
        AliExecutionGrant grant,
        AliResolvedCodingTarget expectedTarget,
        string? documentPhysicalPath) =>
        _semanticWorkspace.RequireStaticGrantVersion(
            grant,
            expectedTarget,
            documentPhysicalPath,
            additionalVersions: null);

    internal Task<AliRoslynSolutionFingerprintSnapshot> BindLoadedAsync(
        AliRoslynWorkspaceSession session,
        AliResolvedCodingTarget expectedTarget,
        string? documentPhysicalPath,
        CancellationToken cancellationToken) =>
        _semanticWorkspace.BindLoadedAsync(
            session,
            expectedTarget,
            documentPhysicalPath,
            cancellationToken);

    internal Task RequireBoundSemanticFingerprintAsync(
        AliRoslynWorkspaceSession session,
        CancellationToken cancellationToken) =>
        _semanticWorkspace.RequireBoundSemanticFingerprintAsync(
            session,
            cancellationToken);

    private ResolvedQueryInput Resolve(string toolName, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The Roslyn semantic-query arguments must be a JSON object.");
        }

        var targetArgumentName = toolName switch
        {
            AliCapabilityCatalog.RoslynAnalyzeProjectName
                or AliCapabilityCatalog.RoslynFindSymbolName
                or AliCapabilityCatalog.RoslynGetCompletionsName => "projectPath",
            AliCapabilityCatalog.RoslynInspectSolutionName
                or AliCapabilityCatalog.RoslynInspectDocumentName
                or AliCapabilityCatalog.RoslynInspectPositionName
                or AliCapabilityCatalog.RoslynFindReferencesName => "targetPath",
            _ => throw new InvalidOperationException(
                "The target-state adapter received an unregistered Roslyn query identity.")
        };

        ValidateToolSpecificArguments(toolName, arguments);
        var target = _resolver.ResolveExistingTarget(
            RequireString(arguments, targetArgumentName));
        var document = RequiresDocument(toolName)
            ? _resolver.ResolveDocument(target, RequireString(arguments, "documentPath"))
            : null;
        return new ResolvedQueryInput(target, document);
    }

    private static void ValidateToolSpecificArguments(
        string toolName,
        JsonElement arguments)
    {
        if (toolName == AliCapabilityCatalog.RoslynFindSymbolName)
        {
            _ = RequireString(arguments, "query");
        }

        if (RequiresPosition(toolName))
        {
            _ = RequirePositiveInt32(arguments, "line");
            _ = RequirePositiveInt32(arguments, "column");
        }
    }

    private static bool RequiresDocument(string toolName) =>
        toolName is AliCapabilityCatalog.RoslynGetCompletionsName
            or AliCapabilityCatalog.RoslynInspectDocumentName
            or AliCapabilityCatalog.RoslynInspectPositionName
            or AliCapabilityCatalog.RoslynFindReferencesName;

    private static bool RequiresPosition(string toolName) =>
        toolName is AliCapabilityCatalog.RoslynGetCompletionsName
            or AliCapabilityCatalog.RoslynInspectPositionName
            or AliCapabilityCatalog.RoslynFindReferencesName;

    private static string RequireString(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new InvalidDataException(
            $"The exact '{propertyName}' Roslyn query argument is unavailable.");
    }

    private static int RequirePositiveInt32(
        JsonElement arguments,
        string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
            && value >= 1)
        {
            return value;
        }

        throw new InvalidDataException(
            $"The exact '{propertyName}' Roslyn query argument must be a positive integer.");
    }

    private sealed record ResolvedQueryInput(
        AliResolvedCodingTarget Target,
        string? DocumentPhysicalPath);
}
