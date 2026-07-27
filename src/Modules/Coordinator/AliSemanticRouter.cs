using System.ComponentModel;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Lets the model interpret the whole request and choose every required tool.
/// The text envelope is only a transport adapter for connectors without native tool calls.
/// </summary>
internal sealed class AliSemanticRouter
{
    private readonly ILocalModelRuntime _runtime;
    private readonly IChatClient _chatClient;
    private readonly AIFunction _routingTool;

    public AliSemanticRouter(ILocalModelRuntime runtime, IChatClient chatClient)
    {
        _runtime = runtime;
        _chatClient = chatClient;
        _routingTool = AIFunctionFactory.Create(
            (Func<bool, string?, string?, string?, string?, string?, string?, string?, string?, bool, bool, bool, CoordinatorRoutingPlan>)BuildRoutingPlan,
            "plan_response",
            "Select every tool needed to answer the user's complete request. This is a semantic model decision, not keyword routing. Personal facts about the human user require memoryQuery. Current or changeable facts require currentWebQuery. Questions about Ali's configured name require needAssistantIdentity. Questions about Ali's available tools require needToolCatalog. Local documents require localLibraryQuery. Compound requests may select several fields. For casual conversation or stable general knowledge, set answerDirectly true and leave tool fields empty. Only set factToRemember or reminder fields after an explicit user request.");
    }

    public async Task<CoordinatorRoutingPlan> PlanAsync(
        IReadOnlyList<MeaiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var planningMessages = new List<MeaiChatMessage>
        {
            new(
                MeaiChatRole.System,
                string.Join(
                    Environment.NewLine,
                    "You are Ali's semantic tool-routing pass.",
                    "Read the user's complete meaning, including nested and compound requests.",
                    "You must call plan_response exactly once. Do not answer the user in text.",
                    "Any question about a personal fact, name, preference, relationship, prior instruction, or remembered detail about the human user requires a memory query even if you think you know the answer.",
                    "Any request for news, current events, or facts that can change requires a current web query.",
                    "Any question about Ali's available tools, capabilities, integrations, or what Ali can access requires the tool catalog.",
                    "Use the authoritative local clock tool for the current local date or time; that does not also require a web query.",
                    "Distinguish the human user's identity from Ali's configured assistant identity.",
                    "Select all needed tools together; do not force a compound request into one route.",
                    "Use direct answer only for ordinary conversation and stable knowledge that needs no stored or current evidence."))
        };
        planningMessages.AddRange(messages.Where(message => message.Role != MeaiChatRole.System));

        if (!_runtime.ActiveProfile.SupportsToolCalls)
        {
            return await GetTextRoutingPlanAsync(planningMessages, cancellationToken).ConfigureAwait(false);
        }

        var response = await _chatClient.GetResponseAsync(
            planningMessages,
            new ChatOptions
            {
                Tools = [_routingTool],
                ToolMode = ChatToolMode.RequireSpecific(_routingTool.Name),
                AllowMultipleToolCalls = false,
                MaxOutputTokens = 384,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["ali.internalRouting"] = true
                }
            },
            cancellationToken).ConfigureAwait(false);
        var call = response.Messages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .FirstOrDefault(candidate => candidate.Name.Equals(_routingTool.Name, StringComparison.Ordinal));
        if (call is null)
        {
            return await GetTextRoutingPlanAsync(planningMessages, cancellationToken).ConfigureAwait(false);
        }

        var result = await _routingTool
            .InvokeAsync(new AIFunctionArguments(call.Arguments), cancellationToken)
            .ConfigureAwait(false);
        if (result is System.Text.Json.JsonElement element)
        {
            var plan = System.Text.Json.JsonSerializer.Deserialize<CoordinatorRoutingPlan>(element.GetRawText());
            if (plan is not null)
            {
                return plan;
            }
        }

        if (result is CoordinatorRoutingPlan routingPlan)
        {
            return routingPlan;
        }

