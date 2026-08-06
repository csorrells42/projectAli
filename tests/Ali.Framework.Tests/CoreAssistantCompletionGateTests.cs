using Ali.Modules.Coding;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Ali.Framework.Tests;

public sealed class CoreAssistantCompletionGateTests
{
    [Fact]
    public void FailedBuild_BlocksSuccessfulCompletion()
    {
        var gate = CreateGate();

        Observe(
            gate,
            "build-1",
            AliCapabilityCatalog.DotNetBuildName,
            JsonSerializer.SerializeToElement(Build(false, "CS1002: ; expected")));

        Assert.True(gate.TryGetBlocker(out var blocker));
        Assert.Contains("build failed", blocker.ContinuationInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS1002", blocker.TruthfulFailureAnswer, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceRepairThenPassingBuild_AllowsCompletion()
    {
        var gate = CreateGate();
        Observe(gate, "build-1", AliCapabilityCatalog.DotNetBuildName, Build(false, "compile failed"));
        Observe(gate, "write-1", AliCapabilityCatalog.FileWriteName, "File written.");
        Observe(
            gate,
            "build-2",
            AliCapabilityCatalog.DotNetBuildName,
            JsonSerializer.SerializeToElement(Build(true, "Build succeeded.")));

        Assert.False(gate.TryGetBlocker(out _));
    }

    [Fact]
    public void SourceChangeAfterPassingBuild_RequiresFreshBuild()
    {
        var gate = CreateGate();
        Observe(gate, "write-1", AliCapabilityCatalog.FileWriteName, SourceWriteResult(), SourceFileArguments());
        Observe(gate, "build-1", AliCapabilityCatalog.DotNetBuildName, Build(true, "Build succeeded."));

        // A passing build alone does not clear completion: by design the gate also
        // requires a fresh launch after any edit. AliMinimumMessage deliberately
        // does not enforce this specific sub-check live (see its SkippedBlockerCode)
        // to avoid forcing a run nobody asked for, but the raw gate still reports it.
        Assert.True(gate.TryGetBlocker(out var afterBuildOnly));
        Assert.Equal("run-missing-or-stale", afterBuildOnly.Code);

        Observe(gate, "write-2", AliCapabilityCatalog.FileReplaceName, SourceWriteResult(), SourceFileArguments());

        Assert.True(gate.TryGetBlocker(out var blocker));
        Assert.Contains("current build", blocker.ContinuationInstruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaterFailedBuild_OverridesEarlierPassingBuild()
    {
        var gate = CreateGate();
        Observe(gate, "build-1", AliCapabilityCatalog.DotNetBuildName, Build(true, "Build succeeded."));
        Observe(gate, "build-2", AliCapabilityCatalog.DotNetBuildName, Build(false, "Build failed."));

        Assert.True(gate.TryGetBlocker(out _));
    }

    [Fact]
    public void OrdinaryChatAndReadOnlyAnalysis_AreNotBlocked()
    {
        var chatGate = CreateGate();
        var analysisGate = CreateGate();
        Observe(
            analysisGate,
            "analysis-1",
            AliCapabilityCatalog.RoslynAnalyzeProjectName,
            new RoslynAnalysisResult(true, "App.csproj", "No errors.", 1, [], []));

        Assert.False(chatGate.TryGetBlocker(out _));
        Assert.False(analysisGate.TryGetBlocker(out _));
    }

    [Fact]
    public void FailedLaunch_BlocksUntilSuccessfulRunResult()
    {
        var gate = CreateGate();
        Observe(
            gate,
            "run-1",
            AliCapabilityCatalog.DotNetRunName,
            JsonSerializer.SerializeToElement(
                new DotNetRunResult(false, "App.csproj", "Launch failed.", null, null)));
        Assert.True(gate.TryGetBlocker(out var failed));
        Assert.Contains("dotnet_run_project", failed.ContinuationInstruction, StringComparison.Ordinal);

        Observe(
            gate,
            "run-2",
            AliCapabilityCatalog.DotNetRunName,
            JsonSerializer.SerializeToElement(
                new DotNetRunResult(true, "App.csproj", "Started.", "App.exe", 42)));

        Assert.False(gate.TryGetBlocker(out _));
    }

    [Fact]
    public void SourceChangeAfterSuccessfulRun_RequiresFreshBuildAndLaunch()
    {
        var gate = CreateGate();
        Observe(gate, "write-1", AliCapabilityCatalog.FileWriteName, SourceWriteResult(), SourceFileArguments());
        Observe(gate, "build-1", AliCapabilityCatalog.DotNetBuildName, Build(true, "Build succeeded."));
        Observe(
            gate,
            "run-1",
            AliCapabilityCatalog.DotNetRunName,
            JsonSerializer.SerializeToElement(
                new DotNetRunResult(true, "App.csproj", "Started.", "App.exe", 42)));
        Assert.False(gate.TryGetBlocker(out _));

        Observe(gate, "write-2", AliCapabilityCatalog.FileReplaceName, SourceWriteResult(), SourceFileArguments());
        Observe(gate, "build-2", AliCapabilityCatalog.DotNetBuildName, Build(true, "Build succeeded."));

        Assert.True(gate.TryGetBlocker(out var blocker));
        Assert.Contains("dotnet_run_project", blocker.ContinuationInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public void StopAfterSuccessfulRun_RequiresRelaunchBeforeCompletion()
    {
        var gate = CreateGate();
        Observe(
            gate,
            "run-1",
            AliCapabilityCatalog.DotNetRunName,
            JsonSerializer.SerializeToElement(
                new DotNetRunResult(true, "App.csproj", "Started.", "App.exe", 42)));
        Observe(
            gate,
            "stop-1",
            AliCapabilityCatalog.DotNetStopProjectName,
            JsonSerializer.SerializeToElement(
                new DotNetStopProjectResult(true, "App.csproj", "Stopped.", "App.exe", 42, false)));

        Assert.True(gate.TryGetBlocker(out var blocker));
        Assert.Contains("dotnet_run_project", blocker.ContinuationInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedBlockerFingerprint_StaysStableUntilNewEvidence()
    {
        var gate = CreateGate();
        Observe(gate, "build-1", AliCapabilityCatalog.DotNetBuildName, Build(false, "same failure"));
        Assert.True(gate.TryGetBlocker(out var first));
        Assert.True(gate.TryGetBlocker(out var second));
        Assert.Equal(first.Fingerprint, second.Fingerprint);

        Observe(gate, "write-1", AliCapabilityCatalog.FileWriteName, SourceWriteResult(), SourceFileArguments());
        Assert.True(gate.TryGetBlocker(out var afterProgress));
        Assert.NotEqual(first.Fingerprint, afterProgress.Fingerprint);
    }

    private static CoreAssistantCompletionGate CreateGate() => new();

    private static void Observe(
        CoreAssistantCompletionGate gate,
        string callId,
        string toolName,
        object result,
        IDictionary<string, object?>? arguments = null)
    {
        gate.Track(new FunctionCallContent(callId, toolName, arguments));
        gate.Observe(new FunctionResultContent(callId, result));
    }

    private static IDictionary<string, object?> SourceFileArguments(string fileName = "Program.cs") =>
        new Dictionary<string, object?> { ["fileName"] = fileName };

    private static JsonElement SourceWriteResult(string fileName = "Program.cs") =>
        JsonSerializer.SerializeToElement(
            new McpSourceFileResult(true, fileName, "File written successfully."));

    private static DotNetBuildResult Build(bool success, string summary) =>
        new(
            success,
            "App.csproj",
            "Release",
            success ? 0 : 1,
            summary,
            summary,
            success ? "App.exe" : null,
            10,
            WarningCount: 0,
            ErrorCount: success ? 0 : 1);
}
