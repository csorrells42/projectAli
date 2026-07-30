using Ali.Modules.Coordinator;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;
using Microsoft.Extensions.AI;

namespace Ali.Framework.Tests;

public sealed class RuntimeRequestSafetyTests
{
    [Fact]
    public void TokenBudget_PreservesRequestedOutputWhenTheTurnFits()
    {
        var budget = ModelRequestTokenBudgetCalculator.Calculate(
            32_768,
            8_192,
            ["A short ordinary request."],
            [],
            messageCount: 1,
            toolCount: 0,
            imageCount: 0);

        Assert.Equal(8_192, budget.EffectiveOutputTokens);
        Assert.False(budget.WasClamped);
    }

    [Fact]
    public void TokenBudget_ClampsOnlyTheOutputAllowanceWhenTheTurnNeedsHeadroom()
    {
        var budget = ModelRequestTokenBudgetCalculator.Calculate(
            4_096,
            2_048,
            [new string('x', 6_000)],
            [],
            messageCount: 2,
            toolCount: 0,
            imageCount: 0);

        Assert.InRange(budget.EffectiveOutputTokens, 128, 2_047);
        Assert.True(budget.WasClamped);
        Assert.True(
            budget.EstimatedInputTokens + budget.SafetyReserveTokens + budget.EffectiveOutputTokens
            <= budget.ContextTokens);
    }

    [Fact]
    public void TokenBudget_RejectsAnInputThatCannotFitBeforeCallingTheModel()
    {
        var error = Assert.Throws<ModelContextCapacityException>(() =>
            ModelRequestTokenBudgetCalculator.Calculate(
                4_096,
                1_024,
                [new string('x', 20_000)],
                [],
                messageCount: 2,
                toolCount: 0,
                imageCount: 0));

        Assert.Contains("No request was sent to the model", error.Message, StringComparison.Ordinal);
        Assert.Contains("larger context", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivityHeadline_DoesNotLeakRawToolArgumentsOrResults()
    {
        var activity = new List<AssistantStreamChunk>();
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Build a chess game.",
            activity.Add);
        turn.RegisterToolPlan(new CoordinatorToolPlan(
            "call-write",
            "file_access_write",
            "the chess board is incomplete",
            "write the board and legal move logic",
            "read the final source and verify the requested behavior",
            "Ali sees: the chess board is incomplete -> chose file access write -> plan: write the board and legal move logic",
            "File access write returned -> Ali next: read the final source and verify the requested behavior",
            "{\"content\":\"\\u003Chtml\\u003E...\"}"));
        var inner = AIFunctionFactory.Create(
            (string content) => new { success = true, content },
            "file_access_write",
            "Write a file.");
        var wrapped = new ActivityReportingAIFunction(inner, () => turn);

        await wrapped.InvokeAsync(
            new AIFunctionArguments { ["content"] = "<html>chess</html>" },
            TestContext.Current.CancellationToken);

        Assert.Contains(activity, item =>
            item.IsActivity
            && item.Text.Contains("chose file access write", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(activity, item =>
            (item.ActivityDetail?.Contains("Arguments:", StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.ActivityDetail?.Contains("\\u003C", StringComparison.Ordinal) ?? false)
            || (item.ActivityDetail?.Contains("<html>", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public void ActivityView_FlattensMultilineTextAndHidesStructuredPayloads()
    {
        var item = new AgentActivityItemViewModel(new AssistantStreamChunk(
            "conversation",
            "user",
            "assistant",
            "Ali chose a tool\nfor the next step",
            Ali.Modules.Evidence.EvidenceStatus.Unknown,
            IsActivity: true,
            ActivityKind: AgentActivityKind.ToolCall,
            ActivityDetail: "{\"content\":\"\\u003Chtml\\u003E\"}"));

        Assert.Equal("Ali chose a tool for the next step", item.Title);
        Assert.Equal("Technical payload omitted from the human activity view.", item.Detail);
    }
}
