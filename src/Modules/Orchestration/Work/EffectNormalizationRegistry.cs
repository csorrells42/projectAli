using System.Collections.Frozen;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.Work;

/// <summary>
/// A capability-specific, structural contract for effect equivalence. Implementations must
/// project only schema-defined target and outcome fields. Display text, timestamps, durations,
/// request IDs, and other observation noise must not be returned by either normalizer.
/// </summary>
public interface IEffectNormalizationAdapter
{
    /// <summary>
    /// Exact registered tool names handled by this adapter. Two tools can be equivalent only
    /// when their adapters deliberately declare the same <see cref="EffectFamily"/>.
    /// </summary>
    IReadOnlyCollection<string> ToolNames { get; }

    /// <summary>
    /// Stable, adapter-owned semantic family. This is an identity value, not display text.
    /// </summary>
    string EffectFamily { get; }

    AdapterNormalizedEffectTarget NormalizeTarget(EffectTargetNormalizationRequest request);

    AdapterNormalizedDomainOutcome NormalizeDomainOutcome(
        EffectOutcomeNormalizationRequest request);
}

public sealed record EffectTargetNormalizationRequest(
    string ToolName,
    JsonElement Arguments);

public sealed record EffectOutcomeNormalizationRequest(
    string ToolName,
    JsonElement Arguments,
    JsonElement Result,
    ToolInvocationOutcome Invocation,
    EffectResultKind ResultKind);

public sealed record AdapterNormalizedEffectTarget(JsonElement StableTarget);

public sealed record AdapterNormalizedDomainOutcome(
    string Code,
    JsonElement StableDomainState);

/// <summary>
/// Immutable preparation result retained with an accepted tool call.
/// </summary>
public sealed class PreparedEffectNormalization
{
    private readonly JsonElement _arguments;
    private readonly JsonElement _normalizedTarget;

    internal PreparedEffectNormalization(
        object registryToken,
        string toolName,
        string argumentsDigest,
        string effectFamily,
        JsonElement arguments,
        JsonElement normalizedTarget,
        EffectIdentity effectIdentity,
        bool adapterDeclaredSemanticEquivalence,
        EffectNormalizationRegistry.AdapterBinding? adapterBinding)
    {
        RegistryToken = registryToken;
        ToolName = toolName;
        ArgumentsDigest = argumentsDigest;
        EffectFamily = effectFamily;
        _arguments = arguments.Clone();
        _normalizedTarget = normalizedTarget.Clone();
        EffectIdentity = effectIdentity;
        AdapterDeclaredSemanticEquivalence = adapterDeclaredSemanticEquivalence;
        AdapterBinding = adapterBinding;
    }

    internal object RegistryToken { get; }

    internal EffectNormalizationRegistry.AdapterBinding? AdapterBinding { get; }

    internal JsonElement ArgumentsSnapshot => _arguments;

    public string ToolName { get; }

    public string ArgumentsDigest { get; }

    public string EffectFamily { get; }

    public JsonElement NormalizedTarget => _normalizedTarget.Clone();

    public EffectIdentity EffectIdentity { get; }

    public bool AdapterDeclaredSemanticEquivalence { get; }
}

/// <summary>
/// Adapter-normalized terminal identity. The normalized projection is safe to use for effect
/// comparison, but remains domain evidence rather than user-facing prose.
/// </summary>
public sealed class NormalizedEffectOutcome
{
    private readonly JsonElement _normalizedDomainOutcome;

    internal NormalizedEffectOutcome(
        string normalizedCode,
        JsonElement normalizedDomainOutcome,
        EffectOutcomeIdentity identity,
        bool adapterDeclaredSemanticEquivalence)
    {
        NormalizedCode = normalizedCode;
        _normalizedDomainOutcome = normalizedDomainOutcome.Clone();
        Identity = identity;
        AdapterDeclaredSemanticEquivalence = adapterDeclaredSemanticEquivalence;
    }

    public string NormalizedCode { get; }

    public JsonElement NormalizedDomainOutcome => _normalizedDomainOutcome.Clone();

