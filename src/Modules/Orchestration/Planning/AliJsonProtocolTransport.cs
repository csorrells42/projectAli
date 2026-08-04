using System.Text.Json;

namespace Ali.Modules.Orchestration.Planning;

/// <summary>
/// Grammar-safe provider transport shared by every typed orchestration lane. The provider sees one
/// required string; Ali keeps the lane-specific schema authoritative in the prompt and validates the
/// parsed inner object locally before it can affect state.
/// </summary>
internal static class AliJsonProtocolTransport
{
    internal const int MaximumEnvelopeCharacters = 1_048_576;
    internal const int MaximumPayloadCharacters = 262_144;
    internal const int MaximumJsonDepth = 64;

    internal static bool TryDecode(
        JsonElement envelope,
        string payloadDescription,
        out JsonElement payload,
        out string error)
    {
        payload = default;
        error = string.Empty;
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            error = $"The {payloadDescription} transport must be one JSON object.";
            return false;
        }

        if (envelope.GetRawText().Length > MaximumEnvelopeCharacters)
        {
            error = $"The {payloadDescription} transport exceeded its bounded size.";
            return false;
        }

        var properties = envelope.EnumerateObject().ToArray();
        if (properties.Length != 1
            || !string.Equals(
                properties[0].Name,
                AliOrchestrationProtocol.DecisionJsonPropertyName,
                StringComparison.Ordinal))
        {
            error = $"The {payloadDescription} transport contained a missing, duplicate, or additional property.";
            return false;
        }

        var serializedPayload = properties[0].Value.ValueKind == JsonValueKind.String
            ? properties[0].Value.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(serializedPayload))
        {
            error = $"The {payloadDescription} transport payload must be a non-empty JSON string.";
            return false;
        }

        if (serializedPayload.Length > MaximumPayloadCharacters)
        {
            error = $"The {payloadDescription} transport payload exceeded its bounded size.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                serializedPayload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"The {payloadDescription} transport payload must contain one JSON object.";
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The {payloadDescription} transport payload was not one valid JSON object: {exception.Message}";
            return false;
        }
    }
}
