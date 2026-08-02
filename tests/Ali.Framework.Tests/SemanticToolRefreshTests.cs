using System.Runtime.CompilerServices;
using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Ali.Framework.Tests;

public sealed class FullToolRegistryPlanningTests
{
    [Fact]
    public async Task CriticDenial_ReplansWithTheCompleteToolRegistryStillAvailable()
    {
        const string request = "Build the C# game, make sure it runs, and prove it is playable.";
        var turn = Turn(request);
        using var model = new ScriptedChatClient(
            "{\"action\":\"final\",\"answer\":\"The project scaffold is ready, but build and execution tools are unavailable.\"}",
            "NO\nThe scaffold alone does not contain game logic and there is no build, run, or application-verification evidence.",
            "{\"action\":\"call\",\"assessment\":\"The scaffold still needs playable game logic.\",\"tool\":\"file_access_write\",\"arguments\":{\"fileName\":\"Desktop/Game/MainWindow.xaml.cs\",\"content\":\"complete game logic\"},\"summary\":\"Write the playable implementation\",\"next\":\"Build, run, and verify the completed game.\"}");
        using var client = new AliToolCallingChatClient(
            model,
            new DevelopmentLocalModelRuntime(),
            "Bob",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var tools = new[]
        {
            Tool("dotnet_create_project", "Create a new .NET project scaffold."),
            Tool("file_access_write", "Write or replace source files in an approved project."),
            Tool("dotnet_build_project", "Build a .NET project and return compiler diagnostics."),
            Tool("coding_run_project", "Run a built application and return process evidence."),
            Tool("coding_verify_application", "Inspect and verify that an application implements the requested behavior.")
        };

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request)],
            new ChatOptions { Tools = tools.Cast<AITool>().ToList() },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("file_access_write", call.Name);
        Assert.Equal(3, model.CallCount);
        var initialPlannerPrompt = string.Join("\n", model.ObservedMessages[0].Select(message => message.Text));
        var replanningPrompt = string.Join("\n", model.ObservedMessages[2].Select(message => message.Text));
        foreach (var tool in tools)
        {
            Assert.Contains(tool.Name, initialPlannerPrompt, StringComparison.Ordinal);
            Assert.Contains(tool.Name, replanningPrompt, StringComparison.Ordinal);
        }
        Assert.Contains("build, run, or application-verification evidence", replanningPrompt, StringComparison.Ordinal);
        Assert.Equal(1, model.ObservedMessages.Count(messages =>
            messages.Any(message => message.Text?.Contains("QUALITY CONTROL PASS", StringComparison.Ordinal) == true)));
    }

    [Fact]
    public async Task CurrentWeatherDirectDraft_IsRejectedAndGetsALiveToolWithoutEntityDrift()
    {
        const string request = "What is the current weather in Tullahoma, Tennessee?";
        var turn = Turn(request);
        using var model = new ScriptedChatClient(
            "{\"action\":\"final\",\"answer\":\"Tulsa, Oklahoma is warm today.\"}",
            "NO\nThe draft substitutes Tulsa for Tullahoma and has no successful live evidence for the requested current weather.",
            "{\"action\":\"call\",\"assessment\":\"Current evidence for the requested Tennessee location is missing.\",\"tool\":\"search_current_web\",\"arguments\":{\"query\":\"current weather Tullahoma Tennessee\"},\"summary\":\"Retrieve live weather evidence for Tullahoma\",\"next\":\"Answer from the returned Tullahoma observations or forecast.\"}");
        using var client = new AliToolCallingChatClient(
            model,
            new DevelopmentLocalModelRuntime(),
            "Bob",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var search = Tool("search_current_web", "Search live web sources for current facts.");

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request)],
            new ChatOptions { Tools = [search] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("search_current_web", call.Name);
        Assert.Contains("Tullahoma", call.Arguments!["query"]?.ToString(), StringComparison.Ordinal);
        var criticPrompt = string.Join("\n", model.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("direct model answer is not authoritative evidence", criticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silently substitutes a different entity", criticPrompt, StringComparison.OrdinalIgnoreCase);
        var replanningPrompt = string.Join("\n", model.ObservedMessages[2].Select(message => message.Text));
        Assert.Contains("substitutes Tulsa for Tullahoma", replanningPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulWeatherEvidence_CannotBeReplacedByAnAvailabilityRefusal()
    {
        const string request = "What is the current weather in Tullahoma, Tennessee?";
        var turn = Turn(request);
        turn.UsedEvidenceTool = true;
        turn.UsedCurrentWebSearch = true;
        turn.WebSearchAttempts = 1;
        turn.WebSources.Add(new CoordinatorSourceItem(
            "National Weather Service",
            "weather",
            "https://forecast.weather.gov/",
            DateTimeOffset.UtcNow,
            "Tullahoma observation at 10:55 AM CDT: 78 F with light wind."));
        using var model = new ScriptedChatClient(
            "{\"action\":\"final\",\"answer\":\"I do not have real-time weather information.\"}",
            "NO\nThe successful National Weather Service result contains a current Tullahoma observation, so the draft contradicts available evidence.",
            "{\"action\":\"final\",\"answer\":\"The National Weather Service reports 78 F with light wind in Tullahoma at 10:55 AM CDT.\"}",
            "YES\nThe answer preserves Tullahoma and accurately synthesizes the successful current weather evidence.");
        using var client = new AliToolCallingChatClient(
            model,
            new DevelopmentLocalModelRuntime(),
            "Bob",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var search = Tool("search_current_web", "Search live web sources for current facts.");
        var result = new AIChatMessage(AIChatRole.Tool, string.Empty);
        result.Contents.Add(new FunctionResultContent(
            "call-weather",
            new
            {
                success = true,
                provider = "Google grounding",
                sources = turn.WebSources
            }));

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request), result],
            new ChatOptions { Tools = [search] },
            TestContext.Current.CancellationToken);

        Assert.Contains("78 F", response.Text, StringComparison.Ordinal);
        Assert.Contains("Tullahoma", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("do not have real-time", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, model.CallCount);
        Assert.Equal(2, model.ObservedMessages.Count(messages =>
            messages.Any(message => message.Text?.Contains("QUALITY CONTROL PASS", StringComparison.Ordinal) == true)));
        Assert.DoesNotContain(model.ObservedMessages.SelectMany(messages => messages), message =>
            message.Text?.Contains("CURRENT-EVIDENCE GATE", StringComparison.Ordinal) == true);
        var criticPrompt = string.Join("\n", model.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("claims the information or capability is unavailable", criticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("78 F", criticPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningArtifactBuildFailure_PreservesBuildEvidenceAndSelectsApprovedStopTool()
    {
        const string request = "Finish the game, rebuild it, and make sure it runs.";
        var turn = Turn(request);
        turn.UsedEvidenceTool = true;
        using var model = new ScriptedChatClient(
            "{\"action\":\"final\",\"answer\":\"The build and execution tools are unavailable.\"}",
            "NO\nThe build and run tools already succeeded. The later build reports that process 9800 is running the target artifact, so the approved stop-project action is the missing next step.",
            "{\"action\":\"call\",\"assessment\":\"The previously launched game is locking its build output.\",\"tool\":\"dotnet_stop_project\",\"arguments\":{\"projectPath\":\"Desktop/TicTacToe/TicTacToe.csproj\",\"configuration\":\"Debug\"},\"summary\":\"Request approval to close process 9800\",\"next\":\"Rebuild and relaunch after the target closes.\"}");
        using var client = new AliToolCallingChatClient(
            model,
            new DevelopmentLocalModelRuntime(),
            "Bob",
            () => turn);
        using var activeTurn = client.BeginTurn(turn);
        var build = Tool("dotnet_build_project", "Build a .NET project and return structured MSBuild evidence.");
        var run = Tool("dotnet_run_project", "Launch an already-built .NET application.");
        var stop = AIFunctionFactory.Create(
            (string projectPath, string? configuration) => projectPath + configuration,
            "dotnet_stop_project",
            "Close the running target application for an approved .NET project after permission.");
        var buildSuccess = ToolResult("call-build-1", new
        {
            success = true,
            summary = "Roslyn/MSBuild succeeded and produced a launchable artifact.",
            artifactPath = "Desktop/TicTacToe/bin/Debug/net10.0-windows/TicTacToe.exe"
        });
        var runSuccess = ToolResult("call-run", new
        {
            success = true,
            processId = 9800,
            artifactPath = "Desktop/TicTacToe/bin/Debug/net10.0-windows/TicTacToe.exe"
        });
        var buildLocked = ToolResult("call-build-2", new
        {
            success = false,
            failureKind = "RunningTarget",
            blockingProcessId = 9800,
            summary = "The project target is still running, so MSBuild was not started.",
            artifactPath = "Desktop/TicTacToe/bin/Debug/net10.0-windows/TicTacToe.exe"
        });

        var response = await client.GetResponseAsync(
            [new AIChatMessage(AIChatRole.User, request), buildSuccess, runSuccess, buildLocked],
            new ChatOptions { Tools = [build, run, stop] },
            TestContext.Current.CancellationToken);

        var call = Assert.Single(response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Equal("dotnet_stop_project", call.Name);
        Assert.Equal("Desktop/TicTacToe/TicTacToe.csproj", call.Arguments!["projectPath"]?.ToString());
        var criticPrompt = string.Join("\n", model.ObservedMessages[1].Select(message => message.Text));
        Assert.Contains("failed invocation of a registered tool proves", criticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not erase the successful build", criticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RunningTarget", criticPrompt, StringComparison.Ordinal);
        var replanningPrompt = string.Join("\n", model.ObservedMessages[2].Select(message => message.Text));
        Assert.Contains("process 9800", replanningPrompt, StringComparison.Ordinal);
        Assert.Contains("dotnet_stop_project", replanningPrompt, StringComparison.Ordinal);
    }

    private static CoordinatorTurnContext Turn(string request) => new(
        "conversation",
        "user-message",
        "assistant-message",
        request,
        _ => { });

    private static AIChatMessage ToolResult(string callId, object result)
    {
        var message = new AIChatMessage(AIChatRole.Tool, string.Empty);
        message.Contents.Add(new FunctionResultContent(callId, result));
        return message;
    }

    private static AIFunction Tool(string name, string description) =>
        AIFunctionFactory.Create(() => "ok", name, description);

    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public int CallCount { get; private set; }

        public List<IReadOnlyList<AIChatMessage>> ObservedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AIChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedMessages.Add(messages.ToList());
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("The scripted model received an unexpected extra pass.");
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
