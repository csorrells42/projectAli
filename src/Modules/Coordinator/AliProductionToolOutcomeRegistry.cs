using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Ali.Modules.Coding;
using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Debugging;
using Ali.Modules.Coding.Dependencies;
using Ali.Modules.Coding.Delivery;
using Ali.Modules.Coding.Embedded.Arduino;
using Ali.Modules.Coding.Embedded.RaspberryPi;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coding.Indexing;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Native;
using Ali.Modules.Coding.Operations;
using Ali.Modules.Coding.Performance;
using Ali.Modules.Coding.Quality;
using Ali.Modules.Coding.Release;
using Ali.Modules.Coding.RoslynActions;
using Ali.Modules.Coding.SourceControl;
using Ali.Modules.Coding.Verification;
using Ali.Modules.Coding.VisualStudio;
using Ali.Modules.Internet;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Planning;
using Ali.Modules.ToolDiscovery;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coordinator;

internal sealed record AliCompletedToolOutcomeRequest
{
    public AliCompletedToolOutcomeRequest(
        TurnIdentity turnIdentity,
        string callId,
        string toolName,
        object? result)
    {
        TurnIdentity = turnIdentity ?? throw new ArgumentNullException(nameof(turnIdentity));
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        CallId = callId;
        ToolName = toolName;
        Result = result;
    }

    public TurnIdentity TurnIdentity { get; }

    public string CallId { get; }

    public string ToolName { get; }

    public object? Result { get; }
}

internal enum AliToolOutcomeContractKind
{
    TypedReturn,
    ProviderBoundarySignal
}

/// <summary>
/// The closed production allowlist for completed tool outcomes. Every result-owned
/// contract names its exact CLR result type. Framework providers whose public return
/// channel can contain both data and friendly errors require an exact-key sidecar
/// signal instead. No contract examines arbitrary JSON, property names, or prose.
/// </summary>
internal sealed class AliProductionToolOutcomeRegistry
{
    private static readonly IReadOnlyDictionary<string, AliToolOutcomeContract> Contracts =
        CreateContracts();
    private static readonly JsonSerializerOptions TypedReturnJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly AliFrameworkToolOutcomeSidecar _frameworkOutcomes;

    internal AliProductionToolOutcomeRegistry(AliFrameworkToolOutcomeSidecar frameworkOutcomes)
    {
        _frameworkOutcomes = frameworkOutcomes
            ?? throw new ArgumentNullException(nameof(frameworkOutcomes));
    }

    internal static IReadOnlySet<string> ContractedToolNames { get; } =
        Contracts.Keys.ToFrozenSet(StringComparer.Ordinal);

