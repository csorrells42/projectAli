using System.Text.Json;
using Ali.Modules.Coordinator;

namespace Ali.Modules.Orchestration.Work;

/// <summary>
/// Production exact-name effect adapters. These mappings are schema-owned mechanical
/// identities; they never infer a target or effect family from prose.
/// </summary>
internal static class AliProductionEffectNormalizations
{
    internal static EffectNormalizationRegistry Create() => new(
    [
        new FileContentEffectAdapter(),
        new FileMoveEffectAdapter()
    ]);

    private sealed class FileContentEffectAdapter : IEffectNormalizationAdapter
    {
        public IReadOnlyCollection<string> ToolNames { get; } =
        [
            AliCapabilityCatalog.FileWriteName,
            AliCapabilityCatalog.FileReplaceName,
            AliCapabilityCatalog.FileReplaceLinesName
        ];

        public string EffectFamily => "ali-file-content-at-virtual-path-v1";

        public AdapterNormalizedEffectTarget NormalizeTarget(
            EffectTargetNormalizationRequest request) =>
            new(JsonSerializer.SerializeToElement(new
            {
                virtualPath = RequirePath(request.Arguments, "fileName")
            }));

        public AdapterNormalizedDomainOutcome NormalizeDomainOutcome(
            EffectOutcomeNormalizationRequest request) =>
            NormalizeFileOutcome(request, "file-content");
    }

    private sealed class FileMoveEffectAdapter : IEffectNormalizationAdapter
    {
        public IReadOnlyCollection<string> ToolNames { get; } =
        [
            AliCapabilityCatalog.FileMoveName
        ];

        public string EffectFamily => "ali-file-move-between-virtual-paths-v1";

        public AdapterNormalizedEffectTarget NormalizeTarget(
            EffectTargetNormalizationRequest request) =>
            new(JsonSerializer.SerializeToElement(new
            {
                sourcePath = RequirePath(request.Arguments, "sourcePath"),
                destinationPath = RequirePath(request.Arguments, "destinationPath")
            }));

        public AdapterNormalizedDomainOutcome NormalizeDomainOutcome(
            EffectOutcomeNormalizationRequest request) =>
            NormalizeFileOutcome(request, "file-move");
    }

    private static AdapterNormalizedDomainOutcome NormalizeFileOutcome(
        EffectOutcomeNormalizationRequest request,
        string operation)
    {
        var explicitSuccess = ReadBoolean(request.Result, "success")
            ?? ReadBoolean(request.Result, "succeeded")
            ?? ReadBoolean(request.Result, "ok");
        var changed = ReadBoolean(request.Result, "changed");
        var code = operation + "-" + request.ResultKind.ToString().ToLowerInvariant();
        return new AdapterNormalizedDomainOutcome(
            code,
            JsonSerializer.SerializeToElement(new
            {
                operation,
                resultKind = request.ResultKind.ToString(),
                invocationStatus = request.Invocation.InvocationStatus.ToString(),
                domainOutcome = request.Invocation.DomainOutcome.ToString(),
                request.Invocation.FailureCode,
                explicitSuccess,
                changed
            }));
    }

    private static string RequirePath(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"The exact '{propertyName}' target is unavailable for effect normalization.");
        }

        return value.GetString()!
            .Trim()
            .Replace('\\', '/');
    }

    private static bool? ReadBoolean(JsonElement result, string propertyName)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in result.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        return null;
    }
}
