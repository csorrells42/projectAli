using System.Text.Json;
using Ali.Modules.Runtime;

namespace Ali.UI.ViewModels;

internal static class RuntimeModelChoiceCatalog
{
    public static IReadOnlyList<RuntimeModelChoice> KnownChoices() =>
    [
        RuntimeModelChoice.FromModelId(
            "gpt-oss-20b-mxfp4-GGUF",
            "Installed Lemonade reasoning model",
            displayName: "GPT-OSS 20B - Lemonade",
            family: "GPT-OSS",
            size: "20B",
            quantization: "MXFP4",
            contextTokens: OllamaRuntimeSafetyPolicy.DefaultContextTokens,
            outputTokenLimit: 1024)
    ];

    public static IReadOnlyList<RuntimeModelChoice> ParseRuntimeModelChoices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var choices = new List<RuntimeModelChoice>();

        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var choice = RuntimeModelChoice.FromJsonModel(item);
                if (choice is not null)
                {
                    choices.Add(choice);
                }
            }
        }

        if (document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                var choice = RuntimeModelChoice.FromJsonModel(item);
                if (choice is not null)
                {
                    choices.Add(choice);
                }
            }
        }

        return choices
            .GroupBy(choice => choice.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed record RuntimeModelChoice(
    string Model,
    string DisplayName,
    string Family,
    string Size,
    IReadOnlyList<string> Quantizations,
    IReadOnlyList<int> ContextTokens,
    IReadOnlyList<int> OutputTokenLimits,
    bool StreamingEnabled,
    bool SupportsVision,
    string Source)
{
    public string Label => $"{DisplayName} ({Model})";

    public string DefaultQuantization => Quantizations.FirstOrDefault() ?? "Installed package default";

    public static RuntimeModelChoice FromOptions(OpenAiCompatibleRuntimeOptions options) =>
        FromModelId(
            options.Model,
            "Saved runtime setting",
            displayName: options.DisplayName,
            family: options.Family,
            size: options.Size,
            quantization: options.Quantization,
            streamingEnabled: options.StreamingEnabled,
            supportsVision: options.SupportsVision,
            contextTokens: options.ContextTokens,
            outputTokenLimit: options.OutputTokenLimit);

    public static RuntimeModelChoice? FromJsonModel(JsonElement item)
    {
        var model = ReadStringProperty(item, "id", "name", "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (TryGetProperty(item, "labels", out var labels)
            && labels.ValueKind is JsonValueKind.Array
            && labels.EnumerateArray().Any(label =>
                label.ValueKind is JsonValueKind.String
                && label.GetString() is { } value
                && value is "embeddings" or "embedding" or "reranking" or "transcription" or "tts" or "image"))
        {
            return null;
        }

        JsonElement? details = TryGetProperty(item, "details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.Object
            ? detailsElement
            : null;

        var family = details is { } modelDetails
            ? ReadStringProperty(modelDetails, "family", "families")
            : null;
        var size = details is { } sizeDetails
            ? ReadStringProperty(sizeDetails, "parameter_size", "size")
            : null;
        var quantization = details is { } quantDetails
            ? ReadStringProperty(quantDetails, "quantization_level", "quantization")
            : null;

        return FromModelId(
            model,
            "Installed local runtime model",
            family: family,
            size: size,
            quantization: quantization);
    }

    public static RuntimeModelChoice FromModelId(
        string model,
        string source,
        string? displayName = null,
        string? family = null,
        string? size = null,
        string? quantization = null,
        bool streamingEnabled = true,
        bool? supportsVision = null,
        int? contextTokens = null,
        int? outputTokenLimit = null)
    {
        var normalizedModel = model.Trim();
        var inferredSize = string.IsNullOrWhiteSpace(size) ? InferSize(normalizedModel) : size.Trim();
        var inferredFamily = string.IsNullOrWhiteSpace(family) ? InferFamily(normalizedModel) : family.Trim();
        var inferredVision = supportsVision
            ?? (normalizedModel.Contains("vl", StringComparison.OrdinalIgnoreCase)
                || normalizedModel.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || normalizedModel.Contains("visual", StringComparison.OrdinalIgnoreCase));
        var contextChoices = BuildContextChoices(contextTokens);
        var outputChoices = BuildOutputChoices(outputTokenLimit);
        var quantizationChoices = new[]
        {
            string.IsNullOrWhiteSpace(quantization) ? "Installed package default" : quantization.Trim()
        };

        return new RuntimeModelChoice(
            normalizedModel,
            string.IsNullOrWhiteSpace(displayName) ? InferDisplayName(normalizedModel, inferredSize) : displayName.Trim(),
            inferredFamily,
            inferredSize,
            quantizationChoices,
            contextChoices,
            outputChoices,
            streamingEnabled,
            inferredVision,
            source);
    }

    private static IReadOnlyList<int> BuildContextChoices(int? preferred) =>
        AddPreferred(
            [
                1_024,
                2_048,
                4_096,
                8_192,
                16_384,
                32_768,
                65_536,
                131_072,
                262_144
            ],
            preferred,
            minimum: 512);

    private static IReadOnlyList<int> BuildOutputChoices(int? preferred) =>
        AddPreferred(
            [
                128,
                256,
                512,
                1_024,
                2_048,
                4_096,
                8_192,
                16_384,
                32_768
            ],
            preferred,
            minimum: 1);

    private static IReadOnlyList<int> AddPreferred(IReadOnlyList<int> values, int? preferred, int minimum)
    {
        var set = new SortedSet<int>(values);
        if (preferred.HasValue && preferred.Value >= minimum)
        {
            set.Add(preferred.Value);
        }

        return set.ToList();
    }

    private static string InferDisplayName(string model, string size)
    {
        var lower = model.ToLowerInvariant();
        if (lower.Contains("gpt-oss", StringComparison.Ordinal))
        {
            return $"GPT-OSS {size}";
        }

        if (lower.Contains("qwen3-vl", StringComparison.Ordinal))
        {
            return $"Qwen3 VL {size}";
        }

        if (lower.Contains("qwen3", StringComparison.Ordinal))
        {
            return $"Qwen3 {size}";
        }

        if (lower.Contains("gemma4", StringComparison.Ordinal))
        {
            return $"Gemma 4 {size}";
        }

        if (lower.Contains("deepseek-coder", StringComparison.Ordinal))
        {
            return $"DeepSeek Coder V2 {size}";
        }

        return model;
    }

    private static string InferFamily(string model)
    {
        var lower = model.ToLowerInvariant();
        if (lower.Contains("gpt-oss", StringComparison.Ordinal))
        {
            return "GPT-OSS";
        }

        if (lower.Contains("qwen", StringComparison.Ordinal))
        {
            return "Qwen";
        }

        if (lower.Contains("gemma", StringComparison.Ordinal))
        {
            return "Gemma";
        }

        if (lower.Contains("deepseek", StringComparison.Ordinal))
        {
            return "DeepSeek Coder";
        }

        return "local";
    }

    private static string InferSize(string model)
    {
        foreach (var size in new[] { "120B", "32B", "27B", "26B", "20B", "16B", "14B", "12B", "8B", "4B", "1.7B" })
        {
            if (model.Contains(size, StringComparison.OrdinalIgnoreCase))
            {
                return size;
            }
        }

        return "unknown";
    }

    private static string? ReadStringProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(item, name, out var property))
            {
                if (property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }

                if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return property.ToString();
                }

                if (property.ValueKind == JsonValueKind.Array)
                {
                    var first = property.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.String)
                    {
                        return first.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement property)
    {
        foreach (var candidate in item.EnumerateObject())
        {
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}

