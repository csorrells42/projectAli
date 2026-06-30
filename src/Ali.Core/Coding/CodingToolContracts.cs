namespace Ali.Core.Coding;

public enum CodingToolAction
{
    OpenFile,
    OpenSolution,
    OpenWorkspace,
    ListWorkspace,
    InspectWorkspace,
    AnalyzeArchitecture,
    ShowProjectIntelligence,
    ShowProjectIndex,
    ShowProjectDependencyMap,
    ShowRepoUnderstanding,
    ShowCodingContextPacket,
    ShowSafeCommitCheck,
    ShowWorkspaceHealthScore,
    DraftCommitMessage,
    DraftReleaseNotes,
    ShowCodingSessionTimeline,
    ShowRollbackPlan,
    ShowUiChangeChecklist,
    ComposeTypedPatch,
    ShowFileRiskLabels,
    FindSymbol,
    ShowCrossReferenceMap,
    ShowTestGapReport,
    ExplainKnownError,
    PreviewRollbackPatch,
    ShowFullCodingReadiness,
    ShowMiniCodexStatus,
    ShowValidationLedger,
    ShowValidationQueueRunner,
    ShowMandatorySymbolDiffAudit,
    PlanMultiFileRefactor,
    ShowTestFailurePatchLoop,
    ShowBuildErrorTriage,
    ShowCodebaseMemoryIndex,
    ShowCodingNextBestAction,
    ShowOwnerSafePatchBatch,
    ShowGeneratedFileGuard,
    ShowMiniCodexReadinessReport,
    BuildFeatureIntentPacket,
    PlanBehaviorTests,
    PlanImplementationSlices,
    ShowPatchBundleBuilder,
    ShowTestStubGeneratorPlan,
    ShowFailureLoopState,
    ShowStopConditionDetector,
    ShowSliceRiskScoring,
    ShowFeatureCompletionReceipt,
    ShowFeatureExecutionPacket,
    ShowFeatureWorkContext,
    ShowBehaviorContract,
    ShowPatchSlicePlan,
    ShowApplyGate,
    ShowFeaturePatchDraftPlan,
    ShowExactPatchSynthesis,
    PreviewSynthesizedFeaturePatch,
    ShowAutonomousPatchLoop,
    ShowFeatureSessionLedger,
    ShowPostPatchValidationRouter,
    ShowPatchPreviewIntelligence,
    ShowPlainEnglishFeatureBuilder,
    ShowBuildFeatureLane,
    ShowCSharpSymbolIndex,
    ShowOwnershipMap,
    ShowCallGraph,
    ResolveSemanticSymbol,
    ShowImpactedTests,
    ResolveTestTarget,
    PlanSemanticEdit,
    PlanSafeEditWorkflow,
    MapCompilerDiagnostic,
    VerifyXamlBindings,
    VerifyCommandBindings,
    ShowCommandSurfaceDoctor,
    ScanDeadCommands,
    PlanTask,
    InterpretBuildGoal,
    ShowArchitectureOptions,
    WriteAcceptanceCriteria,
    SuggestFeatureTests,
    DetectCodebasePatterns,
    PlanFeatureFiles,
    ShowRefactorSafetyChecklist,
    ExploreBuildIdea,
    DraftImplementationRoadmap,
    ShowLastRoadmap,
    DiscardLastRoadmap,
    ApproveLastRoadmap,
    StartApprovedRoadmap,
    ShowActiveRoadmapStep,
    ShowNextRoadmapAction,
    ShowRoadmapExecutionPacket,
    ApproveRoadmapExecutionPacket,
    ShowApprovedRoadmapExecutionPacket,
    DiscardApprovedRoadmapExecutionPacket,
    ShowRoadmapExecutionPacketProgress,
    ShowApprovedPacketCommands,
    RunApprovedPacketItem,
    ShowPacketRunLedger,
    PlanPackageLookup,
    PlanDependencyInstallPacket,
    PlanPostEditValidation,
    PreviewProjectScaffold,
    PlanScaffoldApply,
    ResumeBuildPlan,
    ShowBuilderCommandIndex,
    ShowCodingSessionSummary,
    GenerateMorningReport,
    ShowWindowsTroubleshootingToolkit,
    PlanRogueProcessHunt,
    CollectProcessEvidence,
    DiagnosePortOwner,
    DiagnoseFileLock,
    InspectServicesStartup,
    TriageEventLogs,
    PlanProcessStop,
    ExecuteProcessStop,
    DiagnoseBuildLock,
    ClassifyLastFailure,
    ReviewCurrentChanges,
    ShowRoadmapStepChecklist,
    ShowInstallDoctor,
    AdvanceRoadmapStep,
    PauseRoadmap,
    ResumeRoadmap,
    FinishRoadmap,
    RecoverRoadmapState,
    DiagnoseRecoveryState,
    ShowReceipts,
    ShowUserCommandHelp,
    ShowComputerAssistantStatus,
    ShowComputerAssistantCommandIndex,
    PlanFileOrganization,
    PlanDiskCleanup,
    PlanAppInstallTroubleshooting,
    PlanPeripheralSetup,
    ShowComputerTroubleshootingCommandIndex,
    PlanComputerTroubleshooting,
    ShowPdfToolStatus,
    ShowPdfCommandIndex,
    GeneratePdf,
    GenerateCodingReport,
    GenerateInstallReport,
    GenerateTroubleshootingReport,
    InspectPdf,
    ExtractPdfText,
    SummarizePdf,
    ConvertMarkdownToPdf,
    CombinePdfs,
    SplitPdf,
    ShowToolIntegrationStatus,
    GenerateVisualStudioHandoff,
    OpenLastDiagnostic,
    DiagnoseLastFailure,
    SuggestLastFailurePatch,
    ListPackages,
    ListOutdatedPackages,
    AddPackage,
    SearchWorkspace,
    ReadFile,
    CreateFile,
    AppendFile,
    PreviewReplaceText,
    PreviewPatchBundle,
    ShowLastPatchPreview,
    DiscardLastPatchPreview,
    ApplyLastPatchPreview,
    ReplaceText,
    Build,
    Test,
    Restore,
    RunProject,
    GitStatus,
    GitDiff,
    GitLog,
    GitAdd,
    GitCommit,
    GitMerge,
    GitPull,
    GitPush
}

