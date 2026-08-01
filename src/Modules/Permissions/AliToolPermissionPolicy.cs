using Ali.Modules.Coordinator;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;
using Microsoft.Extensions.AI;

namespace Ali.Modules.Permissions;

/// <summary>
/// Ali-specific risk classification only. Agent Framework owns approval requests, argument
/// binding, standing rules, and session-scoped approval persistence.
/// </summary>
internal sealed class AliToolPermissionPolicy(
    Func<CoordinatorTurnContext?> turnAccessor,
    Func<AgentPermissionProfile>? profileAccessor = null,
    IShadowToolObserver? shadowObserver = null)
{
    private static IReadOnlyList<AgentToolPermissionDefinition> TrustedWorkstationTools { get; } =
    [
        new(AliCapabilityCatalog.ForgetCurrentUserMemoryName, "Deletes personal long-term memory"),
        new(AliCapabilityCatalog.ListCurrentUserMemoriesName, "Reads private personal memory"),
        new(AliCapabilityCatalog.CreateCalendarEventName, "Creates a persistent calendar event and Windows notification"),
        new(AliCapabilityCatalog.ResearchWebName, "Starts metered deep web research"),
        new(AliCapabilityCatalog.RunMagenticOrchestrationName, "Starts bounded multi-agent Magentic orchestration", "Controlled by the Agents tab"),
        new(AliCapabilityCatalog.FileWriteName, "Creates or overwrites a workstation file", "New files allowed; overwrite asks"),
        new(AliCapabilityCatalog.FileDeleteName, "Moves a workstation file or folder to recoverable trash"),
        new(AliCapabilityCatalog.FileReplaceName, "Edits matching text in an existing file"),
        new(AliCapabilityCatalog.FileReplaceLinesName, "Edits specific lines in an existing file"),
        new(AliCapabilityCatalog.FileMoveName, "Renames or moves an existing workstation file"),
        new(AliCapabilityCatalog.CodingAgentExecuteName, "Lets the selected external coding engine inspect, modify, build, test, and run an approved project", "Controlled by the Agents tab"),
        new(AliCapabilityCatalog.CodingFormatProjectName, "Reformats existing source files through the detected language provider"),
        new(AliCapabilityCatalog.CodingBuildProjectName, "Executes the detected local build toolchain and project targets"),
        new(AliCapabilityCatalog.CodingTestProjectName, "Executes project test code through the detected language provider"),
        new(AliCapabilityCatalog.CodingRunProjectName, "Executes project code through the detected language provider"),
        new(AliCapabilityCatalog.CodingProbeServiceName, "Transmits an explicit request to an external HTTP service"),
        new(AliCapabilityCatalog.CodingInspectProcessName, "Reads live process resource and thread state"),
        new(AliCapabilityCatalog.VisualStudioBuildName, "Executes Visual Studio MSBuild and writes build evidence"),
        new(AliCapabilityCatalog.VisualStudioOpenName, "Launches Visual Studio with an approved local project"),
        new(AliCapabilityCatalog.GnuNativeExecuteName, "Executes GCC build, test, or application code"),
        new(AliCapabilityCatalog.ArduinoSearchLibrariesName, "Transmits a query to the Arduino library catalog"),
        new(AliCapabilityCatalog.ArduinoInstallCoreName, "Downloads and installs an Arduino board core"),
        new(AliCapabilityCatalog.ArduinoInstallLibraryName, "Downloads and installs an Arduino library"),
        new(AliCapabilityCatalog.ArduinoCreateCompileName, "Creates source code and executes the Arduino compiler"),
        new(AliCapabilityCatalog.ArduinoCompileName, "Executes an Arduino board compiler and writes firmware"),
        new(AliCapabilityCatalog.ArduinoUploadName, "Writes firmware to an explicitly selected hardware port"),
        new(AliCapabilityCatalog.ArduinoOpenIdeName, "Launches Arduino IDE with an approved sketch"),
        new(AliCapabilityCatalog.RaspberryPiProbeName, "Connects to an explicit Raspberry Pi over SSH"),
        new(AliCapabilityCatalog.RaspberryPiInspectLibrariesName, "Reads private package state from a Raspberry Pi"),
        new(AliCapabilityCatalog.RaspberryPiSearchPackagesName, "Queries package metadata on a Raspberry Pi"),
        new(AliCapabilityCatalog.RaspberryPiDeployName, "Transfers private project files to and builds on a Raspberry Pi"),
        new(AliCapabilityCatalog.DotNetCreateProjectName, "Executes the local .NET SDK to create a new project scaffold"),
        new(AliCapabilityCatalog.RoslynFormatProjectName, "Reformats existing C# source files with Roslyn"),
        new(AliCapabilityCatalog.RoslynApplyRenameName, "Renames a C# symbol and every semantic reference with Roslyn"),
        new(AliCapabilityCatalog.DotNetBuildName, "Executes the local .NET SDK and project build targets"),
        new(AliCapabilityCatalog.DotNetRunName, "Launches a compiled local application"),
        new(AliCapabilityCatalog.DotNetStopProjectName, "Closes or terminates the running compiled application for an approved project"),
        new(AliCapabilityCatalog.DotNetTestName, "Executes project test code and writes a TRX result artifact"),
        new(AliCapabilityCatalog.DotNetVerifyName, "Executes approved build and test targets"),
        new(AliCapabilityCatalog.DotNetDebugLaunchName, "Launches project code under the CLR debugger"),
        new(AliCapabilityCatalog.DotNetDebugAttachName, "Attaches the debugger to an approved running process"),
        new(AliCapabilityCatalog.DotNetDebugInspectName, "Reads private process, stack, and local-variable state"),
        new(AliCapabilityCatalog.DotNetDebugEvaluateName, "Evaluates code in a paused process"),
        new(AliCapabilityCatalog.DotNetDebugBreakpointsName, "Changes source breakpoints for a running process"),
        new(AliCapabilityCatalog.DotNetDebugControlName, "Changes execution of a paused process"),
        new(AliCapabilityCatalog.DotNetDebugStopName, "Terminates a debugger session and debuggee"),
        new(AliCapabilityCatalog.DotNetDebugDiagnosticsHandoffName, "Reads process identity for diagnostics handoff"),
        new(AliCapabilityCatalog.DotNetDependencyInspectName, "Transmits package IDs to configured NuGet sources for audit"),
        new(AliCapabilityCatalog.DotNetDependencyApplyName, "Changes project package dependencies"),
        new(AliCapabilityCatalog.GitCreateBranchName, "Changes the repository branch"),
        new(AliCapabilityCatalog.GitCommitName, "Creates a durable source-control commit"),
        new(AliCapabilityCatalog.GitPushName, "Transmits commits to a remote repository"),
        new(AliCapabilityCatalog.DotNetPerformanceMeasureName, "Executes compiled project code for measurement"),
        new(AliCapabilityCatalog.DotNetPerformanceTraceName, "Captures private CPU and allocation trace data from a running process"),
        new(AliCapabilityCatalog.DotNetApplicationVerifyName, "Launches compiled project code for actual-application verification"),
        new(AliCapabilityCatalog.DotNetReleasePublishName, "Builds a distributable release and may restore packages"),
        new(AliCapabilityCatalog.DotNetDeliveryVerifyName, "Executes build, tests, application checks, and optional publish gates")
    ];

    private static IReadOnlyList<AgentToolPermissionDefinition> LockedDownAdditionalTools { get; } =
    [
        new(AliCapabilityCatalog.RememberCurrentUserName, "Writes personal long-term memory"),
        new(AliCapabilityCatalog.CorrectCurrentUserMemoryName, "Changes personal long-term memory"),
        new(AliCapabilityCatalog.GetActiveUserProfileName, "Reads the selected local identity profile"),
        new(AliCapabilityCatalog.RecallUserMemoryName, "Reads private personal memory"),
        new(AliCapabilityCatalog.SearchCurrentWebName, "Transmits a query to configured web sources"),
        new(AliCapabilityCatalog.SearchLocalLibraryName, "Reads indexed local documents"),
        new(AliCapabilityCatalog.FileReadName, "Reads a workstation file"),
        new(AliCapabilityCatalog.FileListName, "Lists approved workstation folders"),
        new(AliCapabilityCatalog.FileSearchName, "Searches text in approved workstation folders"),
        new(AliCapabilityCatalog.FileCopyName, "Copies private workstation files or folders"),
        new(AliCapabilityCatalog.FileCreateDirectoryName, "Creates a workstation folder"),
        new(AliCapabilityCatalog.FileMetadataName, "Reads private workstation metadata or hashes"),
        new(AliCapabilityCatalog.ArchiveCreateName, "Creates an archive from private workstation files"),
        new(AliCapabilityCatalog.ArchiveListName, "Reads private archive contents"),
        new(AliCapabilityCatalog.ArchiveExtractName, "Extracts an archive into workstation files"),
        new(AliCapabilityCatalog.CodingInspectProjectName, "Reads private project structure and toolchain state"),
        new(AliCapabilityCatalog.CodingAgentStatusName, "Reads installed external coding-engine and local runtime readiness"),
        new(AliCapabilityCatalog.CodingIndexProjectName, "Reads and indexes private project source"),
        new(AliCapabilityCatalog.CodingSearchSymbolsName, "Searches a private project source index"),
        new(AliCapabilityCatalog.CodingAnalyzeProjectName, "Reads private source through a semantic analyzer"),
        new(AliCapabilityCatalog.CodingInspectArchitectureName, "Reads private source to build a cross-language dependency graph"),
        new(AliCapabilityCatalog.CodingBuildContextName, "Reads private source to select relevant large-project context"),
        new(AliCapabilityCatalog.VisualStudioInspectName, "Reads installed Visual Studio feature state"),
        new(AliCapabilityCatalog.GnuNativeInspectName, "Reads installed GNU toolchain state"),
        new(AliCapabilityCatalog.ArduinoInspectName, "Reads attached board and installed core state"),
        new(AliCapabilityCatalog.RaspberryPiLibrariesName, "Reads embedded-development library guidance")
    ];

    internal static IReadOnlyList<AgentToolPermissionDefinition> ProtectedTools => TrustedWorkstationTools;

    internal static IReadOnlyList<AgentToolPermissionDefinition> ProtectedToolsFor(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.LockedDown
            ? TrustedWorkstationTools.Concat(LockedDownAdditionalTools).ToArray()
            : TrustedWorkstationTools;

    private static readonly HashSet<string> ApprovalRequiredTools =
        TrustedWorkstationTools.Select(tool => tool.ToolName).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> LockedDownApprovalRequiredTools =
        ProtectedToolsFor(AgentPermissionProfile.LockedDown)
            .Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);

    public AIFunction Apply(
        AIFunction function,
        AliToolTurnRole turnRole = AliToolTurnRole.Default)
        => Apply(
            function,
            RequiresApproval(
                function.Name,
                profileAccessor?.Invoke() ?? AgentPermissionProfile.TrustedWorkstation),
            turnRole);

    internal static bool RequiresApproval(string toolName) =>
        ApprovalRequiredTools.Contains(toolName);

    internal static bool RequiresApproval(string toolName, AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.LockedDown
            ? LockedDownApprovalRequiredTools.Contains(toolName)
            : ApprovalRequiredTools.Contains(toolName);

    public AIFunction Apply(
        AIFunction function,
        bool requiresApproval,
        AliToolTurnRole turnRole = AliToolTurnRole.Default)
    {
        AIFunction observable = new ActivityReportingAIFunction(
            function,
            turnAccessor,
            shadowObserver,
            requiresApproval);
        observable = turnRole switch
        {
            AliToolTurnRole.ExternalCodingAgent =>
                new ExternalCodingOwnershipAIFunction(observable, turnAccessor),
            AliToolTurnRole.ImplementationMutation =>
                new ExternalCodingOwnerGuardAIFunction(
                    observable,
                    turnAccessor,
                    shadowObserver),
            _ => observable
        };
        return requiresApproval
            ? new TurnDenialGuardAIFunction(
                new ApprovalRequiredAIFunction(observable),
                turnAccessor,
                shadowObserver)
            : observable;
    }
}

internal enum AliToolTurnRole
{
    Default,
    ExternalCodingAgent,
    ImplementationMutation
}

internal sealed class ExternalCodingOwnershipAIFunction(
    AIFunction innerFunction,
    Func<CoordinatorTurnContext?> turnAccessor) : DelegatingAIFunction(innerFunction)
{
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var turn = turnAccessor();
        if (turn is not null)
        {
            var job = turn.ClaimExternalCodingAgentOwnership(
                ReadRequiredString(arguments, "targetPath"),
                ReadRequiredString(arguments, "objective"));
            arguments["targetPath"] = job.ProjectPath;
            arguments["objective"] = job.Objective;
        }

        return InnerFunction.InvokeAsync(arguments, cancellationToken);
    }

    private static string ReadRequiredString(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value))
        {
            throw new InvalidOperationException($"The external coding tool requires {name}.");
        }

        var text = value switch
        {
            string direct => direct,
            System.Text.Json.JsonElement element
                when element.ValueKind == System.Text.Json.JsonValueKind.String => element.GetString(),
            _ => value?.ToString()
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"The external coding tool requires a non-empty {name}.");
        }

        return text;
    }
}