    public EffectOutcomeIdentity Identity { get; }

    public bool AdapterDeclaredSemanticEquivalence { get; }
}

/// <summary>
/// Exact-name registry for capability-owned effect normalizers. It never selects an adapter from
/// prose, keywords, descriptions, or argument contents. Unregistered tools receive an exact
/// tool-name plus canonical-arguments identity and cannot claim semantic equivalence.
/// </summary>
public sealed class EffectNormalizationRegistry
{
    private const string DeclaredEffectIdentityKind = "adapter-declared-effect-v1";
    private const string ExactInvocationIdentityKind = "exact-tool-arguments-v1";
    private const string ExactInvocationOutcomeCode = "exact-tool-arguments-outcome-v1";

    private readonly FrozenDictionary<string, AdapterBinding> _bindings;
    private readonly object _registryToken = new();

    public EffectNormalizationRegistry(
        IEnumerable<IEffectNormalizationAdapter>? adapters = null)
    {
        var bindings = new Dictionary<string, AdapterBinding>(StringComparer.Ordinal);
        foreach (var adapter in adapters ?? [])
        {
            ArgumentNullException.ThrowIfNull(adapter);
            var family = RequireIdentityValue(adapter.EffectFamily, nameof(adapter.EffectFamily));
            var toolNames = adapter.ToolNames
                ?? throw new ArgumentException(
                    "An effect-normalization adapter must declare its exact tool names.",
                    nameof(adapters));
            var declaredNames = toolNames
                .Select(toolName => RequireIdentityValue(toolName, nameof(adapter.ToolNames)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (declaredNames.Length == 0)
            {
                throw new ArgumentException(
                    "An effect-normalization adapter must declare at least one exact tool name.",
                    nameof(adapters));
            }

            var binding = new AdapterBinding(adapter, family);
            foreach (var toolName in declaredNames)
            {
                if (!bindings.TryAdd(toolName, binding))
                {
                    throw new ArgumentException(
                        $"Tool '{toolName}' has more than one effect-normalization adapter.",
                        nameof(adapters));
                }
            }
        }

        _bindings = bindings.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static EffectNormalizationRegistry Empty { get; } = new();

    public PreparedEffectNormalization Prepare(
        string toolName,
        JsonElement arguments)
    {
        toolName = RequireIdentityValue(toolName, nameof(toolName));
        var argumentsSnapshot = RequireJsonValue(arguments, nameof(arguments));
        var argumentsDigest = CanonicalDigest(argumentsSnapshot);

        if (!_bindings.TryGetValue(toolName, out var binding))
        {
            var exactTarget = JsonSerializer.SerializeToElement(new
            {
                toolName,
                canonicalArgumentsDigest = argumentsDigest
            });
            var exactIdentity = EffectIdentity.Create(
                ExactInvocationIdentityKind,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tool"] = toolName,
                    ["canonicalArguments"] = argumentsDigest
                });
            return new PreparedEffectNormalization(
                _registryToken,
                toolName,
                argumentsDigest,
                ExactInvocationIdentityKind,
                argumentsSnapshot,
                exactTarget,
                exactIdentity,
                adapterDeclaredSemanticEquivalence: false,
                adapterBinding: null);
        }

        var normalized = binding.Adapter.NormalizeTarget(
            new EffectTargetNormalizationRequest(toolName, argumentsSnapshot.Clone()))
            ?? throw new InvalidDataException(
                $"Effect-normalization adapter for '{toolName}' returned no target.");
        var normalizedTarget = RequireJsonValue(
            normalized.StableTarget,
            nameof(AdapterNormalizedEffectTarget.StableTarget));
        var targetDigest = CanonicalDigest(normalizedTarget);
        var effectIdentity = EffectIdentity.Create(
            DeclaredEffectIdentityKind,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["effectFamily"] = binding.EffectFamily,
                ["normalizedTarget"] = targetDigest
            });
        return new PreparedEffectNormalization(
            _registryToken,
            toolName,
            argumentsDigest,
            binding.EffectFamily,
            argumentsSnapshot,
            normalizedTarget,
            effectIdentity,
            adapterDeclaredSemanticEquivalence: true,
            binding);
    }

    public NormalizedEffectOutcome NormalizeOutcome(
        PreparedEffectNormalization prepared,
        JsonElement result,
        ToolInvocationOutcome invocation,
        EffectResultKind resultKind)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(invocation);
        if (!ReferenceEquals(prepared.RegistryToken, _registryToken))
        {
            throw new ArgumentException(
                "The prepared effect identity belongs to a different normalization registry.",
                nameof(prepared));
        }

        if (!Enum.IsDefined(resultKind))
        {
            throw new ArgumentOutOfRangeException(nameof(resultKind));
        }

        var resultSnapshot = RequireJsonValue(result, nameof(result));
        if (prepared.AdapterBinding is { } binding)
        {
            var normalized = binding.Adapter.NormalizeDomainOutcome(
                new EffectOutcomeNormalizationRequest(
                    prepared.ToolName,
                    prepared.ArgumentsSnapshot.Clone(),
                    resultSnapshot.Clone(),
                    invocation,
                    resultKind))
                ?? throw new InvalidDataException(
                    $"Effect-normalization adapter for '{prepared.ToolName}' returned no outcome.");
            var code = RequireIdentityValue(
                normalized.Code,
                nameof(AdapterNormalizedDomainOutcome.Code));
            var stableState = RequireJsonValue(
                normalized.StableDomainState,
                nameof(AdapterNormalizedDomainOutcome.StableDomainState));
            var stableStateDigest = CanonicalDigest(stableState);
            var identity = EffectOutcomeIdentity.Create(
                invocation,
                resultKind,
                code,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["effectFamily"] = binding.EffectFamily,
                    ["normalizedDomainState"] = stableStateDigest
                });
            return new NormalizedEffectOutcome(
                code,
                DomainOutcomeProjection(code, stableState),
                identity,
                adapterDeclaredSemanticEquivalence: true);
        }

