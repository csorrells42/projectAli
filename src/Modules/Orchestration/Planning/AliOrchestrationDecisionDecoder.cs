using System.Text.Json;
using Ali.Modules.Capabilities;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Planning;

public sealed record OrchestrationDecisionDecodeResult
{
    private OrchestrationDecisionDecodeResult(OrchestrationDecision? decision, string? error)
    {
        Decision = decision;
        Error = error;
    }

    public bool IsSuccess => Decision is not null;

    public OrchestrationDecision? Decision { get; }

    public string? Error { get; }

    internal static OrchestrationDecisionDecodeResult Success(OrchestrationDecision decision) =>
        new(decision, null);

    internal static OrchestrationDecisionDecodeResult Failure(string error) =>
        new(null, error);
}

public static class AliOrchestrationDecisionDecoder
{
    private const int MaximumDecisionJsonCharacters = 262_144;
    private const int MaximumTransportEnvelopeCharacters = 1_048_576;
    private const int MaximumJsonDepth = 64;

    public static OrchestrationDecisionDecodeResult DecodeNative(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var calls = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(call => !call.InformationalOnly)
            .ToArray();
        if (calls.Length != 1)
        {
            return OrchestrationDecisionDecodeResult.Failure(
                $"Expected exactly one orchestration protocol call, but received {calls.Length}.");
        }

        var call = calls[0];
        if (!string.Equals(call.Name, OrchestrationProtocolCapability.ToolName, StringComparison.Ordinal))
        {
            return OrchestrationDecisionDecodeResult.Failure(
                "The model called a task tool directly instead of the orchestration protocol.");
        }

        try
        {
            return DecodeTransport(JsonSerializer.SerializeToElement(
                call.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal)));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return OrchestrationDecisionDecodeResult.Failure(
                $"The orchestration call arguments were not valid JSON: {exception.Message}");
        }
    }

    public static OrchestrationDecisionDecodeResult DecodeCompatibility(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return OrchestrationDecisionDecodeResult.Failure(
                "The compatibility response did not contain an orchestration decision object.");
        }

        if (text.Length > MaximumTransportEnvelopeCharacters)
        {
            return OrchestrationDecisionDecodeResult.Failure(
                "The compatibility orchestration transport exceeded its bounded size.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                });
            return DecodeTransport(document.RootElement);
        }
        catch (JsonException exception)
        {
            return OrchestrationDecisionDecodeResult.Failure(
                $"The compatibility response was not one valid JSON object: {exception.Message}");
        }
    }

    public static OrchestrationDecisionDecodeResult Decode(JsonElement root)
    {
        try
        {
            RequireObject(root, "decision");
            RequireExactProperties(root, "decision", "workUpdate", "materialClaims", "nextAction");

            var workUpdateElement = RequireProperty(root, "workUpdate", "decision");
            OrchestrationWorkUpdate? workUpdate = workUpdateElement.ValueKind == JsonValueKind.Null
                ? null
                : DecodeWorkUpdate(workUpdateElement);
            var claims = DecodeClaims(RequireProperty(root, "materialClaims", "decision"));
            var action = DecodeAction(RequireProperty(root, "nextAction", "decision"));
            return OrchestrationDecisionDecodeResult.Success(
                new OrchestrationDecision(workUpdate, claims, action));
        }
        catch (DecisionDecodeException exception)
        {
            return OrchestrationDecisionDecodeResult.Failure(exception.Message);
        }
    }

    private static OrchestrationDecisionDecodeResult DecodeTransport(JsonElement root)
    {
        try
        {
            RequireObject(root, "transport");
            if (root.GetRawText().Length > MaximumTransportEnvelopeCharacters)
            {
                return OrchestrationDecisionDecodeResult.Failure(
                    "The orchestration transport exceeded its bounded size.");
            }

            RequireExactProperties(
                root,
                "transport",
                AliOrchestrationProtocol.DecisionJsonPropertyName);
            var decisionJson = RequireProperty(
                root,
                AliOrchestrationProtocol.DecisionJsonPropertyName,
                "transport");
            if (decisionJson.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(decisionJson.GetString()))
            {
                return OrchestrationDecisionDecodeResult.Failure(
                    "transport.decisionJson must be a non-empty JSON string.");
            }

            var serializedDecision = decisionJson.GetString()!;
            if (serializedDecision.Length > MaximumDecisionJsonCharacters)
            {
                return OrchestrationDecisionDecodeResult.Failure(
                    "transport.decisionJson exceeded its bounded size.");
            }

            using var document = JsonDocument.Parse(
                serializedDecision,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                });
            return Decode(document.RootElement);
        }
        catch (DecisionDecodeException exception)
        {
            return OrchestrationDecisionDecodeResult.Failure(exception.Message);
        }
        catch (JsonException exception)
        {
            return OrchestrationDecisionDecodeResult.Failure(
                $"transport.decisionJson was not one valid JSON object: {exception.Message}");
        }
    }

    private static OrchestrationWorkUpdate DecodeWorkUpdate(JsonElement element)
    {
        RequireObject(element, "workUpdate");
        RequireExactProperties(element, "workUpdate", "baseRevision", "items");
        var revision = RequireInt64(RequireProperty(element, "baseRevision", "workUpdate"), "workUpdate.baseRevision");
        var itemsElement = RequireProperty(element, "items", "workUpdate");
        RequireArray(itemsElement, "workUpdate.items");
        var items = new List<OrchestrationWorkItemUpdate>();
        var index = 0;
        foreach (var item in itemsElement.EnumerateArray())
        {
            var path = $"workUpdate.items[{index++}]";
            RequireObject(item, path);
            RequireExactProperties(
                item,
                path,
                "workItemId",
                "outcome",
                "status",
                "parentId",
                "supersededById",
                "dependencyIds",
                "evidenceIds");
            items.Add(new OrchestrationWorkItemUpdate(
                RequireString(item, "workItemId", path),
                RequireString(item, "outcome", path),
                ParseWorkStatus(RequireString(item, "status", path), $"{path}.status"),
                ReadNullableString(RequireProperty(item, "parentId", path), $"{path}.parentId"),
                ReadNullableString(RequireProperty(item, "supersededById", path), $"{path}.supersededById"),
                ReadStringArray(RequireProperty(item, "dependencyIds", path), $"{path}.dependencyIds"),
                ReadStringArray(RequireProperty(item, "evidenceIds", path), $"{path}.evidenceIds")));
        }

        return new OrchestrationWorkUpdate(revision, items);
    }

    private static IReadOnlyList<OrchestrationMaterialClaim> DecodeClaims(JsonElement element)
    {
        RequireArray(element, "materialClaims");
        var claims = new List<OrchestrationMaterialClaim>();
        var index = 0;
        foreach (var claim in element.EnumerateArray())
        {
            var path = $"materialClaims[{index++}]";
            RequireObject(claim, path);
            RequireExactProperties(claim, path, "claimId", "statement", "kind", "evidenceIds");
            claims.Add(new OrchestrationMaterialClaim(
                RequireString(claim, "claimId", path),
                RequireString(claim, "statement", path),
                ParseClaimKind(RequireString(claim, "kind", path), $"{path}.kind"),
                ReadStringArray(RequireProperty(claim, "evidenceIds", path), $"{path}.evidenceIds")));
        }

        return claims;
    }

    private static OrchestrationNextAction DecodeAction(JsonElement element)
    {
        RequireObject(element, "nextAction");
        var kind = RequireString(element, "kind", "nextAction");
        return kind switch
        {
            "callTool" => DecodeCallTool(element),
            "expandTools" => DecodeExpandTools(element),
            "inspectEvidencePage" => DecodeInspectEvidencePage(element),
            "inspectWorkPage" => DecodeInspectWorkPage(element),
            "answerDirectly" => DecodeAnswerDirectly(element),
            "requestUserInput" => DecodeRequestUserInput(element),
            "awaitExternalEvent" => DecodeAwaitExternalEvent(element),
            "beginCompletion" => DecodeBeginCompletion(element),
            _ => throw new DecisionDecodeException($"nextAction.kind '{kind}' is not supported.")
        };
    }

    private static CallToolAction DecodeCallTool(JsonElement element)
    {
        RequireExactProperties(element, "nextAction", "kind", "toolName", "arguments", "need", "expectedProgress");
        var argumentsElement = RequireProperty(element, "arguments", "nextAction");
        RequireObject(argumentsElement, "nextAction.arguments");
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in argumentsElement.EnumerateObject())
        {
            if (!arguments.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new DecisionDecodeException(
                    $"nextAction.arguments.{property.Name} appears more than once.");
            }
        }

        return new CallToolAction(
            RequireString(element, "toolName", "nextAction"),
            arguments,
            RequireString(element, "need", "nextAction"),
            RequireString(element, "expectedProgress", "nextAction"));
    }

    private static ExpandToolsAction DecodeExpandTools(JsonElement element)
    {
        RequireExactProperties(element, "nextAction", "kind", "need");
        return new ExpandToolsAction(RequireString(element, "need", "nextAction"));
    }

    private static InspectEvidencePageAction DecodeInspectEvidencePage(JsonElement element)
    {
        RequireExactProperties(
            element,
            "nextAction",
            "kind",
            "afterCursor",
            "snapshotCursor",
            "pageSize");
        return new InspectEvidencePageAction(
            RequireInt64(RequireProperty(element, "afterCursor", "nextAction"), "nextAction.afterCursor"),
            ReadNullableInt64(
                RequireProperty(element, "snapshotCursor", "nextAction"),
                "nextAction.snapshotCursor"),
            RequireInt32(RequireProperty(element, "pageSize", "nextAction"), "nextAction.pageSize"));
    }

    private static InspectWorkPageAction DecodeInspectWorkPage(JsonElement element)
    {
        RequireExactProperties(
            element,
            "nextAction",
            "kind",
            "afterWorkItemId",
            "snapshotRevision",
            "pageSize");
        return new InspectWorkPageAction(
            ReadNullableString(
                RequireProperty(element, "afterWorkItemId", "nextAction"),
                "nextAction.afterWorkItemId"),
            ReadNullableInt64(
                RequireProperty(element, "snapshotRevision", "nextAction"),
                "nextAction.snapshotRevision"),
            RequireInt32(RequireProperty(element, "pageSize", "nextAction"), "nextAction.pageSize"));
    }

    private static AnswerDirectlyAction DecodeAnswerDirectly(JsonElement element)
    {
        RequireExactProperties(element, "nextAction", "kind", "answer");
        return new AnswerDirectlyAction(RequireString(element, "answer", "nextAction"));
    }

    private static RequestUserInputAction DecodeRequestUserInput(JsonElement element)
    {
        RequireExactProperties(element, "nextAction", "kind", "question", "missingInformation");
        return new RequestUserInputAction(
            RequireString(element, "question", "nextAction"),
            RequireString(element, "missingInformation", "nextAction"));
    }

    private static AwaitExternalEventAction DecodeAwaitExternalEvent(JsonElement element)
    {
        RequireExactProperties(element, "nextAction", "kind", "ticketId", "waitingFor");
        return new AwaitExternalEventAction(
            RequireString(element, "ticketId", "nextAction"),
            RequireString(element, "waitingFor", "nextAction"));
    }

    private static BeginCompletionAction DecodeBeginCompletion(JsonElement element)
    {
        RequireExactProperties(element, "nextAction", "kind", "plan");
        var plan = RequireProperty(element, "plan", "nextAction");
        RequireObject(plan, "nextAction.plan");
        RequireExactProperties(
            plan,
            "nextAction.plan",
            "answerId",
            "completionKind",
            "requiredOutcomeIds",
            "requiredClaimIds",
            "bindings",
            "requestedFormat",
            "requestedSections");
        var bindingsElement = RequireProperty(plan, "bindings", "nextAction.plan");
        RequireArray(bindingsElement, "nextAction.plan.bindings");
        var bindings = new List<CompletionEvidenceBinding>();
        var index = 0;
        foreach (var binding in bindingsElement.EnumerateArray())
        {
            var path = $"nextAction.plan.bindings[{index++}]";
            RequireObject(binding, path);
            RequireExactProperties(binding, path, "subjectId", "evidenceIds");
            bindings.Add(new CompletionEvidenceBinding(
                RequireString(binding, "subjectId", path),
                ReadStringArray(RequireProperty(binding, "evidenceIds", path), $"{path}.evidenceIds")));
        }

        return new BeginCompletionAction(new CompletionPlan(
            RequireString(plan, "answerId", "nextAction.plan"),
            ParseCompletionKind(
                RequireString(plan, "completionKind", "nextAction.plan"),
                "nextAction.plan.completionKind"),
            ReadStringArray(
                RequireProperty(plan, "requiredOutcomeIds", "nextAction.plan"),
                "nextAction.plan.requiredOutcomeIds"),
            ReadStringArray(
                RequireProperty(plan, "requiredClaimIds", "nextAction.plan"),
                "nextAction.plan.requiredClaimIds"),
            bindings,
            RequireString(plan, "requestedFormat", "nextAction.plan"),
            ReadStringArray(
                RequireProperty(plan, "requestedSections", "nextAction.plan"),
                "nextAction.plan.requestedSections")));
    }

    private static OrchestrationWorkStatus ParseWorkStatus(string value, string path) => value switch
    {
        "pending" => OrchestrationWorkStatus.Pending,
        "active" => OrchestrationWorkStatus.Active,
        "satisfied" => OrchestrationWorkStatus.Satisfied,
        "impossible" => OrchestrationWorkStatus.Impossible,
        "superseded" => OrchestrationWorkStatus.Superseded,
        _ => throw new DecisionDecodeException($"{path} '{value}' is not supported.")
    };

    private static MaterialClaimKind ParseClaimKind(string value, string path) => value switch
    {
        "currentFact" => MaterialClaimKind.CurrentFact,
        "performedAction" => MaterialClaimKind.PerformedAction,
        "artifact" => MaterialClaimKind.Artifact,
        "permission" => MaterialClaimKind.Permission,
        "code" => MaterialClaimKind.Code,
        "file" => MaterialClaimKind.File,
        "completion" => MaterialClaimKind.Completion,
        "other" => MaterialClaimKind.Other,
        _ => throw new DecisionDecodeException($"{path} '{value}' is not supported.")
    };

    private static CompletionKind ParseCompletionKind(string value, string path) => value switch
    {
        "succeeded" => CompletionKind.Succeeded,
        "provenImpossible" => CompletionKind.ProvenImpossible,
        _ => throw new DecisionDecodeException($"{path} '{value}' is not supported.")
    };

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string path)
    {
        RequireArray(element, path);
        var values = new List<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new DecisionDecodeException($"{path}[{index}] must be a string.");
            }

            values.Add(item.GetString() ?? string.Empty);
            index++;
        }

        return values;
    }

    private static string? ReadNullableString(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new DecisionDecodeException($"{path} must be a string or null.");
        }

        return element.GetString();
    }

    private static long? ReadNullableInt64(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Null
            ? null
            : RequireInt64(element, path);

    private static string RequireString(JsonElement parent, string propertyName, string parentPath)
    {
        var element = RequireProperty(parent, propertyName, parentPath);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new DecisionDecodeException($"{parentPath}.{propertyName} must be a string.");
        }

        return element.GetString() ?? string.Empty;
    }

    private static long RequireInt64(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            throw new DecisionDecodeException($"{path} must be a 64-bit integer.");
        }

        return value;
    }

    private static int RequireInt32(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new DecisionDecodeException($"{path} must be a 32-bit integer.");
        }

        return value;
    }

    private static JsonElement RequireProperty(JsonElement parent, string propertyName, string parentPath)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            throw new DecisionDecodeException($"{parentPath}.{propertyName} is required.");
        }

        return value;
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DecisionDecodeException($"{path} must be an object.");
        }
    }

    private static void RequireArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new DecisionDecodeException($"{path} must be an array.");
        }
    }

    private static void RequireExactProperties(JsonElement element, string path, params string[] propertyNames)
    {
        var expected = new HashSet<string>(propertyNames, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Remove(property.Name))
            {
                throw new DecisionDecodeException($"{path}.{property.Name} is not an allowed property.");
            }
        }

        if (expected.Count > 0)
        {
            throw new DecisionDecodeException(
                $"{path} is missing required properties: {string.Join(", ", expected.OrderBy(name => name, StringComparer.Ordinal))}.");
        }
    }

    private sealed class DecisionDecodeException(string message) : Exception(message);
}