internal sealed class ExternalCodingOwnerGuardAIFunction(
    AIFunction innerFunction,
    Func<CoordinatorTurnContext?> turnAccessor,
    IShadowToolObserver? shadowObserver = null) : DelegatingAIFunction(innerFunction)
{
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var turn = turnAccessor();
        if (turn?.ExternalCodingAgentOwnsTurn == true)
        {
            var exception = new InvalidOperationException(
                "The selected external coding agent owns this programming turn. "
                + "Ali cannot modify the implementation; return the critic or verification diagnostics "
                + "to the external coding agent.");
            TryObservePolicyBlocked(turn, arguments);
            throw exception;
        }

        return InnerFunction.InvokeAsync(arguments, cancellationToken);
    }

    private void TryObservePolicyBlocked(
        CoordinatorTurnContext turn,
        AIFunctionArguments arguments)
    {
        if (shadowObserver is null)
        {
            return;
        }

        if (!turn.TryGetCurrentToolCallId(Name, out var callId)
            || string.IsNullOrWhiteSpace(callId)
            || turn.WasShadowObserved(callId))
        {
            return;
        }

        var observedAtUtc = DateTimeOffset.UtcNow;
        var permission = new EvidencePermissionMetadata("policy-blocked", "none");
        turn.RecordPendingExplicitShadowTerminal(new PendingExplicitShadowTerminal(
            callId,
            Name,
            ExplicitShadowTerminalKind.Denied,
            "external-coding-agent-owns-turn",
            observedAtUtc,
            observedAtUtc,
            permission));
        try
        {
            if (shadowObserver.TryObserveDenied(
                turn.ObservationIdentity,
                callId,
                Name,
                arguments,
                "external-coding-agent-owns-turn",
                observedAtUtc,
                observedAtUtc,
                permission))
            {
                turn.ClearPendingExplicitShadowTerminal(callId);
                turn.MarkShadowObserved(callId);
            }
        }
        catch
        {
            // External coding ownership remains authoritative if shadow storage fails.
        }
    }
}

