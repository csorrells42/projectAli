using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Planning;

internal abstract record AliAnswerCompositionAction;

internal sealed record AliAppendAnswerSegmentAction(
    string AnswerId,
    int Sequence,
    string PreviousSegmentHash,
    string Text,
    IReadOnlyList<string> CoveredClaimIds) : AliAnswerCompositionAction;

internal sealed record AliFinishAnswerAction(string AnswerId) : AliAnswerCompositionAction;

internal sealed record AliAnswerCompositionDecodeResult(
    AliAnswerCompositionAction? Action,
    string? Error)
{
    internal bool IsSuccess => Action is not null;

    internal static AliAnswerCompositionDecodeResult Success(
        AliAnswerCompositionAction action) =>
        new(action ?? throw new ArgumentNullException(nameof(action)), Error: null);

    internal static AliAnswerCompositionDecodeResult Failure(string error) =>
        new(Action: null, string.IsNullOrWhiteSpace(error) ? "invalid-envelope" : error);
}

internal static class AliAnswerCompositionProtocol
{
    internal const string ToolName = "submit_answer_composition";
    internal static JsonElement DecisionSchema { get; } = BuildDecisionSchema();

    internal static AIFunctionDeclaration CreateDeclaration() =>
        AIFunctionFactory.CreateDeclaration(
            ToolName,
            "Return one strict answer-composition decision as JSON text in the decisionJson field.",
            AliOrchestrationProtocol.BuildTransportSchema());

    private static JsonElement BuildDecisionSchema() => JsonSerializer.SerializeToElement(
        new Dictionary<string, object?>
        {
            ["oneOf"] = new object[]
            {
                ObjectSchema(
                    ["kind", "answerId", "sequence", "previousSegmentHash", "text", "coveredClaimIds"],
                    new Dictionary<string, object?>
                    {
                        ["kind"] = ConstString("appendSegment"),
                        ["answerId"] = BoundedString(1, 256),
                        ["sequence"] = new Dictionary<string, object?>
                        {
                            ["type"] = "integer",
                            ["minimum"] = 0
                        },
                        ["previousSegmentHash"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["pattern"] = "^[0-9a-f]{64}$"
                        },
                        ["text"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["minLength"] = 1
                        },
                        ["coveredClaimIds"] = IdentifierArray()
                    }),
                ObjectSchema(
                    ["kind", "answerId"],
                    new Dictionary<string, object?>
                    {
                        ["kind"] = ConstString("finishAnswer"),
                        ["answerId"] = BoundedString(1, 256)
                    })
            }
        });

    private static Dictionary<string, object?> ObjectSchema(
        string[] required,
        Dictionary<string, object?> properties) =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = required,
            ["properties"] = properties
        };

    private static Dictionary<string, object?> IdentifierArray() =>
        new()
        {
            ["type"] = "array",
            ["maxItems"] = 256,
            ["uniqueItems"] = true,
            ["items"] = BoundedString(1, 256)
        };

    private static Dictionary<string, object?> BoundedString(int minimum, int maximum) =>
        new()
        {
            ["type"] = "string",
            ["minLength"] = minimum,
            ["maxLength"] = maximum
        };

    private static Dictionary<string, object?> ConstString(string value) =>
        new()
        {
            ["type"] = "string",
            ["const"] = value
        };
}

