using System.Text.Json;

namespace Ali.Infrastructure.Runtime;

public static class OpenAiStreamParser
{
    public static string? ExtractContentDelta(string dataLine, bool includeReasoning = false)
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
            var content = deltaContent.GetString();
            if (!string.IsNullOrEmpty(content))
            {
                return content;
            }
        }

        if (includeReasoning
            && choice.TryGetProperty("delta", out delta)
            && delta.TryGetProperty("reasoning", out var deltaReasoning)
            && deltaReasoning.ValueKind == JsonValueKind.String)
        {
            return deltaReasoning.GetString();
        }

        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var messageContent)
            && messageContent.ValueKind == JsonValueKind.String)
        {
            var content = messageContent.GetString();
            if (!string.IsNullOrEmpty(content))
            {
                return content;
            }
        }

        if (includeReasoning
            && choice.TryGetProperty("message", out message)
            && message.TryGetProperty("reasoning", out var messageReasoning)
            && messageReasoning.ValueKind == JsonValueKind.String)
        {
            return messageReasoning.GetString();
        }

        return null;
    }

    public static string? ExtractMessageContent(string json, bool includeReasoning = false)
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
            var messageContent = content.GetString();
            if (!string.IsNullOrEmpty(messageContent))
            {
                return messageContent;
            }
        }

        if (includeReasoning
            && choice.TryGetProperty("message", out message)
            && message.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            return reasoning.GetString();
        }

        return null;
    }
}