        throw new InvalidOperationException("Ali's semantic routing decision could not be read.");
    }

    private async Task<CoordinatorRoutingPlan> GetTextRoutingPlanAsync(
        IReadOnlyList<MeaiChatMessage> planningMessages,
        CancellationToken cancellationToken)
    {
        var compatibilityMessages = planningMessages.ToList();
        compatibilityMessages[0] = new MeaiChatMessage(
            MeaiChatRole.System,
            string.Join(
                Environment.NewLine,
                compatibilityMessages[0].Text,
                "This connector uses a compact text tool envelope instead of native function-call JSON.",
                "The speaker of the user message is the human user. I, me, my, mine, and the user refer to that human. A personal fact about that human requires memory.",
                "The identity field is ONLY for Ali's configured assistant identity, such as 'what is your name' or 'who are you'. Never use identity for the human user's identity.",
                "The time field is the authoritative current local computer clock. Do not also select web merely to answer the current local date or time.",
                "Return one line only, with no prose and no semicolons inside a value:",
                "ROUTE direct=<true|false>; memory=<focused query|none>; web=<focused query|none>; web_topic=<news|finance|weather|sports|general|none>; local=<focused query|none>; remember=<exact fact|none>; category=<category|none>; reminder=<title|none>; due=<ISO 8601 local time with offset|none>; identity=<true|false>; time=<true|false>; catalog=<true|false>",
                "Select multiple non-none values for a nested or compound request. Current or changeable facts require web plus the best broad web_topic. Local documents require local. Questions about Ali's available tools or access require catalog. Explicit remember and reminder requests use their fields.",
                "Example human memory: ROUTE direct=false; memory=human user's name; web=none; web_topic=none; local=none; remember=none; category=none; reminder=none; due=none; identity=false; time=false; catalog=false",
                "Example current news: ROUTE direct=false; memory=none; web=latest important OpenAI news today; web_topic=news; local=none; remember=none; category=none; reminder=none; due=none; identity=false; time=false; catalog=false",
                "Example tool catalog: ROUTE direct=false; memory=none; web=none; web_topic=none; local=none; remember=none; category=none; reminder=none; due=none; identity=false; time=false; catalog=true",
                "Example casual: ROUTE direct=true; memory=none; web=none; web_topic=none; local=none; remember=none; category=none; reminder=none; due=none; identity=false; time=false; catalog=false",
                "Never answer the user's question in this routing response."));

        var response = await RequestRouteAsync(compatibilityMessages, cancellationToken).ConfigureAwait(false);
        if (ContainsTextRoutingEnvelope(response.Text))
        {
            return ParseTextRoutingPlan(response.Text);
        }

        var activeUserMessage = compatibilityMessages.Last(message => message.Role == MeaiChatRole.User);
        var retry = await RequestRouteAsync(
            [compatibilityMessages[0], activeUserMessage],
            cancellationToken).ConfigureAwait(false);
        return ParseTextRoutingPlan(retry.Text);
    }

    private Task<ChatResponse> RequestRouteAsync(
        IReadOnlyList<MeaiChatMessage> messages,
        CancellationToken cancellationToken) =>
        _chatClient.GetResponseAsync(
            messages,
            new ChatOptions
            {
                ToolMode = ChatToolMode.None,
                MaxOutputTokens = 384,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["ali.internalRouting"] = true
                }
            },
            cancellationToken);

    private static bool ContainsTextRoutingEnvelope(string? responseText) =>
        !string.IsNullOrWhiteSpace(responseText)
        && responseText.Contains("ROUTE", StringComparison.OrdinalIgnoreCase);

    private static CoordinatorRoutingPlan ParseTextRoutingPlan(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException("The local model returned an empty semantic routing decision.");
        }

        var routeStart = responseText.IndexOf("ROUTE", StringComparison.OrdinalIgnoreCase);
        if (routeStart < 0)
        {
            throw new InvalidOperationException("The local model's semantic routing decision did not contain a route envelope.");
        }

        var routeLine = responseText[(routeStart + "ROUTE".Length)..]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        var fields = routeLine
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

        static string? Optional(IReadOnlyDictionary<string, string> values, string name)
        {
            if (!values.TryGetValue(name, out var value))
            {
                return null;
            }

            var normalized = value.Trim().Trim('"', '\'');
            return normalized.Length == 0
                || normalized.Equals("none", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : normalized;
        }

        static bool Flag(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value)
            && bool.TryParse(value.Trim(), out var parsed)
            && parsed;

        return new CoordinatorRoutingPlan(
            Flag(fields, "direct"),
            Optional(fields, "memory"),
            Optional(fields, "web"),
            Optional(fields, "web_topic"),
            Optional(fields, "local"),
            Optional(fields, "remember"),
            Optional(fields, "category"),
            Optional(fields, "reminder"),
            Optional(fields, "due"),
            Flag(fields, "identity"),
            Flag(fields, "time"),
            Flag(fields, "catalog"));
    }

    private static CoordinatorRoutingPlan BuildRoutingPlan(
        [Description("True only when no memory, web, local-library, reminder, identity, clock, or catalog tool is needed.")] bool answerDirectly,
        [Description("A focused query for saved personal memory, or null.")] string? memoryQuery = null,
        [Description("A focused query for live/current internet evidence, or null.")] string? currentWebQuery = null,
        [Description("A broad current-web topic such as news, finance, weather, sports, or general, or null.")] string? currentWebTopic = null,
        [Description("A focused query for indexed local documents, or null.")] string? localLibraryQuery = null,
        [Description("The exact fact explicitly requested to be remembered, or null.")] string? factToRemember = null,
        [Description("A short category for factToRemember, or null.")] string? memoryCategory = null,
        [Description("The explicitly requested reminder title, or null.")] string? reminderTitle = null,
        [Description("The reminder due time in ISO 8601 with local offset, or null.")] string? reminderDueAtLocal = null,
        [Description("True for questions about Ali's configured assistant identity, not the human user.")] bool needAssistantIdentity = false,
        [Description("True when an authoritative current local clock value is needed.")] bool needCurrentLocalTime = false,
        [Description("True when the user asks what tools, capabilities, integrations, or access Ali has.")] bool needToolCatalog = false) =>
        new(
            answerDirectly,
            memoryQuery,
            currentWebQuery,
            currentWebTopic,
            localLibraryQuery,
            factToRemember,
            memoryCategory,
            reminderTitle,
            reminderDueAtLocal,
            needAssistantIdentity,
            needCurrentLocalTime,
            needToolCatalog);
}