    internal static bool HasContract(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && Contracts.ContainsKey(toolName);

    internal static bool TryGetContractId(string toolName, out string? contractId)
    {
        if (!string.IsNullOrWhiteSpace(toolName)
            && Contracts.TryGetValue(toolName, out var contract))
        {
            contractId = contract.Id;
            return true;
        }

        contractId = null;
        return false;
    }

    internal static AliToolOutcomeContractKind? GetContractKind(string toolName) =>
        Contracts.TryGetValue(toolName, out var contract) ? contract.Kind : null;

    internal static PlanningToolDomainOutcome ClassifyTypedReturn(
        string toolName,
        object? result) =>
        !string.IsNullOrWhiteSpace(toolName)
        && Contracts.TryGetValue(toolName, out var contract)
            ? contract.ClassifyTypedReturn(result)
            : PlanningToolDomainOutcome.Unreported;

    internal static bool TryReadTypedReturn<TResult>(
        object? result,
        [NotNullWhen(true)] out TResult? typed)
    {
        if (result is TResult exact)
        {
            typed = exact;
            return true;
        }

        if (result is JsonElement element)
        {
            try
            {
                typed = element.Deserialize<TResult>(TypedReturnJsonOptions);
                return typed is not null;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Only the exact closed contract schema is accepted. A malformed or
                // incompatible framework payload remains unreported.
            }
        }

        typed = default;
        return false;
    }

    internal PlanningToolDomainOutcome Classify(AliCompletedToolOutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = new AliFrameworkToolOutcomeKey(
            request.TurnIdentity,
            request.CallId,
            request.ToolName);
        if (!Contracts.TryGetValue(request.ToolName, out var contract))
        {
            _frameworkOutcomes.Discard(key);
            return PlanningToolDomainOutcome.Unreported;
        }

        return contract.Classify(request, key, _frameworkOutcomes);
    }

    internal void Discard(TurnIdentity turnIdentity, string callId, string toolName) =>
        _frameworkOutcomes.Discard(new AliFrameworkToolOutcomeKey(turnIdentity, callId, toolName));

    internal int DiscardTurn(TurnIdentity turnIdentity) =>
        _frameworkOutcomes.DiscardTurn(turnIdentity);

    private static IReadOnlyDictionary<string, AliToolOutcomeContract> CreateContracts()
    {
        var contracts = new Dictionary<string, AliToolOutcomeContract>(StringComparer.Ordinal);

        // Coordinator, retrieval, identity, and workstation tools.
        Exact<CoordinatorCapabilityResult>(AliCapabilityCatalog.ListAvailableToolsName);
        Outcome<SemanticToolDiscoveryResult>(
            AliCapabilityCatalog.SemanticDiscoverToolsName,
            result => result.ToolNames.Count > 0
                ? PlanningToolDomainOutcome.Succeeded
                : PlanningToolDomainOutcome.Failed);
        Success<CoordinatorActiveUserResult>(AliCapabilityCatalog.GetActiveUserProfileName, result => result.Selected);
        Outcome<CoordinatorMemoryResult>(
            AliCapabilityCatalog.RecallUserMemoryName,
            result => result.Warnings.Count == 0
                ? PlanningToolDomainOutcome.Succeeded
                : PlanningToolDomainOutcome.Unreported);
        Success<CoordinatorMemoryWriteResult>(AliCapabilityCatalog.MutateParticipantMemoryName, result => result.Saved);
        Success<CoordinatorParticipantMemoryConsentResult>(
            AliCapabilityCatalog.ConsentParticipantMemoryProposalName,
            result => result.Recorded || result.Ready);
        Success<CoordinatorMemoryReconciliationResult>(
            AliCapabilityCatalog.ReconcileParticipantMemoryMutationName,
            result => result.Succeeded);
        Outcome<CoordinatorMemoryResult>(
            AliCapabilityCatalog.ListCurrentUserMemoriesName,
            result => result.Warnings.Count == 0
                ? PlanningToolDomainOutcome.Succeeded
                : PlanningToolDomainOutcome.Unreported);
        Success<CoordinatorSourceResult>(AliCapabilityCatalog.SearchCurrentWebName, result => result.Sources.Count > 0);
        Success<CoordinatorNavigationLinkResult>(AliCapabilityCatalog.CreateGoogleMapsDirectionsLinkName, result => result.Success);
        Success<CoordinatorResearchResult>(AliCapabilityCatalog.ResearchWebName, result => result.Succeeded);
        Success<CoordinatorSourceResult>(AliCapabilityCatalog.SearchLocalLibraryName, result => result.Sources.Count > 0);
        Success<CoordinatorReminderResult>(AliCapabilityCatalog.CreateCalendarEventName, result => result.Saved);
        Exact<CoordinatorIdentityResult>(AliCapabilityCatalog.GetAssistantIdentityName);
        Outcome<string>(
            AliCapabilityCatalog.GetCurrentLocalTimeName,
            value => string.IsNullOrWhiteSpace(value)
                ? PlanningToolDomainOutcome.Failed
                : PlanningToolDomainOutcome.Succeeded);
        Success<WorkstationFileMoveResult>(AliCapabilityCatalog.FileMoveName, result => result.Success);
        Success<WorkstationFileOperationResult>(AliCapabilityCatalog.FileCopyName, result => result.Success);
        Success<WorkstationFileOperationResult>(AliCapabilityCatalog.FileCreateDirectoryName, result => result.Success);
        Success<WorkstationFileMetadataResult>(AliCapabilityCatalog.FileMetadataName, result => result.Success);
        Success<WorkstationArchiveResult>(AliCapabilityCatalog.ArchiveCreateName, result => result.Success);
        Success<WorkstationArchiveResult>(AliCapabilityCatalog.ArchiveListName, result => result.Success);
        Success<WorkstationArchiveResult>(AliCapabilityCatalog.ArchiveExtractName, result => result.Success);

        // Visual Studio, native, embedded, and language-neutral coding tools.
        Success<AliVisualStudioReport>(AliCapabilityCatalog.VisualStudioInspectName, result => result.Success);
        Success<AliVisualStudioActionResult>(AliCapabilityCatalog.VisualStudioBuildName, result => result.Success);
        Success<AliVisualStudioActionResult>(AliCapabilityCatalog.VisualStudioOpenName, result => result.Success);
        Success<AliGnuNativeToolchainReport>(AliCapabilityCatalog.GnuNativeInspectName, result => result.Success);
        Success<AliLanguageOperationResult>(AliCapabilityCatalog.GnuNativeExecuteName, result => result.Success);
        Success<AliArduinoReport>(AliCapabilityCatalog.ArduinoInspectName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoSearchLibrariesName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoInstallCoreName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoInstallLibraryName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoCreateCompileName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoCompileName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoUploadName, result => result.Success);
        Success<AliArduinoOperationResult>(AliCapabilityCatalog.ArduinoOpenIdeName, result => result.Success);
        Exact<IReadOnlyList<AliRaspberryPiLibrary>>(AliCapabilityCatalog.RaspberryPiLibrariesName);
        Success<AliRaspberryPiOperationResult>(AliCapabilityCatalog.RaspberryPiProbeName, result => result.Success);
        Success<AliRaspberryPiOperationResult>(AliCapabilityCatalog.RaspberryPiInspectLibrariesName, result => result.Success);
        Success<AliRaspberryPiOperationResult>(AliCapabilityCatalog.RaspberryPiSearchPackagesName, result => result.Success);
        Success<AliRaspberryPiOperationResult>(AliCapabilityCatalog.RaspberryPiDeployName, result => result.Success);
        Success<AliLanguageCapabilityReport>(AliCapabilityCatalog.CodingListCapabilitiesName, result => result.Success);
        Success<AliCodingProjectInspection>(AliCapabilityCatalog.CodingInspectProjectName, result => result.Success);
        Success<AliSourceIndexResult>(AliCapabilityCatalog.CodingIndexProjectName, result => result.Success);
        Success<AliSymbolSearchResult>(AliCapabilityCatalog.CodingSearchSymbolsName, result => result.Success);
        Success<AliLanguageOperationResult>(AliCapabilityCatalog.CodingAnalyzeProjectName, result => result.Success);
        Success<AliLanguageOperationResult>(AliCapabilityCatalog.CodingFormatProjectName, result => result.Success);
        Success<AliLanguageOperationResult>(AliCapabilityCatalog.CodingBuildProjectName, result => result.Success);
        Success<AliLanguageOperationResult>(AliCapabilityCatalog.CodingTestProjectName, result => result.Success);
        Success<AliLanguageOperationResult>(AliCapabilityCatalog.CodingRunProjectName, result => result.Success);
        Success<AliCrossLanguageArchitectureReport>(AliCapabilityCatalog.CodingInspectArchitectureName, result => result.Success);
        Success<AliCodeContextReport>(AliCapabilityCatalog.CodingBuildContextName, result => result.Success);
        Success<AliExternalServiceProbeResult>(AliCapabilityCatalog.CodingProbeServiceName, result => result.Success);
        Success<AliRuntimeProcessSnapshot>(AliCapabilityCatalog.CodingInspectProcessName, result => result.Success);

        // Roslyn and .NET engineering tools.
        Success<DotNetCreateProjectResult>(AliCapabilityCatalog.DotNetCreateProjectName, result => result.Success);
        Success<RoslynAnalysisResult>(AliCapabilityCatalog.RoslynAnalyzeProjectName, result => result.Success);
        Success<RoslynFormatResult>(AliCapabilityCatalog.RoslynFormatProjectName, result => result.Success);
        Success<RoslynSymbolResult>(AliCapabilityCatalog.RoslynFindSymbolName, result => result.Success);
        Success<RoslynCompletionResult>(AliCapabilityCatalog.RoslynGetCompletionsName, result => result.Success);
        Success<RoslynSolutionOverviewResult>(AliCapabilityCatalog.RoslynInspectSolutionName, result => result.Success);
        Success<RoslynDocumentResult>(AliCapabilityCatalog.RoslynInspectDocumentName, result => result.Success);
        Success<RoslynPositionResult>(AliCapabilityCatalog.RoslynInspectPositionName, result => result.Success);
        Success<RoslynReferenceResult>(AliCapabilityCatalog.RoslynFindReferencesName, result => result.Success);
        Success<AliRoslynTargetInspection>(AliCapabilityCatalog.RoslynInspectTargetName, result => result.Success);
        Success<AliRoslynActionListResult>(AliCapabilityCatalog.RoslynListActionsName, result => result.Success);
        Success<AliRoslynActionPreview>(AliCapabilityCatalog.RoslynPreviewActionName, result => result.Success);
        Success<AliRoslynActionApplication>(
            AliCapabilityCatalog.RoslynApplyActionName,
            result => result.Success && result.Applied);
        Success<AliRoslynActionVerification>(
            AliCapabilityCatalog.RoslynVerifyChangesetName,
            result => result.Success);
        Success<DotNetBuildResult>(AliCapabilityCatalog.DotNetBuildName, result => result.Success);
        Success<DotNetRunResult>(AliCapabilityCatalog.DotNetRunName, result => result.Success);
        Success<DotNetStopProjectResult>(AliCapabilityCatalog.DotNetStopProjectName, result => result.Success);
        Success<DotNetTestResult>(AliCapabilityCatalog.DotNetTestName, result => result.Success);
        Success<DotNetVerificationResult>(AliCapabilityCatalog.DotNetVerifyName, result => result.Success);
        Success<DotNetDebugSessionResult>(AliCapabilityCatalog.DotNetDebugLaunchName, result => result.Success);
        Success<DotNetDebugSessionResult>(AliCapabilityCatalog.DotNetDebugAttachName, result => result.Success);
        Success<DotNetDebugSnapshot>(AliCapabilityCatalog.DotNetDebugInspectName, result => result.Success);
        Success<DotNetDebugEvaluation>(AliCapabilityCatalog.DotNetDebugEvaluateName, result => result.Success);
        Exact<IReadOnlyList<DebugBreakpoint>>(AliCapabilityCatalog.DotNetDebugBreakpointsName);
        Success<DotNetDebugControlResult>(AliCapabilityCatalog.DotNetDebugControlName, result => result.Success);
        Success<DotNetDebugControlResult>(AliCapabilityCatalog.DotNetDebugStopName, result => result.Success);
        Success<DebugDiagnosticsHandoff>(AliCapabilityCatalog.DotNetDebugDiagnosticsHandoffName, result => result.Success);
        Success<DependencyInspectionResult>(AliCapabilityCatalog.DotNetDependencyInspectName, result => result.Success);
        Success<DependencyChangeResult>(AliCapabilityCatalog.DotNetDependencyPreviewName, result => result.Success);
        Success<DependencyChangeResult>(AliCapabilityCatalog.DotNetDependencyApplyName, result => result.Success && result.Applied);
        Success<SourceControlResult>(AliCapabilityCatalog.GitStatusName, result => result.Success);
        Success<SourceControlResult>(AliCapabilityCatalog.GitDiffName, result => result.Success);
        Success<SourceControlResult>(AliCapabilityCatalog.GitHistoryName, result => result.Success);
        Success<SourceControlResult>(AliCapabilityCatalog.GitBlameName, result => result.Success);
        Success<SourceControlResult>(AliCapabilityCatalog.GitCreateBranchName, result => result.Success);
        Success<SourceControlResult>(AliCapabilityCatalog.GitCommitName, result => result.Success);
        Success<SourceControlResult>(AliCapabilityCatalog.GitPushName, result => result.Success);
        Success<ArchitectureInspectionResult>(AliCapabilityCatalog.ArchitectureInspectName, result => result.Success);
        Success<ArchitectureBoundaryResult>(AliCapabilityCatalog.ArchitectureCheckName, result => result.Success);
        Success<QualityScanResult>(AliCapabilityCatalog.DotNetQualityScanName, result => result.Success);
        Success<PerformanceRunResult>(AliCapabilityCatalog.DotNetPerformanceMeasureName, result => result.Success);
        Success<PerformanceComparisonResult>(AliCapabilityCatalog.DotNetPerformanceCompareName, result => result.Success);
        Success<PerformanceTraceResult>(AliCapabilityCatalog.DotNetPerformanceTraceName, result => result.Success);
        Success<ApplicationVerificationResult>(AliCapabilityCatalog.DotNetApplicationVerifyName, result => result.Success);
        Success<DotNetReleaseResult>(AliCapabilityCatalog.DotNetReleasePublishName, result => result.Success);
        Success<EngineeringReportResult>(AliCapabilityCatalog.DotNetArchitectureReportName, result => result.Success);
        Success<AutonomousDeliveryResult>(AliCapabilityCatalog.DotNetDeliveryVerifyName, result => result.Success);

        // Agent Framework file providers use ambiguous model-facing strings. Only
        // exact provider/store boundary signals are authoritative.
        FrameworkRead(AliCapabilityCatalog.FileReadName);
        FrameworkList(AliCapabilityCatalog.FileListName);
        FrameworkList(AliCapabilityCatalog.FileSearchName);
        FrameworkMutation(AliCapabilityCatalog.FileWriteName);
        FrameworkMutation(AliCapabilityCatalog.FileDeleteName);
        FrameworkMutation(AliCapabilityCatalog.FileReplaceName);
        FrameworkMutation(AliCapabilityCatalog.FileReplaceLinesName);
        FrameworkRead(AliCapabilityCatalog.WorkMemoryReadName);
        FrameworkList(AliCapabilityCatalog.WorkMemoryListName);
        FrameworkList(AliCapabilityCatalog.WorkMemorySearchName);
        FrameworkMutation(AliCapabilityCatalog.WorkMemoryWriteName);
        FrameworkMutation(AliCapabilityCatalog.WorkMemoryDeleteName);
        FrameworkMutation(AliCapabilityCatalog.WorkMemoryReplaceName);
        FrameworkMutation(AliCapabilityCatalog.WorkMemoryReplaceLinesName);

        // The later runner hook records these only after exact MAF session-state or
        // AgentSkillsSource verification. Until that signal exists they remain Unreported.
        FrameworkRead(AliCapabilityCatalog.GetAgentModeName);
        FrameworkMutation(AliCapabilityCatalog.SetAgentModeName);
        FrameworkMutation(AliCapabilityCatalog.LoadAgentSkillName);
        FrameworkRead(AliCapabilityCatalog.ReadAgentSkillResourceName);
        FrameworkMutation(AliCapabilityCatalog.RunAgentSkillScriptName);

        return contracts.ToFrozenDictionary(StringComparer.Ordinal);

        void Add(AliToolOutcomeContract contract)
        {
            if (!contracts.TryAdd(contract.ToolName, contract))
            {
                throw new InvalidOperationException(
                    $"Duplicate production outcome contract '{contract.ToolName}'.");
            }
        }

        void Exact<TResult>(string toolName) where TResult : notnull =>
            Outcome<TResult>(toolName, static _ => PlanningToolDomainOutcome.Succeeded);

        void Success<TResult>(string toolName, Func<TResult, bool> succeeded) where TResult : notnull =>
            Outcome<TResult>(
                toolName,
                result => succeeded(result)
                    ? PlanningToolDomainOutcome.Succeeded
                    : PlanningToolDomainOutcome.Failed);

        void Outcome<TResult>(
            string toolName,
            Func<TResult, PlanningToolDomainOutcome> classify) where TResult : notnull =>
            Add(AliToolOutcomeContract.Typed(toolName, classify));

        void FrameworkRead(string toolName) =>
            Add(AliToolOutcomeContract.ProviderBoundary(
                toolName,
                signal => signal switch
                {
                    AliFrameworkToolOutcomeSignal.Found => PlanningToolDomainOutcome.Succeeded,
                    AliFrameworkToolOutcomeSignal.NotFound
                        or AliFrameworkToolOutcomeSignal.Rejected
                        or AliFrameworkToolOutcomeSignal.Failed => PlanningToolDomainOutcome.Failed,
                    _ => PlanningToolDomainOutcome.Unreported
                }));

        void FrameworkList(string toolName) =>
            Add(AliToolOutcomeContract.ProviderBoundary(
                toolName,
                signal => signal switch
                {
                    AliFrameworkToolOutcomeSignal.Completed
                        or AliFrameworkToolOutcomeSignal.NoMatches => PlanningToolDomainOutcome.Succeeded,
                    AliFrameworkToolOutcomeSignal.Rejected
                        or AliFrameworkToolOutcomeSignal.Failed => PlanningToolDomainOutcome.Failed,
                    _ => PlanningToolDomainOutcome.Unreported
                }));

        void FrameworkMutation(string toolName) =>
            Add(AliToolOutcomeContract.ProviderBoundary(
                toolName,
                signal => signal switch
                {
                    AliFrameworkToolOutcomeSignal.Completed
                        or AliFrameworkToolOutcomeSignal.Found => PlanningToolDomainOutcome.Succeeded,
                    AliFrameworkToolOutcomeSignal.NotFound
                        or AliFrameworkToolOutcomeSignal.Rejected
                        or AliFrameworkToolOutcomeSignal.Failed => PlanningToolDomainOutcome.Failed,
                    _ => PlanningToolDomainOutcome.Unreported
                }));
    }

    private sealed class AliToolOutcomeContract
    {
        private readonly Func<
            AliCompletedToolOutcomeRequest,
            AliFrameworkToolOutcomeKey,
            AliFrameworkToolOutcomeSidecar,
            PlanningToolDomainOutcome> _classify;
        private readonly Func<object?, PlanningToolDomainOutcome>? _classifyTypedReturn;

        private AliToolOutcomeContract(
            string toolName,
            AliToolOutcomeContractKind kind,
            Func<
                AliCompletedToolOutcomeRequest,
                AliFrameworkToolOutcomeKey,
                AliFrameworkToolOutcomeSidecar,
                PlanningToolDomainOutcome> classify,
            Func<object?, PlanningToolDomainOutcome>? classifyTypedReturn = null)
        {
            ToolName = toolName;
            Kind = kind;
            Id = $"ali-outcome.{toolName}.v1";
            _classify = classify;
            _classifyTypedReturn = classifyTypedReturn;
        }

        public string ToolName { get; }

        public string Id { get; }

        public AliToolOutcomeContractKind Kind { get; }

        public PlanningToolDomainOutcome Classify(
            AliCompletedToolOutcomeRequest request,
            AliFrameworkToolOutcomeKey key,
            AliFrameworkToolOutcomeSidecar sidecar) =>
            _classify(request, key, sidecar);

        public PlanningToolDomainOutcome ClassifyTypedReturn(object? result) =>
            Kind == AliToolOutcomeContractKind.TypedReturn
                ? _classifyTypedReturn?.Invoke(result)
                    ?? PlanningToolDomainOutcome.Unreported
                : PlanningToolDomainOutcome.Unreported;

        public static AliToolOutcomeContract Typed<TResult>(
            string toolName,
            Func<TResult, PlanningToolDomainOutcome> classify) where TResult : notnull =>
            new(
                toolName,
                AliToolOutcomeContractKind.TypedReturn,
                (request, key, sidecar) =>
                {
                    sidecar.Discard(key);
                    return request.Result is TResult typed
                        ? classify(typed)
                        : PlanningToolDomainOutcome.Unreported;
                },
                result => TryReadTypedReturn<TResult>(result, out var typed)
                    ? classify(typed!)
                    : PlanningToolDomainOutcome.Unreported);

        public static AliToolOutcomeContract ProviderBoundary(
            string toolName,
            Func<AliFrameworkToolOutcomeSignal, PlanningToolDomainOutcome> classify) =>
            new(
                toolName,
                AliToolOutcomeContractKind.ProviderBoundarySignal,
                (_, key, sidecar) => sidecar.TryConsume(key, out var signal)
                    ? classify(signal)
                    : PlanningToolDomainOutcome.Unreported);
    }
}
