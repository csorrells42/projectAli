using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class SemanticToolLibrarianTests
{
    [Fact]
    public async Task SelectAsync_UsesModelMeaningAndReturnsOnlyExactLiveTools()
    {
        using var model = new ScriptedChatClient("coding_inspect_project\ncoding_build_project");
        var librarian = new SemanticToolLibrarian(model, "Ali");
        var inspect = Tool("coding_inspect_project", "Inspect a software project's manifest and source layout.");
        var build = Tool("coding_build_project", "Build a detected software project and return diagnostics.");
        var weather = Tool("search_current_web", "Search live web sources for current information.");

        var selected = await librarian.SelectAsync(
            [new AIChatMessage(AIChatRole.User, "Please repair this application and prove it compiles.")],
            [inspect, build, weather],
            "Please repair this application and prove it compiles.",
            new ChatOptions { MaxOutputTokens = 8192 },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["coding_inspect_project", "coding_build_project"], selected.Select(tool => tool.Name));
        Assert.Null(Assert.Single(model.ObservedOptions).Tools);
        Assert.Equal(512, Assert.Single(model.ObservedOptions).MaxOutputTokens);
        var prompt = string.Join("\n", Assert.Single(model.ObservedMessages).Select(message => message.Text));
        Assert.Contains("repair this application", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search_current_web | Search live web sources", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectAsync_DirectLeavesThePlannerFreeToAnswerWithoutATool()
    {
        using var model = new ScriptedChatClient("DIRECT");
        var librarian = new SemanticToolLibrarian(model, "Ali");

        var selected = await librarian.SelectAsync(
            [new AIChatMessage(AIChatRole.User, "How are you today?")],
            [Tool("search_current_web", "Search current web sources.")],
            "How are you today?",
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(selected);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task SelectAsync_InvalidNameIsReturnedToModelForSemanticRepair()
    {
        using var model = new ScriptedChatClient(
            "imaginary_architecture_wand",
            "coding_inspect_architecture");
        var librarian = new SemanticToolLibrarian(model, "Ali");
        var architecture = Tool(
            "coding_inspect_architecture",
            "Inspect dependency direction, coupling, cycles, and architecture hotspots.");

        var selected = await librarian.SelectAsync(
            [new AIChatMessage(AIChatRole.User, "Find the architectural cycle in this solution.")],
            [architecture],
            "Find the architectural cycle in this solution.",
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("coding_inspect_architecture", Assert.Single(selected).Name);
        Assert.Equal(2, model.CallCount);
        var repairPrompt = string.Join("\n", model.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("not an exact registered tool name", repairPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectAsync_OverLimitSelectionIsReturnedToModelForSmallerSemanticSet()
    {
        var tools = Enumerable.Range(1, SemanticToolLibrarian.MaximumSelectedTools + 1)
            .Select(index => Tool($"tool_{index}", $"Capability {index}."))
            .ToArray();
        using var model = new ScriptedChatClient(
            string.Join("\n", tools.Select(tool => tool.Name)),
            string.Join("\n", tools.Take(SemanticToolLibrarian.MaximumSelectedTools).Select(tool => tool.Name)));
        var librarian = new SemanticToolLibrarian(model, "Ali");

        var selected = await librarian.SelectAsync(
            [new AIChatMessage(AIChatRole.User, "Use the relevant capabilities.")],
            tools,
            "Use the relevant capabilities.",
            null,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(SemanticToolLibrarian.MaximumSelectedTools, selected.Count);
        Assert.Equal(2, model.CallCount);
    }

    private static AIFunction Tool(string name, string description) =>
        AIFunctionFactory.Create(() => "ok", name, description);

    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public int CallCount { get; private set; }

        public List<IReadOnlyList<AIChatMessage>> ObservedMessages { get; } = [];

        public List<ChatOptions> ObservedOptions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedMessages.Add(messages.ToList());
            ObservedOptions.Add(options?.Clone() ?? new ChatOptions());
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("The semantic librarian requested an unexpected model pass.");
            }

            return Task.FromResult(new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                _responses.Dequeue())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
