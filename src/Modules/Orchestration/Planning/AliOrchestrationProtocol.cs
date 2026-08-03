using System.Text.Json;
using System.Text.Json.Nodes;
using Ali.Modules.Capabilities;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Orchestration.Planning;

public static class AliOrchestrationProtocol
{
    public static AIFunctionDeclaration CreateDeclaration(
        IEnumerable<AIFunctionDeclaration>? selectedTaskTools)
    {
        var tools = SnapshotTools(selectedTaskTools);
        var invariant = OrchestrationProtocolCapability.CreateInvariantFunction();
        return AIFunctionFactory.CreateDeclaration(
            invariant.Name,
            invariant.Description,
            BuildDecisionSchema(tools));
    }

    public static JsonElement BuildDecisionSchema(
        IEnumerable<AIFunctionDeclaration>? selectedTaskTools)
    {
        var tools = SnapshotTools(selectedTaskTools);
        var toolSchemas = tools
            .Select((tool, index) => new
            {
                Tool = tool,
                SchemaKey = $"taskTool{index}",
                Schema = RebaseToolSchema(tool.JsonSchema, $"taskTool{index}")
            })
            .ToArray();
        var actionBranches = new List<object>();
        actionBranches.AddRange(toolSchemas.Select(item =>
            BuildCallToolActionSchema(item.Tool, item.SchemaKey)));
        actionBranches.Add(ObjectSchema(
            ["kind", "need"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("expandTools"),
                ["need"] = BoundedString(1, 2_000)
            }));
        actionBranches.Add(ObjectSchema(
            ["kind", "afterCursor", "snapshotCursor", "pageSize"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("inspectEvidencePage"),
                ["afterCursor"] = Describe(
                    NonnegativeIntegerSchema(),
                    "Use 0 for the first page; for continuation use the prior page's nextCursor."),
                ["snapshotCursor"] = Describe(
                    NullableNonnegativeIntegerSchema(),
                    "Use null only for the first page; every continuation must reuse the prior page's snapshotCursor."),
                ["pageSize"] = PageSizeSchema()
            }));
        actionBranches.Add(ObjectSchema(
            ["kind", "afterWorkItemId", "snapshotRevision", "pageSize"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("inspectWorkPage"),
                ["afterWorkItemId"] = Describe(
                    NullableIdentifierSchema(),
                    "Use null for the first page; for continuation reuse the prior page's nextWorkItemId."),
                ["snapshotRevision"] = Describe(
                    NullableNonnegativeIntegerSchema(),
                    "Use null only for the first page; every continuation must reuse the prior page's snapshotRevision."),
                ["pageSize"] = PageSizeSchema()
            }));
        actionBranches.Add(ObjectSchema(
            ["kind", "answer"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("answerDirectly"),
                ["answer"] = BoundedString(1, 32_000)
            }));
        actionBranches.Add(ObjectSchema(
            ["kind", "question", "missingInformation"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("requestUserInput"),
                ["question"] = BoundedString(1, 2_000),
                ["missingInformation"] = BoundedString(1, 2_000)
            }));
        actionBranches.Add(ObjectSchema(
            ["kind", "ticketId", "waitingFor"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("awaitExternalEvent"),
                ["ticketId"] = IdentifierSchema(),
                ["waitingFor"] = BoundedString(1, 2_000)
            }));
        actionBranches.Add(ObjectSchema(
            ["kind", "plan"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("beginCompletion"),
                ["plan"] = BuildCompletionPlanSchema()
            }));

        var rootSchema = ObjectSchema(
            ["workUpdate", "materialClaims", "nextAction"],
            new Dictionary<string, object?>
            {
                ["workUpdate"] = new Dictionary<string, object?>
                {
                    ["oneOf"] = new object[]
                    {
                        new Dictionary<string, object?> { ["type"] = "null" },
                        BuildWorkUpdateSchema()
                    }
                },
                ["materialClaims"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = BuildClaimSchema(),
                    ["maxItems"] = 128
                },
                ["nextAction"] = new Dictionary<string, object?>
                {
                    ["oneOf"] = actionBranches
                }
            });
        if (toolSchemas.Length > 0)
        {
            rootSchema["$defs"] = toolSchemas.ToDictionary(
                item => item.SchemaKey,
                item => (object?)item.Schema,
                StringComparer.Ordinal);
        }

        return JsonSerializer.SerializeToElement(rootSchema);
    }

    private static IReadOnlyList<AIFunctionDeclaration> SnapshotTools(
        IEnumerable<AIFunctionDeclaration>? selectedTaskTools) =>
        (selectedTaskTools ?? [])
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .Where(tool => !string.Equals(
                tool.Name,
                OrchestrationProtocolCapability.ToolName,
                StringComparison.Ordinal))
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

