using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Completion;

internal sealed record AliCompletionCriticDecodeResult(
    AliCompletionCriticVerdict? Verdict,
    string? Error)
{
    internal bool IsSuccess => Verdict is not null && Error is null;

    internal static AliCompletionCriticDecodeResult Success(
        AliCompletionCriticVerdict verdict) =>
        new(verdict, Error: null);

    internal static AliCompletionCriticDecodeResult Failure(string error) =>
        new(Verdict: null, error);
}

internal static class AliCompletionCriticProtocol
{
    internal const string SchemaName = "ali_completion_critic_verdict";
    internal const int MaximumResponseCharacters = 140_000;

    internal static JsonElement JsonSchema { get; } = JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "complete", "basis", "materialUnmetOutcomes" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["complete"] = new Dictionary<string, object?>
                {
                    ["type"] = "boolean"
                },
                ["basis"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = 4_000
                },
                ["materialUnmetOutcomes"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["maxItems"] = 64,
                    ["uniqueItems"] = true,
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                        ["maxLength"] = 2_000
                    }
                }
            }
        });

    internal static AIFunctionDeclaration CreateAdmissionDeclaration() =>
        AIFunctionFactory.CreateDeclaration(
            SchemaName,
            "Return the exact typed completion-critic verdict response.",
            JsonSchema);

    internal static AliCompletionCriticDecodeResult Decode(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.FinishReason != ChatFinishReason.Stop)
        {
            return AliCompletionCriticDecodeResult.Failure(
                "The completion critic did not return an explicit stop finish reason.");
        }

        if (response.Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>()
            .Any())
        {
            return AliCompletionCriticDecodeResult.Failure(
                "The completion critic returned a tool call even though the review lane has no tools.");
        }

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return AliCompletionCriticDecodeResult.Failure(
                "The completion critic returned no verdict object.");
        }

        if (text.Length > MaximumResponseCharacters)
        {
            return AliCompletionCriticDecodeResult.Failure(
                "The completion critic verdict exceeded the bounded protocol size.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return AliCompletionCriticDecodeResult.Failure(
                    "The completion critic verdict must be one JSON object.");
            }

            var exactNames = root.EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            if (exactNames.Length != 3
                || !exactNames.Contains("complete", StringComparer.Ordinal)
                || !exactNames.Contains("basis", StringComparer.Ordinal)
                || !exactNames.Contains("materialUnmetOutcomes", StringComparer.Ordinal))
            {
                return AliCompletionCriticDecodeResult.Failure(
                    "The completion critic verdict contained missing or additional properties.");
            }

            var completeElement = root.GetProperty("complete");
            if (completeElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                return AliCompletionCriticDecodeResult.Failure(
                    "The completion critic complete field must be boolean.");
            }

            var basisElement = root.GetProperty("basis");
            if (basisElement.ValueKind != JsonValueKind.String)
            {
                return AliCompletionCriticDecodeResult.Failure(
                    "The completion critic basis field must be text.");
            }

            var outcomesElement = root.GetProperty("materialUnmetOutcomes");
            if (outcomesElement.ValueKind != JsonValueKind.Array)
            {
                return AliCompletionCriticDecodeResult.Failure(
                    "The completion critic unmet outcomes field must be an array.");
            }

            var outcomes = new List<string>();
            foreach (var item in outcomesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return AliCompletionCriticDecodeResult.Failure(
                        "Every completion critic unmet outcome must be text.");
                }

                outcomes.Add(item.GetString() ?? string.Empty);
            }

            try
            {
                return AliCompletionCriticDecodeResult.Success(
                    new AliCompletionCriticVerdict(
                        completeElement.GetBoolean(),
                        basisElement.GetString() ?? string.Empty,
                        outcomes));
            }
            catch (ArgumentException exception)
            {
                return AliCompletionCriticDecodeResult.Failure(exception.Message);
            }
        }
        catch (JsonException exception)
        {
            return AliCompletionCriticDecodeResult.Failure(
                "The completion critic response was not one valid JSON object: "
                + exception.Message);
        }
    }
}
