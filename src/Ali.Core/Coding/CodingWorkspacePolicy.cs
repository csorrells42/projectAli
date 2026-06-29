using System.Diagnostics.CodeAnalysis;

namespace Ali.Core.Coding;

public sealed class CodingWorkspacePolicy
{
    public CodingWorkspacePolicy(
        string workspaceRoot,
        bool allowExplicitOutsideFileOpen = true,
        bool allowConfirmedBuildTestRunInsideWorkspace = true,
        bool allowConfirmedEditInsideWorkspace = true,
        bool allowGitReadInsideWorkspace = true,
        bool allowConfirmedGitWriteInsideWorkspace = true,
        bool allowConfirmedGitMergeInsideWorkspace = true,
        bool allowGitNetworkOperations = false,
        bool allowPdfRead = true,
        bool allowPdfCreate = true,
        bool allowConfirmedPdfModify = true,
        bool allowConfirmedOutsideEditRun = false,
        bool allowConfirmedSystemAdminActions = true)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));
        }

        WorkspaceRoot = NormalizeDirectory(workspaceRoot);
        AllowExplicitOutsideFileOpen = allowExplicitOutsideFileOpen;
        AllowConfirmedBuildTestRunInsideWorkspace = allowConfirmedBuildTestRunInsideWorkspace;
        AllowConfirmedEditInsideWorkspace = allowConfirmedEditInsideWorkspace;
        AllowGitReadInsideWorkspace = allowGitReadInsideWorkspace;
        AllowConfirmedGitWriteInsideWorkspace = allowConfirmedGitWriteInsideWorkspace;
        AllowConfirmedGitMergeInsideWorkspace = allowConfirmedGitMergeInsideWorkspace;
        AllowGitNetworkOperations = allowGitNetworkOperations;
        AllowPdfRead = allowPdfRead;
        AllowPdfCreate = allowPdfCreate;
        AllowConfirmedPdfModify = allowConfirmedPdfModify;
        AllowConfirmedOutsideEditRun = allowConfirmedOutsideEditRun;
        AllowConfirmedSystemAdminActions = allowConfirmedSystemAdminActions;
    }

    public string WorkspaceRoot { get; }

    public bool AllowExplicitOutsideFileOpen { get; }

    public bool AllowConfirmedBuildTestRunInsideWorkspace { get; }

    public bool AllowConfirmedEditInsideWorkspace { get; }

    public bool AllowGitReadInsideWorkspace { get; }

    public bool AllowConfirmedGitWriteInsideWorkspace { get; }

    public bool AllowConfirmedGitMergeInsideWorkspace { get; }

    public bool AllowGitNetworkOperations { get; }

    public bool AllowPdfRead { get; }

    public bool AllowPdfCreate { get; }

    public bool AllowConfirmedPdfModify { get; }

    public bool AllowConfirmedOutsideEditRun { get; }

    public bool AllowConfirmedSystemAdminActions { get; }

    public static CodingWorkspacePolicy CreateDefault()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new(Path.Combine(documents, "Programming Projects"));
    }

    public bool IsInsideWorkspace(string path)
    {
        if (!TryNormalizePath(path, out var fullPath))
        {
            return false;
        }

        return fullPath.Equals(WorkspaceRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(WorkspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public CodingToolPermission Evaluate(CodingToolRequest request)
    {
        if (request.Action == CodingToolAction.OpenWorkspace)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Opening the approved coding workspace is allowed.");
        }

        if (request.Action == CodingToolAction.ListWorkspace)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Listing the approved coding workspace is allowed.");
        }

        if (request.Action == CodingToolAction.InspectWorkspace)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Inspecting the approved coding workspace is allowed.");
        }

        if (request.Action == CodingToolAction.AnalyzeArchitecture)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Analyzing solution architecture in the approved coding workspace is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.ShowProjectIntelligence)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Scanning project intelligence in the approved coding workspace is read-only and allowed.");
        }

        if (request.Action is CodingToolAction.ShowRepoUnderstanding
            or CodingToolAction.ShowSafeCommitCheck
            or CodingToolAction.ShowWorkspaceHealthScore
            or CodingToolAction.DraftCommitMessage
            or CodingToolAction.DraftReleaseNotes
            or CodingToolAction.ShowCodingSessionTimeline
            or CodingToolAction.ShowRollbackPlan
            or CodingToolAction.ShowUiChangeChecklist
            or CodingToolAction.ComposeTypedPatch
            or CodingToolAction.ShowFileRiskLabels
            or CodingToolAction.FindSymbol
            or CodingToolAction.ShowCrossReferenceMap
            or CodingToolAction.ShowTestGapReport
            or CodingToolAction.ExplainKnownError
            or CodingToolAction.PreviewRollbackPatch
            or CodingToolAction.ShowFullCodingReadiness
            or CodingToolAction.ShowValidationLedger
            or CodingToolAction.ShowCSharpSymbolIndex
            or CodingToolAction.ShowCallGraph
            or CodingToolAction.ResolveSemanticSymbol
            or CodingToolAction.ShowImpactedTests
            or CodingToolAction.ResolveTestTarget
            or CodingToolAction.PlanSemanticEdit
            or CodingToolAction.PlanSafeEditWorkflow
            or CodingToolAction.MapCompilerDiagnostic
            or CodingToolAction.VerifyXamlBindings
            or CodingToolAction.VerifyCommandBindings
            or CodingToolAction.ShowCommandSurfaceDoctor
            or CodingToolAction.ScanDeadCommands)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Reviewing coding readiness, summaries, and rollback planning is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.PlanTask)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Planning a coding task is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.ExploreBuildIdea)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Exploring a build idea is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.DraftImplementationRoadmap)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Drafting an implementation roadmap is read-only and allowed.");
        }

        if (request.Action is CodingToolAction.ShowLastRoadmap
            or CodingToolAction.InterpretBuildGoal
            or CodingToolAction.ShowArchitectureOptions
            or CodingToolAction.WriteAcceptanceCriteria
            or CodingToolAction.SuggestFeatureTests
            or CodingToolAction.DetectCodebasePatterns
            or CodingToolAction.PlanFeatureFiles
            or CodingToolAction.ShowRefactorSafetyChecklist
            or CodingToolAction.DiscardLastRoadmap
            or CodingToolAction.ApproveLastRoadmap
            or CodingToolAction.StartApprovedRoadmap
            or CodingToolAction.ShowActiveRoadmapStep
            or CodingToolAction.ShowNextRoadmapAction
            or CodingToolAction.ShowRoadmapExecutionPacket
            or CodingToolAction.ApproveRoadmapExecutionPacket
            or CodingToolAction.ShowApprovedRoadmapExecutionPacket
            or CodingToolAction.DiscardApprovedRoadmapExecutionPacket
            or CodingToolAction.ShowRoadmapExecutionPacketProgress
            or CodingToolAction.ShowApprovedPacketCommands
            or CodingToolAction.RunApprovedPacketItem
            or CodingToolAction.ShowPacketRunLedger
            or CodingToolAction.PlanPackageLookup
            or CodingToolAction.PlanDependencyInstallPacket
            or CodingToolAction.PlanPostEditValidation
            or CodingToolAction.PreviewProjectScaffold
            or CodingToolAction.PlanScaffoldApply
            or CodingToolAction.ResumeBuildPlan
            or CodingToolAction.ShowBuilderCommandIndex
            or CodingToolAction.ShowCodingSessionSummary
            or CodingToolAction.ShowWindowsTroubleshootingToolkit
            or CodingToolAction.PlanRogueProcessHunt
            or CodingToolAction.CollectProcessEvidence
            or CodingToolAction.DiagnosePortOwner
            or CodingToolAction.DiagnoseFileLock
            or CodingToolAction.InspectServicesStartup
            or CodingToolAction.TriageEventLogs
            or CodingToolAction.PlanProcessStop
            or CodingToolAction.DiagnoseBuildLock
            or CodingToolAction.ClassifyLastFailure
            or CodingToolAction.ShowRoadmapStepChecklist
            or CodingToolAction.ShowInstallDoctor
            or CodingToolAction.AdvanceRoadmapStep
            or CodingToolAction.PauseRoadmap
            or CodingToolAction.ResumeRoadmap
            or CodingToolAction.FinishRoadmap
            or CodingToolAction.RecoverRoadmapState
            or CodingToolAction.DiagnoseRecoveryState
            or CodingToolAction.ShowUserCommandHelp
            or CodingToolAction.ShowComputerAssistantStatus
            or CodingToolAction.ShowComputerAssistantCommandIndex
            or CodingToolAction.PlanFileOrganization
            or CodingToolAction.PlanDiskCleanup
            or CodingToolAction.PlanAppInstallTroubleshooting
            or CodingToolAction.PlanPeripheralSetup
            or CodingToolAction.ShowComputerTroubleshootingCommandIndex
            or CodingToolAction.PlanComputerTroubleshooting)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Managing an implementation roadmap is local planning state and does not change files.");
        }

        if (request.Action == CodingToolAction.ShowReceipts)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Showing recent coding receipts is read-only and allowed.");
        }

        if (request.Action is CodingToolAction.ShowPdfToolStatus or CodingToolAction.ShowPdfCommandIndex)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Showing PDF tool status and commands is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.ShowToolIntegrationStatus)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Showing coding tool integration status is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.GenerateVisualStudioHandoff)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Generating a Visual Studio integration handoff is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.GeneratePdf)
        {
            return AllowPdfCreate
                ? CodingToolPermissionKind.Allow.AsPermission("Generating a PDF in Ali's local generated documents folder is allowed by PDF permissions.")
                : CodingToolPermissionKind.Deny.AsPermission("PDF creation is disabled in PDF permissions.");
        }

        if (request.Action == CodingToolAction.GenerateCodingReport
            || request.Action == CodingToolAction.GenerateMorningReport
            || request.Action == CodingToolAction.GenerateInstallReport
            || request.Action == CodingToolAction.GenerateTroubleshootingReport
            || request.Action == CodingToolAction.ConvertMarkdownToPdf)
        {
            return AllowPdfCreate
                ? CodingToolPermissionKind.Allow.AsPermission("Generating a report PDF in Ali's local generated documents folder is allowed by PDF permissions.")
                : CodingToolPermissionKind.Deny.AsPermission("PDF creation is disabled in PDF permissions.");
        }

        if (request.Action is CodingToolAction.CombinePdfs or CodingToolAction.SplitPdf)
        {
            if (!AllowConfirmedPdfModify)
            {
                return CodingToolPermissionKind.Deny.AsPermission("PDF combine/split actions are disabled in PDF permissions.");
            }

            return request.UserConfirmed
                ? CodingToolPermissionKind.Allow.AsPermission("Confirmed PDF combine/split action is allowed and writes only to Ali's generated documents folder.")
                : CodingToolPermissionKind.RequireConfirmation.AsPermission("PDF combine/split creates derived files and needs explicit confirmation.");
        }

        if (request.Action is CodingToolAction.InspectPdf or CodingToolAction.ExtractPdfText or CodingToolAction.SummarizePdf)
        {
            if (!AllowPdfRead)
            {
                return CodingToolPermissionKind.Deny.AsPermission("PDF read/inspect actions are disabled in PDF permissions.");
            }

            return CodingToolPermissionKind.Allow.AsPermission("PDF read/inspect actions are allowed by PDF permissions.");
        }

        if (request.Action == CodingToolAction.ApplyLastPatchPreview)
        {
            return request.UserConfirmed
                ? CodingToolPermissionKind.Allow.AsPermission("Applying the last patch preview is allowed after explicit confirmation and revalidation.")
                : CodingToolPermissionKind.RequireConfirmation.AsPermission("Applying the last patch preview changes files and needs explicit confirmation.");
        }

        if (request.Action == CodingToolAction.ExecuteProcessStop)
        {
            if (!AllowConfirmedSystemAdminActions)
            {
                return CodingToolPermissionKind.Deny.AsPermission(
                    "System/admin actions are blocked in coding permissions.");
            }

            return request.UserConfirmed
                ? CodingToolPermissionKind.Allow.AsPermission("Stopping a named local process is allowed after explicit confirmation.")
                : CodingToolPermissionKind.RequireConfirmation.AsPermission("Stopping a process changes local system state and needs explicit confirmation.");
        }

        if (request.Action == CodingToolAction.PreviewPatchBundle)
        {
            return EvaluatePatchBundlePreview(request);
        }

        if (request.Action == CodingToolAction.OpenLastDiagnostic)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Opening the last diagnostic file is a read/open action and is limited to the approved workspace.");
        }

        if (request.Action is CodingToolAction.ShowLastPatchPreview or CodingToolAction.DiscardLastPatchPreview)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Showing or discarding a pending patch preview is local state management and does not change files.");
        }

        if (request.Action == CodingToolAction.DiagnoseLastFailure)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Diagnosing the last dotnet failure is read-only and allowed.");
        }

        if (request.Action == CodingToolAction.SuggestLastFailurePatch)
        {
            return CodingToolPermissionKind.Allow.AsPermission("Suggesting a patch from the last dotnet failure is preview-only and allowed.");
        }

        if (request.Action == CodingToolAction.ListPackages && string.IsNullOrWhiteSpace(request.Path))
        {
            return CodingToolPermissionKind.Allow.AsPermission("Listing package references in the approved coding workspace is allowed.");
        }

        if (request.Action == CodingToolAction.SearchWorkspace && string.IsNullOrWhiteSpace(request.Path))
        {
            return CodingToolPermissionKind.Allow.AsPermission("Searching the approved coding workspace is allowed.");
        }

        if (request.Action == CodingToolAction.OpenSolution && string.IsNullOrWhiteSpace(request.Path))
        {
            return CodingToolPermissionKind.Allow.AsPermission("Opening the primary solution in the approved coding workspace is allowed.");
        }

        if (IsBuildTestRun(request.Action) && string.IsNullOrWhiteSpace(request.Path))
        {
            return EvaluateBuildTestRun(WorkspaceRoot, request);
        }

        if (IsEditAction(request.Action) && string.IsNullOrWhiteSpace(request.Path))
        {
            return CodingToolPermissionKind.Deny.AsPermission("A target file path is required for edit actions.");
        }

        if (IsGitAction(request.Action) && string.IsNullOrWhiteSpace(request.Path))
        {
            return EvaluateGit(WorkspaceRoot, request);
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return CodingToolPermissionKind.Deny.AsPermission("A target path is required.");
        }

        if (!TryNormalizePath(request.Path, out var fullPath))
        {
            return CodingToolPermissionKind.Deny.AsPermission("The target path is not a valid local path.");
        }

        var insideWorkspace = IsInsideWorkspace(fullPath);
        return request.Action switch
        {
            CodingToolAction.OpenFile when insideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Opening files inside the coding workspace is allowed."),

            CodingToolAction.OpenFile when request.ExplicitUserPath && AllowExplicitOutsideFileOpen =>
                CodingToolPermissionKind.Allow.AsPermission("Opening an explicit user-provided file path outside the workspace is allowed as a read/open action."),

            CodingToolAction.ReadFile when insideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Reading files inside the coding workspace is allowed."),

            CodingToolAction.ReadFile when request.ExplicitUserPath && AllowExplicitOutsideFileOpen =>
                CodingToolPermissionKind.Allow.AsPermission("Reading an explicit user-provided file path outside the workspace is allowed as a read/open action."),

            CodingToolAction.SearchWorkspace when insideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Searching inside the approved coding workspace is allowed."),

            CodingToolAction.ListPackages when insideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Listing package references inside the approved coding workspace is allowed."),

            CodingToolAction.ListPackages =>
                CodingToolPermissionKind.Deny.AsPermission("Package inspection is limited to the approved coding workspace."),

            CodingToolAction.AddPackage when insideWorkspace =>
                EvaluateBuildTestRun(fullPath, request),

            CodingToolAction.AddPackage =>
                EvaluateBuildTestRun(fullPath, request),

            _ when IsEditAction(request.Action) && insideWorkspace =>
                EvaluateEdit(request, insideWorkspace: true),

            _ when IsEditAction(request.Action) =>
                EvaluateEdit(request, insideWorkspace: false),

            CodingToolAction.PreviewReplaceText when insideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Previewing a literal replacement inside the approved coding workspace is read-only and allowed."),

            CodingToolAction.PreviewReplaceText =>
                AllowConfirmedOutsideEditRun
                    ? CodingToolPermissionKind.Allow.AsPermission("Owner settings allow patch previews outside the approved workspace.")
                    : CodingToolPermissionKind.Deny.AsPermission("Patch previews are limited to the approved coding workspace."),

            CodingToolAction.OpenSolution when insideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Opening solutions inside the coding workspace is allowed."),

            CodingToolAction.OpenSolution when request.ExplicitUserPath =>
                CodingToolPermissionKind.Allow.AsPermission("Opening an explicit user-provided solution outside the workspace is allowed as a read/open action."),

            _ when IsBuildTestRun(request.Action) && insideWorkspace =>
                EvaluateBuildTestRun(fullPath, request),

            _ when IsGitAction(request.Action) && insideWorkspace =>
                EvaluateGit(fullPath, request),

            _ => CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "This coding action is outside the approved workspace and needs explicit confirmation.")
        };
    }

    public static bool TryNormalizePath(string path, [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private CodingToolPermission EvaluateBuildTestRun(string fullPath, CodingToolRequest request)
    {
        if (!IsInsideWorkspace(fullPath))
        {
            if (!AllowConfirmedOutsideEditRun)
            {
                return CodingToolPermissionKind.Deny.AsPermission(
                    "Build, test, restore, package install, and run actions outside the approved workspace are blocked in coding permissions.");
            }

            return request.UserConfirmed
                ? CodingToolPermissionKind.Allow.AsPermission("Owner settings allow confirmed build/test/restore/package-install/run actions outside the approved workspace.")
                : CodingToolPermissionKind.RequireConfirmation.AsPermission(
                    "Owner settings allow outside-workspace build/test/restore/package-install/run actions, but explicit confirmation is still required.");
        }

        if (!AllowConfirmedBuildTestRunInsideWorkspace)
        {
            return CodingToolPermissionKind.Deny.AsPermission(
                "Build, test, restore, package install, and run actions are disabled in coding permissions.");
        }

        return request.UserConfirmed
            ? CodingToolPermissionKind.Allow.AsPermission("Confirmed build/test/restore/package-install/run action inside the approved coding workspace is allowed.")
            : CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "Build, test, restore, package install, and run actions need an explicit confirmation phrase before execution.");
    }

    private static bool IsBuildTestRun(CodingToolAction action) =>
        action is CodingToolAction.Build
            or CodingToolAction.Test
            or CodingToolAction.Restore
            or CodingToolAction.ListOutdatedPackages
            or CodingToolAction.AddPackage
            or CodingToolAction.RunProject;

    private CodingToolPermission EvaluateEdit(CodingToolRequest request, bool insideWorkspace)
    {
        if (!insideWorkspace)
        {
            if (!AllowConfirmedOutsideEditRun)
            {
                return CodingToolPermissionKind.Deny.AsPermission(
                    "Edit/write actions outside the approved workspace are blocked in coding permissions.");
            }

            return request.UserConfirmed
                ? CodingToolPermissionKind.Allow.AsPermission("Owner settings allow confirmed edit/write actions outside the approved workspace.")
                : CodingToolPermissionKind.RequireConfirmation.AsPermission(
                    "Owner settings allow outside-workspace edit/write actions, but explicit confirmation is still required.");
        }

        if (!AllowConfirmedEditInsideWorkspace)
        {
            return CodingToolPermissionKind.Deny.AsPermission(
                "Edit/write actions are disabled in coding permissions.");
        }

        return request.UserConfirmed
            ? CodingToolPermissionKind.Allow.AsPermission("Confirmed edit/write action inside the approved coding workspace is allowed.")
            : CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "Edit/write actions need an explicit confirmation phrase before changing files.");
    }

    private CodingToolPermission EvaluatePatchBundlePreview(CodingToolRequest request)
    {
        if (request.PatchEdits is null || request.PatchEdits.Count == 0)
        {
            return CodingToolPermissionKind.Deny.AsPermission("A patch bundle preview needs at least one file edit.");
        }

        foreach (var edit in request.PatchEdits)
        {
            if (!TryNormalizePath(edit.Path, out var fullPath))
            {
                return CodingToolPermissionKind.Deny.AsPermission("Every patch bundle target must be a valid local path.");
            }

            if (!IsInsideWorkspace(fullPath))
            {
                return CodingToolPermissionKind.Deny.AsPermission("Patch bundle previews are limited to the approved coding workspace.");
            }
        }

        return CodingToolPermissionKind.Allow.AsPermission("Previewing a patch bundle inside the approved coding workspace is read-only and allowed.");
    }

    private static bool IsEditAction(CodingToolAction action) =>
        action is CodingToolAction.CreateFile or CodingToolAction.AppendFile or CodingToolAction.ReplaceText;

    private CodingToolPermission EvaluateGit(string fullPath, CodingToolRequest request)
    {
        if (!IsInsideWorkspace(fullPath))
        {
            return CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "Git actions are limited to the approved coding workspace.");
        }

        return request.Action switch
        {
            CodingToolAction.GitStatus or CodingToolAction.GitDiff or CodingToolAction.GitLog or CodingToolAction.ReviewCurrentChanges
                when AllowGitReadInsideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Read-only Git inspection inside the approved coding workspace is allowed."),

            CodingToolAction.GitStatus or CodingToolAction.GitDiff or CodingToolAction.GitLog or CodingToolAction.ReviewCurrentChanges =>
                CodingToolPermissionKind.Deny.AsPermission("Read-only Git inspection is disabled in coding permissions."),

            CodingToolAction.GitAdd or CodingToolAction.GitCommit
                when !AllowConfirmedGitWriteInsideWorkspace =>
                CodingToolPermissionKind.Deny.AsPermission("Git staging and commit actions are disabled in coding permissions."),

            CodingToolAction.GitAdd or CodingToolAction.GitCommit
                when request.UserConfirmed =>
                CodingToolPermissionKind.Allow.AsPermission("Confirmed Git staging/commit action inside the approved coding workspace is allowed."),

            CodingToolAction.GitAdd or CodingToolAction.GitCommit =>
                CodingToolPermissionKind.RequireConfirmation.AsPermission("Git staging and commit actions need an explicit confirmation phrase before execution."),

            CodingToolAction.GitMerge when !AllowConfirmedGitMergeInsideWorkspace =>
                CodingToolPermissionKind.Deny.AsPermission("Git merge actions are disabled in coding permissions."),

            CodingToolAction.GitMerge when request.UserConfirmed =>
                CodingToolPermissionKind.Allow.AsPermission("Confirmed Git merge inside the approved coding workspace is allowed."),

            CodingToolAction.GitMerge =>
                CodingToolPermissionKind.RequireConfirmation.AsPermission("Git merge needs an explicit confirmation phrase before execution."),

            CodingToolAction.GitPull or CodingToolAction.GitPush when AllowGitNetworkOperations && request.UserConfirmed =>
                CodingToolPermissionKind.Allow.AsPermission("Confirmed Git network action inside the approved coding workspace is allowed."),

            CodingToolAction.GitPull or CodingToolAction.GitPush when AllowGitNetworkOperations =>
                CodingToolPermissionKind.RequireConfirmation.AsPermission("Git network actions need explicit confirmation before execution."),

            CodingToolAction.GitPull or CodingToolAction.GitPush =>
                CodingToolPermissionKind.Deny.AsPermission("Git pull and push are blocked in coding permissions."),

            _ => CodingToolPermissionKind.Deny.AsPermission("Unsupported Git action.")
        };
    }

    private static bool IsGitAction(CodingToolAction action) =>
        action is CodingToolAction.GitStatus
            or CodingToolAction.GitDiff
            or CodingToolAction.GitLog
            or CodingToolAction.ReviewCurrentChanges
            or CodingToolAction.GitAdd
            or CodingToolAction.GitCommit
            or CodingToolAction.GitMerge
            or CodingToolAction.GitPull
            or CodingToolAction.GitPush;
}

file static class CodingToolPermissionKindExtensions
{
    public static CodingToolPermission AsPermission(this CodingToolPermissionKind kind, string reason) =>
        new(kind, reason);
}
