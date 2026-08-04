using Ali.Modules.Coding;
using Ali.Modules.Coding.Engineering;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Keeps only the mechanical proof needed to prevent the fast core path from
/// reporting a C# operation as finished after its own typed result says it failed.
/// This state is turn-local and memory-only; it performs no I/O and makes no
/// decision from user prose.
/// </summary>
internal sealed class CoreAssistantCompletionGate
{
    private readonly Dictionary<string, string> _pendingCalls = new(StringComparer.Ordinal);
    private long _sourceRevision;
    private long _passingBuildRevision = -1;
    private long _passingRunRevision = -1;
    private bool _runAttempted;
    private DotNetCreateProjectResult? _latestCreate;
    private DotNetBuildResult? _latestBuild;
    private DotNetTestResult? _latestTest;
    private DotNetRunResult? _latestRun;
    private DotNetStopProjectResult? _latestStop;

    internal void Track(FunctionCallContent functionCall)
    {
        ArgumentNullException.ThrowIfNull(functionCall);
        if (!string.IsNullOrWhiteSpace(functionCall.CallId)
            && !string.IsNullOrWhiteSpace(functionCall.Name))
        {
            _pendingCalls[functionCall.CallId] = functionCall.Name;
        }
    }

    internal void Observe(FunctionResultContent functionResult)
    {
        ArgumentNullException.ThrowIfNull(functionResult);
        if (!_pendingCalls.Remove(functionResult.CallId, out var toolName))
        {
            return;
        }

        if (functionResult.Exception is not null)
        {
            ObserveThrownTool(toolName, functionResult.Exception);
            return;
        }

        switch (toolName)
        {
            case AliCapabilityCatalog.DotNetCreateProjectName:
                if (AliProductionToolOutcomeRegistry.TryReadTypedReturn<DotNetCreateProjectResult>(
                        functionResult.Result,
                        out var create))
                {
                    _latestCreate = create;
                    if (create.Success)
                    {
                        MarkSourceChanged();
                    }
                }
                break;

            case AliCapabilityCatalog.RoslynFormatProjectName:
                if (AliProductionToolOutcomeRegistry.TryReadTypedReturn<RoslynFormatResult>(
                        functionResult.Result,
                        out var format)
                    && format.Success
                    && format.ChangedFiles.Count > 0)
                {
                    MarkSourceChanged();
                }
                break;

            case AliCapabilityCatalog.DotNetBuildName:
                if (AliProductionToolOutcomeRegistry.TryReadTypedReturn<DotNetBuildResult>(
                        functionResult.Result,
                        out var build))
                {
                    _latestBuild = build;
                    _passingBuildRevision = build.Success ? _sourceRevision : -1;
                }
                break;

            case AliCapabilityCatalog.DotNetTestName:
                if (AliProductionToolOutcomeRegistry.TryReadTypedReturn<DotNetTestResult>(
                        functionResult.Result,
                        out var test))
                {
                    _latestTest = test;
                }
                break;

            case AliCapabilityCatalog.DotNetRunName:
                _runAttempted = true;
                if (AliProductionToolOutcomeRegistry.TryReadTypedReturn<DotNetRunResult>(
                        functionResult.Result,
                        out var run))
                {
                    _latestRun = run;
                    _passingRunRevision = run.Success ? _sourceRevision : -1;
                }
                break;

            case AliCapabilityCatalog.DotNetStopProjectName:
                if (AliProductionToolOutcomeRegistry.TryReadTypedReturn<DotNetStopProjectResult>(
                        functionResult.Result,
                        out var stop))
                {
                    _latestStop = stop;
                    if (stop.Success && _runAttempted)
                    {
                        _passingRunRevision = -1;
                    }
                }
                break;

            case AliCapabilityCatalog.FileWriteName:
            case AliCapabilityCatalog.FileDeleteName:
            case AliCapabilityCatalog.FileReplaceName:
            case AliCapabilityCatalog.FileReplaceLinesName:
                // The provider boundary owns exact file validation. Once it returns,
                // conservatively require a fresh build before claiming C# completion.
                MarkSourceChanged();
                break;
        }
    }