    private static object BuildCallToolActionSchema(
        AIFunctionDeclaration tool,
        string schemaKey) =>
        ObjectSchema(
            ["kind", "toolName", "arguments", "need", "expectedProgress"],
            new Dictionary<string, object?>
            {
                ["kind"] = ConstString("callTool"),
                ["toolName"] = ConstString(tool.Name),
                ["arguments"] = new Dictionary<string, object?>
                {
                    ["$ref"] = $"#/$defs/{schemaKey}"
                },
                ["need"] = BoundedString(1, 1_000),
                ["expectedProgress"] = BoundedString(1, 1_000)
            });

    private static JsonElement RebaseToolSchema(JsonElement schema, string schemaKey)
    {
        var node = JsonNode.Parse(schema.GetRawText())
            ?? throw new InvalidOperationException("A task tool supplied an empty JSON schema.");
        RebaseReferences(node, schemaKey);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RebaseReferences(JsonNode? node, string schemaKey)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Key == "$ref"
                    && property.Value is JsonValue value
                    && value.TryGetValue<string>(out var reference)
                    && reference.StartsWith('#'))
                {
                    jsonObject[property.Key] = reference.Length == 1
                        ? $"#/$defs/{schemaKey}"
                        : $"#/$defs/{schemaKey}{reference[1..]}";
                }
                else
                {
                    RebaseReferences(property.Value, schemaKey);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RebaseReferences(item, schemaKey);
            }
        }
    }

    private static object BuildWorkUpdateSchema() =>
        ObjectSchema(
            ["baseRevision", "items"],
            new Dictionary<string, object?>
            {
                ["baseRevision"] = new Dictionary<string, object?>
                {
                    ["type"] = "integer",
                    ["minimum"] = 0
                },
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["maxItems"] = 128,
                    ["items"] = ObjectSchema(
                        ["workItemId", "outcome", "status", "parentId", "supersededById", "dependencyIds", "evidenceIds"],
                        new Dictionary<string, object?>
                        {
                            ["workItemId"] = IdentifierSchema(),
                            ["outcome"] = BoundedString(1, 2_000),
                            ["status"] = EnumString("pending", "active", "satisfied", "impossible", "superseded"),
                            ["parentId"] = NullableIdentifierSchema(),
                            ["supersededById"] = NullableIdentifierSchema(),
                            ["dependencyIds"] = IdentifierArray(),
                            ["evidenceIds"] = IdentifierArray()
                        })
                }
            });

    private static object BuildClaimSchema() =>
        ObjectSchema(
            ["claimId", "statement", "kind", "evidenceIds"],
            new Dictionary<string, object?>
            {
                ["claimId"] = IdentifierSchema(),
                ["statement"] = BoundedString(1, 2_000),
                ["kind"] = EnumString(
                    "currentFact",
                    "performedAction",
                    "artifact",
                    "permission",
                    "code",
                    "file",
                    "completion",
                    "other"),
                ["evidenceIds"] = IdentifierArray()
            });

    private static object BuildCompletionPlanSchema() =>
        ObjectSchema(
            [
                "answerId",
                "completionKind",
                "requiredOutcomeIds",
                "requiredClaimIds",
                "bindings",
                "requestedFormat",
                "requestedSections"
            ],
            new Dictionary<string, object?>
            {
                ["answerId"] = IdentifierSchema(),
                ["completionKind"] = EnumString("succeeded", "provenImpossible"),
                ["requiredOutcomeIds"] = IdentifierArray(),
                ["requiredClaimIds"] = IdentifierArray(),
                ["bindings"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["maxItems"] = 256,
                    ["items"] = ObjectSchema(
                        ["subjectId", "evidenceIds"],
                        new Dictionary<string, object?>
                        {
                            ["subjectId"] = IdentifierSchema(),
                            ["evidenceIds"] = IdentifierArray()
                        })
                },
                ["requestedFormat"] = BoundedString(0, 1_000),
                ["requestedSections"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["maxItems"] = 64,
                    ["items"] = BoundedString(1, 256)
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

    private static Dictionary<string, object?> IdentifierSchema() => BoundedString(1, 256);

    private static Dictionary<string, object?> Describe(
        Dictionary<string, object?> schema,
        string description)
    {
        schema["description"] = description;
        return schema;
    }

    private static Dictionary<string, object?> NonnegativeIntegerSchema() =>
        new()
        {
            ["type"] = "integer",
            ["minimum"] = 0
        };

    private static Dictionary<string, object?> NullableNonnegativeIntegerSchema() =>
        new()
        {
            ["oneOf"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "null" },
                NonnegativeIntegerSchema()
            }
        };

    private static Dictionary<string, object?> PageSizeSchema() =>
        new()
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["maximum"] = 32
        };

    private static Dictionary<string, object?> NullableIdentifierSchema() =>
        new()
        {
            ["oneOf"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "null" },
                IdentifierSchema()
            }
        };

    private static Dictionary<string, object?> IdentifierArray() =>
        new()
        {
            ["type"] = "array",
            ["maxItems"] = 256,
            ["uniqueItems"] = true,
            ["items"] = IdentifierSchema()
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

    private static Dictionary<string, object?> EnumString(params string[] values) =>
        new()
        {
            ["type"] = "string",
            ["enum"] = values
        };

}