internal sealed class TurnDenialGuardAIFunction(
    AIFunction innerFunction,
    Func<CoordinatorTurnContext?> turnAccessor,
    IShadowToolObserver? shadowObserver = null) : DelegatingAIFunction(innerFunction)
{
    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var turn = turnAccessor();
        if (turn?.PermissionDenied != true)
        {
            return InnerFunction.InvokeAsync(arguments, cancellationToken);
        }

        turn.Report(
            AgentActivityKind.Warning,
            $"Blocked follow-up protected action {Name.Replace('_', ' ')}",
            "A protected action was denied earlier in this turn. Ali will not retry it through another tool or saved permission.");
        TryObservePolicyBlocked(turn, arguments);
        return ValueTask.FromResult<object?>(new
        {
            success = false,
            denied = true,
            message = "A protected action was denied earlier in this turn. Stop the action plan, do not call an alternate tool, and tell the user that no further protected action was performed."
        });
    }

    private void TryObservePolicyBlocked(
        CoordinatorTurnContext turn,
        AIFunctionArguments arguments)
    {
        if (shadowObserver is null)
        {
            return;
        }

        if (!turn.TryGetCurrentToolCallId(Name, out var callId)
            || string.IsNullOrWhiteSpace(callId)
            || turn.WasShadowObserved(callId))
        {
            return;
        }

        var observedAtUtc = DateTimeOffset.UtcNow;
        var permission = new EvidencePermissionMetadata("policy-blocked", "none");
        turn.RecordPendingExplicitShadowTerminal(new PendingExplicitShadowTerminal(
            callId,
            Name,
            ExplicitShadowTerminalKind.Denied,
            "prior-permission-denial",
            observedAtUtc,
            observedAtUtc,
            permission));
        try
        {
            if (shadowObserver.TryObserveDenied(
                turn.ObservationIdentity,
                callId,
                Name,
                arguments,
                "prior-permission-denial",
                observedAtUtc,
                observedAtUtc,
                permission))
            {
                turn.ClearPendingExplicitShadowTerminal(callId);
                turn.MarkShadowObserved(callId);
            }
        }
        catch
        {
            // Policy enforcement and its returned denial must not depend on shadow storage.
        }
    }
}

public sealed record AgentToolPermissionDefinition(
    string ToolName,
    string Reason,
    string DefaultBehavior = "Ask before use");