public enum CodingToolPermissionKind
{
    Allow,
    RequireConfirmation,
    Deny
}

public sealed record CodingToolRequest(
    CodingToolAction Action,
    string? Path,
    int? LineNumber = null,
    bool ExplicitUserPath = false,
    bool UserConfirmed = false,
    string? Query = null,
    string? Content = null,
    string? Replacement = null,
    IReadOnlyList<CodingPatchEdit>? PatchEdits = null,
    IReadOnlyList<string>? AdditionalPaths = null);

public sealed record CodingPatchEdit(
    string Path,
    string OldText,
    string NewText);

public sealed record CodingToolPermission(
    CodingToolPermissionKind Kind,
    string Reason);

public sealed record CodingToolResult(
    bool Handled,
    bool Succeeded,
    string Message,
    string? ToolName = null,
    string? TargetPath = null,
    int? LineNumber = null,
    int? ExitCode = null)
{
    public static CodingToolResult NotHandled { get; } = new(false, false, string.Empty);
}

public sealed record CodingContextPack(
    bool HasContext,
    string Text,
    bool IncludesLastFailure = false)
{
    public static CodingContextPack Empty { get; } = new(false, string.Empty);
}

public sealed record CodingTaskPlan(
    bool HasPlan,
    string Text,
    bool RequiresConfirmation = false)
{
    public static CodingTaskPlan Empty { get; } = new(false, string.Empty);
}

public interface ILocalCodingTool
{
    CodingWorkspacePolicy Policy { get; }

    Task<CodingToolResult> TryHandleAsync(
        string userText,
        CancellationToken cancellationToken);

    Task<CodingContextPack> BuildContextPackAsync(
        string userText,
        CancellationToken cancellationToken);

    Task<CodingTaskPlan> BuildTaskPlanAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken);
}
