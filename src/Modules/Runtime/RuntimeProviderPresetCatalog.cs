using System.Collections.ObjectModel;
using System.Text.Json;

namespace Ali.Modules.Runtime;

public sealed record RuntimeProviderPreset(
    string Id,
    string DisplayName,
    string Endpoint);

public sealed class RuntimeProviderPresetCatalog
{
    public const string ConfigurationFileName = "runtime-provider-presets.json";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyDictionary<string, RuntimeProviderPreset> _llmById;
    private readonly IReadOnlyDictionary<string, RuntimeProviderPreset> _embeddingById;

    private RuntimeProviderPresetCatalog(
        IReadOnlyList<RuntimeProviderPreset> llmPresets,
        IReadOnlyList<RuntimeProviderPreset> embeddingPresets)
    {
        LlmPresets = llmPresets;
        EmbeddingPresets = embeddingPresets;
        _llmById = IndexById(llmPresets);
        _embeddingById = IndexById(embeddingPresets);
    }

    public static string DefaultPath => Path.Combine(
        AppContext.BaseDirectory,
        "Configuration",
        ConfigurationFileName);

    public IReadOnlyList<RuntimeProviderPreset> LlmPresets { get; }

    public IReadOnlyList<RuntimeProviderPreset> EmbeddingPresets { get; }

    public static RuntimeProviderPresetCatalog LoadDefault() => Load(DefaultPath);

    public static RuntimeProviderPresetCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The external runtime provider preset catalog was not found.",
                fullPath);
        }

        RuntimeProviderPresetDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RuntimeProviderPresetDocument>(
                    File.ReadAllText(fullPath),
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "The runtime provider preset catalog is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "The runtime provider preset catalog is not valid JSON.",
                ex);
        }

        var llmPresets = ValidateSection(document.Llm, "llm");
        var embeddingPresets = ValidateSection(document.Embedding, "embedding");
        return new RuntimeProviderPresetCatalog(llmPresets, embeddingPresets);
    }

    public RuntimeProviderPreset RequireLlm(string id) =>
        Require(_llmById, id, "llm");

    public RuntimeProviderPreset RequireEmbedding(string id) =>
        Require(_embeddingById, id, "embedding");

    private static IReadOnlyList<RuntimeProviderPreset> ValidateSection(
        RuntimeProviderPreset[]? presets,
        string sectionName)
    {
        if (presets is null || presets.Length == 0)
        {
            throw new InvalidDataException(
                $"The runtime provider preset catalog requires a non-empty '{sectionName}' array.");
        }

        var validated = new RuntimeProviderPreset[presets.Length];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < presets.Length; index++)
        {
            var preset = presets[index]
                ?? throw new InvalidDataException(
                    $"The '{sectionName}' preset at index {index} is null.");
            ValidateText(preset.Id, sectionName, index, "id");
            ValidateText(preset.DisplayName, sectionName, index, "displayName");
            if (!ids.Add(preset.Id))
            {
                throw new InvalidDataException(
                    $"The '{sectionName}' preset ID '{preset.Id}' is duplicated.");
            }

            if (!string.IsNullOrEmpty(preset.Endpoint))
            {
                if (!string.Equals(preset.Endpoint, preset.Endpoint.Trim(), StringComparison.Ordinal)
                    || !Uri.TryCreate(preset.Endpoint, UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme is not ("http" or "https"))
                {
                    throw new InvalidDataException(
                        $"The '{sectionName}' preset '{preset.Id}' endpoint must be blank or an absolute HTTP/HTTPS URL without surrounding whitespace.");
                }
            }

            validated[index] = preset;
        }

        return Array.AsReadOnly(validated);
    }

    private static void ValidateText(
        string? value,
        string sectionName,
        int index,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The '{sectionName}' preset at index {index} requires a non-blank '{fieldName}' without surrounding whitespace.");
        }
    }

    private static IReadOnlyDictionary<string, RuntimeProviderPreset> IndexById(
        IReadOnlyList<RuntimeProviderPreset> presets)
    {
        var index = presets.ToDictionary(preset => preset.Id, StringComparer.OrdinalIgnoreCase);
        return new ReadOnlyDictionary<string, RuntimeProviderPreset>(index);
    }

    private static RuntimeProviderPreset Require(
        IReadOnlyDictionary<string, RuntimeProviderPreset> presets,
        string id,
        string sectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            throw new KeyNotFoundException(
                $"The '{sectionName}' runtime provider preset ID must not contain surrounding whitespace.");
        }

        return presets.TryGetValue(id, out var preset)
            ? preset
            : throw new KeyNotFoundException(
                $"The external runtime provider preset catalog does not contain '{id}' in '{sectionName}'.");
    }

    private sealed record RuntimeProviderPresetDocument(
        RuntimeProviderPreset[]? Llm,
        RuntimeProviderPreset[]? Embedding);
}