internal static class AliAnswerCompositionDecoder
{
    internal static AliAnswerCompositionDecodeResult DecodeNative(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var calls = response.Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(static call => !call.InformationalOnly)
            .ToArray();
        if (calls.Length != 1
            || !string.Equals(calls[0].Name, AliAnswerCompositionProtocol.ToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(calls[0].CallId))
        {
            return AliAnswerCompositionDecodeResult.Failure("invalid-native-envelope");
        }

        try
        {
            var transport = JsonSerializer.SerializeToElement(
                calls[0].Arguments
                ?? new Dictionary<string, object?>(StringComparer.Ordinal));
            return AliJsonProtocolTransport.TryDecode(
                transport,
                "answer-composition",
                out var payload,
                out var error)
                ? Decode(payload)
                : AliAnswerCompositionDecodeResult.Failure(error);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return AliAnswerCompositionDecodeResult.Failure("invalid-native-json");
        }
    }

    internal static AliAnswerCompositionDecodeResult DecodeCompatibility(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.FinishReason != ChatFinishReason.Stop)
        {
            return AliAnswerCompositionDecodeResult.Failure(
                "answer-composition compatibility transport did not stop explicitly");
        }

        if (response.Messages
            .SelectMany(static message => message.Contents)
            .OfType<FunctionCallContent>()
            .Any())
        {
            return AliAnswerCompositionDecodeResult.Failure(
                "answer-composition compatibility transport returned an unexpected tool call");
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return AliAnswerCompositionDecodeResult.Failure("missing-compatibility-envelope");
        }

        try
        {
            if (response.Text.Length > AliJsonProtocolTransport.MaximumEnvelopeCharacters)
            {
                return AliAnswerCompositionDecodeResult.Failure(
                    "answer-composition transport exceeded its bounded size");
            }

            using var document = JsonDocument.Parse(
                response.Text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = AliJsonProtocolTransport.MaximumJsonDepth
                });
            return AliJsonProtocolTransport.TryDecode(
                document.RootElement,
                "answer-composition",
                out var payload,
                out var error)
                ? Decode(payload)
                : AliAnswerCompositionDecodeResult.Failure(error);
        }
        catch (JsonException)
        {
            return AliAnswerCompositionDecodeResult.Failure("invalid-compatibility-json");
        }
    }

    internal static AliAnswerCompositionDecodeResult Decode(JsonElement root)
    {
        try
        {
            RequireObject(root, "answer composition");
            var kind = RequireString(root, "kind", "answer composition");
            return kind switch
            {
                "appendSegment" => DecodeAppend(root),
                "finishAnswer" => DecodeFinish(root),
                _ => throw new InvalidDataException("The answer composition kind is invalid.")
            };
        }
        catch (InvalidDataException)
        {
            return AliAnswerCompositionDecodeResult.Failure("invalid-composition-envelope");
        }
    }

    private static AliAnswerCompositionDecodeResult DecodeAppend(JsonElement root)
    {
        RequireExactProperties(
            root,
            "kind",
            "answerId",
            "sequence",
            "previousSegmentHash",
            "text",
            "coveredClaimIds");
        var answerId = RequireBoundedString(root, "answerId", 256);
        var sequenceElement = RequireProperty(root, "sequence", "answer composition");
        if (sequenceElement.ValueKind != JsonValueKind.Number
            || !sequenceElement.TryGetInt32(out var sequence)
            || sequence < 0)
        {
            throw new InvalidDataException("The answer segment sequence is invalid.");
        }

        var previousHash = RequireBoundedString(root, "previousSegmentHash", 64);
        if (previousHash.Length != 64
            || previousHash.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The previous answer-segment hash is invalid.");
        }

        var text = RequireString(root, "text", "answer composition");
        if (text.Length == 0)
        {
            throw new InvalidDataException("The answer segment text is empty.");
        }

        var covered = ReadIdentifierArray(root, "coveredClaimIds");
        return AliAnswerCompositionDecodeResult.Success(new AliAppendAnswerSegmentAction(
            answerId,
            sequence,
            previousHash,
            text,
            covered));
    }

    private static AliAnswerCompositionDecodeResult DecodeFinish(JsonElement root)
    {
        RequireExactProperties(root, "kind", "answerId");
        return AliAnswerCompositionDecodeResult.Success(new AliFinishAnswerAction(
            RequireBoundedString(root, "answerId", 256)));
    }

    private static IReadOnlyList<string> ReadIdentifierArray(
        JsonElement parent,
        string propertyName)
    {
        var element = RequireProperty(parent, propertyName, "answer composition");
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Answer claim coverage must be an array.");
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (values.Count == 256
                || item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Answer claim coverage is invalid.");
            }

            var value = item.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            {
                throw new InvalidDataException("An answer claim identifier is invalid.");
            }

            values.Add(value);
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidDataException("Answer claim coverage contains duplicates.");
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private static string RequireBoundedString(
        JsonElement parent,
        string propertyName,
        int maximumLength)
    {
        var value = RequireString(parent, propertyName, "answer composition");
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidDataException($"The answer composition {propertyName} is invalid.");
        }

        return value;
    }

    private static string RequireString(
        JsonElement parent,
        string propertyName,
        string parentPath)
    {
        var element = RequireProperty(parent, propertyName, parentPath);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{parentPath}.{propertyName} must be a string.");
        }

        return element.GetString() ?? string.Empty;
    }

    private static JsonElement RequireProperty(
        JsonElement parent,
        string propertyName,
        string parentPath)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            throw new InvalidDataException($"{parentPath}.{propertyName} is required.");
        }

        return element;
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} must be an object.");
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] expected)
    {
        var names = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (names.Length != expected.Length
            || names.Except(expected, StringComparer.Ordinal).Any()
            || expected.Except(names, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException(
                "The answer composition object has missing or unexpected properties.");
        }
    }
}
