using System.Text.Json;
using Ali.Modules.Runtime;

namespace Ali.UI.ViewModels;

internal static class RuntimeModelChoiceCatalog
{
    public static IReadOnlyList<RuntimeModelChoice> KnownChoices() =>
        Array.Empty<RuntimeModelChoice>();

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
    bool SupportsToolCalls,
    string Source)
{
    public ModelThinkingControl ThinkingControl { get; init; } = ModelThinkingControl.None;

    public string TokenizerIdentity { get; init; } = "provider-reported-or-unknown";

    public string RollingWindowMode { get; init; } = "provider-managed";

    public string Label => $"{DisplayName} ({Model})";

    public string DefaultQuantization => Quantizations.FirstOrDefault() ?? "Installed package default";

    public int DefaultContextTokens => ContextTokens.FirstOrDefault();

    public int DefaultOutputTokenLimit => OutputTokenLimits.FirstOrDefault();

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
            supportsToolCalls: options.SupportsToolCalls,
            contextTokens: options.ContextTokens,
            outputTokenLimit: options.OutputTokenLimit) with
        {
            ThinkingControl = options.ThinkingControl,
            TokenizerIdentity = options.TokenizerIdentity,
            RollingWindowMode = options.RollingWindowMode
        };

    public static RuntimeModelChoice? FromJsonModel(JsonElement item)
    {
        var model = ReadStringProperty(item, "id", "name", "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        if (IsNonChatModel(item, model))
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

        var declaredCapabilities = ReadDeclaredCapabilities(item).ToArray();
        var supportsVision = declaredCapabilities.Any(value =>
            value.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || value.Contains("image", StringComparison.OrdinalIgnoreCase)
            || value.Contains("multimodal", StringComparison.OrdinalIgnoreCase));
        var supportsToolCalls = declaredCapabilities.Any(value =>
            value.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || value.Contains("function", StringComparison.OrdinalIgnoreCase));
        var contextTokens = ReadIntProperty(
            item,
            "context_length",
            "max_context_length",
            "context_window",
            "context_tokens");
        var outputTokenLimit = ReadIntProperty(
            item,
            "max_output_tokens",
            "output_token_limit",
            "completion_tokens");
        var thinkingControl = ReadThinkingControl(item);
        var tokenizerIdentity = ReadStringProperty(item, "tokenizer", "tokenizer_identity")
            ?? "provider-reported-or-unknown";
        var rollingWindowMode = ReadStringProperty(item, "rolling_window", "rolling_window_mode")
            ?? "provider-managed";
        return FromModelId(
            model,
            "Installed runtime model",
            family: family,
            size: size,
            quantization: quantization,
            supportsVision: supportsVision,
            supportsToolCalls: supportsToolCalls,
            contextTokens: contextTokens,
            outputTokenLimit: outputTokenLimit) with
        {
            ThinkingControl = thinkingControl,
            TokenizerIdentity = tokenizerIdentity,
            RollingWindowMode = rollingWindowMode
        };
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
        bool? supportsToolCalls = null,
        int? contextTokens = null,
        int? outputTokenLimit = null)
    {
        var normalizedModel = model.Trim();
        var inferredSize = string.IsNullOrWhiteSpace(size) ? "provider-reported-or-unknown" : size.Trim();
        var inferredFamily = string.IsNullOrWhiteSpace(family) ? "provider-reported-or-unknown" : family.Trim();
        var inferredVision = supportsVision ?? false;
        var inferredToolCalls = supportsToolCalls ?? false;
        var contextChoices = BuildContextChoices(contextTokens);
        var outputChoices = BuildOutputChoices(outputTokenLimit);
        var quantizationChoices = new[]
        {
            string.IsNullOrWhiteSpace(quantization) ? "Installed package default" : quantization.Trim()
        };

        return new RuntimeModelChoice(
            normalizedModel,
            string.IsNullOrWhiteSpace(displayName) ? normalizedModel : displayName.Trim(),
            inferredFamily,
            inferredSize,
            quantizationChoices,
            contextChoices,
            outputChoices,
            streamingEnabled,
            inferredVision,
            inferredToolCalls,
            source);
    }

    private static bool IsNonChatModel(JsonElement item, string model)
    {
        var modelId = model.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? model;
        if (modelId.StartsWith("text-embedding-", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("embedding-", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("embeddings-", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("nomic-embed-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var declaredCapabilities = ReadDeclaredCapabilities(item).ToArray();
        if (declaredCapabilities.Any(IsChatCapability))
        {
            return false;
        }

        return declaredCapabilities.Any(IsNonChatCapability);
    }

    private static IEnumerable<string> ReadDeclaredCapabilities(JsonElement item)
    {
        foreach (var propertyName in new[] { "type", "task", "kind", "capability" })
        {
            if (TryGetProperty(item, propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && property.GetString() is { } value)
            {
                yield return value;
            }
        }

        foreach (var propertyName in new[] { "labels", "capabilities" })
        {
            if (TryGetProperty(item, propertyName, out var values)
                && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String
                        && value.GetString() is { } text)
                    {
                        yield return text;
                    }
                }
            }
        }
    }

    private static bool IsChatCapability(string? value) =>
        value is not null
        && (value.Equals("chat", StringComparison.OrdinalIgnoreCase)
            || value.Equals("completion", StringComparison.OrdinalIgnoreCase)
            || value.Equals("completions", StringComparison.OrdinalIgnoreCase)
            || value.Equals("generation", StringComparison.OrdinalIgnoreCase)
            || value.Equals("text-generation", StringComparison.OrdinalIgnoreCase)
            || value.Equals("text_generation", StringComparison.OrdinalIgnoreCase)
            || value.Equals("llm", StringComparison.OrdinalIgnoreCase)
            || value.Equals("language-model", StringComparison.OrdinalIgnoreCase));

    private static bool IsNonChatCapability(string? value) =>
        value is not null
        && (value.Equals("embedding", StringComparison.OrdinalIgnoreCase)
            || value.Equals("embeddings", StringComparison.OrdinalIgnoreCase)
            || value.Equals("reranking", StringComparison.OrdinalIgnoreCase)
            || value.Equals("reranker", StringComparison.OrdinalIgnoreCase)
            || value.Equals("transcription", StringComparison.OrdinalIgnoreCase)
            || value.Equals("tts", StringComparison.OrdinalIgnoreCase)
            || value.Equals("image", StringComparison.OrdinalIgnoreCase));

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
        var choices = set.ToList();
        if (preferred.HasValue && preferred.Value >= minimum)
        {
            choices.Remove(preferred.Value);
            choices.Insert(0, preferred.Value);
        }
        return choices;
    }

    private static ModelThinkingControl ReadThinkingControl(JsonElement item)
    {
        var text = ReadStringProperty(item, "thinking_control", "reasoning_control");
        return Enum.TryParse<ModelThinkingControl>(text, ignoreCase: true, out var value)
            ? value
            : ModelThinkingControl.None;
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

    private static int? ReadIntProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(item, name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out var value)
                && value > 0)
            {
                return value;
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

