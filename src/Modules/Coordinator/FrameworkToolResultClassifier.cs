using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Mcp;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

internal enum FrameworkToolResultDisposition
{
    CompletedReturn,
    InvocationFailed,
    CapabilityBlockedBeforeInvocation,
    ExternalOutcomeUnknown
}

internal static class FrameworkToolResultClassifier
{
    private const int MaximumSerializedMarkerCharacters = 65_536;

    internal static FrameworkToolResultDisposition Classify(
        FunctionResultContent functionResult) =>
        functionResult.Exception is not null
            ? FrameworkToolResultDisposition.InvocationFailed
            : Classify(functionResult.Result);

    internal static FrameworkToolResultDisposition Classify(object? result) => result switch
    {
        CapabilityInvocationBlockedResult =>
            FrameworkToolResultDisposition.CapabilityBlockedBeforeInvocation,
        IMcpPostDispatchFailureResult =>
            FrameworkToolResultDisposition.ExternalOutcomeUnknown,
        JsonElement element => Classify(element),
        JsonDocument document => Classify(document.RootElement),
        string text => ClassifySerializedText(text),
        IReadOnlyDictionary<string, object?> dictionary => ClassifyDictionary(dictionary),
        _ => FrameworkToolResultDisposition.CompletedReturn
    };

    private static FrameworkToolResultDisposition Classify(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return FrameworkToolResultDisposition.CompletedReturn;
        }

        if (HasBoolean(element, "success", false)
            && HasBoolean(element, "invoked", false)
            && HasString(element, "status", "blocked"))
        {
            return FrameworkToolResultDisposition.CapabilityBlockedBeforeInvocation;
        }

        return HasBoolean(element, "success", false)
               && HasBoolean(element, "invoked", true)
               && HasBoolean(element, "outcomeUnknown", true)
               && HasBoolean(element, "retrySafe", false)
            ? FrameworkToolResultDisposition.ExternalOutcomeUnknown
            : FrameworkToolResultDisposition.CompletedReturn;
    }

    private static FrameworkToolResultDisposition ClassifySerializedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.Length > MaximumSerializedMarkerCharacters
            || !text.TrimStart().StartsWith('{'))
        {
            return FrameworkToolResultDisposition.CompletedReturn;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return Classify(document.RootElement);
        }
        catch (JsonException)
        {
            return FrameworkToolResultDisposition.CompletedReturn;
        }
    }

    private static FrameworkToolResultDisposition ClassifyDictionary(
        IReadOnlyDictionary<string, object?> dictionary)
    {
        if (HasBoolean(dictionary, "success", false)
            && HasBoolean(dictionary, "invoked", false)
            && HasString(dictionary, "status", "blocked"))
        {
            return FrameworkToolResultDisposition.CapabilityBlockedBeforeInvocation;
        }

        return HasBoolean(dictionary, "success", false)
               && HasBoolean(dictionary, "invoked", true)
               && HasBoolean(dictionary, "outcomeUnknown", true)
               && HasBoolean(dictionary, "retrySafe", false)
            ? FrameworkToolResultDisposition.ExternalOutcomeUnknown
            : FrameworkToolResultDisposition.CompletedReturn;
    }

    private static bool HasBoolean(JsonElement element, string name, bool expected) =>
        element.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.Value.GetBoolean() == expected);

    private static bool HasBoolean(
        IReadOnlyDictionary<string, object?> dictionary,
        string name,
        bool expected) =>
        dictionary.Any(pair =>
            pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
            && pair.Value switch
            {
                bool value => value == expected,
                JsonElement value when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                    value.GetBoolean() == expected,
                _ => false
            });

    private static bool HasString(JsonElement element, string name, string expected) =>
        element.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && property.Value.ValueKind == JsonValueKind.String
            && string.Equals(property.Value.GetString(), expected, StringComparison.OrdinalIgnoreCase));

    private static bool HasString(
        IReadOnlyDictionary<string, object?> dictionary,
        string name,
        string expected) =>
        dictionary.Any(pair =>
            pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
            && pair.Value switch
            {
                string value => value.Equals(expected, StringComparison.OrdinalIgnoreCase),
                JsonElement value when value.ValueKind == JsonValueKind.String =>
                    string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase),
                _ => false
            });
}
