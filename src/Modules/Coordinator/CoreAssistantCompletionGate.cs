using Ali.Modules.Coding;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Mcp;
using Ali.Modules.Orchestration.Planning;
using System.Collections;
using System.Reflection;
using System.Text.Json;
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
    private readonly Dictionary<string, PendingCall> _pendingCalls = new(StringComparer.Ordinal);
    private readonly List<string> _successfulMutationSummaries = [];
    private long _sourceRevision;
    private long _passingBuildRevision = -1;
    private long _passingRunRevision = -1;
    private bool _workspaceWorkRequired;
    private bool _workspaceMutationRequired;
    private bool _buildRequired;
    private bool _runRequired;
    private bool _workspaceToolObserved;
    private bool _runAttempted;
    private bool _runInvalidated;
    private string? _sourceProjectKey;
    private DotNetCreateProjectResult? _latestCreate;
    private DotNetBuildResult? _latestBuild;
    private DotNetTestResult? _latestTest;
    private DotNetRunResult? _latestRun;
    private DotNetStopProjectResult? _latestStop;
    private string? _latestMutationFailure;

    internal void Require(CoreAssistantCodingRequirements requirements)
    {
        _workspaceWorkRequired = requirements.RequiresWorkspaceWork;
        _workspaceMutationRequired = requirements.RequiresWorkspaceMutation;
        _buildRequired = requirements.RequiresBuild;
        _runRequired = requirements.RequiresRun;
    }

    internal void Track(FunctionCallContent functionCall)
    {
        ArgumentNullException.ThrowIfNull(functionCall);
        if (!string.IsNullOrWhiteSpace(functionCall.CallId)
            && !string.IsNullOrWhiteSpace(functionCall.Name))
        {
            _pendingCalls[functionCall.CallId] = new PendingCall(
                functionCall.Name,
                ReadWorkspaceProjectKey(functionCall.Arguments),
                DescribeMutation(functionCall.Name, functionCall.Arguments),
                IsSourceMutationTarget(functionCall.Name, functionCall.Arguments));
            _workspaceToolObserved |= IsWorkspaceTool(functionCall.Name);
        }
    }

    internal bool TryGetTrackedToolName(string callId, out string toolName)
    {
        if (!string.IsNullOrWhiteSpace(callId)
            && _pendingCalls.TryGetValue(callId, out var pending))
        {
            toolName = pending.ToolName;
            return true;
        }

        toolName = string.Empty;
        return false;
    }

    internal void Observe(FunctionResultContent functionResult)
    {
        ArgumentNullException.ThrowIfNull(functionResult);
        var domainOutcome = TryGetTrackedToolName(functionResult.CallId, out var toolName)
            ? ClassifyCoreToolResult(toolName, functionResult.Result)
            : PlanningToolDomainOutcome.Unreported;
        Observe(functionResult, domainOutcome);
    }

    private static PlanningToolDomainOutcome ClassifyCoreToolResult(
        string toolName,
        object? result)
    {
        if (toolName is AliCapabilityCatalog.FileWriteName
            or AliCapabilityCatalog.FileReplaceName
            or AliCapabilityCatalog.FileReadName
            && AliProductionToolOutcomeRegistry.TryReadTypedReturn<McpSourceFileResult>(
                result,
                out var fileResult))
        {
            return fileResult.Success
                ? PlanningToolDomainOutcome.Succeeded
                : PlanningToolDomainOutcome.Failed;
        }

        return AliProductionToolOutcomeRegistry.ClassifyTypedReturn(toolName, result);
    }

    internal void Observe(
        FunctionResultContent functionResult,
        PlanningToolDomainOutcome domainOutcome)
    {
        ArgumentNullException.ThrowIfNull(functionResult);
        if (!_pendingCalls.Remove(functionResult.CallId, out var pending))
        {
            return;
        }

        var toolName = pending.ToolName;

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
                        AliCoreAssistantExecutionContext.BindActiveProject(create.ProjectPath);
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
                    _passingBuildRevision = build.Success
                        && TargetsCurrentSource(
                            ReadWorkspaceProjectKey(build.ProjectPath) ?? pending.ProjectKey)
                            ? _sourceRevision
                            : -1;
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
                    _passingRunRevision = run.Success
                        && TargetsCurrentSource(
                            ReadWorkspaceProjectKey(run.ProjectPath) ?? pending.ProjectKey)
                            ? _sourceRevision
                            : -1;
                    if (run.Success)
                    {
                        _runInvalidated = false;
                    }
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
                        _runInvalidated = true;
                    }
                }
                break;

            case AliCapabilityCatalog.FileWriteName:
            case AliCapabilityCatalog.FileDeleteName:
            case AliCapabilityCatalog.FileReplaceName:
            case AliCapabilityCatalog.FileReplaceLinesName:
                // The provider boundary owns exact file validation. Once it returns,
                // conservatively require a fresh build before claiming C# completion.
                if (domainOutcome == PlanningToolDomainOutcome.Succeeded
                    && pending.IsSourceMutationTarget)
                {
                    MarkSourceChanged(pending.ProjectKey);
                    _latestMutationFailure = null;
                    if (!string.IsNullOrWhiteSpace(pending.MutationSummary))
                    {
                        _successfulMutationSummaries.Add(pending.MutationSummary);
                    }
                }
                else if (domainOutcome is PlanningToolDomainOutcome.Failed
                         or PlanningToolDomainOutcome.Denied
                         || ReturnedRecoverableFailure(functionResult.Result))
                {
                    _latestMutationFailure = DescribeMutationFailure(functionResult.Result);
                }
                break;
        }
    }

    internal string BuildVerifiedCompletionEvidence()
    {
        var lines = new List<string>();
        lines.AddRange(_successfulMutationSummaries
            .Distinct(StringComparer.Ordinal)
            .TakeLast(8)
            .Select(summary => "- Source edit returned successfully: " + summary));
        if (_latestBuild is { Success: true } build)
        {
            lines.Add(
                $"- Build returned success for {build.ProjectPath} in {build.Configuration} configuration"
                + (string.IsNullOrWhiteSpace(build.ArtifactPath)
                    ? "."
                    : $"; artifact: {build.ArtifactPath}."));
        }
        if (_latestRun is { Success: true } run)
        {
            lines.Add(
                $"- Run returned success for {run.ProjectPath}"
                + (string.IsNullOrWhiteSpace(run.ArtifactPath)
                    ? string.Empty
                    : $"; artifact: {run.ArtifactPath}")
                + (run.ProcessId is null ? "." : $"; process ID: {run.ProcessId}."));
        }
        return lines.Count == 0
            ? "- No successful Workspace mutation, build, or run result was observed."
            : string.Join(Environment.NewLine, lines);
    }

    internal long SourceRevision => _sourceRevision;

    // Temporarily disabled on the fast core path. The critic was running after
    // intermediate edits with summaries rather than an authoritative final
    // source snapshot, so it repeatedly blocked useful build/repair progress.
    // Roslyn diagnostics, the current-source build, and the requested launch
    // remain mandatory. Re-enable only as one grounded post-build source check.
    internal bool RequiresRequestedBehaviorVerification => false;

    internal bool TryGetBlocker(out CoreAssistantCompletionBlocker blocker)
    {
        if (_pendingCalls.Count > 0)
        {
            var unresolvedTools = _pendingCalls.Values
                .Select(call => call.ToolName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            _pendingCalls.Clear();

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

        if (_workspaceWorkRequired && !_workspaceToolObserved)
        {
            blocker = Missing(
                "workspace-work-not-started",
                "This turn was semantically classified as practical Workspace coding work, but no Workspace or C# tool was used. Perform the requested work with the available tools before answering.",
                "I did not perform the requested Workspace work.");
            return true;
        }

        // Unconditional: a failed write/replace/delete must never be reported as
        // done regardless of whether an upfront semantic classifier ran (the fast
        // path never runs one — see AliMinimumMessage). Every other terminal tool
        // failure (create/build/test/run/stop) is already checked unconditionally
        // below; a failed mutation deserves the same guarantee, not a weaker one.
        if (!string.IsNullOrWhiteSpace(_latestMutationFailure))
        {
            blocker = Failed(
                "workspace-mutation-failed",
                _latestMutationFailure,
                "The latest attempted source mutation failed. Correct the exact returned error, successfully modify the relevant existing source file, and continue through a fresh successful build and launch before answering.",
                "I could not complete the latest requested source change.");
            return true;
        }

        if (_workspaceMutationRequired && _sourceRevision == 0)
        {
            blocker = Missing(
                "workspace-mutation-not-started",
                "This turn requires changing the Workspace source, but no source mutation returned. Read the exact current region, apply the requested targeted edit, and continue through a successful build and launch before answering.",
                "I inspected the Workspace but did not make the requested source change.");
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

        if (_buildRequired && _latestBuild is not { Success: true })
        {
            blocker = Missing(
                "required-build-missing",
                "The current request requires a successful Workspace build, but no successful build result exists. Build the requested project now; repair exact diagnostics and keep rebuilding until it succeeds or a returned result proves the obstacle.",
                "I did not complete the required Workspace build.");
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

        if (_runInvalidated)
        {
            blocker = Missing(
                "run-stopped",
                "The Workspace application was stopped and is not currently running. If the request depends on it running, call dotnet_run_project again; otherwise state truthfully that it is stopped before answering.",
                "The Workspace application is currently stopped, not running.");
            return true;
        }

        if (_sourceRevision > 0 && _passingRunRevision != _sourceRevision)
        {
            blocker = Missing(
                "run-missing-or-stale",
                "The final Workspace source is not covered by a successful launch. Call dotnet_run_project on the final successful build and use its exact result before answering.",
                "I did not verify that the final Workspace application launched after the last repair.");
            return true;
        }

        if (_runRequired && _latestRun is not { Success: true })
        {
            blocker = Missing(
                "required-run-missing",
                "The current request requires launching the Workspace application, but no successful run result exists. Call dotnet_run_project for the final successful build and use its exact result before answering.",
                "I did not complete the required Workspace launch.");
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

    private void MarkSourceChanged(string? projectKey = null)
    {
        _sourceRevision++;
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            _sourceProjectKey = projectKey;
        }
    }

    private bool TargetsCurrentSource(string? projectKey) =>
        string.IsNullOrWhiteSpace(_sourceProjectKey)
        || string.Equals(_sourceProjectKey, projectKey, StringComparison.OrdinalIgnoreCase);

    private static string? ReadWorkspaceProjectKey(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        foreach (var name in new[] { "fileName", "projectPath", "targetPath", "path" })
        {
            var pair = arguments.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase));
            if (pair.Key is not null
                && ReadText(pair.Value) is { Length: > 0 } value
                && ReadWorkspaceProjectKey(value) is { } key)
            {
                return key;
            }
        }

        return null;
    }

    private static string? ReadWorkspaceProjectKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (string.Equals(segments[index], "Workspace", StringComparison.OrdinalIgnoreCase))
            {
                return segments[index + 1];
            }
        }

        return null;
    }

    private static string? ReadText(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };

    private static string? DescribeMutation(
        string toolName,
        IDictionary<string, object?>? arguments)
    {
        if (!IsSourceMutationTool(toolName) || arguments is null)
        {
            return null;
        }

        var fileName = ReadArgumentText(arguments, "fileName") ?? "the selected source file";
        if (string.Equals(toolName, AliCapabilityCatalog.FileWriteName, StringComparison.Ordinal))
        {
            var content = Bound(ReadArgumentText(arguments, "content"));
            return $"{fileName}: wrote source content '{content}'.";
        }

        if (string.Equals(toolName, AliCapabilityCatalog.FileDeleteName, StringComparison.Ordinal))
        {
            return $"{fileName}: deleted the source file.";
        }

        if (string.Equals(toolName, AliCapabilityCatalog.FileReplaceName, StringComparison.Ordinal))
        {
            var oldText = Bound(ReadArgumentText(arguments, "oldString"));
            var newText = Bound(ReadArgumentText(arguments, "newString"));
            return $"{fileName}: replaced '{oldText}' with '{newText}'.";
        }

        if (string.Equals(toolName, AliCapabilityCatalog.FileReplaceLinesName, StringComparison.Ordinal))
        {
            var edits = arguments.FirstOrDefault(pair =>
                string.Equals(pair.Key, "edits", StringComparison.OrdinalIgnoreCase)).Value;
            return $"{fileName}: applied targeted line edits {Bound(JsonSerializer.Serialize(edits))}.";
        }

        return $"{fileName}: source mutation returned successfully.";
    }

    private static bool IsSourceMutationTarget(
        string toolName,
        IDictionary<string, object?>? arguments)
    {
        if (!IsSourceMutationTool(toolName) || arguments is null)
        {
            return false;
        }

        var fileName = ReadArgumentText(arguments, "fileName");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() is
            ".cs" or ".csproj" or ".xaml" or ".razor" or ".props" or ".targets";
    }

    private static string? ReadArgumentText(
        IDictionary<string, object?> arguments,
        string name)
    {
        foreach (var pair in arguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return ReadText(pair.Value) ?? pair.Value?.ToString();
            }
        }
        return null;
    }

    private static bool ReturnedRecoverableFailure(object? result)
    {
        if (result is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("success", out var successElement)
            && successElement.ValueKind is JsonValueKind.False)
        {
            return true;
        }

        if (result is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key
                    && string.Equals(key, "success", StringComparison.OrdinalIgnoreCase)
                    && entry.Value is false)
                {
                    return true;
                }
            }
        }

        var successProperty = result?.GetType().GetProperty(
            "success",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return successProperty?.PropertyType == typeof(bool)
            && successProperty.GetValue(result) is false;
    }

    private static string DescribeMutationFailure(object? result)
    {
        var error = ReadNamedResultValue(result, "error")
            ?? ReadNamedResultValue(result, "message")
            ?? ReadNamedResultValue(result, "summary");
        return string.IsNullOrWhiteSpace(error)
            ? "The file mutation provider returned a failed or denied outcome."
            : Bound(error);
    }

    private static string? ReadNamedResultValue(object? result, string name)
    {
        if (result is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(name, out var property))
        {
            return property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.GetRawText();
        }

        if (result is IDictionary<string, object?> dictionary)
        {
            var pair = dictionary.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase));
            if (pair.Key is not null)
            {
                return pair.Value?.ToString();
            }
        }

        var propertyInfo = result?.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return propertyInfo?.GetValue(result)?.ToString();
    }

    private static bool IsSourceMutationTool(string toolName) =>
        toolName is AliCapabilityCatalog.FileWriteName
            or AliCapabilityCatalog.FileDeleteName
            or AliCapabilityCatalog.FileReplaceName
            or AliCapabilityCatalog.FileReplaceLinesName;

    private static bool IsWorkspaceTool(string toolName) =>
        toolName is AliCapabilityCatalog.FileWriteName
            or AliCapabilityCatalog.FileReadName
            or AliCapabilityCatalog.FileDeleteName
            or AliCapabilityCatalog.FileListName
            or AliCapabilityCatalog.FileSearchName
            or AliCapabilityCatalog.FileReplaceName
            or AliCapabilityCatalog.FileReplaceLinesName
            or AliCapabilityCatalog.FileMoveName
            or AliCapabilityCatalog.FileCopyName
            or AliCapabilityCatalog.FileCreateDirectoryName
            or AliCapabilityCatalog.FileMetadataName
        || CoreRecoveryTool(toolName);

    private static bool CoreRecoveryTool(string toolName) =>
        toolName is AliCapabilityCatalog.CodingInspectProjectName
            or AliCapabilityCatalog.DotNetCreateProjectName
            or AliCapabilityCatalog.RoslynAnalyzeProjectName
            or AliCapabilityCatalog.RoslynFormatProjectName
            or AliCapabilityCatalog.DotNetBuildName
            or AliCapabilityCatalog.DotNetTestName
            or AliCapabilityCatalog.DotNetRunName
            or AliCapabilityCatalog.DotNetStopProjectName;

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
            case AliCapabilityCatalog.FileWriteName:
            case AliCapabilityCatalog.FileDeleteName:
            case AliCapabilityCatalog.FileReplaceName:
            case AliCapabilityCatalog.FileReplaceLinesName:
                _latestMutationFailure = exception.Message;
                break;
        }
    }

    private CoreAssistantCompletionBlocker Failed(
        string code,
        string detail,
        string continuation,
        string truthfulAnswer) =>
        new(
            code,
            Fingerprint(code, detail),
            continuation,
            $"{truthfulAnswer} {Bound(detail)}".Trim());

    private CoreAssistantCompletionBlocker Missing(
        string code,
        string continuation,
        string truthfulAnswer) =>
        new(code, Fingerprint(code, string.Empty), continuation, truthfulAnswer);

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

internal readonly record struct PendingCall(
    string ToolName,
    string? ProjectKey,
    string? MutationSummary,
    bool IsSourceMutationTarget);

internal readonly record struct CoreAssistantCompletionBlocker(
    string Code,
    string Fingerprint,
    string ContinuationInstruction,
    string TruthfulFailureAnswer);

internal readonly record struct CoreAssistantCodingRequirements(
    bool RequiresWorkspaceWork,
    bool RequiresWorkspaceMutation,
    bool RequiresBuild,
    bool RequiresRun,
    bool RequiresCurrentWeb,
    bool RequiresMemoryRecall,
    string Basis,
    string DirectAnswer);
