using System.Collections.Immutable;
using System.Text.Json;
using Ali.Modules.Capabilities;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.Orchestration.Work;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class PlanningContractTests
{
    [Fact]
    public void NativeAndCompatibilityDecoders_ProduceTheSameTypedEnvelope()
    {
        var decisionJson = DecisionJson(CallToolJson("read_file", "path", "README.md"));
        var transportJson = TransportJson(decisionJson);
        using var document = JsonDocument.Parse(transportJson);
        var arguments = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        var nativeMessage = new ChatMessage(ChatRole.Assistant, string.Empty);
        nativeMessage.Contents.Add(new FunctionCallContent(
            "draft-call",
            "submit_orchestration_decision",
            arguments));

        var native = AliOrchestrationDecisionDecoder.DecodeNative(new ChatResponse(nativeMessage));
        var compatibility = AliOrchestrationDecisionDecoder.DecodeCompatibility(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, transportJson)));

        Assert.True(native.IsSuccess, native.Error);
        Assert.True(compatibility.IsSuccess, compatibility.Error);
        Assert.Equal(
            JsonSerializer.Serialize(native.Decision),
            JsonSerializer.Serialize(compatibility.Decision));
        var action = Assert.IsType<CallToolAction>(native.Decision!.NextAction);
        Assert.Equal("Inspect the requested file", action.Need);
        Assert.Equal("The file contents will be available as evidence", action.ExpectedProgress);
    }

    [Fact]
    public void ProviderTransportSchema_IsCompactAndIndependentOfSelectedTaskSchemas()
    {
        var tool = AIFunctionFactory.Create(
            (string path) => path,
            "read_file",
            "Read one file.");

        var withoutTool = AliOrchestrationProtocol.CreateDeclaration([]).JsonSchema.GetRawText();
        var withTool = AliOrchestrationProtocol.CreateDeclaration([tool]).JsonSchema.GetRawText();

        Assert.Equal(withoutTool, withTool);
        Assert.Contains("\"decisionJson\"", withTool, StringComparison.Ordinal);
        Assert.DoesNotContain("read_file", withTool, StringComparison.Ordinal);
        Assert.DoesNotContain("\"oneOf\"", withTool, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$defs\"", withTool, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$ref\"", withTool, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicExpandToolsContract_ExposesOnlyExactExpandableGroupIds()
    {
        var expandableGroupIds = new[] { "files", "current-information" };
        var decisionSchema = AliOrchestrationProtocol.BuildDecisionSchema(
            [],
            expandableGroupIds);
        var expandBranch = decisionSchema.GetProperty("properties")
            .GetProperty("nextAction")
            .GetProperty("oneOf")
            .EnumerateArray()
            .Single(branch => branch.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("const")
                .GetString() == "expandTools");
        var allowed = expandBranch.GetProperty("properties")
            .GetProperty("need")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        var transport = AliOrchestrationProtocol.CreateDeclaration(
            [],
            expandableGroupIds).JsonSchema.GetRawText();

        Assert.Equal(["current-information", "files"], allowed);
        Assert.Contains("current-information", transport, StringComparison.Ordinal);
        Assert.Contains("files", transport, StringComparison.Ordinal);
        Assert.Contains("Never substitute a group name or prose description", transport, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicExpandToolsContract_OmitsExpansionWhenNoDrawerRemainsExpandable()
    {
        var decisionSchema = AliOrchestrationProtocol.BuildDecisionSchema([], []);
        var actionKinds = decisionSchema.GetProperty("properties")
            .GetProperty("nextAction")
            .GetProperty("oneOf")
            .EnumerateArray()
            .Select(branch => branch.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("const")
                .GetString())
            .ToArray();
        var transport = AliOrchestrationProtocol.CreateDeclaration(
            [],
            []).JsonSchema.GetRawText();

        Assert.DoesNotContain("expandTools", actionKinds);
        Assert.Contains(
            "No capability drawer is currently expandable; do not use expandTools",
            transport,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerPrompt_PrioritizesOnePassDirectConversationWithoutOpeningDiscovery()
    {
        var messages = new AliStateBackedChatHistoryAdapter().BuildMessages(
            "hello",
            new AliPlanningTurnInput(0, "{}"),
            "capability directory",
            [],
            AliPlanningAttachmentProjection.Empty,
            expandableToolGroupIds: ["capability-discovery"]);
        var systemPrompt = messages[0].Text;

        Assert.Contains("Use AnswerDirectly immediately for greetings", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("Never open capability discovery", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("answer directly now", systemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderTransportDecoders_RejectRawV1Decisions()
    {
        var rawDecision = DecisionJson(
            "{\"kind\":\"answerDirectly\",\"answer\":\"must not pass\"}");
        using var document = JsonDocument.Parse(rawDecision);
        var rawArguments = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        var nativeMessage = new ChatMessage(ChatRole.Assistant, string.Empty);
        nativeMessage.Contents.Add(new FunctionCallContent(
            "raw-v1",
            OrchestrationProtocolCapability.ToolName,
            rawArguments));

        var native = AliOrchestrationDecisionDecoder.DecodeNative(new ChatResponse(nativeMessage));
        var compatibility = AliOrchestrationDecisionDecoder.DecodeCompatibility(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, rawDecision)));

        Assert.False(native.IsSuccess);
        Assert.False(compatibility.IsSuccess);
        Assert.Contains("not an allowed property", native.Error, StringComparison.Ordinal);
        Assert.Contains("not an allowed property", compatibility.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"decisionJson\":null}")]
    [InlineData("{\"decisionJson\":\"\"}")]
    [InlineData("{\"decisionJson\":\"{}\",\"extra\":true}")]
    [InlineData("{\"decisionJson\":\"{}\",\"decisionJson\":\"{}\"}")]
    [InlineData("{\"decisionJson\":\"```json\\n{}\\n```\"}")]
    [InlineData("{\"decisionJson\":\"\\\"{}\\\"\"}")]
    public void CompatibilityTransport_RejectsMalformedOrAmbiguousEnvelopes(string transport)
    {
        var result = AliOrchestrationDecisionDecoder.DecodeCompatibility(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, transport)));

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void RegisteredToolSchemaValidator_EnforcesValueConstraintsLocally()
    {
        using var schemaDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["name", "count", "tags"],
              "properties": {
                "name": {
                  "type": "string",
                  "minLength": 3,
                  "maxLength": 5,
                  "pattern": "^[A-Z]+$"
                },
                "count": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 3
                },
                "tags": {
                  "type": "array",
                  "minItems": 2,
                  "maxItems": 3,
                  "uniqueItems": true,
                  "items": { "type": "string" }
                }
              }
            }
            """);
        using var valueDocument = JsonDocument.Parse(
            """{"name":"a","count":4,"tags":["x","x"]}""");
        var errors = new List<string>();

        CapabilityJsonSchemaValidator.Validate(
            valueDocument.RootElement,
            schemaDocument.RootElement,
            "CallTool.arguments",
            errors);

        Assert.Contains(errors, error => error.Contains("at least 3 characters", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("string pattern", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("numeric bound 'maximum'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unique items", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisteredToolSchemaValidator_RejectsUnsupportedKeywordsAndTypesFailClosed()
    {
        using var schemaDocument = JsonDocument.Parse(
            """{"type":"mystery","not":{"type":"string"}}""");
        using var valueDocument = JsonDocument.Parse("{}");
        var errors = new List<string>();

        CapabilityJsonSchemaValidator.Validate(
            valueDocument.RootElement,
            schemaDocument.RootElement,
            "CallTool.arguments",
            errors);

        Assert.Contains(errors, error => error.Contains("unsupported registered-schema keyword 'not'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unsupported JSON type", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisteredToolSchemaValidator_RejectsReferencesIntoNonSchemaAnnotations()
    {
        using var schemaDocument = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "value": { "$ref": "#/$defs/Holder/default" }
              },
              "$defs": {
                "Holder": {
                  "type": "string",
                  "default": { "pattern": "[" }
                }
              }
            }
            """);

        Assert.False(CapabilityJsonSchemaValidator.TryValidateSchemaDefinition(
            schemaDocument.RootElement,
            out var reason));
        Assert.Contains("unresolved or non-local", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredToolSchemaValidator_BoundsReferenceGraphExpansion()
    {
        var definitions = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < 20; index++)
        {
            definitions[$"Node{index}"] = new Dictionary<string, object?>
            {
                ["allOf"] = new object[]
                {
                    new Dictionary<string, object?> { ["$ref"] = $"#/$defs/Node{index + 1}" },
                    new Dictionary<string, object?> { ["$ref"] = $"#/$defs/Node{index + 1}" }
                }
            };
        }
        definitions["Node20"] = new Dictionary<string, object?> { ["type"] = "string" };
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["value"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/Node0" }
            },
            ["$defs"] = definitions
        });
        var value = JsonSerializer.SerializeToElement(new { value = "bounded" });
        var errors = new List<string>();

        CapabilityJsonSchemaValidator.Validate(value, schema, "CallTool.arguments", errors);

        Assert.Contains(
            errors,
            error => error.Contains("validation operation count", StringComparison.Ordinal));
    }

    [Fact]
    public void DecisionValidator_EnforcesFullDecisionCardinalityAfterCompactTransport()
    {
        var claims = Enumerable.Range(0, 129)
            .Select(index => new OrchestrationMaterialClaim(
                $"claim-{index}",
                "Bounded claim",
                MaterialClaimKind.CurrentFact,
                []))
            .ToArray();
        var decision = new OrchestrationDecision(
            workUpdate: null,
            claims,
            new AnswerDirectlyAction("The cardinality gate must run first."));

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            new OrchestrationValidationContext(0, []));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "materialClaims exceeds the bounded maximum of 128 items",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicSchema_UsesFinalActionAndWorkGraphFieldNames()
    {
        var tool = AIFunctionFactory.Create(
            (string path) => path,
            "read_file",
            "Read one file.");

        var schema = AliOrchestrationProtocol.BuildDecisionSchema([tool]).GetRawText();

        Assert.Contains("\"toolName\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"need\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"expectedProgress\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"missingInformation\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"waitingFor\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"parentId\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"supersededById\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"assessment\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"statusMessage\"", schema, StringComparison.Ordinal);
        Assert.Contains(tool.JsonSchema.GetRawText(), schema, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicSchema_RebasesTaskToolLocalReferencesAtTheProtocolRoot()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["payload"],
              "properties": { "payload": { "$ref": "#/$defs/Payload" } },
              "$defs": {
                "Payload": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["value"],
                  "properties": { "value": { "type": "string" } }
                }
              }
            }
            """);
        var tool = AIFunctionFactory.CreateDeclaration(
            "nested_tool",
            "Uses a nested schema.",
            document.RootElement);

        var schema = AliOrchestrationProtocol.BuildDecisionSchema([tool]);
        var storedTool = schema.GetProperty("$defs").GetProperty("taskTool0");

        Assert.Equal(
            "#/$defs/taskTool0/$defs/Payload",
            storedTool.GetProperty("properties").GetProperty("payload").GetProperty("$ref").GetString());
        var callBranch = schema.GetProperty("properties")
            .GetProperty("nextAction")
            .GetProperty("oneOf")[0];
        Assert.Equal(
            "#/$defs/taskTool0",
            callBranch.GetProperty("properties").GetProperty("arguments").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Validator_RejectsFailedEvidenceAsTerminalWorkProof()
    {
        var evidence = new AcceptedEvidenceProjection(
            "evidence-1",
            "call-1",
            "build",
            PlanningToolInvocationStatus.Returned,
            PlanningToolDomainOutcome.Failed,
            "{\"success\":false}",
            "work-1");
        var decision = new OrchestrationDecision(
            new OrchestrationWorkUpdate(
                4,
                [
                    new OrchestrationWorkItemUpdate(
                        "work-1",
                        "Build succeeds",
                        OrchestrationWorkStatus.Satisfied,
                        evidenceIds: [evidence.EvidenceId])
                ]),
            [],
            new RequestUserInputAction("What should I do next?", "A next objective is required."));
        var context = new OrchestrationValidationContext(
            4,
            [],
            [evidence.EvidenceId],
            evidenceOutcomes: new Dictionary<string, PlanningToolDomainOutcome>
            {
                [evidence.EvidenceId] = evidence.DomainOutcome
            },
            evidenceProjections: new Dictionary<string, AcceptedEvidenceProjection>
            {
                [evidence.EvidenceId] = evidence
            });

        var result = new OrchestrationDecisionValidator().Validate(decision, context);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Terminal work item 'work-1'", StringComparison.Ordinal)
                     && error.Contains("succeeded evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_SucceededEvidenceCanTerminalizeOnlyItsExactWorkItem()
    {
        var evidence = Evidence(
            "proof-a",
            PlanningToolDomainOutcome.Succeeded,
            "completed",
            "work-a");
        var context = Context(Graph(), [evidence]);

        OrchestrationDecision Decision(string workItemId) => new(
            new OrchestrationWorkUpdate(
                context.WorkGraphRevision,
                [
                    new OrchestrationWorkItemUpdate(
                        workItemId,
                        "Completed work.",
                        OrchestrationWorkStatus.Satisfied,
                        evidenceIds: [evidence.EvidenceId])
                ]),
            [],
            new RequestUserInputAction("What next?", "Another objective is required."));

        var mismatched = new OrchestrationDecisionValidator().Validate(
            Decision("work-b"),
            context);
        var matched = new OrchestrationDecisionValidator().Validate(
            Decision("work-a"),
            context);

        Assert.False(mismatched.IsValid);
        Assert.Contains(
            mismatched.Errors,
            error => error.Contains("work-b", StringComparison.Ordinal)
                     && error.Contains("exact work-item ID", StringComparison.Ordinal));
        Assert.True(matched.IsValid, string.Join(Environment.NewLine, matched.Errors));
    }

    [Theory]
    [InlineData(OrchestrationWorkStatus.Pending)]
    [InlineData(OrchestrationWorkStatus.Active)]
    public void Validator_NonterminalWorkCannotCiteEvidenceFromAnotherWorkItem(
        OrchestrationWorkStatus status)
    {
        var evidence = Evidence(
            "proof-a",
            PlanningToolDomainOutcome.Succeeded,
            "completed A",
            "work-a");
        var context = Context(Graph(), [evidence]);
        var decision = new OrchestrationDecision(
            new OrchestrationWorkUpdate(
                context.WorkGraphRevision,
                [
                    new OrchestrationWorkItemUpdate(
                        "work-b",
                        "Work B must not inherit work A evidence.",
                        status,
                        evidenceIds: [evidence.EvidenceId])
                ]),
            [],
            new RequestUserInputAction(
                "What should Ali do next?",
                "The next action is not known."));

        var result = new OrchestrationDecisionValidator().Validate(decision, context);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("work-b", StringComparison.Ordinal)
                     && error.Contains("ordinal comparison", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsMixedWorkEvidenceEvenWhenOneSucceededReceiptMatches()
    {
        var matching = Evidence(
            "proof-a",
            PlanningToolDomainOutcome.Succeeded,
            "completed A",
            "work-a");
        var foreign = Evidence(
            "proof-b",
            PlanningToolDomainOutcome.Succeeded,
            "completed B",
            "work-b");
        var evidenceIds = new[] { matching.EvidenceId, foreign.EvidenceId };
        var context = Context(Graph(), [matching, foreign]);
        var terminalUpdate = new OrchestrationDecision(
            new OrchestrationWorkUpdate(
                context.WorkGraphRevision,
                [
                    new OrchestrationWorkItemUpdate(
                        "work-a",
                        "Completed work A.",
                        OrchestrationWorkStatus.Satisfied,
                        evidenceIds: evidenceIds)
                ]),
            [],
            new RequestUserInputAction("What next?", "Another objective is required."));
        var terminalGraph = Graph(new WorkNode(
            "work-a",
            "Completed work A.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.CreateRange(evidenceIds)));
        var completion = CompletionDecision(
            requiredOutcomeIds: ["work-a"],
            bindings: [new CompletionEvidenceBinding("work-a", evidenceIds)]);

        var updateResult = new OrchestrationDecisionValidator().Validate(
            terminalUpdate,
            context);
        var completionResult = new OrchestrationDecisionValidator().Validate(
            completion,
            Context(terminalGraph, [matching, foreign]));

        Assert.False(updateResult.IsValid);
        Assert.Contains(
            updateResult.Errors,
            error => error.Contains("work-a", StringComparison.Ordinal)
                     && error.Contains("only evidence bound", StringComparison.Ordinal));
        Assert.False(completionResult.IsValid);
        Assert.Contains(
            completionResult.Errors,
            error => error.Contains("work-a", StringComparison.Ordinal)
                     && error.Contains("exact", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsSupersededWorkWithoutEvidence()
    {
        var decision = new OrchestrationDecision(
            new OrchestrationWorkUpdate(
                2,
                [
                    new OrchestrationWorkItemUpdate(
                        "old",
                        "Old approach",
                        OrchestrationWorkStatus.Superseded,
                        supersededById: "new"),
                    new OrchestrationWorkItemUpdate(
                        "new",
                        "Replacement approach",
                        OrchestrationWorkStatus.Pending)
                ]),
            [],
            new RequestUserInputAction("Confirm the replacement?", "User confirmation is required."));

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            new OrchestrationValidationContext(2, []));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Terminal work item 'old'", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceProjection_IsBoundedAndRedactsStructuredSecrets()
    {
        const string canary = "DO_NOT_PERSIST_THIS_SECRET";
        var evidence = new AcceptedEvidenceProjection(
            "evidence-1",
            "call-1",
            "inspect",
            PlanningToolInvocationStatus.Returned,
            PlanningToolDomainOutcome.Unreported,
            $"{{\"token\":\"{canary}\",\"payload\":\"{new string('x', 4_000)}\"}}");

        Assert.DoesNotContain(canary, evidence.Projection, StringComparison.Ordinal);
        Assert.True(evidence.Projection.Length <= 1_810);
    }

    [Fact]
    public void EvidenceProjection_RejectsNoncanonicalAndOverlongWorkItemIdentities()
    {
        foreach (var workItemId in new[] { " noncanonical ", new string('w', 257) })
        {
            Assert.Throws<ArgumentException>(() => new AcceptedEvidenceProjection(
                "evidence-1",
                "call-1",
                "inspect",
                PlanningToolInvocationStatus.Returned,
                PlanningToolDomainOutcome.Succeeded,
                "accepted projection",
                workItemId));
        }
    }

    [Fact]
    public void Validator_AllowsMoreThanSchemaArrayLimitWhenGraphIsReadyAndAnswerSubjectsAreBounded()
    {
        var evidence = Enumerable.Range(0, 300)
            .Select(index => Evidence(
                $"proof-{index:D3}",
                PlanningToolDomainOutcome.Succeeded,
                "ready",
                $"outcome-{index:D3}"))
            .ToArray();
        var nodes = ImmutableDictionary.CreateBuilder<string, WorkNode>(StringComparer.Ordinal);
        for (var index = 0; index < 300; index++)
        {
            var id = $"outcome-{index:D3}";
            nodes.Add(
                id,
                new WorkNode(
                    id,
                    "Completed authoritative outcome.",
                    ParentId: null,
                    WorkNodeStatus.Satisfied,
                    ImmutableArray<string>.Empty,
                    ImmutableArray.Create(evidence[index].EvidenceId)));
        }

        var graph = new WorkGraphSnapshot(9, nodes.ToImmutable());
        var decision = CompletionDecision(
            requiredOutcomeIds: ["outcome-000"],
            bindings: [new CompletionEvidenceBinding("outcome-000", [evidence[0].EvidenceId])]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, evidence));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Single(((BeginCompletionAction)decision.NextAction).Plan.RequiredOutcomeIds);
    }

    [Fact]
    public void Validator_ProvenImpossibleRequiresTerminalGraphWithAnImpossibleOutcome()
    {
        var impossibleEvidence = Evidence(
            "impossible-proof",
            PlanningToolDomainOutcome.Succeeded,
            "proof",
            "impossible");
        var satisfiedEvidence = Evidence(
            "satisfied-proof",
            PlanningToolDomainOutcome.Succeeded,
            "proof",
            "satisfied");
        var validGraph = Graph(
            new WorkNode(
                "impossible",
                "Proven impossible outcome.",
                ParentId: null,
                WorkNodeStatus.Impossible,
                ImmutableArray<string>.Empty,
                ImmutableArray.Create(impossibleEvidence.EvidenceId)),
            new WorkNode(
                "satisfied",
                "Completed supporting outcome.",
                ParentId: null,
                WorkNodeStatus.Satisfied,
                ImmutableArray<string>.Empty,
                ImmutableArray.Create(satisfiedEvidence.EvidenceId)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["impossible"],
            bindings: [new CompletionEvidenceBinding("impossible", [impossibleEvidence.EvidenceId])],
            completionKind: CompletionKind.ProvenImpossible);

        var valid = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(validGraph, [impossibleEvidence, satisfiedEvidence]));
        var noImpossible = new OrchestrationDecisionValidator().Validate(
            CompletionDecision(
                requiredOutcomeIds: ["satisfied"],
                bindings:
                [
                    new CompletionEvidenceBinding(
                        "satisfied",
                        [satisfiedEvidence.EvidenceId])
                ],
                completionKind: CompletionKind.ProvenImpossible),
            Context(
                Graph(new WorkNode(
                    "satisfied",
                    "Only satisfied outcome.",
                    ParentId: null,
                    WorkNodeStatus.Satisfied,
                    ImmutableArray<string>.Empty,
                    ImmutableArray.Create(satisfiedEvidence.EvidenceId))),
                [satisfiedEvidence]));

        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors));
        Assert.False(noImpossible.IsValid);
        Assert.Contains(
            noImpossible.Errors,
            error => error.Contains("at least one authoritative Impossible outcome", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PlanningToolDomainOutcome.Failed)]
    [InlineData(PlanningToolDomainOutcome.Denied)]
    [InlineData(PlanningToolDomainOutcome.Unreported)]
    public void Validator_RejectsUnselectedTerminalNodeWithoutSucceededExactEvidence(
        PlanningToolDomainOutcome hiddenOutcome)
    {
        var selectedEvidence = Evidence(
            "selected-proof",
            PlanningToolDomainOutcome.Succeeded,
            "selected",
            "selected-outcome");
        var hiddenEvidence = Evidence(
            "hidden-proof",
            hiddenOutcome,
            "hidden",
            "hidden-outcome");
        var graph = Graph(
            new WorkNode(
                "selected-outcome",
                "Selected answer subject.",
                ParentId: null,
                WorkNodeStatus.Satisfied,
                ImmutableArray<string>.Empty,
                ImmutableArray.Create(selectedEvidence.EvidenceId)),
            new WorkNode(
                "hidden-outcome",
                "Unselected terminal subject.",
                ParentId: null,
                WorkNodeStatus.Satisfied,
                ImmutableArray<string>.Empty,
                ImmutableArray.Create(hiddenEvidence.EvidenceId)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["selected-outcome"],
            bindings:
            [
                new CompletionEvidenceBinding(
                    "selected-outcome",
                    [selectedEvidence.EvidenceId])
            ]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, [selectedEvidence, hiddenEvidence]));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("hidden-outcome", StringComparison.Ordinal)
                     && error.Contains("exact succeeded evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_CompletionEvidenceBindingCannotCrossWorkItems()
    {
        var evidence = Evidence(
            "proof-a",
            PlanningToolDomainOutcome.Succeeded,
            "completed",
            "work-a");
        var graph = Graph(new WorkNode(
            "work-b",
            "Completed work B.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.Create(evidence.EvidenceId)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["work-b"],
            bindings: [new CompletionEvidenceBinding("work-b", [evidence.EvidenceId])]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, [evidence]));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("work-b", StringComparison.Ordinal)
                     && error.Contains("exact work-item ID", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_UnboundSucceededEvidenceCanSupportClaimButNotWork()
    {
        var workEvidence = Evidence(
            "work-proof",
            PlanningToolDomainOutcome.Succeeded,
            "work completed",
            "outcome");
        var claimEvidence = Evidence(
            "claim-proof",
            PlanningToolDomainOutcome.Succeeded,
            "claim confirmed");
        var graph = Graph(new WorkNode(
            "outcome",
            "Completed outcome.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.Create(workEvidence.EvidenceId)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["outcome"],
            requiredClaimIds: ["claim"],
            bindings:
            [
                new CompletionEvidenceBinding("outcome", [workEvidence.EvidenceId]),
                new CompletionEvidenceBinding("claim", [claimEvidence.EvidenceId])
            ],
            claims:
            [
                new OrchestrationMaterialClaim(
                    "claim",
                    "A material fact was confirmed.",
                    MaterialClaimKind.Completion,
                    [claimEvidence.EvidenceId])
            ]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, [workEvidence, claimEvidence]));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Validator_RejectsMaterialClaimOmittedFromCompletionSubjects()
    {
        var evidence = Evidence(
            "claim-proof",
            PlanningToolDomainOutcome.Succeeded,
            "claim",
            "outcome");
        var graph = Graph(new WorkNode(
            "outcome",
            "Completed outcome.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.Create(evidence.EvidenceId)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["outcome"],
            bindings: [new CompletionEvidenceBinding("outcome", [evidence.EvidenceId])],
            claims:
            [
                new OrchestrationMaterialClaim(
                    "unrequired-claim",
                    "This claim must not reach the composer.",
                    MaterialClaimKind.Completion,
                    [evidence.EvidenceId])
            ]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, [evidence]));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("omits material claim 'unrequired-claim'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsRequiredMaterialClaimWithoutSucceededExactEvidence()
    {
        var outcomeEvidence = Evidence(
            "outcome-proof",
            PlanningToolDomainOutcome.Succeeded,
            "outcome",
            "outcome");
        var claimEvidence = Evidence(
            "denied-claim-proof",
            PlanningToolDomainOutcome.Denied,
            "claim denied");
        var graph = Graph(new WorkNode(
            "outcome",
            "Completed outcome.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.Create(outcomeEvidence.EvidenceId)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["outcome"],
            requiredClaimIds: ["claim"],
            bindings:
            [
                new CompletionEvidenceBinding("outcome", [outcomeEvidence.EvidenceId]),
                new CompletionEvidenceBinding("claim", [claimEvidence.EvidenceId])
            ],
            claims:
            [
                new OrchestrationMaterialClaim(
                    "claim",
                    "This claim lacks successful proof.",
                    MaterialClaimKind.Completion,
                    [claimEvidence.EvidenceId])
            ]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, [outcomeEvidence, claimEvidence]));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("Material claim 'claim'", StringComparison.Ordinal)
                     && error.Contains("exact succeeded evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsCompletionDossierWithMoreThanThirtyTwoUniqueEvidenceItems()
    {
        var evidence = Enumerable.Range(0, 33)
            .Select(index => Evidence(
                $"proof-{index:D2}",
                PlanningToolDomainOutcome.Succeeded,
                index.ToString(),
                "outcome"))
            .ToArray();
        var evidenceIds = evidence.Select(item => item.EvidenceId).ToArray();
        var graph = Graph(new WorkNode(
            "outcome",
            "Completed outcome.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.CreateRange(evidenceIds)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["outcome"],
            bindings: [new CompletionEvidenceBinding("outcome", evidenceIds)]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, evidence));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("33 unique evidence projections", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsCompletionDossierOverFortyEightThousandProjectedCharacters()
    {
        var evidence = Enumerable.Range(0, 27)
            .Select(index => Evidence(
                $"proof-{index:D2}",
                PlanningToolDomainOutcome.Succeeded,
                new string((char)('a' + index % 26), 2_000),
                "outcome"))
            .ToArray();
        var evidenceIds = evidence.Select(item => item.EvidenceId).ToArray();
        var graph = Graph(new WorkNode(
            "outcome",
            "Completed outcome.",
            ParentId: null,
            WorkNodeStatus.Satisfied,
            ImmutableArray<string>.Empty,
            ImmutableArray.CreateRange(evidenceIds)));
        var decision = CompletionDecision(
            requiredOutcomeIds: ["outcome"],
            bindings: [new CompletionEvidenceBinding("outcome", evidenceIds)]);

        var result = new OrchestrationDecisionValidator().Validate(
            decision,
            Context(graph, evidence));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("projected characters", StringComparison.Ordinal)
                     && error.Contains("48000", StringComparison.Ordinal));
    }

    private static AcceptedEvidenceProjection Evidence(
        string evidenceId,
        PlanningToolDomainOutcome outcome,
        string projection,
        string? workItemId = null) =>
        new(
            evidenceId,
            "call-" + evidenceId,
            "test-tool",
            PlanningToolInvocationStatus.Returned,
            outcome,
            projection,
            workItemId);

    private static WorkGraphSnapshot Graph(params WorkNode[] nodes) =>
        new(
            4,
            nodes.ToImmutableDictionary(node => node.Id, StringComparer.Ordinal));

    private static OrchestrationValidationContext Context(
        WorkGraphSnapshot graph,
        IEnumerable<AcceptedEvidenceProjection> evidence)
    {
        var projections = evidence.ToDictionary(
            item => item.EvidenceId,
            item => item,
            StringComparer.Ordinal);
        return new OrchestrationValidationContext(
            stateRevision: 12,
            selectedTools: [],
            acceptedEvidenceIds: projections.Keys,
            evidenceOutcomes: projections.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.DomainOutcome,
                StringComparer.Ordinal),
            workGraphRevision: graph.Revision,
            authoritativeWorkGraph: graph,
            evidenceProjections: projections);
    }

    private static OrchestrationDecision CompletionDecision(
        IEnumerable<string> requiredOutcomeIds,
        IEnumerable<CompletionEvidenceBinding> bindings,
        IEnumerable<OrchestrationMaterialClaim>? claims = null,
        IEnumerable<string>? requiredClaimIds = null,
        CompletionKind completionKind = CompletionKind.Succeeded) =>
        new(
            workUpdate: null,
            materialClaims: claims,
            new BeginCompletionAction(new CompletionPlan(
                "answer",
                completionKind,
                requiredOutcomeIds,
                requiredClaimIds,
                bindings,
                requestedFormat: "concise",
                requestedSections: [])));

    internal static string DecisionJson(string nextAction) =>
        $$"""
        {
          "workUpdate": null,
          "materialClaims": [],
          "nextAction": {{nextAction}}
        }
        """;

    internal static string TransportJson(string decisionJson) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AliOrchestrationProtocol.DecisionJsonPropertyName] = decisionJson
        });

    internal static string CallToolJson(string toolName, string argumentName, string argumentValue) =>
        $$"""
        {
          "kind": "callTool",
          "toolName": "{{toolName}}",
          "arguments": { "{{argumentName}}": "{{argumentValue}}" },
          "need": "Inspect the requested file",
          "expectedProgress": "The file contents will be available as evidence"
        }
        """;

    internal static string ToolDecisionJson(
        string toolName,
        string argumentName,
        string argumentValue) =>
        $$"""
        {
          "workUpdate": {
            "baseRevision": 0,
            "items": [
              {
                "workItemId": "work-read-requested-file",
                "outcome": "Read the requested file",
                "status": "active",
                "parentId": null,
                "supersededById": null,
                "dependencyIds": [],
                "evidenceIds": []
              }
            ]
          },
          "materialClaims": [],
          "nextAction": {{CallToolJson(toolName, argumentName, argumentValue)}}
        }
        """;
}