    internal bool TryGetBlocker(out CoreAssistantCompletionBlocker blocker)
    {
        if (_pendingCalls.Count > 0)
        {
            var unresolvedTools = _pendingCalls.Values
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var unresolvedMutation = unresolvedTools.Any(IsSourceMutationTool);
            _pendingCalls.Clear();
            if (unresolvedMutation)
            {
                // A file provider may have performed some or all of the mutation before
                // losing its terminal result. Treat the live tree as changed so the
                // continuation must inspect and build what is actually on disk.
                MarkSourceChanged();
            }

            var toolList = string.Join(", ", unresolvedTools);
            blocker = Missing(
                "tool-result-missing",
                $"The tool call did not return a terminal result: {toolList}. Inspect the live Workspace state, retry or recover the exact operation, and continue the requested work. Do not answer or give up while this operation is unresolved.",
                $"I could not verify completion because these tool calls returned no terminal result: {toolList}.");
            return true;
        }

        if (_latestCreate is { Success: false } failedCreate)
        {
            blocker = Failed(
                "create-failed",
                failedCreate.Summary,
                "The Workspace project creation failed. Inspect the exact tool result, repair the cause, create the project successfully, then build it before answering.",
                "I could not complete the requested Workspace project creation.");
            return true;
        }

        if (_latestBuild is { Success: false } failedBuild)
        {
            blocker = Failed(
                "build-failed",
                failedBuild.Summary,
                "The latest Workspace build failed. Inspect the exact build result, repair the source with the available Workspace and Roslyn tools, and build again before answering.",
                "I could not complete the requested Workspace build.");
            return true;
        }

        if (_sourceRevision > 0 && _passingBuildRevision != _sourceRevision)
        {
            blocker = Missing(
                "build-missing-or-stale",
                "The Workspace source is not covered by a successful current build. Build the project now; if it fails, repair it and keep rebuilding until it succeeds or an exact tool result proves the obstacle.",
                "I did not verify the current Workspace source because no successful build covered the final source revision.");
            return true;
        }

        if (_latestTest is { Success: false } failedTest)
        {
            blocker = Failed(
                "test-failed",
                failedTest.Summary,
                "The latest test run failed. Repair the exact failures and run the tests again before answering.",
                "I could not complete the requested verification because the latest test run failed.");
            return true;
        }

        if (_latestRun is { Success: false } failedRun)
        {
            blocker = Failed(
                "run-failed",
                failedRun.Summary,
                "The Workspace application did not launch. Inspect the exact run result, repair or rebuild as required, and call dotnet_run_project again before answering.",
                "I built the Workspace application but could not launch it.");
            return true;
        }

        if (_sourceRevision > 0 && _passingRunRevision != _sourceRevision)
        {
            blocker = Missing(
                "run-missing-or-stale",
                "The final Workspace source is not covered by a successful launch, or the application was stopped during repair. Call dotnet_run_project on the final successful build and use its exact result before answering.",
                "I did not verify that the final Workspace application launched after the last repair.");
            return true;
        }

        if (_latestStop is { Success: false } failedStop)
        {
            blocker = Failed(
                "stop-failed",
                failedStop.Summary,
                "The Workspace application did not stop. Inspect the exact stop result and call dotnet_stop_project again if it remains running.",
                "I could not stop the requested Workspace application.");
            return true;
        }

        blocker = default;
        return false;
    }

    private void MarkSourceChanged() => _sourceRevision++;

    private static bool IsSourceMutationTool(string toolName) =>
        toolName is AliCapabilityCatalog.FileWriteName
            or AliCapabilityCatalog.FileDeleteName
            or AliCapabilityCatalog.FileReplaceName
            or AliCapabilityCatalog.FileReplaceLinesName;

    private void ObserveThrownTool(string toolName, Exception exception)
    {
        var summary = $"{exception.GetType().Name} occurred while the tool was running.";
        switch (toolName)
        {
            case AliCapabilityCatalog.DotNetCreateProjectName:
                _latestCreate = new DotNetCreateProjectResult(
                    false, string.Empty, string.Empty, null, summary, string.Empty, 0);
                break;
            case AliCapabilityCatalog.DotNetBuildName:
                _latestBuild = new DotNetBuildResult(
                    false, string.Empty, string.Empty, null, summary, string.Empty, null, 0);
                _passingBuildRevision = -1;
                break;
            case AliCapabilityCatalog.DotNetTestName:
                _latestTest = new DotNetTestResult(
                    false, string.Empty, string.Empty, 0, 0, 0, 0, summary, [], null, string.Empty, 0, false);
                break;
            case AliCapabilityCatalog.DotNetRunName:
                _runAttempted = true;
                _passingRunRevision = -1;
                _latestRun = new DotNetRunResult(false, string.Empty, summary, null, null);
                break;
            case AliCapabilityCatalog.DotNetStopProjectName:
                _latestStop = new DotNetStopProjectResult(false, string.Empty, summary, null, null, false);
                break;
        }
    }

    private CoreAssistantCompletionBlocker Failed(
        string code,
        string detail,
        string continuation,
        string truthfulAnswer) =>
        new(
            Fingerprint(code, detail),
            continuation,
            $"{truthfulAnswer} {Bound(detail)}".Trim());

    private CoreAssistantCompletionBlocker Missing(
        string code,
        string continuation,
        string truthfulAnswer) =>
        new(Fingerprint(code, string.Empty), continuation, truthfulAnswer);

    private string Fingerprint(string code, string detail) =>
        string.Join(
            "|",
            code,
            _sourceRevision,
            _passingBuildRevision,
            _passingRunRevision,
            _latestCreate?.Success,
            _latestBuild?.Success,
            _latestTest?.Success,
            _latestRun?.Success,
            _latestStop?.Success,
            Bound(detail));

    private static string Bound(string? value)
    {
        const int maximumCharacters = 1_000;
        var normalized = string.Join(
            " ",
            (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters];
    }
}

internal readonly record struct CoreAssistantCompletionBlocker(
    string Fingerprint,
    string ContinuationInstruction,
    string TruthfulFailureAnswer);
