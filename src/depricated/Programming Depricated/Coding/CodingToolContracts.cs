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
    StartCodingSession,
    ContinueCurrentCodingSession,
    ShowCurrentCodingSession,
    ClearCurrentCodingSession,
    ShowCodingSessionHistory,
    ShowCurrentProjectCommandDefaults,
    SaveCurrentProjectCommandDefaults,
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
    PreviewBehaviorTestPatch,
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
    PreviewGuidedFeatureBundle,
    ShowAutonomousPatchLoop,
    ShowFeatureSessionLedger,
    ShowValidationRepairRunner,
    ShowFeatureRunController,
    ShowPostPatchValidationRouter,
    ShowPatchPreviewIntelligence,
    ShowGuidedFeatureWorkflow,
    ShowFeatureImplementationPlanner,
    ShowFeatureIntakeNormalizer,
    ShowAutonomousFeatureOrchestrator,
    ShowImplementationEvidencePack,
    ShowBuildThisFeature,
    ShowRoslynEditPlannerV2,
    ShowMultiFilePatchSynthesisV2,
    ShowPatternCopyPlan,
    ShowBehaviorTestGeneratorV2,
    ShowImplementationSliceState,
    ShowPostApplyRepairLoopV2,
    ShowSemanticDiffSummary,
    ShowMiniCodexScoreV3,
    ShowConcretePatchAuthoring,
    ShowPatchBodyGenerator,
    ShowPatternCommandScaffolder,
    ShowUiBundlePlanner,
    ShowPatchConfidenceScore,
    ShowSliceExecutorPreview,
    ShowFailureToPatchV3,
    ShowSemanticChangeReceipt,
    ShowValidationChainPlanner,
    ShowDataSystemsGuide,
    ShowDataStructureChooser,
    ShowSqlPerformanceGuide,
    ShowServiceArchitectureGuide,
    ShowCacheQueueGuide,
    ShowConsoleCodingGuide,
    ShowWpfCodingGuide,
    ShowWpfLayoutGuide,
    ShowWpfControlsGuide,
    ShowWpfStylingGuide,
    ShowWpfComplexWindowGuide,
    ShowActiveWorkspaceProject,
    ShowProjectControlCenter,
    ShowCurrentProjectMemory,
    SaveCurrentProjectMemory,
    OpenCurrentProjectFolder,
    ShowOwnerApprovedApplyPacket,
    ShowRoslynInsertionPlanner,
    ShowIntentDiffComposer,
    ShowBehaviorSpecTestScaffold,
    ShowRepeatFailureMemory,
    ShowFirstDiagnosticRepairRoute,
    ShowValidationCommandMinimizer,
    ShowUiBindingRepairPlanner,
    ShowAuthoringSequenceFlow,
    ShowPlainEnglishCodingCapabilityCard,
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
    string NewText,
    bool ReplaceEntireFile = false);

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

public sealed record CodingActionPlan(
    bool UseCodingTool,
    string Command,
    string Summary,
    double Confidence = 0,
    string SelectedPath = "",
    string UnderstoodGoal = "",
    string ExecutionMode = "",
    string SelectedTool = "",
    string CommandGoal = "",
    IReadOnlyList<string>? AcceptanceCriteria = null,
    IReadOnlyList<string>? InfoUsed = null,
    string Diagnostic = "",
    string RawOutputExcerpt = "")
{
    public static CodingActionPlan NoAction { get; } = new(false, string.Empty, string.Empty, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<string>());
}

public sealed record CodingPatchPlan(
    bool HasPatch,
    IReadOnlyList<CodingPatchEdit> Edits,
    string Summary,
    double Confidence = 0,
    string? StopReason = null,
    string SelectedPath = "",
    IReadOnlyList<string>? CriteriaCoverage = null)
{
    public static CodingPatchPlan NoPatch { get; } = new(false, [], string.Empty, 0, null, string.Empty, Array.Empty<string>());
}

public interface ICodingActionPlanner
{
    Task<CodingActionPlan> PlanAsync(
        string userText,
        IReadOnlyList<Ali.Core.Runtime.ChatMessage> history,
        CancellationToken cancellationToken,
        CodingContextPack? contextPack = null);
}

public interface ICodingPatchPlanner
{
    Task<CodingPatchPlan> PlanPatchAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken,
        CodingActionPlan? actionPlan = null);
}

public interface ILocalCodingTool
{
    CodingWorkspacePolicy Policy { get; }

    Task<CodingToolResult> TryHandleAsync(
        string userText,
        CancellationToken cancellationToken);

    Task<CodingContextPack> BuildContextPackAsync(
        string userText,
        CancellationToken cancellationToken,
        bool force = false);

    Task<CodingTaskPlan> BuildTaskPlanAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken);

    Task<CodingToolResult> PreviewPatchBundleAsync(
        string goal,
        IReadOnlyList<CodingPatchEdit> edits,
        CancellationToken cancellationToken);
}
