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
        Assert.Equal(2, inner.CallCount);
        var repairPrompt = string.Join("\n", inner.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("MALFORMED PRIOR DRAFT", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("exactly one valid JSON object", repairPrompt, StringComparison.Ordinal);
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
        Assert.Equal(2, inner.CallCount);
        Assert.IsType<ChatResponseFormatJson>(inner.Formats[0]);
        Assert.Null(inner.Formats[1]);
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
    public void MainHarness_AllowsACompleteBoundedCodingRunway()
    {
        Assert.True(AliAgentHarnessRunner.MaximumToolIterations >= 16);
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
                "{\"action\":\"final\",\"answer\":\"Created Desktop/touch.txt.\"}")));
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
    public async Task OversizedFrameworkAndToolText_AreCompactedBeforeCallingTheLocalModel()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new AIChatMessage(
                AIChatRole.Assistant,
                "{\"action\":\"final\",\"answer\":\"Handled the bounded result.\"}")));
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
                "{\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/App/App.csproj\",\"content\":\"clean\"},\"summary\":\"Remove the unresolved references before rebuilding\"}")));
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
                "{\"action\":\"final\",\"answer\":\"From the limited results returned, these are the two strongest matches. Their broader importance is my inference, not a source-established ranking.\"}")));
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
                "{\"action\":\"call\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/App/App.csproj\",\"content\":\"clean\"},\"summary\":\"Remove the warning source\"}")));
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
                "{\"action\":\"call\",\"tool\":\"file_access_replace\",\"arguments\":{},\"summary\":\"Remove the warning source\"}")));
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
                "{\"action\":\"final\",\"answer\":\"From the five returned excerpts, these are the two strongest matches. Their broader importance is my inference, not a source-established ranking.\"}")));
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
                "{\"action\":\"final\",\"answer\":\"Your saved bridge validation codeword is cobalt-heron-4729.\"}")));
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
                "{\"action\":\"final\",\"answer\":\"I did not update that memory because permission was denied.\"}")));
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
                "{\"action\":\"call\",\"tool\":\"file_access_read\",\"arguments\":{\"fileName\":\"Desktop/App/App.csproj\"},\"summary\":\"Read the requested authoritative project file\"}")));
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
                "{\"action\":\"final\",\"answer\":\"108 tools across Ali native, Agent Framework, Roslyn, and integration sources.\"}")));
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
        Assert.Contains("Do not prepend, repeat, summarize", decisionPrompt, StringComparison.Ordinal);
        Assert.Contains("Omit self-directed planning notes", decisionPrompt, StringComparison.Ordinal);
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
                "{\"action\":\"final\",\"answer\":\"Finished safely.\"}")));
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
