using System.Collections.Frozen;
using System.Text.Json;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Orchestration.Work;

public interface IActionTargetStateAdapter
{
    IReadOnlyCollection<string> ToolNames { get; }

    TargetStateSnapshot Capture(string toolName, JsonElement arguments);
}

public sealed record TargetStateSnapshot(
    IReadOnlyDictionary<string, string> TargetVersions,
    IReadOnlyDictionary<string, string> ArtifactVersions,
    IReadOnlyDictionary<string, string> DiagnosticStates,
    IReadOnlyDictionary<string, string> TestStates)
{
    public static TargetStateSnapshot Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed class PreparedTargetState
{
    private readonly JsonElement _arguments;
    private readonly IActionTargetStateAdapter? _adapter;

    internal PreparedTargetState(
        string toolName,
        string argumentsDigest,
        JsonElement arguments,
        IActionTargetStateAdapter? adapter)
    {
        ToolName = toolName;
        ArgumentsDigest = argumentsDigest;
        _arguments = arguments.Clone();
        _adapter = adapter;
    }

    public string ToolName { get; }

    public string ArgumentsDigest { get; }

    internal IActionTargetStateAdapter? Adapter => _adapter;

    internal JsonElement Arguments => _arguments.Clone();
}

/// <summary>
/// Exact tool-name target-state capture. Unknown tools receive a conservative unsupported
/// snapshot and can never claim that an external target changed.
/// </summary>
public sealed class TargetStateRegistry
{
    private const int MaximumMapEntries = 64;
    private const int MaximumValueCharacters = 512;
    private readonly FrozenDictionary<string, IActionTargetStateAdapter> _adapters;

    public TargetStateRegistry(IEnumerable<IActionTargetStateAdapter>? adapters = null)
    {
        var map = new Dictionary<string, IActionTargetStateAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters ?? [])
        {
            ArgumentNullException.ThrowIfNull(adapter);
            foreach (var name in adapter.ToolNames
                         ?? throw new ArgumentException("A target-state adapter must declare tool names."))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (!map.TryAdd(name, adapter))
                {
                    throw new ArgumentException(
                        $"Tool '{name}' has more than one target-state adapter.",
                        nameof(adapters));
                }
            }
        }

        _adapters = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static TargetStateRegistry Empty { get; } = new();

    public PreparedTargetState Prepare(string toolName, JsonElement arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (arguments.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("Target-state arguments must be defined.", nameof(arguments));
        }

        var bytes = CanonicalEvidenceJson.SerializeToUtf8Bytes(arguments);
        string digest;
        try
        {
            digest = WorkIdentityCanonicalizer.CanonicalJsonDigest(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }

        _adapters.TryGetValue(toolName, out var adapter);
        return new PreparedTargetState(toolName, digest, arguments, adapter);
    }

    public TargetStateSnapshot Capture(PreparedTargetState prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Adapter is null)
        {
            var conservative = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["capture"] = "unsupported",
                ["tool"] = prepared.ToolName,
                ["canonicalArguments"] = prepared.ArgumentsDigest
            };
            return new TargetStateSnapshot(
                conservative,
                conservative,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var captured = prepared.Adapter.Capture(prepared.ToolName, prepared.Arguments)
            ?? throw new InvalidDataException(
                $"Target-state adapter for '{prepared.ToolName}' returned no snapshot.");
        return new TargetStateSnapshot(
            ValidateMap(captured.TargetVersions, "target versions"),
            ValidateMap(captured.ArtifactVersions, "artifact versions"),
            ValidateMap(captured.DiagnosticStates, "diagnostic states"),
            ValidateMap(captured.TestStates, "test states"));
    }

    private static IReadOnlyDictionary<string, string> ValidateMap(
        IReadOnlyDictionary<string, string>? source,
        string label)
    {
        if (source is null || source.Count > MaximumMapEntries)
        {
            throw new InvalidDataException($"The target-state {label} map is missing or unbounded.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Length > MaximumValueCharacters
                || string.IsNullOrWhiteSpace(pair.Value)
                || pair.Value.Length > MaximumValueCharacters
                || !result.TryAdd(pair.Key, pair.Value))
            {
                throw new InvalidDataException($"The target-state {label} map is invalid.");
            }
        }

        return result;
    }
}