        // No adapter means no authority to remove volatile fields or assert semantic sameness.
        // Bind the terminal identity to the exact invocation and observed result digest instead.
        var exactStableFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool"] = prepared.ToolName,
            ["canonicalArguments"] = prepared.ArgumentsDigest,
            ["invocationStatus"] = ((int)invocation.InvocationStatus)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["domainOutcome"] = ((int)invocation.DomainOutcome)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["failureCode"] = invocation.FailureCode ?? string.Empty,
            ["resultDigest"] = invocation.ResultDigest
        };
        var exactIdentity = EffectOutcomeIdentity.Create(
            invocation,
            resultKind,
            ExactInvocationOutcomeCode,
            exactStableFields);
        var exactProjection = JsonSerializer.SerializeToElement(new
        {
            code = ExactInvocationOutcomeCode,
            toolName = prepared.ToolName,
            canonicalArgumentsDigest = prepared.ArgumentsDigest,
            invocationStatus = invocation.InvocationStatus.ToString(),
            domainOutcome = invocation.DomainOutcome.ToString(),
            invocation.FailureCode,
            invocation.ResultDigest
        });
        return new NormalizedEffectOutcome(
            ExactInvocationOutcomeCode,
            exactProjection,
            exactIdentity,
            adapterDeclaredSemanticEquivalence: false);
    }

    private static JsonElement DomainOutcomeProjection(string code, JsonElement stableState) =>
        JsonSerializer.SerializeToElement(new
        {
            code,
            stableDomainState = stableState
        });

    private static string CanonicalDigest(JsonElement value)
    {
        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(value);
        try
        {
            return WorkIdentityCanonicalizer.CanonicalJsonDigest(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static JsonElement RequireJsonValue(JsonElement value, string parameterName)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException(
                "Effect normalization requires a defined JSON value.",
                parameterName);
        }

        return value.Clone();
    }

    private static string RequireIdentityValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Effect-normalization identity values cannot be blank.",
                parameterName);
        }

        return value;
    }

    internal sealed record AdapterBinding(
        IEffectNormalizationAdapter Adapter,
        string EffectFamily);
}
