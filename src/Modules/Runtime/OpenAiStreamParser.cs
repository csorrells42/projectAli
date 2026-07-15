using System.Text.Json;

namespace Ali.Modules.Runtime;

public sealed record OpenAiStreamEvent(
    string? Content,
    string? FinishReason,
    bool IsDone);

public sealed record OpenAiMessageResult(
    string? Content,
    string? FinishReason);

public static class OpenAiStreamParser
{
    public static string? ExtractContentDelta(string dataLine, bool includeReasoning = false)
        => ExtractStreamEvent(dataLine, includeReasoning).Content;

    public static OpenAiStreamEvent ExtractStreamEvent(string dataLine, bool includeReasoning = false)
    {
        var payload = dataLine.Trim();
        if (payload.Length == 0)
        {
            return new OpenAiStreamEvent(null, null, IsDone: false);
        }

        if (payload == "[DONE]")
        {
            return new OpenAiStreamEvent(null, null, IsDone: true);
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return new OpenAiStreamEvent(null, null, IsDone: false);
        }

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            && finishReasonElement.ValueKind == JsonValueKind.String
                ? finishReasonElement.GetString()
                : null;

        if (choice.TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("content", out var deltaContent)
            && deltaContent.ValueKind == JsonValueKind.String)
        {
            var content = deltaContent.GetString();
            if (!string.IsNullOrEmpty(content))
            {
                return new OpenAiStreamEvent(content, finishReason, IsDone: false);
            }
        }

        if (includeReasoning
            && choice.TryGetProperty("delta", out delta)
            && delta.TryGetProperty("reasoning", out var deltaReasoning)
            && deltaReasoning.ValueKind == JsonValueKind.String)
        {
            return new OpenAiStreamEvent(deltaReasoning.GetString(), finishReason, IsDone: false);
        }

        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var messageContent)
            && messageContent.ValueKind == JsonValueKind.String)
        {
            var content = messageContent.GetString();
            if (!string.IsNullOrEmpty(content))
            {
                return new OpenAiStreamEvent(content, finishReason, IsDone: false);
            }
        }

        if (includeReasoning
            && choice.TryGetProperty("message", out message)
            && message.TryGetProperty("reasoning", out var messageReasoning)
            && messageReasoning.ValueKind == JsonValueKind.String)
        {
            return new OpenAiStreamEvent(messageReasoning.GetString(), finishReason, IsDone: false);
        }

        return new OpenAiStreamEvent(null, finishReason, IsDone: false);
    }

    public static string? ExtractMessageContent(string json, bool includeReasoning = false)
        => ExtractMessageResult(json, includeReasoning).Content;

    public static OpenAiMessageResult ExtractMessageResult(string json, bool includeReasoning = false)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return new OpenAiMessageResult(null, null);
        }

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            && finishReasonElement.ValueKind == JsonValueKind.String
                ? finishReasonElement.GetString()
                : null;

        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            var messageContent = content.GetString();
            if (!string.IsNullOrEmpty(messageContent))
            {
                return new OpenAiMessageResult(messageContent, finishReason);
            }
        }

        if (includeReasoning
            && choice.TryGetProperty("message", out message)
            && message.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            return new OpenAiMessageResult(reasoning.GetString(), finishReason);
        }

        return new OpenAiMessageResult(null, finishReason);
    }
}
