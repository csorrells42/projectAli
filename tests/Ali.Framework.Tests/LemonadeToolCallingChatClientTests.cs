using System.Runtime.CompilerServices;
using System.Text;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Ali.Modules.Runtime.Models;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class LemonadeToolCallingChatClientTests
{
    [Fact]
    public void GptOssRuntimeOffersLargeJobOutputLimits()
    {
        var choice = Assert.Single(RuntimeModelChoiceCatalog.KnownChoices());

        Assert.Contains(4096, choice.OutputTokenLimits);
        Assert.Contains(8192, choice.OutputTokenLimits);
        Assert.Contains(16384, choice.OutputTokenLimits);
        Assert.Contains(65536, choice.ContextTokens);
        Assert.Contains(131072, choice.ContextTokens);
        Assert.Equal(131072, OllamaRuntimeSafetyPolicy.MaximumContextTokens);
        Assert.Equal(65536, OllamaRuntimeSafetyPolicy.ClampContextTokens(65536));
        Assert.Equal(131072, OllamaRuntimeSafetyPolicy.ClampContextTokens(131072));
    }

    [Fact]
    public async Task PlainFinalAnswer_IsReturnedWithoutASecondModelPassOrRewrite()
    {
        const string answer = "I am doing well today, thank you.";
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                $$"""{"action":"final","answer":"{{answer}}"}""")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "read_current_state",
            "Read authoritative current state when it is needed.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "How are you doing today?")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Equal(answer, response.Text);
        Assert.Equal(1, inner.CallCount);
        var decisionPrompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.Contains("use them instead of claiming incapability", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("giving manual shell instructions", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("reason hierarchically", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("recursively split each unsolved part", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not confuse completing one leaf", decisionPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisibleTurnFinalDecision_IsSemanticallyAuditedWithoutPhraseMatching()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Repair the existing chess control and build it.",
            _ => { });
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Here is a design overview for the repaired chess control.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/AliChess/ChessBoardView.xaml.cs\",\"content\":\"complete source\"},\"summary\":\"Write the requested implementation\",\"basis\":\"No successful write or build result proves the requested repair exists.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var write = AIFunctionFactory.Create(
            (string fileName, string content) => $"wrote {fileName}",
            "file_access_write",
            "Write a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText)],
            new ChatOptions { Tools = [write] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        Assert.Equal(2, inner.CallCount);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("CURRENT HUMAN TURN", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("AVAILABLE TOOLS AND ARGUMENT SCHEMAS", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("\"fileName\"", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("\"required\"", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("was the complete outcome requested", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete outcome tree", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smaller solvable branches", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future-tense", auditPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CriticToolCallMissingRequiredArgument_IsRepairedAgainstAuthoritativeSchema()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "What are the most important software engineering developments today?",
            _ => { })
        {
            UsedEvidenceTool = true
        };
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"These weak snippets prove the ranking.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"search_current_web\",\"arguments\":{\"query\":\"software engineering developments July 29 2026\"},\"summary\":\"Refine the search\",\"basis\":\"The available evidence does not establish the requested current ranking.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"search_current_web\",\"arguments\":{\"query\":\"software engineering developments July 29 2026\",\"topic\":\"software engineering\"},\"summary\":\"Refine the search with a valid topic\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var search = AIFunctionFactory.Create(
            (string query, string topic) => $"{topic}: {query}",
            "search_current_web",
            "Search current web sources by query and topic.");
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent("call-search", new { sources = new[] { "weak" } }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), result],
            new ChatOptions { Tools = [search] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("search_current_web", call.Name);
        Assert.True(call.Arguments!.ContainsKey("query"));
        Assert.True(call.Arguments.ContainsKey("topic"));
        Assert.Equal(3, inner.CallCount);
        var repairPrompt = string.Join("\n", inner.ObservedMessages[2].Select(message => message.Text));
        Assert.Contains("missing required argument(s): topic", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("\"query\"", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("\"topic\"", repairPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleStringTool_GenericArgumentName_IsMappedOnlyWhenSchemaIsUnambiguous()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"research_web\",\"arguments\":{\"query\":\"official .NET releases\"},\"summary\":\"Research primary sources\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var research = AIFunctionFactory.Create(
            (string question) => question,
            "research_web",
            "Research a question on the web.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Research official .NET releases.")],
            new ChatOptions { Tools = [research] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("research_web", call.Name);
        Assert.True(call.Arguments!.ContainsKey("question"));
        Assert.False(call.Arguments.ContainsKey("query"));
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ProviderInternalToolName_IsNeverExecutedAndGetsOneSchemaBoundedRepair()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"tavily_search\",\"arguments\":{\"query\":\"official release notes\"},\"summary\":\"Search Tavily\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"research_web\",\"arguments\":{\"question\":\"Find official release notes\"},\"summary\":\"Use Ali's registered research tool\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var research = AIFunctionFactory.Create(
            (string question) => question,
            "research_web",
            "Research a question on the web.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Find official release notes.")],
            new ChatOptions { Tools = [research] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("research_web", call.Name);
        Assert.Equal(2, inner.CallCount);
        var repairPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("'tavily_search' is not a registered tool", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("research_web", repairPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Search Tavily", call.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolHeavyAudit_CompactsEvidenceBeforeCallingTheCritic()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"I cannot finish this requested repair.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"coding_build_project\",\"arguments\":{\"projectPath\":\"Desktop/AliChess/AliChess.csproj\"},\"summary\":\"Verify the final source\",\"basis\":\"The requested repair has no successful verification result.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var build = AIFunctionFactory.Create(
            (string projectPath) => $"built {projectPath}",
            "coding_build_project",
            "Build a .NET project.");
        var messages = new List<AIChatMessage>
        {
            new(AIChatRole.User, "Finish and verify the chess control without surrendering.")
        };
        for (var index = 0; index < 24; index++)
        {
            var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
            result.Contents.Add(new FunctionResultContent(
                $"call-{index}",
                new { success = true, detail = new string((char)('a' + index % 26), 5_500) }));
            messages.Add(result);
        }

        var response = await client.GetResponseAsync(
            messages,
            new ChatOptions { Tools = [build] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("coding_build_project", call.Name);
        Assert.Equal(2, inner.CallCount);
        var auditCharacterCount = inner.ObservedMessages[1]
            .Sum(message => message.Text?.Length ?? 0);
        Assert.True(
            auditCharacterCount < 40_000,
            $"Audit prompt remained too large: {auditCharacterCount} characters.");
    }

    [Fact]
    public async Task CriticBinaryNoVerdict_ReturnsTheJobToTheToolLoop()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Repair, build, and launch the chess project.",
            _ => { });
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"I cannot finish the repair.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"coding_build_project\",\"arguments\":{\"projectPath\":\"Desktop/AliChess/AliChess.csproj\"},\"summary\":\"Build the final source now\",\"basis\":\"No successful build result exists for the requested project.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var build = AIFunctionFactory.Create(
            (string projectPath) => $"built {projectPath}",
            "coding_build_project",
            "Build the requested project.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText)],
            new ChatOptions { Tools = [build] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("coding_build_project", call.Name);
        Assert.Equal(2, inner.CallCount);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("two plain-text lines", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("YES or NO", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("not the planner", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("final semantic acceptance review", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does what the human intended", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future-tense promise", auditPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CriticNoVerdict_ReturnsItsBasisToThePlannerForTheNextAction()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Build a complete playable chess game.",
            activity.Add);
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"The chess game is complete.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "NO\nThe source evidence shows the board initialization is still missing.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"assessment\":\"board initialization is missing\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/Chess/MainWindow.xaml.cs\",\"content\":\"complete board\",\"overwrite\":true},\"summary\":\"implement the complete board\",\"next\":\"inspect and build the result\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var write = AIFunctionFactory.Create(
            (string fileName, string content, bool overwrite) => fileName,
            "file_access_write",
            "Write a project file.");
        var priorResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        priorResult.Contents.Add(new FunctionResultContent(
            "call-write",
            new { success = true, summary = "Wrote a placeholder file." }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), priorResult],
            new ChatOptions { Tools = [write] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        Assert.Equal(3, inner.CallCount);
        Assert.Contains(activity, item =>
            item.IsActivity
            && item.Text.Contains("Critic answered no", StringComparison.OrdinalIgnoreCase));
        var criticPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("valid terminal result", criticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authoritative evidence conclusively proves", criticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("There is no partial-credit", criticPrompt, StringComparison.OrdinalIgnoreCase);
        var replanPrompt = string.Join("\n", inner.ObservedMessages[2].Select(message => message.Text));
        Assert.Contains("board initialization is still missing", replanPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resume your planner role", replanPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CriticCannotTurnUnfinishedSuccessfulWorkIntoABlocker()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Build a complete playable chess game.",
            _ => { });
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"The project builds, but chess logic still needs to be implemented.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"blocked\":true,\"action\":\"final\",\"answer\":\"The game is unfinished.\",\"basis\":\"The source is still a skeleton.\",\"evidenceQuote\":\"success\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/ChessGame/Program.cs\",\"content\":\"complete chess engine\",\"overwrite\":true},\"summary\":\"Implement the next atomic chess subsystem\",\"basis\":\"The successful scaffold and build prove progress, not an impossibility.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var write = AIFunctionFactory.Create(
            (string fileName, string content, bool overwrite) => fileName,
            "file_access_write",
            "Write a project file.");
        var successfulResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        successfulResult.Contents.Add(new FunctionResultContent(
            "call-build",
            new { success = true, warningCount = 0, summary = "Skeleton project built." }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), successfulResult],
            new ChatOptions { Tools = [write] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        Assert.Equal(3, inner.CallCount);
        var repairPrompt = string.Join("\n", inner.ObservedMessages[2].Select(message => message.Text));
        Assert.Contains("blocked final requires evidenceQuote", repairPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("return NO", repairPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not choose a tool", repairPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CriticCanReportImpossibleOnlyAfterMultipleDistinctToolFailures()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Build the requested project.",
            _ => { });
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"The build could not be completed.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"blocked\":true,\"action\":\"final\",\"answer\":\"I could not build the project because two independent build attempts failed with the required compiler unavailable.\",\"basis\":\"Two distinct authoritative tool failures report the same external compiler blocker.\",\"evidenceQuote\":\"Required compiler unavailable\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var build = AIFunctionFactory.Create(() => "ok", "coding_build_project", "Build a project.");
        var firstFailure = new AIChatMessage(AIChatRole.Tool, string.Empty);
        firstFailure.Contents.Add(new FunctionResultContent(
            "call-build-1",
            new { success = false, error = "Required compiler unavailable" }));
        var secondFailure = new AIChatMessage(AIChatRole.Tool, string.Empty);
        secondFailure.Contents.Add(new FunctionResultContent(
            "call-build-2",
            new { success = false, error = "Required compiler unavailable after environment refresh" }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), firstFailure, secondFailure],
            new ChatOptions { Tools = [build] },
            TestContext.Current.CancellationToken);

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("two independent build attempts failed", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task SarcasticComplaint_ReassertsTheOriginalOutcomeInsteadOfBecomingANewLiteralRequest()
    {
        const string originalRequest = "Build me a complete playable chess game.";
        const string incompleteAnswer = "The project builds, but the chess logic is not implemented yet.";
        const string complaint = "did i ask for a chess game or did I ask for some half finished flaming bag of dog shit on my doorstep";
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-current",
            "assistant-current",
            complaint,
            _ => { });
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"You asked for a half-finished flaming bag, not a chess game.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/ChessGame/ChessEngine.cs\",\"content\":\"complete chess engine\",\"overwrite\":false},\"summary\":\"Continue the original chess-game request\",\"basis\":\"The complaint criticizes the unfinished prior result and reasserts the original playable-chess outcome.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var write = AIFunctionFactory.Create(
            (string fileName, string content, bool overwrite) => fileName,
            "file_access_write",
            "Write a project file.");

        var response = await client.GetResponseAsync(
            [
                new AIChatMessage(AIChatRole.User, originalRequest),
                new AIChatMessage(AIChatRole.Assistant, incompleteAnswer),
                new AIChatMessage(AIChatRole.User, complaint)
            ],
            new ChatOptions { Tools = [write] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        Assert.Equal(2, inner.CallCount);
        var decisionPrompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.Contains("rhetorical questions", decisionPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never claim the human requested the defective result", decisionPrompt, StringComparison.OrdinalIgnoreCase);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains(originalRequest, auditPrompt, StringComparison.Ordinal);
        Assert.Contains(incompleteAnswer, auditPrompt, StringComparison.Ordinal);
        Assert.Contains(complaint, auditPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedDraftPlanning_IsRepairedAndNeverShownToTheUser()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "We need several files. This is lengthy, so maybe I should stop and ask the user.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "The parser failed again, so I should probably give up.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "Still not an executable action.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/Game/MainWindow.xaml\",\"content\":\"<Window />\"},\"summary\":\"Continue building the requested app\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tool = AIFunctionFactory.Create(
            (string fileName, string content) => $"wrote {fileName}",
            "file_access_write",
            "Write a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Build the complete app; do not stop halfway.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        Assert.DoesNotContain("maybe I should stop", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, inner.CallCount);
        var repairPrompt = string.Join("\n", inner.ObservedMessages[3].Select(message => message.Text));
        Assert.Contains("MALFORMED PRIOR DRAFT", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("exactly one valid JSON object", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("not evidence", repairPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PegNativeStructuredDecoderFailure_RetriesWithoutServerGrammarAndValidatesLocally()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Finish the current job.",
            activity.Add);
        using var inner = new PegFailureThenSuccessChatClient();
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var tool = AIFunctionFactory.Create(() => "ok", "read_current_state", "Read state.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Finish the current job.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Equal("Finished safely.", response.Text);
        Assert.Equal(3, inner.CallCount);
        Assert.IsType<ChatResponseFormatJson>(inner.Formats[0]);
        Assert.Null(inner.Formats[1]);
        Assert.IsType<ChatResponseFormatJson>(inner.Formats[2]);
        Assert.Contains(activity, item =>
            item.IsActivity
            && item.ActivityKind == AgentActivityKind.Warning
            && item.Text.Contains("retrying a structured action", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReplaceLinesCall_AppendsTheFrameworkRequiredTrailingNewline()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"file_access_replace_lines\",\"arguments\":{\"fileName\":\"Desktop/Game.cs\",\"edits\":[{\"line_number\":10,\"new_line\":\"        UpdateScore();\"}]},\"summary\":\"Update the score\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tool = AIFunctionFactory.Create(() => "ok", "file_access_replace_lines", "Replace exact lines.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Fix the score update.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        var edits = Assert.IsType<System.Text.Json.JsonElement>(call.Arguments!["edits"]);
        Assert.EndsWith("\n", edits[0].GetProperty("new_line").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateScore();        ", edits[0].GetProperty("new_line").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolCallAdapter_NormalizesArgumentsBeforeFrameworkInvocation()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"call","tool":"file_access_write","arguments":{"fileName":"C:\\Users\\Chris\\Documents\\report.txt","content":"updated","overwrite":true},"summary":"Update the report"}""")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null,
            (toolName, arguments) =>
            {
                Assert.Equal("file_access_write", toolName);
                arguments["fileName"] = "Documents/report.txt";
                return arguments;
            });
        var tool = AIFunctionFactory.Create(() => "ok", "file_access_write", "Write a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Update the existing report.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("Documents/report.txt", Assert.IsType<string>(call.Arguments!["fileName"]));
    }

    [Fact]
    public void MainHarness_DoesNotTerminateWorkAtAnArbitraryIterationCount()
    {
        Assert.Equal(int.MaxValue, AliAgentHarnessRunner.MaximumToolIterations);
    }

    [Fact]
    public async Task ActivityMessages_UseConfiguredAssistantName()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Hello",
            activity.Add);
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"Hello there."}""")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Bob",
            () => turn);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "read_current_state",
            "Read authoritative current state when it is needed.");

        await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Hello")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains(activity, item => item.Text.Contains("Bob", StringComparison.Ordinal));
        Assert.DoesNotContain(activity, item =>
            item.Text.Contains("Ali", StringComparison.Ordinal)
            || (item.ActivityDetail?.Contains("Ali", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task TruncatedFinalAnswer_ContinuesWithoutResendingTheToolCatalog()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"public class Game {\n"""))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"    static void Main() {}\n}"}"""))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "file_access_write",
            "Create a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Write a C# game.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("public class Game", response.Text, StringComparison.Ordinal);
        Assert.Contains("static void Main", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("output limit", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);
        Assert.DoesNotContain(
            "AVAILABLE TOOLS",
            string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedFinalAnswer_ReplacesRestartedPartialLineAtContinuationSeam()
    {
        const string toolName = "dotnet_release_publish";
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"| dotnet_application_verify | Verify the app. |\\n| dotnet_release_publish | Create a .NET publish folder with a\"}"))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"| dotnet_release_publish | Create a .NET publish folder with a manifest. |\\n| dotnet_delivery_verify | Verify delivery. |\"}"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tool = AIFunctionFactory.Create(() => "ok", "list_available_tools", "List every tool.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "List every tool row.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, response.Text.Split(toolName, StringSplitOptions.None).Length - 1);
        Assert.Contains("with a manifest", response.Text, StringComparison.Ordinal);
        Assert.Contains("dotnet_delivery_verify", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedFinalEnvelopeWithLengthFinishReason_StillContinues()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"Desktop tree through Desktops.exe"}"""))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"final","answer":"and the remaining files through the end of the tree."}"""))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(() => "ok", "file_access_ls", "List files.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Fully expand the tree.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("Desktops.exe", response.Text, StringComparison.Ordinal);
        Assert.Contains("end of the tree", response.Text, StringComparison.Ordinal);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task StreamingBoundaryLengthFinish_ContinuesEvenWithoutRegisteredTools()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "Rows one through eighty-three."))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "Rows eighty-four through one hundred ten."))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "Rows one hundred eleven through one hundred twenty."))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var text = new StringBuilder();
        ChatFinishReason? finishReason = null;

        await foreach (var update in client.GetStreamingResponseAsync(
                           [new AIChatMessage(AIChatRole.User, "List every row.")],
                           new ChatOptions(),
                           TestContext.Current.CancellationToken))
        {
            text.Append(update.Text);
            finishReason = update.FinishReason ?? finishReason;
        }

        Assert.Contains("one through eighty-three", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("eighty-four through one hundred ten", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("one hundred eleven through one hundred twenty", text.ToString(), StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.Stop, finishReason);
        Assert.Equal(3, inner.CallCount);
        Assert.Null(inner.Formats[1]);
        Assert.Null(inner.Formats[2]);
    }

    [Fact]
    public async Task TruncatedToolCall_ContinuesAndRunsAsOneCompleteToolRequest()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """{"action":"call","tool":"file_access_write","arguments":{"fileName":"Desktop/Game.cs","content":"public class Game {\n"""))
            {
                FinishReason = ChatFinishReason.Length
            },
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                """    static void Main() {}\n}","overwrite":false},"summary":"Create the game"}"""))
            {
                FinishReason = ChatFinishReason.Stop
            });
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "file_access_write",
            "Create a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Create Desktop/Game.cs.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        var content = Assert.IsType<System.Text.Json.JsonElement>(call.Arguments!["content"]);
        Assert.Contains("static void Main", content.GetString(), StringComparison.Ordinal);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task ApprovedToolResult_IsPresentedAsAuthoritativeExecutionEvidence()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Created Desktop/touch.txt.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"Created Desktop/touch.txt.\",\"basis\":\"The successful write result proves Desktop/touch.txt was created.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Charlie",
            () => null);
        var tool = AIFunctionFactory.Create(
            () => "ok",
            "file_access_write",
            "Create a requested file.");
        var callId = $"call-{Guid.NewGuid():N}";
        var assistant = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        assistant.Contents.Add(new FunctionCallContent(callId, "file_access_write",
            new Dictionary<string, object?> { ["fileName"] = "Desktop/touch.txt" }));
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(callId, new { success = true, path = "Desktop/touch.txt" }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Create touch.txt on the desktop."), assistant, result],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("Created", response.Text, StringComparison.Ordinal);
        var decisionPrompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.Contains("resumes the exact suspended tool call", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("authoritative evidence", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("Never contradict a successful result", decisionPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedCompletedToolCall_IsBlockedAndRepairedIntoFinalAnswer()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "List every tool.",
            activity.Add);
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"list_available_tools\",\"arguments\":{},\"summary\":\"List again\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Here are all 120 authoritative rows.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"Here are all 120 authoritative rows.\",\"basis\":\"The authoritative collection result declares a total of 120 rows.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        var tool = AIFunctionFactory.Create(() => "ok", "list_available_tools", "List every tool.");
        var callId = $"call-{Guid.NewGuid():N}";
        var priorCall = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        priorCall.Contents.Add(new FunctionCallContent(
            callId,
            "list_available_tools",
            new Dictionary<string, object?>()));
        var priorResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        priorResult.Contents.Add(new FunctionResultContent(callId, new { total = 120 }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "List every tool."), priorCall, priorResult],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("120 authoritative rows", response.Text, StringComparison.Ordinal);
        Assert.Equal(3, inner.CallCount);
        var repairPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("UNCHANGED PLAN LOOP STOPPED", repairPrompt, StringComparison.Ordinal);
        Assert.Contains(activity, item => item.Text.Contains("Detected an unchanged plan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedFrameworkAndToolText_AreCompactedBeforeCallingTheLocalModel()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Handled the bounded result.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"Handled the bounded result.\",\"basis\":\"The requested bounded inspection result was supplied.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tool = AIFunctionFactory.Create(() => "ok", "file_access_grep", "Search approved files.");
        var callId = $"call-{Guid.NewGuid():N}";
        var system = new AIChatMessage(AIChatRole.System, new string('s', 100_000));
        var call = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        call.Contents.Add(new FunctionCallContent(callId, "file_access_grep"));
        var result = new AIChatMessage(AIChatRole.Tool, new string('t', 200_000));
        result.Contents.Add(new FunctionResultContent(callId, new { matches = new string('m', 200_000) }));

        var response = await client.GetResponseAsync(
            [system, new AIChatMessage(AIChatRole.User, "Find the classes."), call, result],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.Contains("Handled", response.Text, StringComparison.Ordinal);
        var observedCharacters = inner.ObservedMessages[0].Sum(message => message.Text?.Length ?? 0);
        Assert.True(observedCharacters < 40_000, $"Compatibility prompt remained too large: {observedCharacters} characters.");
        var observedPrompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.Contains("framework instructions compacted", observedPrompt, StringComparison.Ordinal);
        Assert.Contains("tool message compacted", observedPrompt, StringComparison.Ordinal);
        Assert.Contains("tool result compacted", observedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoritativeToolInventory_PreservesEveryToolRowWithoutHeadTailCompaction()
    {
        var inventory = AliCapabilityCatalog.ListAvailableTools(new AgentOrchestrationSettings());

        var serialized = LemonadeToolCallingChatClient.SerializeToolResultForModel(inventory);
        using var document = System.Text.Json.JsonDocument.Parse(serialized);
        var root = document.RootElement;
        var rows = root.GetProperty("tools").EnumerateArray().ToArray();

        Assert.Equal(inventory.Tools.Count, root.GetProperty("total").GetInt32());
        Assert.Equal(inventory.Tools.Count, rows.Length);
        Assert.Equal(inventory.Tools.Select(tool => tool.Name), rows.Select(row => row[0].GetString()));
        Assert.All(rows, row => Assert.Equal(2, row.GetArrayLength()));
        Assert.DoesNotContain("tool result compacted", serialized, StringComparison.Ordinal);
        Assert.True(serialized.Length <= 6_000, $"Compact inventory was {serialized.Length:N0} characters.");
    }

    [Fact]
    public async Task LargeDynamicToolCatalog_IsCompactedWithoutDroppingToolNames()
    {
        using var inner = new RecordingChatClient(new ChatResponse(new AIChatMessage(
            AIChatRole.Assistant,
            "{\"action\":\"final\",\"answer\":\"Catalog handled.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var tools = Enumerable.Range(0, 113)
            .Select(index => (AITool)AIFunctionFactory.Create(
                (string value) => value,
                $"dynamic_tool_{index:000}",
                $"Tool {index}: " + new string('x', 1_000)))
            .ToList();

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Use the appropriate registered tool if needed.")],
            new ChatOptions { Tools = tools },
            TestContext.Current.CancellationToken);

        Assert.Contains("Catalog handled", response.Text, StringComparison.Ordinal);
        var prompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.True(prompt.Length < 50_000, $"Dynamic tool catalog remained too large: {prompt.Length} characters.");
        Assert.Contains("dynamic_tool_000", prompt, StringComparison.Ordinal);
        Assert.Contains("dynamic_tool_112", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 300), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubstantialToolRun_FinalDraftIsAuditedAndCanReturnToTheToolLoop()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Everything is clean and complete.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/App/App.csproj\",\"content\":\"clean\"},\"summary\":\"Remove the unresolved references before rebuilding\",\"basis\":\"The successful build still reported unresolved work, so the requested clean result is not complete.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var inspect = AIFunctionFactory.Create(() => "ok", "coding_inspect_project", "Inspect the project.");
        var write = AIFunctionFactory.Create(() => "ok", "file_access_write", "Write the corrected project file.");
        var firstCallId = $"call-{Guid.NewGuid():N}";
        var secondCallId = $"call-{Guid.NewGuid():N}";
        var firstCall = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        firstCall.Contents.Add(new FunctionCallContent(firstCallId, "coding_inspect_project"));
        var firstResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        firstResult.Contents.Add(new FunctionResultContent(firstCallId, new { success = true, manifest = "App.csproj" }));
        var secondCall = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        secondCall.Contents.Add(new FunctionCallContent(secondCallId, "coding_build_project"));
        var secondResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        secondResult.Contents.Add(new FunctionResultContent(secondCallId, new { success = true, warningCount = 8 }));

        var response = await client.GetResponseAsync(
            [
                new AIChatMessage(AIChatRole.User, "Clean every warning and rebuild."),
                firstCall,
                firstResult,
                secondCall,
                secondResult
            ],
            new ChatOptions { Tools = [inspect, write] },
            TestContext.Current.CancellationToken);

        var correctiveCall = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", correctiveCall.Name);
        Assert.Equal(2, inner.CallCount);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("QUALITY CONTROL PASS", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("choose the exact next tool call", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("warningCount", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("denied or rejected permission", auditPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleEvidenceToolResult_IsAuditedForClaimCalibration()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "What are the two most important developments today? Distinguish reporting from inference.",
            _ => { })
        {
            UsedEvidenceTool = true
        };
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"These are unquestionably the two most important developments.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"From the limited results returned, these are the two strongest matches. Their broader importance is my inference, not a source-established ranking.\",\"basis\":\"The answer now limits its claim to the evidence returned and labels the ranking as inference.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var search = AIFunctionFactory.Create(() => "ok", "search_current_web", "Search current web sources.");
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(
            "call-search",
            new { status = "Found two excerpts", sources = new[] { "one", "two" } }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), result],
            new ChatOptions { Tools = [search] },
            TestContext.Current.CancellationToken);

        Assert.Contains("limited results", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("distinguish what the retrieved material directly reports", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("unsupported superlative", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("selection basis and limits", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("does not itself prove that ranking", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not claim the search was exhaustive", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("copy them verbatim", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("Current UTC timestamp for freshness comparison", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("internal fetch timestamp", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("third-party blog", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("freshness", auditPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SingleMutationResult_IsAuditedAndCannotFinishWithAPlaceholder()
    {
        const string request = "Build and launch a complete WPF chess game in Desktop/AliChess.";
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"I cannot finish the full chess game in this interaction.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/AliChess/ChessGame.cs\",\"content\":\"complete engine\",\"overwrite\":false},\"summary\":\"Continue the requested implementation\",\"basis\":\"The existing source is still a placeholder, so the requested complete game is not done.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var write = AIFunctionFactory.Create(
            () => "ok",
            "file_access_write",
            "Write a requested project file.");
        var callId = $"call-{Guid.NewGuid():N}";
        var call = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        call.Contents.Add(new FunctionCallContent(callId, "file_access_write"));
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(
            callId,
            "File written, but source still says: Main content will be added here by the updated implementation."));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request), call, result],
            new ChatOptions { Tools = [write] },
            TestContext.Current.CancellationToken);

        var correctiveCall = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", correctiveCall.Name);
        Assert.Equal(2, inner.CallCount);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("placeholder", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a concrete blocker", auditPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real permission mechanism", auditPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedRouteSearch_CriticCallsMapHandoffInsteadOfAllowingInventedPaperDirections()
    {
        const string request = "Give me directions from home to a Publix, then Waffle House, then a gym, and back home.";
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            request,
            _ => { })
        {
            UsedEvidenceTool = true,
            UsedCurrentWebSearch = true,
            WebSearchAttempts = 2
        };
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Turn right, drive 1.5 miles to an invented Publix, then continue for 15 minutes.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"maps_create_directions_link\",\"arguments\":{\"origin\":\"home\",\"destination\":\"home\",\"waypoints\":[\"Publix near Stuart, FL\",\"Waffle House near Stuart, FL\",\"gym near Stuart, FL\"],\"travelMode\":\"driving\"},\"summary\":\"Create a live map route without inventing directions\",\"basis\":\"No route-capable tool result proves the requested directions.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var search = AIFunctionFactory.Create(() => "no reliable route", "search_current_web", "Search web sources.");
        var maps = AIFunctionFactory.Create(
            () => "map",
            AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName,
            "Create a map handoff.");
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(
            "call-search",
            new { status = "No reliable route evidence", sources = Array.Empty<string>(), canRetry = false }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request), result],
            new ChatOptions { Tools = [search, maps] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal(AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName, call.Name);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("never manufacture turn-by-turn steps", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("Ordinary web snippets and model knowledge are not route evidence", auditPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentWebFreshnessGate_RejectsFetchTimeAsObservationTimeAndCanRefineSearch()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "What is the current weather, and is it suitable for outdoor painting?",
            _ => { })
        {
            UsedEvidenceTool = true,
            UsedCurrentWebSearch = true,
            WebSearchAttempts = 1
        };
        turn.WebSources.Add(new CoordinatorSourceItem(
            "Old weather page",
            "weather",
            "https://example.com/weather",
            new DateTimeOffset(2026, 7, 29, 20, 0, 0, TimeSpan.Zero),
            "Observed conditions on July 20, 2026: clear and 88 F. Humidity was not reported."));
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"As of July 29 it is clear and humidity is probably low, so paint now.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"Retrieved July 29: conditions are clear, so paint now.\",\"basis\":\"The draft summarizes the returned source, subject to the dedicated freshness gate.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"search_current_web\",\"arguments\":{\"query\":\"weather observation July 29 2026\",\"topic\":\"weather\"},\"summary\":\"Verify a same-day observation before recommending\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var search = AIFunctionFactory.Create(() => "ok", "search_current_web", "Search current web sources.");
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(
            "call-search",
            new { status = "Fetched July 29", sources = turn.WebSources }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), result],
            new ChatOptions { Tools = [search] },
            TestContext.Current.CancellationToken);

        var retry = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("search_current_web", retry.Name);
        Assert.Equal(3, inner.CallCount);
        var freshnessPrompt = string.Join("\n", inner.ObservedMessages[2].Select(message => message.Text));
        Assert.Contains("internal fetch timestamp", freshnessPrompt, StringComparison.Ordinal);
        Assert.Contains("not publication evidence", freshnessPrompt, StringComparison.Ordinal);
        Assert.Contains("Remaining live-search attempts this turn: 1", freshnessPrompt, StringComparison.Ordinal);
        Assert.Contains("missing measurement remains unknown", freshnessPrompt, StringComparison.Ordinal);
        Assert.Contains("July 20, 2026", freshnessPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolResultsAreCountedAcrossFrameworkPacketsForOneTurn()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Clean every warning and rebuild.",
            _ => { });
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"call\",\"tool\":\"coding_build_project\",\"arguments\":{\"targetPath\":\"Desktop/App\"},\"summary\":\"Build after inspection\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"The warning is harmless.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/App/App.csproj\",\"content\":\"clean\"},\"summary\":\"Remove the warning source\",\"basis\":\"The build result reports eight warnings, so the requested clean build is not complete.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var inspect = AIFunctionFactory.Create(() => "ok", "coding_inspect_project", "Inspect.");
        var build = AIFunctionFactory.Create(() => "ok", "coding_build_project", "Build.");
        var write = AIFunctionFactory.Create(() => "ok", "file_access_write", "Write.");
        var firstId = $"call-{Guid.NewGuid():N}";
        var firstResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        firstResult.Contents.Add(new FunctionResultContent(firstId, new { success = true }));

        var firstResponse = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Clean every warning and rebuild."), firstResult],
            new ChatOptions { Tools = [inspect, build, write] },
            TestContext.Current.CancellationToken);
        Assert.Equal("coding_build_project", Assert.Single(firstResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);

        var secondId = $"call-{Guid.NewGuid():N}";
        var secondResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        secondResult.Contents.Add(new FunctionResultContent(secondId, new { success = true, warningCount = 8 }));
        var secondResponse = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Clean every warning and rebuild."), secondResult],
            new ChatOptions { Tools = [inspect, build, write] },
            TestContext.Current.CancellationToken);

        Assert.Equal("file_access_write", Assert.Single(secondResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);
        Assert.Equal(3, inner.CallCount);
        Assert.Contains("QUALITY CONTROL PASS", string.Join("\n", inner.ObservedMessages[2].Select(message => message.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeToolCallingFinal_IsAuditedAfterMultipleToolResults()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Fix every diagnostic, rebuild, and run the app.",
            activity.Add);
        var nativeCallMessage = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        nativeCallMessage.Contents.Add(new FunctionCallContent("call-build", "coding_build_project"));
        using var inner = new RecordingChatClient(
            new ChatResponse(nativeCallMessage),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "The app is complete with no warnings.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_replace\",\"arguments\":{},\"summary\":\"Remove the warning source\",\"basis\":\"The build result reports nine warnings, so the requested clean runnable app is not complete.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new NativeToolRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var build = AIFunctionFactory.Create(() => "ok", "coding_build_project", "Build.");
        var replace = AIFunctionFactory.Create(() => "ok", "file_access_replace", "Replace.");
        var inspectResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        inspectResult.Contents.Add(new FunctionResultContent("call-inspect", new { success = true }));

        var firstResponse = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), inspectResult],
            new ChatOptions { Tools = [build, replace] },
            TestContext.Current.CancellationToken);
        Assert.Equal("coding_build_project", Assert.Single(firstResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);

        var buildResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        buildResult.Contents.Add(new FunctionResultContent(
            "call-build",
            new { success = true, warningCount = 9 }));
        var secondResponse = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), buildResult],
            new ChatOptions { Tools = [build, replace] },
            TestContext.Current.CancellationToken);

        Assert.Equal("file_access_replace", Assert.Single(secondResponse.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);
        Assert.Equal(3, inner.CallCount);
        Assert.Contains(activity, item =>
            item.IsActivity
            && item.ActivityKind == AgentActivityKind.Planning
            && item.Text.Contains("checking the evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NativeToolCallingFinal_IsAuditedAfterOneEvidenceResult()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "What are the two most important developments today? Distinguish reporting from inference.",
            activity.Add)
        {
            UsedEvidenceTool = true
        };
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "These are unquestionably the two most important developments.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"From the five returned excerpts, these are the two strongest matches. Their broader importance is my inference, not a source-established ranking.\",\"basis\":\"The answer reports the evidence limits and labels broader importance as inference.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new NativeToolRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var search = AIFunctionFactory.Create(() => "ok", "search_current_web", "Search current web sources.");
        var searchResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        searchResult.Contents.Add(new FunctionResultContent(
            "call-search",
            new { status = "Found five excerpts", sources = new[] { "one", "two", "three", "four", "five" } }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), searchResult],
            new ChatOptions { Tools = [search] },
            TestContext.Current.CancellationToken);

        Assert.Contains("five returned excerpts", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);
        Assert.Contains(activity, item =>
            item.IsActivity
            && item.ActivityKind == AgentActivityKind.Planning
            && item.Text.Contains("checking the evidence", StringComparison.OrdinalIgnoreCase));
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("unsupported superlative", auditPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeMemoryRecallFinal_IsAuditedWhenItContradictsTheRetrievedFact()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "What is my bridge validation codeword? Answer from durable memory only.",
            _ => { })
        {
            UsedEvidenceTool = true
        };
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "I do not have a durable record of that codeword.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"Your saved bridge validation codeword is cobalt-heron-4729.\",\"basis\":\"The durable-memory tool result contains the exact requested codeword.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new NativeToolRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var recall = AIFunctionFactory.Create(() => "ok", "recall_user_memory", "Recall durable memory.");
        var memoryResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        memoryResult.Contents.Add(new FunctionResultContent(
            "call-recall",
            new
            {
                status = "Found 1 matching saved memories.",
                memories = new[] { new { text = "temporary test fact: the bridge validation codeword is cobalt-heron-4729" } }
            }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), memoryResult],
            new ChatOptions { Tools = [recall] },
            TestContext.Current.CancellationToken);

        Assert.Contains("cobalt-heron-4729", response.Text, StringComparison.Ordinal);
        Assert.Equal(2, inner.CallCount);
        Assert.Contains(
            "cobalt-heron-4729",
            string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeDeniedPermissionFinal_IsAuditedBeforeClaimingTheMutationSucceeded()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-message",
            "assistant-message",
            "Correct my saved memory from raven-2048 to raven-7777.",
            activity.Add);
        turn.RecordPermissionDecision(AgentToolApprovalChoice.Deny);
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "I've updated the memory to the correct value.")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"blocked\":true,\"action\":\"final\",\"answer\":\"I did not update that memory because permission was denied.\",\"basis\":\"The user denied the required mutation permission, so the requested update could not be performed.\",\"evidenceQuote\":\"Denied by the user.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new NativeToolRuntime(),
            "Ali",
            () => null);
        using var activeTurn = client.BeginTurn(turn);
        var correct = AIFunctionFactory.Create(() => "ok", "correct_current_user_memory", "Correct durable memory.");
        var deniedResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        deniedResult.Contents.Add(new FunctionResultContent(
            "call-correct",
            "Tool call invocation rejected. Denied by the user."));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, turn.OriginalUserText), deniedResult],
            new ChatOptions { Tools = [correct] },
            TestContext.Current.CancellationToken);

        Assert.Contains("did not update", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission was denied", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, inner.CallCount);
        Assert.Contains(activity, item =>
            item.IsActivity
            && item.ActivityKind == AgentActivityKind.Planning
            && item.Text.Contains("checking the evidence", StringComparison.OrdinalIgnoreCase));
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("Denied by the user", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("final boundary", auditPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeToolCallAdapter_NormalizesArgumentsBeforeFrameworkInvocation()
    {
        var nativeCallMessage = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        nativeCallMessage.Contents.Add(new FunctionCallContent(
            "call-write",
            "file_access_write",
            new Dictionary<string, object?>
            {
                ["fileName"] = @"C:\Users\Chris\Documents\report.txt",
                ["content"] = "updated",
                ["overwrite"] = true
            }));
        using var inner = new RecordingChatClient(new ChatResponse(nativeCallMessage));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new NativeToolRuntime(),
            "Ali",
            () => null,
            (toolName, arguments) =>
            {
                Assert.Equal("file_access_write", toolName);
                arguments["fileName"] = "Documents/report.txt";
                return arguments;
            });
        var tool = AIFunctionFactory.Create(() => "ok", "file_access_write", "Write a requested file.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Update the report.")],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("Documents/report.txt", Assert.IsType<string>(call.Arguments!["fileName"]));
    }

    [Fact]
    public async Task EvidenceCritic_RequiresTheSpecificSourceRequestedByTheHuman()
    {
        const string request = "Confirm the target framework from the actual project file, build, and run it.";
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"The build path implies net10.0-windows and the app is running.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":false,\"action\":\"call\",\"tool\":\"file_access_read\",\"arguments\":{\"fileName\":\"Desktop/App/App.csproj\"},\"summary\":\"Read the requested authoritative project file\",\"basis\":\"The requested framework fact has not been verified from the specified project file.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var read = AIFunctionFactory.Create(() => "ok", "file_access_read", "Read a file.");
        var build = AIFunctionFactory.Create(() => "ok", "coding_build_project", "Build a project.");
        var run = AIFunctionFactory.Create(() => "ok", "coding_run_project", "Run a project.");
        var buildResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        buildResult.Contents.Add(new FunctionResultContent(
            "call-build",
            new { success = true, warningCount = 0, artifact = "bin/Release/net10.0-windows/App.exe" }));
        var runResult = new AIChatMessage(AIChatRole.Tool, string.Empty);
        runResult.Contents.Add(new FunctionResultContent(
            "call-run",
            new { success = true, processId = 1234 }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request), buildResult, runResult],
            new ChatOptions { Tools = [read, build, run] },
            TestContext.Current.CancellationToken);

        Assert.Equal("file_access_read", Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);
        var auditPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("specific file", auditPrompt, StringComparison.Ordinal);
        Assert.Contains("not a substitute", auditPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolContinuation_PreservesCurrentHumanTurnAndRejectsStalePriorAnswer()
    {
        const string currentRequest = "Tell me only the exact tool count and major source categories.";
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user-current",
            "assistant-current",
            currentRequest,
            activity.Add);
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"108 tools across Ali native, Agent Framework, Roslyn, and integration sources.\"}")),
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"108 tools across Ali native, Agent Framework, Roslyn, and integration sources.\",\"basis\":\"The authoritative tool inventory reports exactly 108 tools and the requested source categories.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => turn);
        var tool = AIFunctionFactory.Create(() => "108", "list_available_tools", "Return the current tool inventory.");
        var toolCallId = $"call-{Guid.NewGuid():N}";
        var oldAssistant = new AIChatMessage(AIChatRole.Assistant, "Name: Chris. Current time: yesterday.");
        var call = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        call.Contents.Add(new FunctionCallContent(toolCallId, "list_available_tools"));
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(toolCallId, new { count = 108 }));

        var response = await client.GetResponseAsync(
            [
                new AIChatMessage(AIChatRole.User, "What is my name and the current time?"),
                oldAssistant,
                new AIChatMessage(AIChatRole.User, currentRequest),
                call,
                result
            ],
            new ChatOptions { Tools = [tool] },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Current time", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("108 tools", response.Text, StringComparison.OrdinalIgnoreCase);
        var decisionPrompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.Contains("CURRENT HUMAN TURN", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains(currentRequest, decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("Separate the requested action from its stated purpose", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("context, not authorization", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("do not call the same tool again with identical arguments", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not prepend, repeat, summarize", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("Omit self-directed planning notes", decisionPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileDeleteCatalog_AdvertisesRecoverableFolderDeletionDespiteFrameworkDefaultDescription()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"I can move that folder into recoverable trash after approval.\"}")));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new DevelopmentLocalModelRuntime(),
            "Ali",
            () => null);
        var delete = AIFunctionFactory.Create(
            (Func<string, bool>)(_ => true),
            AliCapabilityCatalog.FileDeleteName,
            "Delete a file.");

        _ = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, "Move this folder to recoverable trash.")],
            new ChatOptions { Tools = [delete] },
            TestContext.Current.CancellationToken);

        var decisionPrompt = string.Join("\n", inner.ObservedMessages[0].Select(message => message.Text));
        Assert.Contains("file or complete folder tree", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("trash destination is selected internally", decisionPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"description\":\"Delete a file.\"", decisionPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeToolCalling_MergesEverySystemGroundingBlockBeforeTheModelCall()
    {
        var nativeCallMessage = new AIChatMessage(AIChatRole.Assistant, string.Empty);
        nativeCallMessage.Contents.Add(new FunctionCallContent("call-memory", "recall_user_memory"));
        using var inner = new RecordingChatClient(new ChatResponse(nativeCallMessage));
        using var client = new LemonadeToolCallingChatClient(
            inner,
            new NativeToolRuntime(),
            "Ali",
            () => null);
        var recall = AIFunctionFactory.Create(
            (string query) => query,
            "recall_user_memory",
            "Recall a saved memory.");

        await client.GetResponseAsync(
            [
                new AIChatMessage(AIChatRole.System, "You are Ali."),
                new AIChatMessage(
                    AIChatRole.System,
                    "ACTIVE USER IDENTITY PROFILE: Christopher lives at 18865 George Mims Rd., Andalusia, AL 36421."),
                new AIChatMessage(AIChatRole.User, "Where is home?")
            ],
            new ChatOptions { Tools = [recall] },
            TestContext.Current.CancellationToken);

        var observed = Assert.Single(inner.ObservedMessages);
        var system = Assert.Single(observed, message => message.Role == AIChatRole.System);
        Assert.Contains("You are Ali.", system.Text, StringComparison.Ordinal);
        Assert.Contains("ACTIVE USER IDENTITY PROFILE", system.Text, StringComparison.Ordinal);
        Assert.Contains("18865 George Mims Rd.", system.Text, StringComparison.Ordinal);
        Assert.Equal("Where is home?", Assert.Single(observed, message => message.Role == AIChatRole.User).Text);
    }

    private sealed class RecordingChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public List<IReadOnlyList<AIChatMessage>> ObservedMessages { get; } = [];

        public List<ChatResponseFormat?> Formats { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedMessages.Add(messages.ToList());
            Formats.Add(options?.ResponseFormat);
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new ChatResponse(new AIChatMessage(AIChatRole.Assistant, "script exhausted")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in result.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class PegFailureThenSuccessChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public List<ChatResponseFormat?> Formats { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Formats.Add(options?.ResponseFormat);
            if (CallCount == 1)
            {
                throw new InvalidOperationException(
                    "HTTP 500: The model produced output that does not match the expected peg-native format");
            }

            return Task.FromResult(new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                CallCount == 2
                    ? "{\"action\":\"final\",\"answer\":\"Finished safely.\"}"
                    : "{\"taskComplete\":true,\"action\":\"final\",\"answer\":\"Finished safely.\",\"basis\":\"The requested bounded action completed successfully.\"}")));
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

    private sealed class NativeToolRuntime : ILocalModelRuntime
    {
        public ModelProfile ActiveProfile { get; } = ModelProfile.UnconfiguredFactorySafe() with
        {
            SupportsToolCalls = true
        };

        public async IAsyncEnumerable<ModelToken> StreamChatAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<RuntimeHealthCheck> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeHealthCheck(
                true,
                "Native tool test runtime is ready.",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));
    }
}
