using System.Text.Json;

namespace Ali.Infrastructure.Runtime;

public static class OpenAiStreamParser
{
    public static string? ExtractContentDelta(string dataLine)
    {
        var payload = dataLine.Trim();
        if (payload.Length == 0 || payload == "[DONE]")
        {
            return null;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];

        if (choice.TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("content", out var deltaContent)
            && deltaContent.ValueKind == JsonValueKind.String)
        {
            return deltaContent.GetString();
        }

        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var messageContent)
            && messageContent.ValueKind == JsonValueKind.String)
        {
            return messageContent.GetString();
        }

        return null;
    }

    public static string? ExtractMessageContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];

        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        return null;
    }
}
