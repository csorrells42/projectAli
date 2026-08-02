using Ali.Modules.Coordinator;
using Ali.Modules.Evidence;
using Ali.UI.ViewModels;
using System.Diagnostics;
using System.Text.Json;

namespace Ali.Framework.Tests;

public sealed class AgentActivityPresentationTests
{
    [Theory]
    [InlineData(
        @"Working on C:\Users\clsor\Documents\Codex\ProjectAli\src\UI\MainWindow.xaml",
        "Working on MainWindow.xaml")]
    [InlineData(
        "Reading \"C:\\Users\\clsor\\OneDrive\\Documents\\Project Ali\\src\\App.xaml.cs\"",
        "Reading \"App.xaml.cs\"")]
    [InlineData(
        @"Reading C:\Users\clsor\OneDrive\Documents\Project Ali\src\App.xaml.cs next",
        "Reading App.xaml.cs next")]
    [InlineData(
        @"Inspecting \\server\share\folder\settings.json.",
        "Inspecting settings.json.")]
    [InlineData(
        "Reviewing /home/chris/project/src/main.py",
        "Reviewing main.py")]
    [InlineData(
        "Reviewing src/UI/ViewModels/MainWindowViewModel.cs",
        "Reviewing MainWindowViewModel.cs")]
    [InlineData(
        @"Working in C:\Users\clsor\Documents\Codex\ProjectAli\",
        "Working in ProjectAli")]
    [InlineData(
        @"Working in C:\Users\clsor\OneDrive\Documents\Project Ali\artifacts",
        "Working in artifacts")]
    [InlineData(
        "Ali's reading src/UI/Main.cs and she's checking it.",
        "Ali's reading Main.cs and she's checking it.")]
    [InlineData(
        "Reading 'C:\\Users\\clsor\\OneDrive\\Documents\\Project Ali\\src\\App.xaml.cs'",
        "Reading 'App.xaml.cs'")]
    [InlineData(
        "Don't inspect 'C:\\Program Files\\Ali\\App.xaml.cs' yet",
        "Don't inspect 'App.xaml.cs' yet")]
    [InlineData(
        @"Copied C:\source\folder to D:\destination\folder",
        "Copied folder to folder")]
    [InlineData(
        @"Copied C:\Project One\source to D:\Backup Sets\destination",
        "Copied source to destination")]
    [InlineData(
        "Checking https://example.com/api/status and input/output",
        "Checking https://example.com/api/status and input/output")]
    public void VisibleActivityText_IsFilenameFirst_WithoutMutatingDiagnosticText(
        string source,
        string expectedDisplay)
    {
        var chunk = CreateChunk(source, AgentActivityKind.ToolCall);

        var item = new AgentActivityItemViewModel(chunk);

        Assert.Equal(source, chunk.Text);
        Assert.Equal(source, item.Title);
        Assert.Equal(expectedDisplay, item.DisplayTitle);
        Assert.StartsWith("Working:", item.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void LongStructuredApprovalPayload_IsOmittedBeforeDisplayBounding()
    {
        var canary = "first-secret-canary";
        var payload = JsonSerializer.Serialize(new
        {
            content = canary + new string('x', 900),
            path = @"C:\Users\clsor\Documents\Codex\ProjectAli\src\Secrets.cs"
        });
        Assert.True(payload.Length > 320);
        var prompt = new AgentToolApprovalPrompt(
            "approval-1",
            "file_access_write",
            payload,
            "Write a file.");
        var chunk = CreateChunk(
            "Permission needed",
            AgentActivityKind.Approval,
            detail: payload,
            approvalPrompt: prompt);

        var item = new AgentActivityItemViewModel(chunk);

        Assert.Equal("Technical payload omitted from the human activity view.", item.Detail);
        Assert.Equal("Technical payload omitted from the human activity view.", item.DisplayDetail);
        Assert.DoesNotContain(canary, item.DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("Secrets.cs", item.DisplayText, StringComparison.Ordinal);
        Assert.Same(prompt, chunk.ApprovalPrompt);
        Assert.Equal(payload, chunk.ApprovalPrompt!.Arguments);
    }

    [Theory]
    [InlineData("{\"password\":\"short-secret\"")]
    [InlineData("Arguments: {\"token\":\"short-secret\"}")]
    [InlineData("```json {\"apiKey\":\"short-secret\"} ```")]
    [InlineData("[DEBUG] {\"token\":\"short-secret\"}")]
    [InlineData("[INFO] Arguments: {\"token\":\"short-secret\"}")]
    [InlineData("[INFO]: ```json {\"token\":\"short-secret\"} ```")]
    public void ProbableStructuredPayload_FailsClosedInTheHumanActivityView(string payload)
    {
        var item = new AgentActivityItemViewModel(CreateChunk(
            "Permission update",
            AgentActivityKind.Approval,
            detail: payload));

        Assert.Equal("Technical payload omitted from the human activity view.", item.Detail);
        Assert.Equal("Technical payload omitted from the human activity view.", item.DisplayDetail);
        Assert.DoesNotContain("short-secret", item.DisplayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentToolExecutionOutcome.Completed, "Returned", "returned")]
    [InlineData(AgentToolExecutionOutcome.Failed, "Failed", "failed")]
    [InlineData(AgentToolExecutionOutcome.Cancelled, "Cancelled", "was cancelled")]
    public void RuntimeReceipt_UsesAccurateTerminalWordingWithoutClaimingSuccess(
        AgentToolExecutionOutcome outcome,
        string expectedLabel,
        string expectedReceiptVerb)
    {
        var receipt = new AgentToolExecutionReceipt(
            "file_access_read",
            outcome,
            "The invocation produced a bounded result summary.",
            DateTimeOffset.UtcNow);
        var chunk = CreateChunk(
            @"Read C:\Users\clsor\Documents\Codex\ProjectAli\src\Program.cs",
            AgentActivityKind.ToolResult,
            receipt: receipt);

        var item = new AgentActivityItemViewModel(chunk);

        Assert.Same(receipt, item.ExecutionReceipt);
        Assert.Equal(expectedLabel, item.StatusLabel);
        Assert.Contains("Program.cs", item.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectAli", item.Headline, StringComparison.Ordinal);
        Assert.Contains("Runtime receipt: File access read", item.ReceiptText, StringComparison.Ordinal);
        Assert.Contains(expectedReceiptVerb, item.ReceiptText, StringComparison.Ordinal);
        Assert.Contains(item.ReceiptText, item.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("succeeded", item.SummaryText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The invocation produced a bounded result summary.", receipt.Summary);
    }

    [Fact]
    public void ReceiptBearingSummary_PrioritizesReceiptAndShortensDetailAndReceiptPaths()
    {
        var receipt = new AgentToolExecutionReceipt(
            "file_access_read",
            AgentToolExecutionOutcome.Completed,
            "Recorded /tmp/project/logs/receipt.json",
            DateTimeOffset.UtcNow);
        var item = new AgentActivityItemViewModel(CreateChunk(
            "Read source file",
            AgentActivityKind.ToolResult,
            detail: @"Next inspect C:\Users\clsor\Documents\Codex\ProjectAli\src\MainWindow.xaml",
            receipt: receipt));

        Assert.Equal("Next inspect MainWindow.xaml", item.DisplayDetail);
        Assert.Contains("receipt.json", item.ReceiptText, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/project", item.ReceiptText, StringComparison.Ordinal);
        Assert.Contains(item.ReceiptText, item.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain(item.DisplayDetail, item.SummaryText, StringComparison.Ordinal);
        Assert.True(item.SummaryText.Length <= 323);
    }

    [Fact]
    public void ReceiptDisplayName_HidesStableInternalIdentityButPreservesItInTheReceipt()
    {
        const string internalName = "mcp_server_0123456789abcdef_tool_fedcba9876543210";
        const string displayName = "Build Server: inspect solution";
        var receipt = new AgentToolExecutionReceipt(
            internalName,
            AgentToolExecutionOutcome.Completed,
            "Returned a bounded result.",
            DateTimeOffset.UtcNow)
        {
            DisplayName = displayName
        };

        var item = new AgentActivityItemViewModel(CreateChunk(
            $"{displayName} returned; Ali is evaluating the result.",
            AgentActivityKind.ToolResult,
            receipt: receipt));

        Assert.Contains(displayName, item.ReceiptText, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", item.DisplayText, StringComparison.Ordinal);
        Assert.Equal(internalName, item.ExecutionReceipt!.ToolName);
    }

    [Fact]
    public void ReceiptBearingSummary_KeepsReceiptVisibleWhenHeadlineIsLong()
    {
        var receipt = new AgentToolExecutionReceipt(
            "file_access_read",
            AgentToolExecutionOutcome.Completed,
            "Bounded runtime summary.",
            DateTimeOffset.UtcNow);
        var item = new AgentActivityItemViewModel(CreateChunk(
            "Reading " + new string('x', 600),
            AgentActivityKind.ToolResult,
            receipt: receipt));

        Assert.StartsWith("Runtime receipt: File access read returned.", item.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Bounded runtime summary.", item.SummaryText, StringComparison.Ordinal);
        Assert.True(item.SummaryText.Length <= 323);
    }

    [Theory]
    [InlineData("[3/10] Compiling")]
    [InlineData("[INFO] Restoring")]
    [InlineData("[50%] Downloading")]
    [InlineData("[INFO]: Restoring")]
    [InlineData("[BUILD]- Linking")]
    public void HumanBracketedProgress_IsPreserved(string detail)
    {
        var item = new AgentActivityItemViewModel(CreateChunk(
            "Build update",
            AgentActivityKind.Status,
            detail: detail));

        Assert.Equal(detail, item.Detail);
        Assert.Equal(detail, item.DisplayDetail);
        Assert.Contains(detail, item.SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        AgentToolExecutionOutcome.Completed,
        "File access read returned; Ali is evaluating the result.")]
    [InlineData(AgentToolExecutionOutcome.Failed, "File access read failed")]
    [InlineData(AgentToolExecutionOutcome.Cancelled, "File access read was cancelled")]
    public void ProductionTerminalTitle_IsNotPrefixedWithTheSameOutcomeTwice(
        AgentToolExecutionOutcome outcome,
        string title)
    {
        var receipt = new AgentToolExecutionReceipt(
            "file_access_read",
            outcome,
            "Bounded runtime summary.",
            DateTimeOffset.UtcNow);
        var item = new AgentActivityItemViewModel(CreateChunk(
            title,
            outcome == AgentToolExecutionOutcome.Failed
                ? AgentActivityKind.Error
                : AgentActivityKind.ToolResult,
            receipt: receipt));

        Assert.Equal(title, item.Headline);
    }

    [Fact]
    public void ErrorTitleWithoutReceipt_IsNotPrefixedWithFailedTwice()
    {
        const string title = "Agent run failed safely";
        var item = new AgentActivityItemViewModel(CreateChunk(
            title,
            AgentActivityKind.Error));

        Assert.Equal(title, item.Headline);
    }

    [Fact]
    public void HostileUnterminatedQuotedPath_IsBoundedBeforeFormatting()
    {
        _ = new AgentActivityItemViewModel(CreateChunk("Warmup src/UI/Main.cs", AgentActivityKind.ToolCall));
        var segments = Enumerable.Repeat("s", 1_000).ToArray();
        segments[0] = new string('a', 37);
        var hostile = "\"" + string.Join('/', segments) + "/Target.cs).";
        Assert.Equal(2_048, hostile.Length);
        var stopwatch = Stopwatch.StartNew();

        var item = new AgentActivityItemViewModel(CreateChunk(hostile, AgentActivityKind.ToolCall));
        var fallback = AgentActivityItemViewModel.ShortenFilePathsDeterministically(hostile);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Formatting took {stopwatch.Elapsed}.");
        Assert.Equal("\"Target.cs).", item.DisplayTitle);
        Assert.Equal("\"Target.cs).", fallback);
        Assert.True(item.DisplayTitle.Length <= 323);
    }

    [Theory]
    [InlineData(
        "Processing \"C:\\deep\\source\\Main.cs\", \\\\server\\share\\settings.json; /opt/ali/run.py! src/UI/App.xaml.cs?",
        "Processing \"Main.cs\", settings.json; run.py! App.xaml.cs?")]
    [InlineData(
        "Reading `C:/mixed\\separators/deep/Worker.cs` and './relative/path/config.json'.",
        "Reading `Worker.cs` and 'config.json'.")]
    [InlineData(
        "Copied C:\\Project One\\source to D:\\Backup Sets\\destination",
        "Copied source to destination")]
    public void DeterministicPathFallback_HandlesPathKindsQuotesPunctuationAndMultiplePaths(
        string source,
        string expected)
    {
        var display = AgentActivityItemViewModel.ShortenFilePathsDeterministically(source);

        Assert.Equal(expected, display);
    }

    [Fact]
    public void DeterministicPathFallback_LeavesMixedHttpUrlsUnchanged()
    {
        const string source =
            "Checking https://example.com/src/server.cs?next=/api/data.json then src/UI/Main.cs and http://127.0.0.1:1234/a/b.json.";

        var display = AgentActivityItemViewModel.ShortenFilePathsDeterministically(source);

        Assert.Equal(
            "Checking https://example.com/src/server.cs?next=/api/data.json then Main.cs and http://127.0.0.1:1234/a/b.json.",
            display);
    }

    private static AssistantStreamChunk CreateChunk(
        string text,
        AgentActivityKind kind,
        string? detail = null,
        AgentToolApprovalPrompt? approvalPrompt = null,
        AgentToolExecutionReceipt? receipt = null) =>
        new(
            "conversation",
            "user",
            "assistant",
            text,
            EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: kind,
            ActivityDetail: detail,
            ApprovalPrompt: approvalPrompt,
            ExecutionReceipt: receipt);
}
