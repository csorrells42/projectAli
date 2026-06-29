using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Ali.Core.Coding;
using Ali.Infrastructure.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ali.Infrastructure.Coding;

public sealed class LocalCodingToolService(
    CodingWorkspacePolicy policy,
    string dataRoot,
    ICodingProcessLauncher? processLauncher = null,
    ICodingCommandRunner? commandRunner = null,
    string? configuredNotepadPlusPlusPath = null,
    string? configuredVisualStudioPath = null,
    string? pdfWorkspaceRoot = null) : ILocalCodingTool
{
    private static readonly JsonSerializerOptions RoadmapJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private const int MaxListedEntries = 120;
    private const int MaxSearchMatches = 30;
    private const int MaxReadCharacters = 12_000;
    private const int MaxCommandOutputCharacters = 8_000;
    private const int MaxContextPackCharacters = 18_000;
    private const int MaxContextSearchMatches = 14;
    private const int MaxReceiptEntries = 12;
    private const int MaxDiagnosticLines = 12;
    private const int MaxEditContentCharacters = 20_000;
    private const int MaxPdfTextCharacters = 40_000;
    private const int MaxReplaceFileCharacters = 500_000;
    private const int MaxPatchBundleEdits = 8;
    private const int MaxWorkspaceSummaryEntries = 20;
    private static readonly TimeSpan DotNetCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] IgnoredDirectoryNames =
    [
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "packages",
        ".agents",
        ".codex"
    ];
    private static readonly char[] ContextTokenSeparators =
        [' ', '\t', '\r', '\n', ',', '.', '?', '!', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', '"', '\'', '`'];
    private static readonly HashSet<string> CodingContextTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "app",
        "async",
        "build",
        "bug",
        "class",
        "code",
        "compile",
        "compiler",
        "csharp",
        "debug",
        "dependency",
        "dotnet",
        "error",
        "exception",
        "fail",
        "failed",
        "fix",
        "function",
        "method",
        "namespace",
        "package",
        "project",
        "solution",
        "test",
        "wpf"
    };
    private static readonly HashSet<string> ContextStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "after",
        "again",
        "also",
        "and",
        "are",
        "can",
        "could",
        "does",
        "for",
        "from",
        "have",
        "help",
        "how",
        "into",
        "just",
        "like",
        "need",
        "please",
        "should",
        "that",
        "the",
        "this",
        "what",
        "when",
        "where",
        "with",
        "would",
        "you"
    };

    private readonly ICodingProcessLauncher _processLauncher = processLauncher ?? new CodingProcessLauncher();
    private readonly ICodingCommandRunner _commandRunner = commandRunner ?? new CodingCommandRunner();
    private readonly string _actionLogPath = Path.Combine(dataRoot, "coding-tool-actions.jsonl");
    private readonly string _roadmapStatePath = Path.Combine(dataRoot, "Coding", "roadmap-execution-state.json");
    private readonly string _approvedPacketPath = Path.Combine(dataRoot, "Coding", "approved-step-packet.json");
    private string _pdfWorkspaceRoot = string.IsNullOrWhiteSpace(pdfWorkspaceRoot)
        ? Path.Combine(dataRoot, "GeneratedDocuments")
        : Path.GetFullPath(pdfWorkspaceRoot.Trim().Trim('"'));
    private CodingToolRequest? _lastDotNetRequest;
    private CodingToolResult? _lastDotNetResult;
    private CodingToolRequest? _lastPatchPreviewRequest;
    private CodingToolRequest? _lastRoadmapRequest;
    private bool _lastRoadmapApproved;
    private bool _approvedRoadmapStarted;
    private bool _roadmapStateLoaded;
    private RoadmapExecutionState? _roadmapState;
    private bool _approvedPacketLoaded;
    private ApprovedRoadmapExecutionPacket? _approvedPacket;
    private string? _configuredNotepadPlusPlusPath = configuredNotepadPlusPlusPath;
    private string? _configuredVisualStudioPath = configuredVisualStudioPath;

    public CodingWorkspacePolicy Policy { get; private set; } = policy;

    public void UpdatePolicy(CodingWorkspacePolicy policy)
    {
        Policy = policy;
    }

    public void UpdateSettings(CodingToolSettings settings)
    {
        Policy = settings.ToPolicy();
        _configuredNotepadPlusPlusPath = settings.NotepadPlusPlusPath;
        _configuredVisualStudioPath = settings.VisualStudioPath;
        _pdfWorkspaceRoot = settings.ResolvePdfWorkspaceRoot(dataRoot);
    }

    public async Task<CodingToolResult> TryHandleAsync(
        string userText,
        CancellationToken cancellationToken)
    {
        if (!CodingToolRequestParser.TryParse(userText, out var request))
        {
            return CodingToolResult.NotHandled;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var permission = Policy.Evaluate(request);
        if (permission.Kind == CodingToolPermissionKind.Deny)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: {permission.Reason}");
        }

        if (permission.Kind == CodingToolPermissionKind.RequireConfirmation)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool needs confirmation: {permission.Reason}");
        }

        var result = request.Action switch
        {
            CodingToolAction.OpenWorkspace => await OpenWorkspaceAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ListWorkspace => ListWorkspace(),
            CodingToolAction.InspectWorkspace => InspectWorkspace(),
            CodingToolAction.AnalyzeArchitecture => AnalyzeArchitecture(),
            CodingToolAction.ShowProjectIntelligence => ShowProjectIntelligence(),
            CodingToolAction.ShowRepoUnderstanding => await ShowRepoUnderstandingAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowSafeCommitCheck => await ShowSafeCommitCheckAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowWorkspaceHealthScore => await ShowWorkspaceHealthScoreAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.DraftCommitMessage => await DraftCommitMessageAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.DraftReleaseNotes => await DraftReleaseNotesAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowCodingSessionTimeline => ShowCodingSessionTimeline(),
            CodingToolAction.ShowRollbackPlan => await ShowRollbackPlanAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowUiChangeChecklist => ShowUiChangeChecklist(request),
            CodingToolAction.ComposeTypedPatch => await ComposeTypedPatchAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowFileRiskLabels => await ShowFileRiskLabelsAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.FindSymbol => FindSymbol(request),
            CodingToolAction.ShowCrossReferenceMap => ShowCrossReferenceMap(request),
            CodingToolAction.ShowTestGapReport => await ShowTestGapReportAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ExplainKnownError => ExplainKnownError(request),
            CodingToolAction.PreviewRollbackPatch => await PreviewRollbackPatchAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowFullCodingReadiness => await ShowFullCodingReadinessAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowValidationLedger => ShowValidationLedger(),
            CodingToolAction.ShowCSharpSymbolIndex => ShowCSharpSymbolIndex(),
            CodingToolAction.ShowCallGraph => ShowCallGraph(request),
            CodingToolAction.ResolveSemanticSymbol => ResolveSemanticSymbol(request),
            CodingToolAction.ShowImpactedTests => await ShowImpactedTestsAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.PlanSemanticEdit => await PlanSemanticEditAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.MapCompilerDiagnostic => MapCompilerDiagnostic(request),
            CodingToolAction.VerifyXamlBindings => VerifyXamlBindings(),
            CodingToolAction.VerifyCommandBindings => VerifyCommandBindings(),
            CodingToolAction.ScanDeadCommands => ScanDeadCommands(),
            CodingToolAction.PlanTask => await PlanTaskAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.InterpretBuildGoal => InterpretBuildGoal(request),
            CodingToolAction.ShowArchitectureOptions => ShowArchitectureOptions(request),
            CodingToolAction.WriteAcceptanceCriteria => WriteAcceptanceCriteria(request),
            CodingToolAction.SuggestFeatureTests => SuggestFeatureTests(request),
            CodingToolAction.DetectCodebasePatterns => DetectCodebasePatterns(),
            CodingToolAction.PlanFeatureFiles => PlanFeatureFiles(request),
            CodingToolAction.ShowRefactorSafetyChecklist => ShowRefactorSafetyChecklist(request),
            CodingToolAction.ExploreBuildIdea => ExploreBuildIdea(request),
            CodingToolAction.DraftImplementationRoadmap => DraftImplementationRoadmap(request),
            CodingToolAction.ShowLastRoadmap => ShowLastRoadmap(),
            CodingToolAction.DiscardLastRoadmap => DiscardLastRoadmap(),
            CodingToolAction.ApproveLastRoadmap => ApproveLastRoadmap(),
            CodingToolAction.StartApprovedRoadmap => StartApprovedRoadmap(),
            CodingToolAction.ShowActiveRoadmapStep => ShowActiveRoadmapStep(),
            CodingToolAction.ShowNextRoadmapAction => await ShowNextRoadmapActionAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowRoadmapExecutionPacket => await ShowRoadmapExecutionPacketAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ApproveRoadmapExecutionPacket => await ApproveRoadmapExecutionPacketAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowApprovedRoadmapExecutionPacket => ShowApprovedRoadmapExecutionPacket(),
            CodingToolAction.DiscardApprovedRoadmapExecutionPacket => DiscardApprovedRoadmapExecutionPacket(),
            CodingToolAction.ShowRoadmapExecutionPacketProgress => await ShowRoadmapExecutionPacketProgressAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowApprovedPacketCommands => ShowApprovedPacketCommands(),
            CodingToolAction.RunApprovedPacketItem => await RunApprovedPacketItemAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowPacketRunLedger => await ShowPacketRunLedgerAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.PlanPackageLookup => PlanPackageLookup(request),
            CodingToolAction.PlanDependencyInstallPacket => PlanDependencyInstallPacket(request),
            CodingToolAction.PlanPostEditValidation => await PlanPostEditValidationAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.PreviewProjectScaffold => PreviewProjectScaffold(request),
            CodingToolAction.PlanScaffoldApply => PlanScaffoldApply(request),
            CodingToolAction.ResumeBuildPlan => await ResumeBuildPlanAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowBuilderCommandIndex => ShowBuilderCommandIndex(),
            CodingToolAction.ShowCodingSessionSummary => await ShowCodingSessionSummaryAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowWindowsTroubleshootingToolkit => ShowWindowsTroubleshootingToolkit(),
            CodingToolAction.PlanRogueProcessHunt => PlanRogueProcessHunt(request),
            CodingToolAction.CollectProcessEvidence => CollectProcessEvidence(request),
            CodingToolAction.DiagnosePortOwner => await DiagnosePortOwnerAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.DiagnoseFileLock => DiagnoseFileLock(request),
            CodingToolAction.InspectServicesStartup => InspectServicesStartup(),
            CodingToolAction.TriageEventLogs => TriageEventLogs(),
            CodingToolAction.PlanProcessStop => PlanProcessStop(request),
            CodingToolAction.ExecuteProcessStop => await ExecuteProcessStopAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.DiagnoseBuildLock => DiagnoseBuildLock(),
            CodingToolAction.ClassifyLastFailure => ClassifyLastFailure(),
            CodingToolAction.ReviewCurrentChanges => await ReviewCurrentChangesAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowRoadmapStepChecklist => await ShowRoadmapStepChecklistAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowInstallDoctor => ShowInstallDoctor(),
            CodingToolAction.AdvanceRoadmapStep => AdvanceRoadmapStep(),
            CodingToolAction.PauseRoadmap => PauseRoadmap(),
            CodingToolAction.ResumeRoadmap => ResumeRoadmap(),
            CodingToolAction.FinishRoadmap => FinishRoadmap(),
            CodingToolAction.RecoverRoadmapState => RecoverRoadmapState(),
            CodingToolAction.DiagnoseRecoveryState => await DiagnoseRecoveryStateAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowReceipts => ShowReceipts(),
            CodingToolAction.ShowUserCommandHelp => ShowUserCommandHelp(),
            CodingToolAction.ShowComputerAssistantStatus => ShowComputerAssistantStatus(),
            CodingToolAction.ShowComputerAssistantCommandIndex => ShowComputerAssistantCommandIndex(),
            CodingToolAction.PlanFileOrganization => PlanFileOrganization(request),
            CodingToolAction.PlanDiskCleanup => PlanDiskCleanup(request),
            CodingToolAction.PlanAppInstallTroubleshooting => PlanAppInstallTroubleshooting(request),
            CodingToolAction.PlanPeripheralSetup => PlanPeripheralSetup(request),
            CodingToolAction.ShowComputerTroubleshootingCommandIndex => ShowComputerTroubleshootingCommandIndex(),
            CodingToolAction.PlanComputerTroubleshooting => PlanComputerTroubleshooting(request),
            CodingToolAction.ShowPdfToolStatus => ShowPdfToolStatus(),
            CodingToolAction.ShowPdfCommandIndex => ShowPdfCommandIndex(),
            CodingToolAction.ShowToolIntegrationStatus => ShowToolIntegrationStatus(),
            CodingToolAction.GenerateVisualStudioHandoff => GenerateVisualStudioHandoff(),
            CodingToolAction.GeneratePdf => await GeneratePdfAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GenerateCodingReport => await GenerateCodingReportAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GenerateMorningReport => await GenerateMorningReportAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GenerateInstallReport => await GenerateInstallReportAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GenerateTroubleshootingReport => await GenerateTroubleshootingReportAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.InspectPdf => await InspectPdfAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ExtractPdfText => await ExtractPdfTextAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.SummarizePdf => await SummarizePdfAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ConvertMarkdownToPdf => await ConvertMarkdownToPdfAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.CombinePdfs => await CombinePdfsAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.SplitPdf => await SplitPdfAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.OpenLastDiagnostic => await OpenLastDiagnosticAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.DiagnoseLastFailure => await DiagnoseLastFailureAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.SuggestLastFailurePatch => await SuggestLastFailurePatchAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.ListPackages => ListPackages(request),
            CodingToolAction.SearchWorkspace => SearchWorkspace(request),
            CodingToolAction.ReadFile => await ReadFileAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.PreviewReplaceText => await PreviewReplaceTextAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.PreviewPatchBundle => await PreviewPatchBundleAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ShowLastPatchPreview => await ShowLastPatchPreviewAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.DiscardLastPatchPreview => DiscardLastPatchPreview(),
            CodingToolAction.ApplyLastPatchPreview => await ApplyLastPatchPreviewAsync(cancellationToken).ConfigureAwait(false),
            CodingToolAction.CreateFile or CodingToolAction.AppendFile or CodingToolAction.ReplaceText =>
                await EditFileAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.OpenSolution => await OpenSolutionAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.Build or CodingToolAction.Test or CodingToolAction.Restore
                or CodingToolAction.ListOutdatedPackages or CodingToolAction.AddPackage
                or CodingToolAction.RunProject =>
                await RunDotNetCommandAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GitStatus or CodingToolAction.GitDiff or CodingToolAction.GitLog
                or CodingToolAction.GitAdd or CodingToolAction.GitCommit or CodingToolAction.GitMerge
                or CodingToolAction.GitPull or CodingToolAction.GitPush =>
                await RunGitCommandAsync(request, cancellationToken).ConfigureAwait(false),
            _ => await OpenFileAsync(request, cancellationToken).ConfigureAwait(false)
        };

        StoreLastPatchPreview(request, result);
        StoreLastRoadmap(request, result);
        await AppendLogAsync(request, result, permission, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CodingContextPack> BuildContextPackAsync(
        string userText,
        CancellationToken cancellationToken)
    {
        return await BuildContextPackAsync(userText, force: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingContextPack> BuildContextPackAsync(
        string userText,
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!force && !ShouldBuildCodingContext(userText))
        {
            return CodingContextPack.Empty;
        }

        var includesLastFailure = _lastDotNetResult is { Succeeded: false };
        var lines = new List<string>
        {
            "Ali coding context pack (read-only).",
            "Use this context to answer coding questions about the approved local workspace.",
            "Do not claim files were changed, builds were run, or tests were run unless a tool result below proves it.",
            "When proposing code changes, keep them small and tell the user edits require explicit confirmation before Ali writes files.",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Current user request: {userText.Trim()}"
        };

        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            lines.Add($"Coding workspace does not exist yet: {Policy.WorkspaceRoot}");
            return new CodingContextPack(true, string.Join(Environment.NewLine, lines), includesLastFailure);
        }

        var inspection = InspectWorkspace();
        AddContextSection(lines, "Workspace map", inspection.Message, 5_000);

        var packageReport = ListPackages(new CodingToolRequest(CodingToolAction.ListPackages, null));
        if (packageReport.Succeeded)
        {
            AddContextSection(lines, "Package references", packageReport.Message, 4_000);
        }

        var relevantFiles = EnumerateWorkspaceFiles()
            .Where(IsContextRelevantFile)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWorkspaceSummaryEntries)
            .Select(RelativeToWorkspace)
            .ToList();
        if (relevantFiles.Count > 0)
        {
            lines.Add("Relevant source/config files:");
            lines.AddRange(relevantFiles.Select(path => $"- {path}"));
        }

        if (_lastDotNetRequest is not null && _lastDotNetResult is { Succeeded: false } lastDotNetResult)
        {
            AddContextSection(
                lines,
                "Last failed dotnet command",
                lastDotNetResult.Message,
                5_500);
            await AddDiagnosticFileExcerptsAsync(lines, lastDotNetResult.Message, cancellationToken).ConfigureAwait(false);
        }

        var searchTerms = ExtractContextSearchTerms(userText);
        var matches = FindContextMatches(searchTerms);
        if (matches.Count > 0)
        {
            lines.Add("Relevant workspace matches:");
            lines.AddRange(matches.Select(match => $"- {match}"));
        }

        return new CodingContextPack(
            true,
            TrimForChat(string.Join(Environment.NewLine, lines), MaxContextPackCharacters),
            includesLastFailure);
    }

    public Task<CodingTaskPlan> BuildTaskPlanAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!contextPack.HasContext && !ShouldBuildCodingContext(userText))
        {
            return Task.FromResult(CodingTaskPlan.Empty);
        }

        var goal = string.IsNullOrWhiteSpace(userText)
            ? "coding task"
            : userText.Trim();
        var wantsEdit = MentionsAny(goal, "add", "change", "edit", "fix", "implement", "modify", "patch", "repair", "update", "write");
        var wantsVerification = MentionsAny(goal, "build", "compile", "run", "test", "verify");
        var wantsGit = MentionsAny(goal, "commit", "git", "merge", "push", "pull");
        var wantsVisualStudio = MentionsAny(goal, "devenv", "extension", "ide", "tool window", "visual studio", "vsix");
        var requiresConfirmation = wantsEdit || wantsVerification || wantsGit || contextPack.IncludesLastFailure;

        var lines = new List<string>
        {
            "Coding task plan:",
            $"Goal: {goal}",
            "Receipts available:",
            $"- Approved workspace: {Policy.WorkspaceRoot}",
            contextPack.HasContext
                ? "- Read-only project context: workspace map, package references, and relevant files are available."
                : "- Read-only project context: not available yet."
        };

        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : null;
        if (!string.IsNullOrWhiteSpace(primaryTarget))
        {
            lines.Add($"- Primary solution/project: {primaryTarget}");
        }

        if (contextPack.IncludesLastFailure)
        {
            lines.Add("- Last failed dotnet command: diagnostic summary and source excerpts are available.");
        }

        lines.Add("Proposed steps:");
        var step = 1;
        lines.Add($"{step++}. Inspect the provided workspace context and identify the smallest relevant files.");
        if (contextPack.IncludesLastFailure)
        {
            lines.Add($"{step++}. Use the last dotnet diagnostic and included file excerpts to explain the likely failure.");
        }
        else
        {
            lines.Add($"{step++}. Read or search only the files needed for this goal.");
        }

        lines.Add($"{step++}. Propose the smallest safe change or answer, with file paths and line references when available.");
        lines.Add($"{step++}. Identify the likely impact surface: command parser, policy gate, local service behavior, tests, and docs when the task changes Ali's coding command surface.");
        if (wantsEdit || contextPack.IncludesLastFailure)
        {
            lines.Add($"{step++}. Wait for explicit confirmation before writing files. Confirmed edits must use the guarded file-edit path.");
        }

        if (wantsVerification || wantsEdit || contextPack.IncludesLastFailure)
        {
            lines.Add($"{step++}. After confirmed edits, run only the relevant confirmed build/test command and report the result.");
        }

        if (wantsGit)
        {
            lines.Add($"{step++}. Use read-only git status/diff first; staging, commits, merges, pull, or push require their configured confirmation gates.");
        }

        if (wantsVisualStudio)
        {
            lines.Add($"{step++}. Use `generate visual studio integration plan` for the deterministic VS extension/companion handoff before implementing UI-side integration.");
        }

        lines.Add("Impact checklist:");
        lines.Add("- Parser: add or adjust command phrases only when the command is deterministic.");
        lines.Add("- Policy: keep read-only actions allowed and writes/builds/Git behind existing confirmation gates.");
        lines.Add("- Service: record receipts and avoid claiming external IDE state unless the launcher/tool result proves it.");
        lines.Add("- Tests/docs: cover parser routing, service output, and the user-facing truth boundary.");
        lines.Add("Permission gates:");
        lines.Add("- Read/open/search/inspect inside the approved workspace can proceed as read-only actions.");
        lines.Add("- File writes require an explicit confirmation phrase before Ali changes files.");
        lines.Add("- Build, test, restore, package install, and run require confirmation before execution.");
        lines.Add("- Git write/network actions follow the Git permission settings and may be blocked.");

        return Task.FromResult(new CodingTaskPlan(
            true,
            string.Join(Environment.NewLine, lines),
            requiresConfirmation));
    }

    private async Task<CodingToolResult> PlanTaskAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        var goal = request.Query ?? "coding task";
        var contextPack = await BuildContextPackAsync(goal, force: true, cancellationToken).ConfigureAwait(false);
        var plan = await BuildTaskPlanAsync(goal, contextPack, cancellationToken).ConfigureAwait(false);
        return new CodingToolResult(
            true,
            plan.HasPlan,
            plan.HasPlan ? plan.Text : "Coding task planner needs a clearer coding goal.",
            "Coding task planner",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult InterpretBuildGoal(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "unspecified build goal");
        var summaries = GetWorkspaceProjectSummaries();
        var primaryTarget = GetPrimaryTarget();
        var lines = new List<string>
        {
            "Build goal interpreter:",
            $"Goal: {goal}",
            "No files were changed.",
            "Interpretation:",
            $"- Project type: {ClassifyGoalType(goal)}",
            $"- Likely first milestone: one owner-visible workflow with a narrow validation receipt.",
            $"- Primary solution/project: {primaryTarget ?? "not found"}",
            $"- Current workspace fit: {summaries.Count} project file(s) detected."
        };

        AddArchitectureRecommendationCards(lines, goal, summaries);
        lines.Add("Approval checkpoints:");
        lines.Add("- Choose one architecture option before edits.");
        lines.Add("- Approve package/library lookup before treating candidates as current.");
        lines.Add("- Approve dependency install, file edits, build/test, and Git writes through their normal gates.");
        lines.Add("Suggested next commands:");
        lines.Add($"- show architecture options {goal}");
        lines.Add($"- write acceptance criteria {goal}");
        lines.Add($"- suggest tests for {goal}");
        lines.Add($"- draft implementation roadmap {goal}");
        lines.Add($"- plan package lookup {goal}");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Build goal interpreter", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowArchitectureOptions(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current build goal");
        var summaries = GetWorkspaceProjectSummaries();
        var lines = new List<string>
        {
            "Architecture option cards:",
            $"Goal: {goal}",
            "No files were changed.",
            "Truth boundary: these are design options, not implementation proof."
        };

        AddArchitectureRecommendationCards(lines, goal, summaries);
        lines.Add("Decision guide:");
        lines.Add("- Prefer the path that gives a visible result with the fewest new dependencies.");
        lines.Add("- Use a library boundary when logic must be shared by WPF, WebHelper, CLI, or VSIX.");
        lines.Add("- Use an adapter/bridge boundary when talking to Visual Studio, SolidWorks, browsers, shells, or external processes.");
        lines.Add("Next commands:");
        lines.Add($"- plan feature files {goal}");
        lines.Add($"- show refactor safety checklist {goal}");
        lines.Add($"- draft implementation roadmap {goal}");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Architecture options", Policy.WorkspaceRoot);
    }

    private CodingToolResult WriteAcceptanceCriteria(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current feature");
        var lines = new List<string>
        {
            "Acceptance criteria:",
            $"Goal: {goal}",
            "No files were changed.",
            "Done means:",
            "- The owner-facing command or UI path is named and deterministic.",
            "- The happy path produces a visible result or receipt.",
            "- Failure paths say what happened and what safe command comes next.",
            "- Permission gates are unchanged or explicitly documented.",
            "- Focused parser/service tests cover the new behavior.",
            "- Build/test validation has a passing receipt.",
            "- User/engineering docs describe the capability and the boundary."
        };

        if (MentionsAny(goal, "package", "library", "dependency", "nuget", "install"))
        {
            lines.Add("- Dependency changes include restore/build validation and rollback notes.");
        }

        if (MentionsAny(goal, "visual studio", "vsix", "ide", "tool window"))
        {
            lines.Add("- Visual Studio changes preserve Ali helper approval gates and do not create direct write authority inside VS.");
        }

        if (MentionsAny(goal, "screenshot", "image", "bug", "vision"))
        {
            lines.Add("- Screenshot analysis separates visible evidence, inferred cause, and required code/log confirmation.");
        }

        lines.Add("Next commands:");
        lines.Add($"- suggest tests for {goal}");
        lines.Add("- plan post edit validation");
        lines.Add("- show roadmap step checklist");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Acceptance criteria", Policy.WorkspaceRoot);
    }

    private CodingToolResult SuggestFeatureTests(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current feature");
        var files = Directory.Exists(Policy.WorkspaceRoot)
            ? EnumerateWorkspaceFiles().Take(10_000).ToList()
            : [];
        var summaries = GetWorkspaceProjectSummaries();
        var buildCommands = DiscoverBuildCommands(files, summaries, GetPrimaryTarget()).ToList();
        var testCommands = DiscoverTestCommands(files, summaries, GetPrimaryTarget()).ToList();
        var lines = new List<string>
        {
            "Feature test suggestions:",
            $"Goal: {goal}",
            "No files were changed.",
            $"Detected stacks: {FormatInlineList(DetectStackSignals(files, summaries))}",
            "Focused tests:",
            "- Parser route test for every new owner phrase.",
            "- Service output test for key sections and truth boundaries.",
            "- Policy test if permission behavior changes.",
            "- Regression test for the bug or workflow being improved.",
            "- Full harness after command-surface or shared-service changes."
        };

        if (MentionsAny(goal, "ui", "visual studio", "vsix", "webhelper", "companion", "screen"))
        {
            lines.Add("- UI smoke check for button visibility, command staging, and non-overlapping output.");
        }

        if (MentionsAny(goal, "package", "dependency", "restore", "build", "test"))
        {
            lines.Add("- Package/build tests must prove confirmation is required before restore/install/build/test execution.");
        }

        if (MentionsAny(goal, "screenshot", "image", "vision"))
        {
            lines.Add("- Vision/screenshot test should use a tiny deterministic fixture first, then a manual real-model certification.");
        }

        lines.Add("Validation commands:");
        lines.Add("- plan post edit validation");
        foreach (var command in buildCommands.Take(3))
        {
            lines.Add($"- {command}");
        }

        foreach (var command in testCommands.Take(3))
        {
            lines.Add($"- {command}");
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Feature test suggestions", Policy.WorkspaceRoot);
    }

    private CodingToolResult DetectCodebasePatterns()
    {
        var summaries = GetWorkspaceProjectSummaries();
        var files = Directory.Exists(Policy.WorkspaceRoot)
            ? EnumerateWorkspaceFiles().Take(10_000).ToList()
            : [];
        var lines = new List<string>
        {
            "Codebase pattern detector:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            "No files were changed.",
            $"Projects scanned: {summaries.Count}",
            $"C# files detected: {files.Count(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))}",
            $"XAML files detected: {files.Count(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))}",
            $"JSON files detected: {files.Count(file => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))}"
        };

        lines.Add($"Detected stacks: {FormatInlineList(DetectStackSignals(files, summaries))}");
        lines.Add($"Style signals: {FormatInlineList(DetectStyleSignals(files, summaries))}");
        lines.Add($"Build commands: {FormatInlineList(DiscoverBuildCommands(files, summaries, GetPrimaryTarget()))}");
        lines.Add($"Test commands: {FormatInlineList(DiscoverTestCommands(files, summaries, GetPrimaryTarget()))}");
        var packages = summaries.SelectMany(summary => summary.PackageReferences).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(18).ToList();
        lines.Add("Observed package patterns:");
        lines.Add(packages.Count == 0 ? "- none detected" : $"- {string.Join(", ", packages)}");
        lines.Add("Observed project roles:");
        foreach (var group in summaries.GroupBy(summary => summary.ProjectRole, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"- {group.Key}: {group.Count()}");
        }

        lines.Add("Implementation pattern guidance:");
        lines.Add("- Keep command parsing in Ali.Core and local execution in Ali.Infrastructure.");
        lines.Add("- Keep WPF/WebHelper/VSIX as thin surfaces over deterministic commands.");
        lines.Add("- Add tests beside existing Ali.Tests harness patterns before widening behavior.");
        lines.Add("- Update docs when the owner command surface changes.");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Codebase patterns", Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanFeatureFiles(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current feature");
        var lines = new List<string>
        {
            "Feature file planner:",
            $"Goal: {goal}",
            "No files were changed.",
            "Likely files to inspect or touch:"
        };

        if (MentionsAny(goal, "command", "coding", "builder", "package", "roadmap", "plan", "test", "git", "build"))
        {
            lines.Add("- src/Ali.Core/Coding/CodingToolContracts.cs");
            lines.Add("- src/Ali.Core/Coding/CodingToolRequestParser.cs");
            lines.Add("- src/Ali.Core/Coding/CodingWorkspacePolicy.cs");
            lines.Add("- src/Ali.Infrastructure/Coding/LocalCodingToolService.cs");
            lines.Add("- tests/Ali.Tests/Program.cs");
        }

        if (MentionsAny(goal, "visual studio", "vsix", "tool window", "ide"))
        {
            lines.Add("- src/Ali.App.VisualStudioExtension/AliCompanionToolWindowControl.cs");
            lines.Add("- src/Ali.App.VisualStudioBridge/Program.cs");
            lines.Add("- src/Ali.App.WebHelper/Program.cs");
        }

        if (MentionsAny(goal, "manual", "docs", "pdf", "report"))
        {
            lines.Add("- docs/USER_GUIDE.md");
            lines.Add("- docs/ENGINEERING_NOTES.md");
        }

        if (MentionsAny(goal, "voice", "piper", "whisper", "microphone", "speech"))
        {
            lines.Add("- src/Ali.App.Wpf/ViewModels/MainWindowViewModel.cs");
            lines.Add("- local voice adapter/settings classes under src/Ali.Infrastructure");
        }

        if (MentionsAny(goal, "screenshot", "image", "vision", "attachment"))
        {
            lines.Add("- src/Ali.App.Wpf/ViewModels/MainWindowViewModel.cs");
            lines.Add("- src/Ali.Core/Orchestration/ConversationOrchestrator.cs");
            lines.Add("- src/Ali.Infrastructure/Runtime/OpenAiCompatibleLocalModelRuntime.cs");
            lines.Add("- tests/Ali.Tests/Program.cs");
        }

        lines.Add("Planning commands:");
        lines.Add($"- show refactor safety checklist {goal}");
        lines.Add($"- write acceptance criteria {goal}");
        lines.Add($"- suggest tests for {goal}");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Feature file planner", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowRefactorSafetyChecklist(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current change");
        var lines = new List<string>
        {
            "Refactor safety checklist:",
            $"Goal: {goal}",
            "No files were changed.",
            "Review before editing:",
            "- Does this cross a public interface, serialized state, settings file, or receipt schema?",
            "- Does this alter permission gates, command parsing, model prompts, or external process execution?",
            "- Does this affect WPF/WebHelper/VSIX behavior differently?",
            "- Does this need migration, backward compatibility, or crash recovery?",
            "- Are tests narrow enough to catch the intended behavior without blessing unrelated churn?",
            "Stop and compare options when:",
            "- The requested change requires a new package, internet lookup, installer, registry, PATH, signing, or trust-store change.",
            "- The model would have to guess code behavior without reading the relevant files.",
            "- The safest fix is not an exact previewable edit."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Refactor safety", Policy.WorkspaceRoot);
    }

    private CodingToolResult ExploreBuildIdea(CodingToolRequest request)
    {
        var goal = string.IsNullOrWhiteSpace(request.Query)
            ? "unspecified build idea"
            : request.Query.Trim();

        var files = Directory.Exists(Policy.WorkspaceRoot)
            ? EnumerateWorkspaceFiles().Take(10_000).ToList()
            : [];
        var summaries = files
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWorkspaceSummaryEntries)
            .Select(ReadProjectSummary)
            .ToList();
        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : null;

        var lines = new List<string>
        {
            "Build idea scout:",
            $"Goal: {goal}",
            "No files were changed.",
            "Truth boundary: library names below are exploration candidates, not installed packages or verified latest versions.",
            $"Workspace root: {Policy.WorkspaceRoot}"
        };

        if (!string.IsNullOrWhiteSpace(primaryTarget))
        {
            lines.Add($"Primary solution/project: {primaryTarget}");
        }

        if (summaries.Count > 0)
        {
            AddBuildIdeaWorkspaceFit(lines, summaries);
        }
        else
        {
            lines.Add("Workspace fit: no project files were available for a local fit check.");
        }

        AddImplementationPaths(lines, goal);
        AddArchitectureRecommendationCards(lines, goal, summaries);
        AddLibraryExploration(lines, goal);
        AddApprovalCheckpoints(lines);
        AddBuildIdeaNextCommands(lines, goal);

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Build idea scout",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult DraftImplementationRoadmap(CodingToolRequest request)
    {
        var goal = string.IsNullOrWhiteSpace(request.Query)
            ? "unspecified implementation"
            : request.Query.Trim();
        var files = Directory.Exists(Policy.WorkspaceRoot)
            ? EnumerateWorkspaceFiles().Take(10_000).ToList()
            : [];
        var summaries = files
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWorkspaceSummaryEntries)
            .Select(ReadProjectSummary)
            .ToList();
        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : null;

        var lines = new List<string>
        {
            "Implementation roadmap:",
            $"Goal: {goal}",
            "No files were changed.",
            $"Workspace root: {Policy.WorkspaceRoot}"
        };

        if (!string.IsNullOrWhiteSpace(primaryTarget))
        {
            lines.Add($"Primary solution/project: {primaryTarget}");
        }

        AddRoadmapArchitectureFit(lines, summaries);
        AddRoadmapPhases(lines, goal);
        AddRoadmapImpactSurface(lines, goal);
        AddRoadmapTestStrategy(lines, goal);
        AddRoadmapRiskRegister(lines, goal);
        AddRoadmapDefinitionOfDone(lines);
        AddApprovalCheckpoints(lines);
        AddRoadmapNextCommands(lines, goal);

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Implementation roadmap",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowLastRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_lastRoadmapRequest is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No implementation roadmap is pending. Draft one first with: draft implementation roadmap <goal>",
                "Implementation roadmap",
                Policy.WorkspaceRoot);
        }

        var roadmap = DraftImplementationRoadmap(_lastRoadmapRequest);
        var status = _roadmapState is { Finished: true }
            ? "Roadmap status: finished."
            : _roadmapState is { Paused: true }
                ? "Roadmap status: paused."
                : _lastRoadmapApproved
                    ? _approvedRoadmapStarted
                        ? "Roadmap status: approved and started."
                        : "Roadmap status: approved and ready to start."
                    : "Roadmap status: pending approval.";
        var next = _roadmapState is { Finished: true }
            ? "Next command: draft implementation roadmap <goal>"
            : _roadmapState is { Paused: true }
                ? "Next command: resume roadmap"
                : _lastRoadmapApproved
                    ? "Next command: start approved roadmap"
                    : "Next command: approve last roadmap";
        var step = _roadmapState is null
            ? string.Empty
            : $"{Environment.NewLine}Active step: {FormatRoadmapCurrentStep(_roadmapState)}";

        return roadmap with
        {
            Message = $"{status}{Environment.NewLine}{next}{step}{Environment.NewLine}{Environment.NewLine}{roadmap.Message}",
            ToolName = "Implementation roadmap"
        };
    }

    private CodingToolResult DiscardLastRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_lastRoadmapRequest is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No pending implementation roadmap was waiting to be discarded.",
                "Implementation roadmap",
                Policy.WorkspaceRoot);
        }

        var goal = _lastRoadmapRequest.Query ?? "unspecified implementation";
        ClearRoadmapState();

        return new CodingToolResult(
            true,
            true,
            $"Discarded implementation roadmap. No files were changed.{Environment.NewLine}Goal: {goal}",
            "Implementation roadmap",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ApproveLastRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_lastRoadmapRequest is null)
        {
            return new CodingToolResult(
                true,
                false,
                "No implementation roadmap is pending. Draft one first with: draft implementation roadmap <goal>",
                "Implementation roadmap",
                Policy.WorkspaceRoot);
        }

        var goal = _lastRoadmapRequest.Query ?? "unspecified implementation";
        _lastRoadmapApproved = true;
        _approvedRoadmapStarted = false;
        _roadmapState = (_roadmapState ?? CreateRoadmapState(goal)) with
        {
            Goal = goal,
            Approved = true,
            Started = false,
            Paused = false,
            Finished = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastReceiptSummary = "Roadmap approved. No files were changed."
        };
        SaveRoadmapState();

        return new CodingToolResult(
            true,
            true,
            $"Approved implementation roadmap. No files were changed.{Environment.NewLine}Goal: {goal}{Environment.NewLine}Next command: start approved roadmap",
            "Implementation roadmap",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult StartApprovedRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_lastRoadmapRequest is null)
        {
            return new CodingToolResult(
                true,
                false,
                "No implementation roadmap is pending. Draft and approve a roadmap before starting.",
                "Roadmap execution",
                Policy.WorkspaceRoot);
        }

        if (!_lastRoadmapApproved)
        {
            return new CodingToolResult(
                true,
                false,
                "The pending implementation roadmap is not approved yet. Use: approve last roadmap",
                "Roadmap execution",
                Policy.WorkspaceRoot);
        }

        _approvedRoadmapStarted = true;
        var goal = _lastRoadmapRequest.Query ?? "unspecified implementation";
        _roadmapState = (_roadmapState ?? CreateRoadmapState(goal)) with
        {
            Goal = goal,
            Approved = true,
            Started = true,
            Paused = false,
            Finished = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastReceiptSummary = "Roadmap execution started. No files were changed."
        };
        SaveRoadmapState();

        var lines = new List<string>
        {
            "Approved roadmap execution started:",
            $"Goal: {goal}",
            "No files were changed.",
            "Current execution mode: guided phase loop. Ali will propose the next safe action, then stop at write/build/package/Git approval boundaries.",
            "Phase 1: active roadmap step tracking.",
            $"Current step: {FormatRoadmapCurrentStep(_roadmapState)}",
            "Recommended next Ali commands:",
            "- show next coding action",
            "- show execution packet",
            "- show active roadmap step",
            "- analyze solution architecture",
            "- inspect coding workspace",
            $"- plan coding task {goal}",
            "Confirmed execution commands available after the next step is clear:",
            "- preview patch bundle",
            "- confirm apply last patch preview",
            "- confirm dotnet add package \"Package.Id\" to \"path\"",
            "- confirm dotnet build \"path\"",
            "- confirm dotnet test \"path\"",
            "- confirm git add all",
            "- confirm git commit \"message\"",
            "Stop boundaries:",
            "- Package lookup/install needs explicit approval.",
            "- File edits must go through preview/apply confirmation.",
            "- Build/test/run commands need confirmation.",
            "- Git write/network actions remain behind Git permission gates.",
            $"Recovery file: {_roadmapStatePath}"
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Roadmap execution",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowActiveRoadmapStep()
    {
        LoadRoadmapStateIfNeeded();
        if (_roadmapState is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No active roadmap state was found. Draft and start a roadmap first.",
                "Roadmap execution",
                Policy.WorkspaceRoot);
        }

        return new CodingToolResult(
            true,
            true,
            FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true),
            "Roadmap execution",
            Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowNextRoadmapActionAsync(CancellationToken cancellationToken)
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : Policy.WorkspaceRoot;

        var lines = new List<string>
        {
            "Next coding action:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Primary target: {primaryTarget}",
            "No files were changed."
        };

        if (_roadmapState is null)
        {
            lines.Add("Roadmap state: none active.");
            lines.Add("Recommended action: use the guided builder flow, then approve a roadmap before execution.");
            lines.Add("Guided builder flow:");
            lines.Add("- interpret build goal <goal>");
            lines.Add("- show architecture options <goal>");
            lines.Add("- write acceptance criteria <goal>");
            lines.Add("- suggest tests for <goal>");
            lines.Add("- draft implementation roadmap <goal>");
            lines.Add("- approve last roadmap");
            lines.Add("- start approved roadmap");
            lines.Add("Useful support commands:");
            lines.Add("- detect codebase patterns");
            lines.Add("- plan feature files <goal>");
            lines.Add("- show refactor safety checklist <goal>");
            lines.Add("- show coding skill command index");
            lines.Add("Confidence: high. Ali has no active roadmap to continue yet.");
            lines.Add("Stop rule: do not edit, install packages, build, test, or commit until an approved roadmap or explicit confirmed command exists.");
            return new CodingToolResult(
                true,
                true,
                string.Join(Environment.NewLine, lines),
                "Next coding action",
                Policy.WorkspaceRoot);
        }

        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);

        lines.Add($"Goal: {_roadmapState.Goal}");
        lines.Add($"Status: {DescribeRoadmapState(_roadmapState)}");
        lines.Add($"Current step: {FormatRoadmapCurrentStep(_roadmapState)}");
        lines.Add($"Git: {gitStatus.Summary}");
        if (latestReceipt is null)
        {
            lines.Add("Latest receipt: none");
        }
        else
        {
            var target = string.IsNullOrWhiteSpace(latestReceipt.TargetPath) ? string.Empty : $" target={latestReceipt.TargetPath}";
            var exit = latestReceipt.ExitCode is null ? string.Empty : $" exit={latestReceipt.ExitCode.Value}";
            lines.Add($"Latest receipt: {latestReceipt.Timestamp:u} {latestReceipt.Action} {(latestReceipt.Succeeded ? "succeeded" : "failed")}{exit}{target}");
        }

        var recommendation = BuildNextRoadmapRecommendation(_roadmapState, gitStatus, latestDotNetReceipt, primaryTarget);
        lines.Add($"Recommended action: {recommendation.Action}");
        lines.Add($"Confidence: {recommendation.Confidence}");
        lines.Add("Why:");
        lines.AddRange(recommendation.Reasons.Select(reason => $"- {reason}"));
        lines.Add("Exact safe commands:");
        lines.AddRange(recommendation.Commands.Select(command => $"- {command}"));
        lines.Add("Approval gates:");
        lines.Add("- Preview/edit commands still require preview plus explicit apply confirmation.");
        lines.Add("- Build, test, restore, run, and package install commands still require confirmation.");
        lines.Add("- Git write/network commands still follow Git permission gates.");
        lines.Add("Stop and compare options when:");
        lines.Add("- Git has unexpected changes.");
        lines.Add("- The last build/test/package receipt failed and no deterministic patch suggestion is available.");
        lines.Add("- The next step requires a package, library, or external tool that has not been approved for lookup/install.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Next coding action",
            Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowRoadmapExecutionPacketAsync(CancellationToken cancellationToken)
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : Policy.WorkspaceRoot;

        var lines = new List<string>
        {
            "Coding execution packet:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Primary target: {primaryTarget}",
            "No files were changed.",
            "Truth boundary: this packet suggests commands only; Ali does not edit, install, build, test, run, or commit until the matching approval gate is used."
        };

        if (_roadmapState is null)
        {
            lines.Add("Packet status: setup needed.");
            lines.Add("Reason: no active roadmap state exists.");
            lines.Add("Setup commands:");
            lines.Add("- explore build idea <goal>");
            lines.Add("- draft implementation roadmap <goal>");
            lines.Add("- approve last roadmap");
            lines.Add("- start approved roadmap");
            lines.Add("Stop rule: do not execute build, package, edit, run, or Git commands from a packet until a roadmap exists and the owner has approved the step.");
            return new CodingToolResult(
                true,
                true,
                string.Join(Environment.NewLine, lines),
                "Coding execution packet",
                Policy.WorkspaceRoot);
        }

        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var recommendation = BuildNextRoadmapRecommendation(_roadmapState, gitStatus, latestDotNetReceipt, primaryTarget);
        var currentStep = GetRoadmapCurrentStep(_roadmapState);
        var packetStatus = DescribeExecutionPacketStatus(_roadmapState, gitStatus, latestDotNetReceipt);

        lines.Add($"Packet status: {packetStatus}");
        lines.Add($"Goal: {_roadmapState.Goal}");
        lines.Add($"Roadmap status: {DescribeRoadmapState(_roadmapState)}");
        lines.Add($"Current step: {FormatRoadmapCurrentStep(_roadmapState)}");
        lines.Add($"Recommended action: {recommendation.Action}");
        lines.Add($"Confidence: {recommendation.Confidence}");
        lines.Add("Evidence snapshot:");
        lines.Add($"- Git: {gitStatus.Summary}");
        lines.Add(latestReceipt is null
            ? "- Latest receipt: none"
            : FormatReceiptSummary("- Latest receipt", latestReceipt));
        if (latestDotNetReceipt is not null && !ReferenceEquals(latestDotNetReceipt, latestReceipt))
        {
            lines.Add(FormatReceiptSummary("- Latest dotnet-style receipt", latestDotNetReceipt));
        }

        lines.Add("Read-only prep:");
        AddUniqueCommands(lines, [
            "show next coding action",
            "show crash recovery status",
            "show coding receipts"
        ]);

        lines.Add("Execution candidates:");
        AddUniqueCommands(lines, BuildExecutionCandidateCommands(_roadmapState, recommendation, currentStep, primaryTarget));

        lines.Add("Validation commands:");
        AddUniqueCommands(lines, BuildValidationCommands(_roadmapState, currentStep, primaryTarget));

        lines.Add("Closeout commands:");
        AddUniqueCommands(lines, BuildCloseoutCommands(_roadmapState, currentStep));

        lines.Add("Approval gates:");
        lines.Add("- File edits: use preview patch bundle, review it, then confirm apply last patch preview.");
        lines.Add("- Packages: confirm restore/package/check commands before NuGet changes or network checks.");
        lines.Add("- Build/test/run: use confirm dotnet build/test/run commands.");
        lines.Add("- Git writes: review git status/diff first, then use confirmed git add/commit only after validation.");

        lines.Add("Stop and compare options when:");
        lines.Add("- Packet status says blocked or review-first.");
        lines.Add("- The command needs a package/library/tool that has not been approved for lookup/install.");
        lines.Add("- The suggested change is not an exact literal patch preview.");
        lines.Add("- Git shows unexpected files or a validation receipt failed.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Coding execution packet",
            Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ApproveRoadmapExecutionPacketAsync(CancellationToken cancellationToken)
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        if (!TryGetActiveRoadmapForStepChange(out var state, out var error))
        {
            return error with
            {
                ToolName = "Approved execution packet"
            };
        }

        var packet = await BuildApprovedRoadmapExecutionPacketAsync(state, cancellationToken).ConfigureAwait(false);
        _approvedPacket = packet;
        SaveApprovedPacket();

        var lines = new List<string>
        {
            "Approved execution packet:",
            $"Goal: {packet.Goal}",
            $"Step: {packet.StepIndex + 1}: {packet.Step}",
            $"Packet status: {packet.PacketStatus}",
            $"Recommended action: {packet.RecommendedAction}",
            "No files were changed.",
            "Truth boundary: approval stores this packet as local planning state only. It does not run edits, packages, builds, tests, run commands, or Git writes.",
            "Next commands:",
            "- show approved packet",
            "- show packet progress",
            "- run one listed command only through its normal approval gate",
            $"Packet file: {_approvedPacketPath}"
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Approved execution packet",
            _approvedPacketPath);
    }

    private CodingToolResult ShowApprovedRoadmapExecutionPacket()
    {
        LoadApprovedPacketIfNeeded();
        if (_approvedPacket is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No approved execution packet is active. Use: approve execution packet",
                "Approved execution packet",
                _approvedPacketPath);
        }

        return new CodingToolResult(
            true,
            true,
            FormatApprovedPacket(_approvedPacket, includePath: true),
            "Approved execution packet",
            _approvedPacketPath);
    }

    private CodingToolResult DiscardApprovedRoadmapExecutionPacket()
    {
        LoadApprovedPacketIfNeeded();
        if (_approvedPacket is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No approved execution packet was active.",
                "Approved execution packet",
                _approvedPacketPath);
        }

        var goal = _approvedPacket.Goal;
        ClearApprovedPacket();
        return new CodingToolResult(
            true,
            true,
            $"Discarded approved execution packet. No files were changed.{Environment.NewLine}Goal: {goal}",
            "Approved execution packet",
            _approvedPacketPath);
    }

    private async Task<CodingToolResult> ShowRoadmapExecutionPacketProgressAsync(CancellationToken cancellationToken)
    {
        LoadApprovedPacketIfNeeded();
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        if (_approvedPacket is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No approved execution packet is active. Use: approve execution packet",
                "Execution packet progress",
                _approvedPacketPath);
        }

        var packet = _approvedPacket;
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var stale = _roadmapState is null
            || !_roadmapState.Goal.Equals(packet.Goal, StringComparison.Ordinal)
            || _roadmapState.CurrentStepIndex != packet.StepIndex
            || _roadmapState.UpdatedAt > packet.RoadmapUpdatedAt;

        var lines = new List<string>
        {
            "Execution packet progress:",
            $"Goal: {packet.Goal}",
            $"Step: {packet.StepIndex + 1}: {packet.Step}",
            $"Packet status: {(stale ? "stale: roadmap changed after packet approval" : "active")}",
            $"Approved: {packet.ApprovedAt:u}",
            $"Roadmap snapshot: {packet.RoadmapUpdatedAt:u}",
            $"Git: {gitStatus.Summary}",
            latestReceipt is null
                ? "Latest receipt: none"
                : FormatReceiptSummary("Latest receipt", latestReceipt),
            latestDotNetReceipt is null
                ? "Latest dotnet-style receipt: none"
                : FormatReceiptSummary("Latest dotnet-style receipt", latestDotNetReceipt),
            "Packet receipt match:"
        };
        lines.AddRange(BuildPacketReceiptMatchLines(packet, receipts, gitStatus, stale));
        lines.AddRange([
            "Progress lanes:",
            "- Prep: review read-only commands and current context.",
            "- Execute: choose one candidate command and use its normal approval gate.",
            "- Validate: run confirmed build/test when code or packages change.",
            "- Closeout: review receipts and Git before marking the roadmap step complete.",
            "Next safe commands:"
        ]);
        lines.Add("- show approved packet");
        lines.Add(stale ? "- discard approved packet" : "- show execution packet");
        lines.Add("- show coding receipts");
        lines.Add("- show crash recovery status");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Execution packet progress",
            _approvedPacketPath);
    }

    private CodingToolResult ShowApprovedPacketCommands()
    {
        LoadApprovedPacketIfNeeded();
        if (_approvedPacket is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No approved execution packet is active. Use: approve execution packet",
                "Packet command console",
                _approvedPacketPath);
        }

        var items = FlattenApprovedPacketCommands(_approvedPacket);
        var lines = new List<string>
        {
            "Packet command console:",
            $"Goal: {_approvedPacket.Goal}",
            $"Step: {_approvedPacket.StepIndex + 1}: {_approvedPacket.Step}",
            "Truth boundary: this console lists stored packet commands. Mutating or confirmed commands still require: confirm run packet item N",
            "Commands:"
        };
        foreach (var item in items)
        {
            var gate = PacketCommandNeedsConfirmation(item.Command) ? "confirmation required" : "read-only";
            lines.Add($"{item.Number}. [{item.Section}] {item.Command} ({gate})");
        }

        lines.Add("Next safe commands:");
        lines.Add("- run packet item 1");
        lines.Add("- confirm run packet item N");
        lines.Add("- show packet ledger");
        lines.Add("- show packet progress");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Packet command console",
            _approvedPacketPath);
    }

    private async Task<CodingToolResult> RunApprovedPacketItemAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        LoadApprovedPacketIfNeeded();
        if (_approvedPacket is null)
        {
            return new CodingToolResult(
                true,
                false,
                "No approved execution packet is active. Use: approve execution packet",
                "Packet command console",
                _approvedPacketPath);
        }

        if (!int.TryParse(request.Query, out var requestedNumber) || requestedNumber < 1)
        {
            return new CodingToolResult(
                true,
                false,
                "Choose a packet item number. Example: run packet item 1",
                "Packet command console",
                _approvedPacketPath);
        }

        var items = FlattenApprovedPacketCommands(_approvedPacket);
        var item = items.FirstOrDefault(candidate => candidate.Number == requestedNumber);
        if (item is null)
        {
            return new CodingToolResult(
                true,
                false,
                $"Packet item {requestedNumber} was not found. Use: show packet commands",
                "Packet command console",
                _approvedPacketPath);
        }

        if (PacketCommandNeedsConfirmation(item.Command) && !request.UserConfirmed)
        {
            return new CodingToolResult(
                true,
                false,
                $"Packet item {item.Number} needs explicit confirmation before Ali runs it.{Environment.NewLine}Command: {item.Command}{Environment.NewLine}Use: confirm run packet item {item.Number}",
                "Packet command console",
                _approvedPacketPath);
        }

        var result = await TryHandleAsync(item.Command, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            $"Ran packet item {item.Number}:",
            $"Section: {item.Section}",
            $"Command: {item.Command}",
            $"Result: {(result.Succeeded ? "succeeded" : "failed")}",
            string.Empty,
            TrimForChat(result.Message, 10_000)
        };

        return new CodingToolResult(
            true,
            result.Succeeded,
            string.Join(Environment.NewLine, lines),
            "Packet command console",
            result.TargetPath ?? _approvedPacketPath,
            ExitCode: result.ExitCode);
    }

    private async Task<CodingToolResult> ShowPacketRunLedgerAsync(CancellationToken cancellationToken)
    {
        LoadApprovedPacketIfNeeded();
        if (_approvedPacket is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No approved execution packet is active. Use: approve execution packet",
                "Packet run ledger",
                _approvedPacketPath);
        }

        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var packetReceipts = receipts
            .Where(receipt => receipt.Timestamp >= _approvedPacket.ApprovedAt)
            .OrderBy(receipt => receipt.Timestamp)
            .ToList();
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<string>
        {
            "Packet run ledger:",
            $"Goal: {_approvedPacket.Goal}",
            $"Step: {_approvedPacket.StepIndex + 1}: {_approvedPacket.Step}",
            $"Approved: {_approvedPacket.ApprovedAt:u}",
            $"Git: {gitStatus.Summary}",
            $"Receipts since approval: {packetReceipts.Count}"
        };

        if (packetReceipts.Count == 0)
        {
            lines.Add("- none yet");
        }
        else
        {
            foreach (var receipt in packetReceipts)
            {
                lines.Add(FormatReceiptSummary("-", receipt));
            }
        }

        lines.Add("Packet receipt match:");
        lines.AddRange(BuildPacketReceiptMatchLines(_approvedPacket, receipts, gitStatus, stale: false));
        lines.Add("Next safe commands:");
        lines.Add("- show packet commands");
        lines.Add("- show packet progress");
        lines.Add("- resume build plan");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Packet run ledger",
            _approvedPacketPath);
    }

    private CodingToolResult PlanPackageLookup(CodingToolRequest request)
    {
        var goal = string.IsNullOrWhiteSpace(request.Query)
            ? "current roadmap step"
            : request.Query.Trim();
        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : Policy.WorkspaceRoot;

        var lines = new List<string>
        {
            "Package/library lookup plan:",
            $"Goal: {goal}",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Primary target: {primaryTarget}",
            "No network lookup, package restore, or install was run.",
            "Truth boundary: package names below are exploration lanes, not current version claims. Approve live lookup before trusting availability, licensing, or latest versions.",
            "Candidate exploration lanes:"
        };

        AddPackageLookupCandidateLanes(lines, goal);
        lines.Add("Dependency risk cards:");
        lines.Add("- Fit risk: verify the package targets the same framework and app shape as the project.");
        lines.Add("- License risk: check the license before using it in a distributable build.");
        lines.Add("- Maintenance risk: inspect recent releases, issue activity, and .NET compatibility.");
        lines.Add("- Security risk: run approved restore/outdated/vulnerability checks before committing.");
        lines.Add("- Integration risk: prototype behind one service interface before spreading calls across the app.");
        lines.Add("Approval path:");
        lines.Add($"- list packages \"{primaryTarget}\"");
        lines.Add($"- confirm check outdated packages \"{primaryTarget}\"");
        lines.Add($"- confirm dotnet restore \"{primaryTarget}\"");
        lines.Add($"- confirm dotnet add package \"Package.Id\" to \"{primaryTarget}\"");
        lines.Add("Stop rule: if the package affects app architecture, generate an execution packet and get approval before installing.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Package lookup plan",
            primaryTarget);
    }

    private CodingToolResult PreviewProjectScaffold(CodingToolRequest request)
    {
        var goal = string.IsNullOrWhiteSpace(request.Query)
            ? "new project feature"
            : request.Query.Trim();
        var safeName = BuildSafeScaffoldName(goal);
        var lines = new List<string>
        {
            "Project scaffold preview:",
            $"Goal: {goal}",
            $"Workspace root: {Policy.WorkspaceRoot}",
            "No directories, files, projects, packages, or solution entries were created.",
            "Preview bundle:",
            $"- src/{safeName}/{safeName}.csproj",
            $"- src/{safeName}/README.md",
            $"- src/{safeName}/{safeName}Service.cs",
            $"- tests/{safeName}.Tests/{safeName}.Tests.csproj",
            $"- tests/{safeName}.Tests/{safeName}ServiceTests.cs",
            "Suggested first implementation shape:",
            "- Keep core logic in a small service class with no UI dependency.",
            "- Add one test project or test file before wiring UI/VS commands.",
            "- Add package references only after package lookup approval.",
            "Approval path:",
            $"- plan package lookup {goal}",
            $"- preview patch bundle file \"src/{safeName}/README.md\" replace \"\" with \"<approved content>\"",
            "- confirm apply last patch preview",
            "- confirm dotnet build \"path-to-solution-or-project\"",
            "Stop rule: scaffold previews are planning state only. Use guarded file creation or patch previews before writing."
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Project scaffold preview",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanDependencyInstallPacket(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current dependency goal");
        var primaryTarget = GetPrimaryTarget() ?? Policy.WorkspaceRoot;
        var lines = new List<string>
        {
            "Dependency install packet:",
            $"Goal: {goal}",
            $"Primary target: {primaryTarget}",
            "No package lookup, restore, install, build, or test was run.",
            "Truth boundary: this packet is an approval plan. Versions, licenses, and package health require approved live lookup.",
            "Prep:",
            $"1. plan package lookup {goal}",
            $"2. list packages \"{primaryTarget}\"",
            $"3. show refactor safety checklist {goal}",
            "Approval commands:",
            $"- confirm dotnet restore \"{primaryTarget}\"",
            $"- confirm dotnet add package \"Package.Id\" to \"{primaryTarget}\"",
            $"- confirm dotnet build \"{primaryTarget}\"",
            $"- confirm dotnet test \"{primaryTarget}\"",
            "Rollback notes:",
            "- Use git status/diff before install.",
            "- If install changes project files unexpectedly, stop and compare options.",
            "- Revert by removing the PackageReference through an approved patch/edit or git reset only when the owner explicitly requests it."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Dependency install packet", primaryTarget);
    }

    private async Task<CodingToolResult> PlanPostEditValidationAsync(CancellationToken cancellationToken)
    {
        var primaryTarget = GetPrimaryTarget() ?? Policy.WorkspaceRoot;
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "Post-edit build loop:",
            $"Workspace: {Policy.WorkspaceRoot}",
            $"Target: {primaryTarget}",
            $"Git: {gitStatus.Summary}",
            latestReceipt is null ? "Latest receipt: none" : FormatReceiptSummary("Latest receipt", latestReceipt),
            latestDotNetReceipt is null ? "Latest validation: none" : FormatReceiptSummary("Latest validation", latestDotNetReceipt),
            "Validation plan:",
            "- Patch preview: review any pending patch before applying edits.",
            $"- Build: run a confirmed build for {primaryTarget}.",
            $"- Tests: run confirmed tests for {primaryTarget}.",
            "- Review: check Git status and diff after validation.",
            "- Commit: commit only after expected changes and receipts look good.",
            "Failure loop:",
            "- classify last build failure",
            "- diagnose last build failure",
            "- suggest patch from last failure",
            "- If Ali is unsure, stop and compare options before applying edits."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Post-edit validation", primaryTarget);
    }

    private CodingToolResult PlanScaffoldApply(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "new project feature");
        var safeName = BuildSafeScaffoldName(goal);
        var primaryTarget = GetPrimaryTarget() ?? Policy.WorkspaceRoot;
        var lines = new List<string>
        {
            "Scaffold apply flow:",
            $"Goal: {goal}",
            $"Safe name: {safeName}",
            $"Primary target: {primaryTarget}",
            "No directories, files, projects, packages, or solution entries were created.",
            "Current implementation boundary: Ali can preview scaffold shape and can create/append/replace files only through existing confirmed file-edit commands.",
            "Recommended packets:",
            $"1. preview project scaffold {goal}",
            $"2. write acceptance criteria {goal}",
            $"3. suggest tests for {goal}",
            "4. Use confirmed create-file commands for each approved file, or prepare a small literal patch bundle when replacing known text.",
            "5. Approve restore/build/test validation only after reviewing the file plan.",
            "Example approval commands:",
            $"- confirm create file \"<workspace>\\src\\{safeName}\\README.md\" with \"<approved content>\"",
            $"- confirm create file \"<workspace>\\src\\{safeName}\\{safeName}Service.cs\" with \"<approved content>\"",
            $"- confirm dotnet build \"{primaryTarget}\"",
            "Stop rule: solution/project creation is not automatic in this flow yet; use explicit confirmed file/project commands when that executor is added."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Scaffold apply flow", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowBuilderCommandIndex()
        => new(true, true, CodingAbilityCatalog.BuildBuilderCommandIndex(), "Coding skill command index", Policy.WorkspaceRoot);

    private async Task<CodingToolResult> ShowCodingSessionSummaryAsync(CancellationToken cancellationToken)
    {
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "Coding session summary:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Git: {gitStatus.Summary}",
            $"Receipts inspected: {receipts.Count}",
            latestReceipt is null ? "Latest receipt: none" : FormatReceiptSummary("Latest receipt", latestReceipt),
            latestDotNetReceipt is null ? "Latest dotnet-style receipt: none" : FormatReceiptSummary("Latest dotnet-style receipt", latestDotNetReceipt),
            _roadmapState is null
                ? "Roadmap: none active"
                : $"Roadmap: {DescribeRoadmapState(_roadmapState)}; step {FormatRoadmapCurrentStep(_roadmapState)}",
            _approvedPacket is null
                ? "Approved packet: none active"
                : $"Approved packet: step {_approvedPacket.StepIndex + 1}, approved {_approvedPacket.ApprovedAt:u}",
            "Next useful commands:",
            "- show next coding action",
            "- show roadmap step checklist",
            "- plan post edit validation",
            "- show coding skill command index"
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Coding session summary", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowComputerAssistantStatus()
        => new(
            true,
            true,
            CodingAbilityCatalog.BuildComputerAssistantStatus(Policy.WorkspaceRoot, _pdfWorkspaceRoot),
            "Computer assistant status",
            Policy.WorkspaceRoot);

    private CodingToolResult ShowUserCommandHelp()
        => new(true, true, CodingAbilityCatalog.BuildUserCommandHelpGuide(), "Ali plain-language guide", Policy.WorkspaceRoot);

    private CodingToolResult ShowComputerAssistantCommandIndex()
        => new(true, true, CodingAbilityCatalog.BuildComputerAssistantCommandIndex(), "Computer assistant command index", Policy.WorkspaceRoot);

    private CodingToolResult PlanFileOrganization(CodingToolRequest request)
    {
        var target = CleanGoal(request.Query, "the folder you want organized");
        var resolvedTarget = ResolveCommonFolderHint(target);
        var targetExists = !string.IsNullOrWhiteSpace(resolvedTarget) && Directory.Exists(resolvedTarget);
        var lines = new List<string>
        {
            "File organization plan:",
            $"Target: {target}",
            resolvedTarget is null ? "Resolved folder: not enough information yet" : $"Resolved folder: {resolvedTarget}",
            "No files were moved, copied, renamed, or deleted.",
            "Suggested workflow:",
            "1. Inspect the folder top level and identify file families by extension, age, size, and owner meaning.",
            "2. Create a proposed destination map such as Documents, Photos, Installers, Archives, Projects, Receipts, and Review.",
            "3. Preview moves as a written plan before touching files.",
            "4. Move by copy-then-verify when the files matter, keeping originals until the owner approves cleanup.",
            "5. Use duplicate checks before deleting anything.",
            "Safety rules:",
            "- Never delete originals during the first organization pass.",
            "- Skip cloud sync/system/application folders unless the owner explicitly names them.",
            "- Keep project folders, source repos, installers, license files, and recent work visible in the plan."
        };

        if (targetExists && resolvedTarget is not null)
        {
            lines.Add("Top-level read-only snapshot:");
            lines.AddRange(SummarizeFolderTopLevel(resolvedTarget));
        }
        else
        {
            lines.Add("Next prompt:");
            lines.Add("- Give Ali a folder path, for example: plan file organization \"C:\\Users\\<you>\\Downloads\"");
        }

        lines.Add("Next safe upgrade:");
        lines.Add("- Add a preview-only file move plan that lists exact source and destination paths for approval.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "File organization plan",
            targetExists && resolvedTarget is not null ? resolvedTarget : Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanDiskCleanup(CodingToolRequest request)
    {
        var target = string.IsNullOrWhiteSpace(request.Query) ? "this PC" : request.Query.Trim();
        var lines = new List<string>
        {
            "Disk cleanup plan:",
            $"Target: {target}",
            "No files were deleted and no Windows settings were changed.",
            "Read-only triage:",
            "- Check free space by drive.",
            "- Identify large owner folders before touching caches or system areas.",
            "- Review Downloads, Desktop, Videos, Pictures, installers, old exports, and temporary build outputs.",
            "- Check OneDrive sync state before moving cloud-backed folders.",
            "Built-in Windows paths to consider with owner approval:",
            "- Settings -> System -> Storage -> Temporary files",
            "- Disk Cleanup / cleanmgr",
            "- Recycle Bin review",
            "- Installed apps sorted by size/date",
            "Ali-safe cleanup sequence:",
            "1. Snapshot drive free space and top suspect folders.",
            "2. Propose deletes/moves in a preview list.",
            "3. Back up or copy important files first.",
            "4. Apply only owner-confirmed operations.",
            "5. Re-check free space and app behavior after cleanup.",
            "Stop rules:",
            "- Do not delete Windows, Program Files, AppData, .git, .vs, node_modules, package caches, or model folders without a narrow approved reason.",
            "- Do not clear browser profiles, credentials, certificates, or model data as a generic cleanup step."
        };

        lines.Add("Drive snapshot:");
        lines.AddRange(SummarizeDriveSpace());
        lines.Add("Next safe commands:");
        lines.Add("- plan file organization \"C:\\Users\\<you>\\Downloads\"");
        lines.Add("- show windows troubleshooting toolkit");
        lines.Add("- show install doctor");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Disk cleanup plan", Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanAppInstallTroubleshooting(CodingToolRequest request)
    {
        var target = CleanGoal(request.Query, "the app or installer problem");
        var lines = new List<string>
        {
            "App install troubleshooting plan:",
            $"Target: {target}",
            "No installer was run and no system settings were changed.",
            "Evidence to gather:",
            "- Exact app name, version, installer source, and whether it is offline/web/bootstrap installer.",
            "- Error text, screenshot, installer log path, and Windows Event Viewer Application errors around the install time.",
            "- Required runtime stack: .NET, Visual C++ redistributable, WebView2, GPU/audio/USB drivers, or vendor services.",
            "- Current Windows version, free disk space, antivirus/security prompt, and whether a reboot is pending.",
            "Troubleshooting path:",
            "1. Verify the installer source and checksum/signature when available.",
            "2. Close conflicting app instances and inspect file locks before retrying.",
            "3. Run the vendor repair/update tool only after the owner approves it.",
            "4. Check logs before reinstalling blindly.",
            "5. If the fix is uncertain, compare repair, reinstall, clean uninstall, and vendor support paths.",
            "Useful Ali commands:",
            "- show install doctor",
            "- triage event logs",
            "- collect process evidence <app-name>",
            "- diagnose file lock \"<locked-file>\"",
            "- plan disk cleanup",
            "Approval boundaries:",
            "- Admin installers, driver installs, PATH changes, registry edits, service changes, trust-store/signing changes, and uninstallers need explicit approval."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "App install troubleshooting plan", Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanPeripheralSetup(CodingToolRequest request)
    {
        var target = CleanGoal(request.Query, "the device or setup symptom");
        var lines = new List<string>
        {
            "Peripheral setup plan:",
            $"Target: {target}",
            "No drivers, devices, audio settings, or Windows settings were changed.",
            "General setup path:",
            "1. Identify the exact device model, cable type, port, power requirement, and vendor driver/control app.",
            "2. Confirm Windows sees the device in Device Manager, Sound settings, Bluetooth, or the vendor utility.",
            "3. Confirm the app using the device has the correct input/output device selected.",
            "4. Test with a simple built-in app before troubleshooting the advanced app.",
            "5. Change one variable at a time and write down the result.",
            "Audio kit notes:",
            "- Scarlett Solo/2i2 interfaces usually need the correct Focusrite Control/driver generation for Windows.",
            "- AT2040 is a dynamic XLR microphone; it connects through the audio interface, not USB.",
            "- FetHead-style inline preamps require phantom power from the interface to power the preamp, while protecting the dynamic mic from needing phantom itself.",
            "- Start with conservative gain, speak at normal distance, watch clipping/green-red indicators, then adjust in small steps.",
            "- The Shure SH-BROADCAST2 boom arm setup is mechanical: clamp/mount stability, XLR strain relief, and mic position matter.",
            "Useful Ali commands:",
            "- show computer assistant commands",
            "- show windows troubleshooting toolkit",
            "- collect process evidence <vendor-app>",
            "- triage event logs",
            "Approval boundaries:",
            "- Driver installs, firmware updates, default device changes, exclusive-mode changes, service changes, and registry edits need approval."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Peripheral setup plan", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowComputerTroubleshootingCommandIndex()
        => new(true, true, ComputerTroubleshootingCatalog.BuildCommandIndex(), "Computer troubleshooting command index", Policy.WorkspaceRoot);

    private CodingToolResult PlanComputerTroubleshooting(CodingToolRequest request)
    {
        var (scenario, detail) = ParseComputerTroubleshootingQuery(request.Query);
        var lines = new List<string>
        {
            "Computer troubleshooting plan:",
            $"Scenario: {scenario}",
            string.IsNullOrWhiteSpace(detail) ? "Detail: none provided" : $"Detail: {detail}",
            "No files, apps, services, drivers, devices, browser data, network settings, or Windows settings were changed.",
            "First pass:",
            "- Reproduce the symptom once and write down the exact app, device, error text, time, and recent changes.",
            "- Check whether the issue is system-wide or limited to one app, device, account, network, or file.",
            "- Gather read-only evidence before changing settings.",
            "- Try the least invasive approved step first.",
            "Scenario-specific checklist:"
        };

        lines.AddRange(ComputerTroubleshootingCatalog.BuildScenarioChecklist(scenario));
        lines.Add("Useful Ali commands:");
        lines.Add("- show computer troubleshooting commands");
        lines.Add("- show windows troubleshooting toolkit");
        lines.Add("- collect process evidence <name-or-pid>");
        lines.Add("- triage event logs");
        lines.Add("- show install doctor");
        lines.Add("Approval boundaries:");
        lines.Add("- Deletes, driver installs, firmware updates, repair tools, uninstallers, registry changes, service/startup changes, firewall/DNS/IP changes, browser resets, credential changes, and process stops need explicit approval.");
        lines.Add("- If the evidence is unclear, pause and compare options instead of guessing.");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), $"{scenario} troubleshooting plan", Policy.WorkspaceRoot);
    }

    private static (string Scenario, string Detail) ParseComputerTroubleshootingQuery(string? query)
    {
        var value = CleanGoal(query, "General computer");
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (value, string.Empty);
        }

        var scenario = value[..separator].Trim();
        var detail = value[(separator + 1)..].Trim();
        return (string.IsNullOrWhiteSpace(scenario) ? "General computer" : scenario, detail);
    }

    private static string? ResolveCommonFolderHint(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var trimmed = target.Trim().Trim('"');
        if (Path.IsPathFullyQualified(trimmed))
        {
            return trimmed;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return null;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "downloads" or "download" => Path.Combine(userProfile, "Downloads"),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "documents" or "docs" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" or "photos" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null
        };
    }

    private static IEnumerable<string> SummarizeFolderTopLevel(string folderPath)
    {
        var lines = new List<string>();

        try
        {
            var directories = Directory.EnumerateDirectories(folderPath)
                .Take(25)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            var files = Directory.EnumerateFiles(folderPath)
                .Take(500)
                .Select(path => new FileInfo(path))
                .ToList();
            var extensionGroups = files
                .GroupBy(file => string.IsNullOrWhiteSpace(file.Extension) ? "(no extension)" : file.Extension.ToLowerInvariant())
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(group => $"{group.Key}: {group.Count()} file(s)")
                .ToList();
            var largestFiles = files
                .OrderByDescending(file => file.Length)
                .Take(8)
                .Select(file => $"- {file.Name} ({file.Length / 1024d / 1024d:0.0} MB)")
                .ToList();

            lines.Add($"- Subfolders sampled: {directories.Count}");
            if (directories.Count > 0)
            {
                lines.Add($"- Subfolder examples: {string.Join(", ", directories.Take(8))}");
            }

            lines.Add($"- Files sampled: {files.Count}");
            if (extensionGroups.Count > 0)
            {
                lines.Add($"- Extension mix: {string.Join("; ", extensionGroups)}");
            }

            if (largestFiles.Count > 0)
            {
                lines.Add("- Largest sampled files:");
                lines.AddRange(largestFiles);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            lines.Add($"- Folder snapshot unavailable: {ex.GetType().Name}: {ex.Message}");
        }

        if (lines.Count == 0)
        {
            lines.Add("- Folder exists but no top-level files or folders were sampled.");
        }

        return lines;
    }

    private static IEnumerable<string> SummarizeDriveSpace()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(drive =>
                {
                    var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                    var totalGb = drive.TotalSize / 1024d / 1024d / 1024d;
                    return $"- {drive.Name} {freeGb:0.0} GB free of {totalGb:0.0} GB";
                })
                .ToList();

            return drives.Count == 0
                ? ["- No ready drives were found."]
                : drives;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [$"- Drive snapshot unavailable: {ex.GetType().Name}: {ex.Message}"];
        }
    }

    private async Task<CodingToolResult> ResumeBuildPlanAsync(CancellationToken cancellationToken)
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        LoadApprovedPacketIfNeeded();
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<string>
        {
            "Build resume plan:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Git: {gitStatus.Summary}",
            latestReceipt is null
                ? "Latest receipt: none"
                : FormatReceiptSummary("Latest receipt", latestReceipt),
            latestDotNetReceipt is null
                ? "Latest dotnet-style receipt: none"
                : FormatReceiptSummary("Latest dotnet-style receipt", latestDotNetReceipt),
            _roadmapState is null
                ? "Roadmap: none active"
                : $"Roadmap: {DescribeRoadmapState(_roadmapState)}; step {FormatRoadmapCurrentStep(_roadmapState)}",
            _approvedPacket is null
                ? "Approved packet: none active"
                : $"Approved packet: step {_approvedPacket.StepIndex + 1}, approved {_approvedPacket.ApprovedAt:u}",
            "Resume recommendation:"
        };

        if (latestDotNetReceipt is { Succeeded: false })
        {
            lines.Add("- Diagnose the failure first; do not continue builds blindly.");
            lines.Add("- diagnose last build failure");
            lines.Add("- suggest patch from last failure");
            lines.Add("- If no deterministic patch is available, compare options before editing.");
        }
        else if (_approvedPacket is not null)
        {
            lines.Add("- Continue from the approved packet command console.");
            lines.Add("- show packet commands");
            lines.Add("- show packet ledger");
            lines.Add("- show packet progress");
        }
        else if (_roadmapState is not null)
        {
            lines.Add("- Rebuild the next step packet from the active roadmap.");
            lines.Add("- show next coding action");
            lines.Add("- show execution packet");
            lines.Add("- approve execution packet");
        }
        else
        {
            lines.Add("- Start by scouting or drafting a roadmap.");
            lines.Add("- explore build idea <goal>");
            lines.Add("- draft implementation roadmap <goal>");
        }

        lines.Add("Crash recovery guard:");
        lines.Add("- If Ali knows a deterministic fix, use preview/confirmation gates.");
        lines.Add("- If Ali is not sure, stop and compare options before changing code or packages.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Build resume plan",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowWindowsTroubleshootingToolkit()
    {
        var lines = new List<string>
        {
            "Windows troubleshooting toolkit:",
            "Truth boundary: this is a read-only command cookbook. Ali does not kill processes, change services, edit startup items, or repair Windows from this command.",
            "PowerShell process checks:",
            "- Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 Id,ProcessName,CPU,Path",
            "- Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 15 Id,ProcessName,@{Name='MB';Expression={[math]::Round($_.WorkingSet64/1MB,1)}},Path",
            "- Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine | Out-GridView",
            "CMD process checks:",
            "- tasklist /v",
            "- tasklist /svc",
            "Ports and listeners:",
            "- Get-NetTCPConnection -State Listen | Sort-Object LocalPort | Select-Object LocalAddress,LocalPort,OwningProcess",
            "- netstat -ano | findstr LISTENING",
            "- Get-Process -Id <pid>",
            "Services and startup:",
            "- Get-Service | Sort-Object Status,Name",
            "- Get-CimInstance Win32_StartupCommand | Select-Object Name,Command,Location,User",
            "Build/file lock investigation:",
            "- Get-Process dotnet,MSBuild,VBCSCompiler,Ali.App.WebHelper -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path",
            "- tasklist /FI \"IMAGENAME eq dotnet.exe\"",
            "- Use Sysinternals Handle or Process Explorer only after owner approval if a file lock cannot be identified with built-in tools.",
            "Event and health checks:",
            "- Get-EventLog -LogName System -Newest 50 | Select-Object TimeGenerated,EntryType,Source,Message",
            "- Get-EventLog -LogName Application -Newest 50 | Select-Object TimeGenerated,EntryType,Source,Message",
            "- Get-Volume",
            "- Get-PhysicalDisk",
            "- Test-NetConnection <host> -Port <port>",
            "Approval gates:",
            "- Stopping a process needs explicit approval and a named PID/process.",
            "- Disabling startup/service entries needs explicit approval and a rollback note.",
            "- Deleting files, clearing caches, changing registry, firewall, PATH, or trust settings needs explicit approval."
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Windows troubleshooting toolkit",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanRogueProcessHunt(CodingToolRequest request)
    {
        var target = string.IsNullOrWhiteSpace(request.Query)
            ? "the suspicious process, locked file, or busy port"
            : request.Query.Trim();
        var lines = new List<string>
        {
            "Rogue process hunt plan:",
            $"Target: {target}",
            "No processes were stopped and no system settings were changed.",
            "Step 1 - identify symptoms:",
            "- Is it high CPU, high memory, a locked file, a listening port, startup reappearing, or a crashing app?",
            "Step 2 - gather read-only evidence:",
            "- Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 Id,ProcessName,CPU,Path",
            "- Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 15 Id,ProcessName,WorkingSet64,Path",
            "- Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match '<target>' } | Select-Object ProcessId,Name,CommandLine",
            "- netstat -ano | findstr <port-or-name>",
            "- tasklist /FI \"PID eq <pid>\" /V",
            "Step 3 - connect owner to executable:",
            "- Get-Process -Id <pid> | Select-Object Id,ProcessName,Path,StartTime",
            "- Get-CimInstance Win32_Process -Filter \"ProcessId=<pid>\" | Select-Object ProcessId,ParentProcessId,CommandLine,ExecutablePath",
            "Step 4 - decide action:",
            "- If the process is known and safe to restart, ask for explicit approval before Stop-Process.",
            "- If unsure, collect path, command line, parent PID, service name, startup entry, and recent event logs before acting.",
            "Step 5 - approved stop examples:",
            "- Stop-Process -Id <pid>",
            "- taskkill /PID <pid>",
            "- Stop-Service -Name <service-name>",
            "Stop rule: if the executable path, parent process, service owner, or purpose is unclear, do not kill it yet; compare options first."
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Rogue process hunt plan",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult CollectProcessEvidence(CodingToolRequest request)
    {
        var query = request.Query?.Trim();
        var processes = Process.GetProcesses()
            .Select(SnapshotProcess)
            .Where(snapshot => ProcessSnapshotMatches(snapshot, query))
            .OrderByDescending(snapshot => snapshot.WorkingSetBytes)
            .ThenBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();

        var lines = new List<string>
        {
            "Process evidence:",
            string.IsNullOrWhiteSpace(query) ? "Filter: top local processes by memory" : $"Filter: {query}",
            "No processes were stopped and no system settings were changed."
        };

        if (processes.Count == 0)
        {
            lines.Add("- No matching processes were found.");
        }
        else
        {
            lines.Add("Matches:");
            foreach (var process in processes)
            {
                lines.Add($"- PID {process.Id}: {process.Name}; memory {process.WorkingSetMegabytes:0.0} MB; started {process.StartTimeText}; path {process.Path ?? "unavailable"}");
            }
        }

        lines.Add("Next safe commands:");
        lines.Add("- plan process stop <pid>");
        lines.Add("- confirm stop process <pid>");
        lines.Add("- diagnose port <port>");
        lines.Add("- diagnose build lock");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Process evidence",
            Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> DiagnosePortOwnerAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(request.Query, out var port) || port is < 1 or > 65535)
        {
            return new CodingToolResult(true, false, "Port diagnosis needs a port number from 1 to 65535. Example: diagnose port 8765", "Port owner", Policy.WorkspaceRoot);
        }

        var run = await _commandRunner.RunAsync(
            "netstat",
            ["-ano"],
            GetReadOnlyCommandWorkingDirectory(),
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        var output = MergeCommandOutput(run);
        var matches = ParseNetstatPortOwners(run.StandardOutput, port);

        var lines = new List<string>
        {
            "Port owner diagnostic:",
            $"Port: {port}",
            "No processes were stopped and no firewall/network settings were changed.",
            run.TimedOut ? "netstat status: timed out" : $"netstat exit code: {run.ExitCode}"
        };

        if (run.ExitCode != 0 || run.TimedOut)
        {
            lines.Add(TrimForChat(output, 4_000));
        }
        else if (matches.Count == 0)
        {
            lines.Add("- No listener/connection for that local port was found in netstat output.");
        }
        else
        {
            lines.Add("Matches:");
            foreach (var match in matches)
            {
                var process = TrySnapshotProcess(match.ProcessId);
                var processText = process is null
                    ? "process details unavailable"
                    : $"{process.Name}; memory {process.WorkingSetMegabytes:0.0} MB; path {process.Path ?? "unavailable"}";
                lines.Add($"- {match.Protocol} {match.LocalAddress} {match.State} PID {match.ProcessId}: {processText}");
            }
        }

        var suggestedPid = matches.Select(match => match.ProcessId).Distinct().FirstOrDefault();
        lines.Add("Next safe commands:");
        lines.Add(suggestedPid > 0
            ? $"- collect process evidence {suggestedPid}"
            : "- collect process evidence <pid>");
        lines.Add("- plan process stop <pid>");
        lines.Add("- confirm stop process <pid>");

        return new CodingToolResult(
            true,
            run.ExitCode == 0 && !run.TimedOut,
            string.Join(Environment.NewLine, lines),
            "Port owner",
            Policy.WorkspaceRoot,
            ExitCode: run.ExitCode);
    }

    private CodingToolResult DiagnoseFileLock(CodingToolRequest request)
    {
        var target = string.IsNullOrWhiteSpace(request.Query)
            ? "the locked file"
            : request.Query.Trim();
        var lines = new List<string>
        {
            "File lock diagnostic:",
            $"Target: {target}",
            "No processes were stopped and no files were changed.",
            "Built-in evidence Ali can gather now:",
            "- collect process evidence dotnet",
            "- collect process evidence MSBuild",
            "- collect process evidence VBCSCompiler",
            "- collect process evidence Ali.App.WebHelper",
            "- collect process evidence devenv",
            "PowerShell/CMD commands to compare manually:",
            "- tasklist /m",
            "- tasklist /FI \"IMAGENAME eq dotnet.exe\" /V",
            "- Get-Process dotnet,MSBuild,VBCSCompiler,Ali.App.WebHelper,devenv -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path",
            "Deep lock tools:",
            "- Sysinternals Handle or Process Explorer can identify exact file handles, but Ali should only suggest them after owner approval.",
            "Next safe command:",
            "- diagnose build lock"
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "File lock diagnostic",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult InspectServicesStartup()
    {
        var lines = new List<string>
        {
            "Services/startup inspector:",
            "No services were stopped, disabled, started, or changed.",
            "Read-only PowerShell:",
            "- Get-Service | Sort-Object Status,Name | Select-Object Status,Name,DisplayName",
            "- Get-CimInstance Win32_Service | Select-Object Name,State,StartMode,PathName",
            "- Get-CimInstance Win32_StartupCommand | Select-Object Name,Command,Location,User",
            "Read-only CMD:",
            "- sc query state= all",
            "- wmic startup get Caption,Command,Location,User",
            "Approval gate:",
            "- Any service stop/start/disable/delete action needs explicit owner approval, a named service, and a rollback note."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Services/startup inspector", Policy.WorkspaceRoot);
    }

    private CodingToolResult TriageEventLogs()
    {
        var lines = new List<string>
        {
            "Event log triage:",
            "No event logs were cleared and no system settings were changed.",
            "Read-only PowerShell:",
            "- Get-EventLog -LogName System -EntryType Error,Warning -Newest 50 | Select-Object TimeGenerated,EntryType,Source,EventID,Message",
            "- Get-EventLog -LogName Application -EntryType Error,Warning -Newest 50 | Select-Object TimeGenerated,EntryType,Source,EventID,Message",
            "- Get-WinEvent -LogName System -MaxEvents 50 | Select-Object TimeCreated,LevelDisplayName,ProviderName,Id,Message",
            "Useful filters:",
            "- Filter around the crash time first.",
            "- Compare application errors, service-control-manager events, disk warnings, and .NET runtime events.",
            "Stop rule: do not clear logs during diagnosis; they are evidence."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Event log triage", Policy.WorkspaceRoot);
    }

    private CodingToolResult PlanProcessStop(CodingToolRequest request)
    {
        var query = request.Query?.Trim();
        var lines = new List<string>
        {
            "Approved process stop plan:",
            string.IsNullOrWhiteSpace(query) ? "Target: not specified" : $"Target: {query}",
            "No process was stopped.",
            "Required before execution:",
            "- Numeric PID.",
            "- Evidence that the PID belongs to the intended process.",
            "- Confirmation that stopping it is acceptable.",
            "Safe next commands:",
            string.IsNullOrWhiteSpace(query) ? "- collect process evidence <name-or-pid>" : $"- collect process evidence {query}",
            "- confirm stop process <pid>",
            "Rollback note:",
            "- If the process is a helper/service, know how to restart it before stopping it.",
            "Stop rule: if PID, path, parent process, or purpose is unclear, do not stop it yet."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Process stop plan", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ExecuteProcessStopAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(request.Query, out var pid) || pid < 1)
        {
            return new CodingToolResult(true, false, "Stopping a process requires a numeric PID. Example: confirm stop process 1234", "Process stop", Policy.WorkspaceRoot);
        }

        if (pid == Environment.ProcessId)
        {
            return new CodingToolResult(true, false, "Process stop blocked: Ali will not stop the process currently executing this command.", "Process stop", Policy.WorkspaceRoot);
        }

        var before = TrySnapshotProcess(pid);
        if (before is null)
        {
            return new CodingToolResult(true, false, $"Process stop blocked: PID {pid} was not found.", "Process stop", Policy.WorkspaceRoot);
        }

        var run = await _commandRunner.RunAsync(
            "taskkill",
            ["/PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            GetReadOnlyCommandWorkingDirectory(),
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        var output = TrimForChat(MergeCommandOutput(run), MaxCommandOutputCharacters);
        var message = new List<string>
        {
            "Process stop result:",
            $"Requested PID: {pid}",
            $"Before: {before.Name}; path {before.Path ?? "unavailable"}",
            run.TimedOut ? "taskkill status: timed out" : $"taskkill exit code: {run.ExitCode}",
            string.IsNullOrWhiteSpace(output) ? "taskkill output: none" : output,
            "Receipt boundary: Ali requested a normal taskkill by PID. No force flag was used."
        };

        return new CodingToolResult(
            true,
            run.ExitCode == 0 && !run.TimedOut,
            string.Join(Environment.NewLine, message),
            "Process stop",
            before.Path,
            ExitCode: run.ExitCode);
    }

    private CodingToolResult DiagnoseBuildLock()
    {
        var processNames = new[] { "dotnet", "MSBuild", "VBCSCompiler", "Ali.App.WebHelper", "devenv" };
        var snapshots = Process.GetProcesses()
            .Select(SnapshotProcess)
            .Where(snapshot => processNames.Any(name => snapshot.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.Id)
            .ToList();
        var lines = new List<string>
        {
            "Build lock diagnostic:",
            "No processes were stopped and no build outputs were deleted.",
            "Common lock suspects:"
        };

        if (snapshots.Count == 0)
        {
            lines.Add("- No common build-lock suspect processes were found.");
        }
        else
        {
            foreach (var snapshot in snapshots)
            {
                lines.Add($"- PID {snapshot.Id}: {snapshot.Name}; memory {snapshot.WorkingSetMegabytes:0.0} MB; path {snapshot.Path ?? "unavailable"}");
            }
        }

        lines.Add("Recommended recovery:");
        lines.Add("- If WebHelper is locking Ali DLLs, stop only Ali.App.WebHelper, rebuild, then restart it.");
        lines.Add("- If compiler/build servers are stale, run: dotnet build-server shutdown");
        lines.Add("- If Visual Studio is holding build output, close the solution or stop debugging.");
        lines.Add("- Use confirm stop process <pid> only after verifying the PID belongs to the intended helper.");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Build lock diagnostic", Policy.WorkspaceRoot);
    }

    private CodingToolResult ClassifyLastFailure()
    {
        if (_lastDotNetRequest is null || _lastDotNetResult is not { Succeeded: false } result)
        {
            return new CodingToolResult(true, true, "No failed dotnet command is stored in this Ali session.", "Failure classifier", Policy.WorkspaceRoot);
        }

        var category = ClassifyFailureMessage(result.Message);
        var lines = new List<string>
        {
            "Failure classifier:",
            $"Action: {_lastDotNetRequest.Action}",
            $"Target: {result.TargetPath ?? _lastDotNetRequest.Path ?? Policy.WorkspaceRoot}",
            $"Category: {category}",
            result.ExitCode is null ? "Exit code: unavailable" : $"Exit code: {result.ExitCode.Value}",
            "Next safe commands:"
        };

        lines.AddRange(category switch
        {
            "locked file" => ["- diagnose build lock", "- collect process evidence Ali.App.WebHelper", "- dotnet build-server shutdown"],
            "restore/package" => ["- plan package lookup current failure", "- confirm dotnet restore \"path\""],
            "compiler" => ["- diagnose last build failure", "- open build error", "- suggest patch from last failure"],
            "test" => ["- diagnose last test failure", "- read failing test file", "- plan coding task fix failing test"],
            "missing sdk/tool" => ["- show install doctor", "- inspect services startup"],
            _ => ["- diagnose last build failure", "- show coding receipts", "- resume build plan"]
        });

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Failure classifier", result.TargetPath ?? Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowRoadmapStepChecklistAsync(CancellationToken cancellationToken)
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "Roadmap step acceptance checklist:",
            _roadmapState is null ? "Roadmap: none active" : $"Roadmap: {DescribeRoadmapState(_roadmapState)}",
            _roadmapState is null ? "Step: unavailable" : $"Step: {FormatRoadmapCurrentStep(_roadmapState)}",
            $"Git: {gitStatus.Summary}",
            latestDotNetReceipt is null ? "Latest validation: none" : FormatReceiptSummary("Latest validation", latestDotNetReceipt),
            "Checklist:",
            $"- Read-only prep reviewed: {(receipts.Any(IsPacketPrepReceipt) ? "yes" : "not proven by receipts")}",
            $"- Execute action recorded: {(receipts.Any(IsPacketExecutionReceipt) ? "yes" : "not proven by receipts")}",
            $"- Validation clean: {(latestDotNetReceipt is { Succeeded: true } ? "yes" : latestDotNetReceipt is { Succeeded: false } ? "no, latest validation failed" : "not proven by receipts")}",
            $"- Git reviewed/clean: {(gitStatus.Available && gitStatus.Clean ? "yes" : "review needed")}",
            "Recommendation:"
        };

        if (latestDotNetReceipt is { Succeeded: false })
        {
            lines.Add("- Do not mark the step complete yet. Diagnose or fix the failure first.");
        }
        else if (gitStatus.HasUncommittedChanges)
        {
            lines.Add("- Review git diff/status before marking complete or committing.");
        }
        else
        {
            lines.Add("- If owner-visible behavior is complete and receipts match the step, it is reasonable to mark the step complete.");
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Roadmap checklist", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowInstallDoctor()
    {
        var visualStudio = CodingToolLocator.FindVisualStudio(_configuredVisualStudioPath);
        var notepadPlusPlus = CodingToolLocator.FindNotepadPlusPlus(_configuredNotepadPlusPlusPath);
        var devRun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ali", "DevRun", "Ali.App.Wpf.exe");
        var vsix = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Ali.App.VisualStudioExtension", "bin", "Debug", "net472", "Ali.App.VisualStudioExtension.vsix");
        var runtimeSettingsPath = RuntimeSettingsStore.GetSettingsPath(dataRoot);
        var runtimeSettings = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot);
        var helperUrl = Environment.GetEnvironmentVariable("ALI_HELPER_URL") ?? "http://127.0.0.1:8765/";
        var lines = new List<string>
        {
            "Ali install doctor:",
            "No files were changed and no installers were run.",
            $"- Workspace root: {Policy.WorkspaceRoot}",
            $"- Workspace exists: {Directory.Exists(Policy.WorkspaceRoot)}",
            $"- DevRun executable: {(File.Exists(devRun) ? devRun : "missing")}",
            $"- PDF workspace: {_pdfWorkspaceRoot}",
            $"- Visual Studio: {visualStudio ?? "not found"}",
            $"- Notepad++: {notepadPlusPlus ?? "not found"}",
            $"- VSIX build artifact: {(File.Exists(Path.GetFullPath(vsix)) ? Path.GetFullPath(vsix) : "not found from current app base")}",
            $"- WebHelper bridge URL: {helperUrl}",
            $"- Runtime settings: {(File.Exists(runtimeSettingsPath) ? runtimeSettingsPath : "missing")}",
            $"- Saved runtime model: {runtimeSettings?.Model ?? "not configured"}",
            $"- Current .NET runtime: {Environment.Version}",
            $"- OS: {Environment.OSVersion}",
            "Manual dependency checks:",
            "- dotnet --info",
            "- git --version",
            "- ollama list",
            "- Confirm `ali-deepseek-coder-v2:16b-low` is installed for coding chat.",
            "- Confirm `qwen3-vl:8b` or `ali-qwen3-vl:8b-low` is installed only if vision/image reasoning is needed.",
            "- show visual studio integration",
            "- show pdf tool status",
            "- show windows troubleshooting toolkit",
            "Safe install/repair sequence:",
            "- Build: dotnet build .\\Ali.sln --no-restore -p:UseSharedCompilation=false -nr:false",
            "- Test: dotnet run --project .\\tests\\Ali.Tests\\Ali.Tests.csproj --no-build",
            "- Refresh only `%LOCALAPPDATA%\\Ali\\DevRun`; do not create DevRun-* folders.",
            "- Install/update the VSIX into the selected Visual Studio Community instance.",
            "- Start WebHelper on loopback before using VS Companion commands.",
            "Repair boundary: installer, VSIX install, signing, trust-store, PATH, registry, firewall, and service changes require explicit owner approval."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Install doctor", devRun);
    }

    private CodingToolResult AdvanceRoadmapStep()
    {
        LoadRoadmapStateIfNeeded();
        if (!TryGetActiveRoadmapForStepChange(out var state, out var error))
        {
            return error;
        }

        var nextIndex = state.CurrentStepIndex + 1;
        if (nextIndex >= state.Steps.Count)
        {
            _roadmapState = state with
            {
                Finished = true,
                Paused = false,
                CurrentStepIndex = Math.Max(0, state.Steps.Count - 1),
                UpdatedAt = DateTimeOffset.UtcNow,
                LastReceiptSummary = $"Completed final roadmap step: {FormatRoadmapCurrentStep(state)}"
            };
            SyncRoadmapFieldsFromState(_roadmapState);
            SaveRoadmapState();
            return new CodingToolResult(
                true,
                true,
                $"Roadmap finished. No files were changed by this state update.{Environment.NewLine}{FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true)}",
                "Roadmap execution",
                Policy.WorkspaceRoot);
        }

        _roadmapState = state with
        {
            CurrentStepIndex = nextIndex,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastReceiptSummary = $"Advanced from step {state.CurrentStepIndex + 1} to step {nextIndex + 1}."
        };
        SyncRoadmapFieldsFromState(_roadmapState);
        SaveRoadmapState();
        return new CodingToolResult(
            true,
            true,
            $"Roadmap step advanced. No files were changed by this state update.{Environment.NewLine}{FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true)}",
            "Roadmap execution",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult PauseRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_roadmapState is null)
        {
            return new CodingToolResult(true, false, "No roadmap state is available to pause.", "Roadmap execution", Policy.WorkspaceRoot);
        }

        _roadmapState = _roadmapState with
        {
            Paused = true,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastReceiptSummary = "Roadmap paused. No files were changed."
        };
        SyncRoadmapFieldsFromState(_roadmapState);
        SaveRoadmapState();
        return new CodingToolResult(
            true,
            true,
            $"Paused roadmap execution. No files were changed.{Environment.NewLine}{FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true)}",
            "Roadmap execution",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ResumeRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_roadmapState is null)
        {
            return new CodingToolResult(true, false, "No roadmap state is available to resume.", "Roadmap execution", Policy.WorkspaceRoot);
        }

        _roadmapState = _roadmapState with
        {
            Paused = false,
            Finished = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastReceiptSummary = "Roadmap resumed. No files were changed."
        };
        SyncRoadmapFieldsFromState(_roadmapState);
        SaveRoadmapState();
        return new CodingToolResult(
            true,
            true,
            $"Resumed roadmap execution. No files were changed.{Environment.NewLine}{FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true)}",
            "Roadmap execution",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult FinishRoadmap()
    {
        LoadRoadmapStateIfNeeded();
        if (_roadmapState is null)
        {
            return new CodingToolResult(true, false, "No roadmap state is available to finish.", "Roadmap execution", Policy.WorkspaceRoot);
        }

        _roadmapState = _roadmapState with
        {
            Finished = true,
            Paused = false,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastReceiptSummary = "Roadmap manually marked finished. No files were changed."
        };
        SyncRoadmapFieldsFromState(_roadmapState);
        SaveRoadmapState();
        return new CodingToolResult(
            true,
            true,
            $"Finished roadmap execution state. No files were changed.{Environment.NewLine}{FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true)}",
            "Roadmap execution",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult RecoverRoadmapState()
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        if (_roadmapState is null)
        {
            return new CodingToolResult(
                true,
                true,
                $"No saved roadmap state was found at: {_roadmapStatePath}",
                "Roadmap recovery",
                _roadmapStatePath);
        }

        return new CodingToolResult(
            true,
            true,
            $"Recovered roadmap state from disk.{Environment.NewLine}{FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true)}",
            "Roadmap recovery",
            _roadmapStatePath);
    }

    private async Task<CodingToolResult> DiagnoseRecoveryStateAsync(CancellationToken cancellationToken)
    {
        _roadmapStateLoaded = false;
        LoadRoadmapStateIfNeeded();
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestReceipt = receipts.LastOrDefault();
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var activeRoadmap = _roadmapState is { Approved: true, Started: true, Finished: false };
        var validationAfterRoadmapUpdate = _roadmapState is not null
            && receipts.Any(receipt => receipt.Timestamp >= _roadmapState.UpdatedAt && IsValidationReceipt(receipt));

        var lines = new List<string>
        {
            "Crash recovery diagnostics:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Roadmap state file: {_roadmapStatePath}",
            $"Action receipt log: {_actionLogPath}",
            "Active roadmap:"
        };

        if (_roadmapState is null)
        {
            lines.Add("- none recovered");
        }
        else
        {
            lines.Add($"- status: {DescribeRoadmapState(_roadmapState)}");
            lines.Add($"- current step: {FormatRoadmapCurrentStep(_roadmapState)}");
            lines.Add($"- updated: {_roadmapState.UpdatedAt:u}");
            lines.Add($"- last roadmap note: {_roadmapState.LastReceiptSummary}");
        }

        lines.Add("Interrupted command check:");
        if (latestDotNetReceipt is null)
        {
            lines.Add("- no dotnet/build/test/package receipt found yet");
            lines.Add("- if a command was interrupted before it wrote a receipt, rerun the confirmed validation command before marking a step complete");
        }
        else
        {
            var dotNetStatus = latestDotNetReceipt.Succeeded ? "succeeded" : "failed";
            var exit = latestDotNetReceipt.ExitCode is null ? string.Empty : $" exit={latestDotNetReceipt.ExitCode.Value}";
            var target = string.IsNullOrWhiteSpace(latestDotNetReceipt.TargetPath) ? string.Empty : $" target={latestDotNetReceipt.TargetPath}";
            lines.Add($"- last dotnet-style receipt: {latestDotNetReceipt.Timestamp:u} {latestDotNetReceipt.Action} {dotNetStatus}{exit}{target}");

            if (!latestDotNetReceipt.Succeeded)
            {
                lines.Add("- last validation did not pass; do not mark the roadmap step complete yet");
            }
            else if (_roadmapState is not null && latestDotNetReceipt.Timestamp < _roadmapState.UpdatedAt)
            {
                lines.Add("- last validation is older than the current roadmap state; rerun validation before continuing");
            }
            else
            {
                lines.Add("- last validation has a success receipt");
            }
        }

        lines.Add("Git working tree:");
        lines.Add($"- {gitStatus.Summary}");
        foreach (var entry in gitStatus.Entries.Take(8))
        {
            lines.Add($"- {entry}");
        }

        if (gitStatus.Entries.Count > 8)
        {
            lines.Add($"- plus {gitStatus.Entries.Count - 8} more change(s)");
        }

        lines.Add("Roadmap versus receipts:");
        if (_roadmapState is null)
        {
            lines.Add("- no active roadmap state to compare against receipts");
        }
        else if (latestReceipt is null)
        {
            lines.Add("- no readable receipts yet; use the roadmap state only as a planning marker");
        }
        else
        {
            lines.Add($"- latest receipt: {latestReceipt.Timestamp:u} {latestReceipt.Action} {(latestReceipt.Succeeded ? "succeeded" : "failed")}");
            lines.Add(validationAfterRoadmapUpdate
                ? "- at least one validation/edit receipt exists after the current roadmap update"
                : "- no validation/edit receipt exists after the current roadmap update");
        }

        lines.Add("Suggested continue path:");
        if (gitStatus.HasUncommittedChanges)
        {
            lines.Add("- run: git status");
            lines.Add("- review changed files before continuing the roadmap");
        }
        else if (activeRoadmap)
        {
            lines.Add("- run: show active roadmap step");
            lines.Add("- run: show execution packet");
            lines.Add("- rerun the needed confirmed build/test/package command if no current success receipt exists");
        }
        else
        {
            lines.Add("- run: plan coding task <goal>");
        }

        lines.Add("Suggested fix path:");
        if (latestDotNetReceipt is { Succeeded: false })
        {
            lines.Add("- run: diagnose last build failure");
            lines.Add("- if the diagnosis names an exact code change, run: suggest patch from last failure");
            lines.Add("- apply only after preview and confirmation");
        }
        else
        {
            lines.Add("- if Ali has a concrete, receipt-backed fix, ask for a guarded patch preview and confirm before applying");
            lines.Add("- if the evidence is unclear, pause and compare options before editing");
        }

        lines.Add("Suggested rollback path:");
        lines.Add("- do not auto-reset or discard changes");
        lines.Add("- use git diff / git status to identify what changed, then choose continue, patch, commit, or manual rollback");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Recovery diagnostics",
            Policy.WorkspaceRoot);
    }

    private void LoadRoadmapStateIfNeeded()
    {
        if (_roadmapStateLoaded)
        {
            return;
        }

        _roadmapStateLoaded = true;
        if (!File.Exists(_roadmapStatePath))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(_roadmapStatePath);
            _roadmapState = JsonSerializer.Deserialize<RoadmapExecutionState>(stream, RoadmapJsonOptions);
            if (_roadmapState is not null)
            {
                SyncRoadmapFieldsFromState(_roadmapState);
            }
        }
        catch
        {
            _roadmapState = null;
        }
    }

    private void SaveRoadmapState()
    {
        _roadmapStateLoaded = true;
        if (_roadmapState is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_roadmapStatePath)!);
        using var stream = File.Create(_roadmapStatePath);
        JsonSerializer.Serialize(stream, _roadmapState, RoadmapJsonOptions);
    }

    private void LoadApprovedPacketIfNeeded()
    {
        if (_approvedPacketLoaded)
        {
            return;
        }

        _approvedPacketLoaded = true;
        if (!File.Exists(_approvedPacketPath))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(_approvedPacketPath);
            _approvedPacket = JsonSerializer.Deserialize<ApprovedRoadmapExecutionPacket>(stream, RoadmapJsonOptions);
        }
        catch
        {
            _approvedPacket = null;
        }
    }

    private void SaveApprovedPacket()
    {
        _approvedPacketLoaded = true;
        if (_approvedPacket is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_approvedPacketPath)!);
        using var stream = File.Create(_approvedPacketPath);
        JsonSerializer.Serialize(stream, _approvedPacket, RoadmapJsonOptions);
    }

    private void ClearApprovedPacket()
    {
        _approvedPacket = null;
        _approvedPacketLoaded = true;
        if (File.Exists(_approvedPacketPath))
        {
            File.Delete(_approvedPacketPath);
        }
    }

    private void ClearRoadmapState()
    {
        _lastRoadmapRequest = null;
        _lastRoadmapApproved = false;
        _approvedRoadmapStarted = false;
        _roadmapState = null;
        _roadmapStateLoaded = true;
        if (File.Exists(_roadmapStatePath))
        {
            File.Delete(_roadmapStatePath);
        }

        ClearApprovedPacket();
    }

    private void SyncRoadmapFieldsFromState(RoadmapExecutionState state)
    {
        _lastRoadmapRequest = new CodingToolRequest(
            CodingToolAction.DraftImplementationRoadmap,
            null,
            Query: state.Goal);
        _lastRoadmapApproved = state.Approved;
        _approvedRoadmapStarted = state.Started;
    }

    private RoadmapExecutionState CreateRoadmapState(string goal) =>
        new(
            Goal: goal,
            Approved: false,
            Started: false,
            Paused: false,
            Finished: false,
            CurrentStepIndex: 0,
            Steps: BuildRoadmapExecutionSteps(goal),
            LastReceiptSummary: "Roadmap drafted. No files were changed.",
            UpdatedAt: DateTimeOffset.UtcNow);

    private static IReadOnlyList<string> BuildRoadmapExecutionSteps(string goal)
    {
        var steps = new List<string>
        {
            "Clarify owner-visible behavior and non-goals.",
            "Inspect workspace architecture and identify the smallest impact surface.",
            "Plan the exact next code/package/build action.",
            "Preview or execute the approved action through Ali's guarded command.",
            "Run confirmed validation for the changed surface.",
            "Review receipts and decide whether the step is complete.",
            "Stage and commit the completed phase when Chris approves."
        };

        if (MentionsAny(goal, "package", "library", "dependency", "nuget", "install"))
        {
            steps.Insert(3, "Approve and run any required package lookup or package install.");
        }

        return steps;
    }

    private static string FormatRoadmapCurrentStep(RoadmapExecutionState state)
    {
        if (state.Steps.Count == 0)
        {
            return "no steps recorded";
        }

        var index = Math.Clamp(state.CurrentStepIndex, 0, state.Steps.Count - 1);
        return $"{index + 1}/{state.Steps.Count}: {state.Steps[index]}";
    }

    private static string GetRoadmapCurrentStep(RoadmapExecutionState state)
    {
        if (state.Steps.Count == 0)
        {
            return string.Empty;
        }

        var index = Math.Clamp(state.CurrentStepIndex, 0, state.Steps.Count - 1);
        return state.Steps[index];
    }

    private static string DescribeRoadmapState(RoadmapExecutionState state) =>
        state.Finished
            ? "finished"
            : state.Paused
                ? "paused"
                : state.Started
                    ? "active"
                    : state.Approved
                        ? "approved"
                        : "pending approval";

    private string FormatRoadmapStatus(RoadmapExecutionState state, bool includeRecoveryPath)
    {
        var status = DescribeRoadmapState(state);
        var lines = new List<string>
        {
            "Roadmap execution state:",
            $"Goal: {state.Goal}",
            $"Status: {status}",
            $"Current step: {FormatRoadmapCurrentStep(state)}",
            $"Updated: {state.UpdatedAt:u}",
            $"Last receipt snapshot: {state.LastReceiptSummary}",
            "Next safe commands:"
        };

        if (!state.Approved)
        {
            lines.Add("- approve last roadmap");
        }
        else if (!state.Started)
        {
            lines.Add("- start approved roadmap");
        }
        else if (state.Paused)
        {
            lines.Add("- resume roadmap");
        }
        else if (state.Finished)
        {
            lines.Add("- generate coding report");
        }
        else
        {
            lines.Add("- plan coding task <current step>");
            lines.Add("- show next coding action");
            lines.Add("- show execution packet");
            lines.Add("- preview patch bundle");
            lines.Add("- confirm dotnet build \"path\"");
            lines.Add("- mark roadmap step complete");
            lines.Add("- pause roadmap");
        }

        if (includeRecoveryPath)
        {
            lines.Add($"Recovery file: {_roadmapStatePath}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static RoadmapNextActionRecommendation BuildNextRoadmapRecommendation(
        RoadmapExecutionState state,
        GitWorkingTreeStatus gitStatus,
        CodingReceipt? latestDotNetReceipt,
        string primaryTarget)
    {
        if (!state.Approved)
        {
            return new RoadmapNextActionRecommendation(
                "approve the roadmap or revise it before execution",
                "high. Approval state is explicit.",
                ["The roadmap exists but is still pending approval.", "Approving a roadmap changes only Ali's local planning state."],
                ["approve last roadmap", "show pending roadmap"]);
        }

        if (!state.Started)
        {
            return new RoadmapNextActionRecommendation(
                "start the approved roadmap",
                "high. The roadmap is approved and waiting to start.",
                ["Starting records execution state without changing code.", "After start, Ali can track the current step and recovery file."],
                ["start approved roadmap", "show active roadmap step"]);
        }

        if (state.Paused)
        {
            return new RoadmapNextActionRecommendation(
                "resume the roadmap or inspect recovery before changing anything",
                "high. The roadmap is intentionally paused.",
                ["Paused state should be cleared before step work continues.", "Recovery status can compare receipts and Git state first."],
                ["show crash recovery status", "resume roadmap"]);
        }

        if (state.Finished)
        {
            return new RoadmapNextActionRecommendation(
                "generate a report or commit the completed phase after review",
                "medium. The roadmap is finished, but Git state still decides the final closeout path.",
                ["Finished roadmap state does not prove Git is clean or committed.", "A report captures receipts for handoff."],
                ["show coding receipts", "generate coding report", "git status", "confirm git commit \"message\""]);
        }

        if (gitStatus.HasUncommittedChanges)
        {
            return new RoadmapNextActionRecommendation(
                "review the working tree before continuing",
                "high. Git reports uncommitted changes.",
                ["Unexpected or unreviewed changes can make roadmap recovery misleading.", "Read-only Git inspection is allowed before deciding whether to continue, patch, or commit."],
                ["git status", "git diff", "show crash recovery status"]);
        }

        if (latestDotNetReceipt is { Succeeded: false })
        {
            return new RoadmapNextActionRecommendation(
                "diagnose the failed validation and preview a fix only if evidence is concrete",
                "high. The latest dotnet-style receipt failed.",
                ["A failed build/test/package receipt blocks marking the step complete.", "Ali can suggest a deterministic patch only for failure shapes she understands."],
                ["diagnose last build failure", "suggest patch from last failure", "show crash recovery status"]);
        }

        var step = state.Steps.Count == 0
            ? string.Empty
            : state.Steps[Math.Clamp(state.CurrentStepIndex, 0, state.Steps.Count - 1)];
        if (MentionsAny(step, "Clarify", "owner", "behavior", "non-goals"))
        {
            return new RoadmapNextActionRecommendation(
                "clarify the owner-visible behavior and boundaries",
                "medium. This is a planning step, so the output depends on the goal wording.",
                ["No code action is needed yet.", "A build idea scout can compare paths and libraries without claiming current package versions."],
                [$"explore build idea {state.Goal}", $"plan coding task {step}"]);
        }

        if (MentionsAny(step, "Inspect", "architecture", "impact surface"))
        {
            return new RoadmapNextActionRecommendation(
                "inspect architecture and identify the smallest impact surface",
                "high. These are read-only workspace commands.",
                ["Architecture and workspace inspection are deterministic local reads.", "This should happen before selecting files to edit."],
                ["analyze solution architecture", "inspect coding workspace", $"plan coding task {state.Goal}"]);
        }

        if (MentionsAny(step, "Plan", "exact next", "code", "package", "build action"))
        {
            return new RoadmapNextActionRecommendation(
                "create the exact next guarded task plan",
                "medium. Ali can plan safely, but execution still needs approval.",
                ["The current step is about choosing the next concrete action.", "Package lookup/install, edits, builds, and Git writes remain gated."],
                [$"plan coding task {state.Goal}", "list packages", "show visual studio integration"]);
        }

        if (MentionsAny(step, "Preview", "execute", "approved action", "patch"))
        {
            return new RoadmapNextActionRecommendation(
                "prepare a guarded preview or run the approved confirmed command",
                "medium. The correct command depends on the selected implementation action.",
                ["File edits should be previewed before apply.", "Package/build/test/run commands need explicit confirmation."],
                ["preview patch bundle", $"confirm dotnet build \"{primaryTarget}\"", $"confirm dotnet test \"{primaryTarget}\""]);
        }

        if (MentionsAny(step, "validation", "changed surface", "build", "test"))
        {
            return new RoadmapNextActionRecommendation(
                "run confirmed validation for the changed surface",
                "medium. The primary target is deterministic, but the owner still approves execution.",
                ["Validation commands are not run automatically.", "A passing receipt is needed before the step should be marked complete."],
                [$"confirm dotnet build \"{primaryTarget}\"", $"confirm dotnet test \"{primaryTarget}\"", "diagnose last build failure"]);
        }

        if (MentionsAny(step, "receipts", "decide", "complete"))
        {
            return new RoadmapNextActionRecommendation(
                "review receipts, then advance only if the evidence supports it",
                latestDotNetReceipt is { Succeeded: true } ? "high. The latest dotnet-style receipt succeeded." : "medium. Receipt review is still needed.",
                ["Roadmap advancement changes planning state only.", "Do not mark complete after a failed or missing validation when validation was required."],
                ["show coding receipts", "show crash recovery status", "mark roadmap step complete"]);
        }

        if (MentionsAny(step, "Stage", "commit", "completed phase"))
        {
            return new RoadmapNextActionRecommendation(
                "review Git state and commit only after approval",
                "medium. Git status must be reviewed before write actions.",
                ["Git staging and commits remain confirmed actions.", "Commit only after build/test receipts support the phase."],
                ["git status", "git diff", "confirm git add all", "confirm git commit \"message\""]);
        }

        return new RoadmapNextActionRecommendation(
            "plan the current roadmap step before executing",
            "medium. The step is active but does not map to a specialized command lane.",
            ["Ali can produce a guarded plan from the current step.", "Execution remains behind the normal approval gates."],
            [$"plan coding task {step}", "show crash recovery status"]);
    }

    private static string DescribeExecutionPacketStatus(
        RoadmapExecutionState state,
        GitWorkingTreeStatus gitStatus,
        CodingReceipt? latestDotNetReceipt)
    {
        if (!state.Approved)
        {
            return "not-ready: roadmap needs approval";
        }

        if (!state.Started)
        {
            return "not-ready: roadmap has not started";
        }

        if (state.Paused)
        {
            return "not-ready: roadmap is paused";
        }

        if (state.Finished)
        {
            return "closeout: roadmap is finished";
        }

        if (gitStatus.HasUncommittedChanges)
        {
            return "review-first: Git has uncommitted changes";
        }

        if (latestDotNetReceipt is { Succeeded: false })
        {
            return "blocked: latest validation failed";
        }

        return "ready: use the packet commands through approval gates";
    }

    private static string FormatReceiptSummary(string label, CodingReceipt receipt)
    {
        var target = string.IsNullOrWhiteSpace(receipt.TargetPath) ? string.Empty : $" target={receipt.TargetPath}";
        var exit = receipt.ExitCode is null ? string.Empty : $" exit={receipt.ExitCode.Value}";
        return $"{label}: {receipt.Timestamp:u} {receipt.Action} {(receipt.Succeeded ? "succeeded" : "failed")}{exit}{target}";
    }

    private static IReadOnlyList<string> BuildPacketReceiptMatchLines(
        ApprovedRoadmapExecutionPacket packet,
        IReadOnlyList<CodingReceipt> receipts,
        GitWorkingTreeStatus gitStatus,
        bool stale)
    {
        var packetReceipts = receipts
            .Where(receipt => receipt.Timestamp >= packet.ApprovedAt)
            .OrderBy(receipt => receipt.Timestamp)
            .ToList();
        var latestDotNet = packetReceipts.LastOrDefault(IsDotNetReceipt);
        var prepDone = packetReceipts.Any(IsPacketPrepReceipt);
        var executionDone = packetReceipts.Any(IsPacketExecutionReceipt);
        var validationDone = packetReceipts.Any(IsPacketValidationReceipt);
        var validationFailed = latestDotNet is { Succeeded: false };
        var closeoutDone = packetReceipts.Any(IsPacketCloseoutReceipt);

        var lines = new List<string>
        {
            $"- Receipts since approval: {packetReceipts.Count}",
            $"- Prep: {(prepDone ? "done" : "waiting")} (read-only context or packet review)",
            $"- Execute: {(executionDone ? "done" : "waiting")} (candidate command through normal approval gate)"
        };

        if (validationFailed)
        {
            lines.Add("- Validate: blocked (latest dotnet-style receipt failed)");
        }
        else
        {
            lines.Add($"- Validate: {(validationDone ? "done" : "waiting")} (confirmed build/test/package receipt)");
        }

        if (stale)
        {
            lines.Add("- Closeout: blocked (packet is stale against roadmap state)");
        }
        else if (gitStatus.HasUncommittedChanges)
        {
            lines.Add("- Closeout: review-first (Git has uncommitted changes)");
        }
        else
        {
            lines.Add($"- Closeout: {(closeoutDone ? "done" : "waiting")} (receipt review, roadmap advance, report, or commit)");
        }

        return lines;
    }

    private static bool IsPacketPrepReceipt(CodingReceipt receipt) =>
        receipt.Succeeded
        && receipt.Action is nameof(CodingToolAction.ShowNextRoadmapAction)
            or nameof(CodingToolAction.ShowRoadmapExecutionPacket)
            or nameof(CodingToolAction.ShowApprovedRoadmapExecutionPacket)
            or nameof(CodingToolAction.ShowReceipts)
            or nameof(CodingToolAction.DiagnoseRecoveryState);

    private static bool IsPacketExecutionReceipt(CodingReceipt receipt) =>
        receipt.Succeeded
        && receipt.Action is nameof(CodingToolAction.PreviewPatchBundle)
            or nameof(CodingToolAction.ApplyLastPatchPreview)
            or nameof(CodingToolAction.CreateFile)
            or nameof(CodingToolAction.AppendFile)
            or nameof(CodingToolAction.ReplaceText)
            or nameof(CodingToolAction.Restore)
            or nameof(CodingToolAction.AddPackage)
            or nameof(CodingToolAction.RunProject);

    private static bool IsPacketValidationReceipt(CodingReceipt receipt) =>
        receipt.Succeeded
        && (IsDotNetReceipt(receipt)
            || receipt.Action is nameof(CodingToolAction.ShowLastPatchPreview)
                or nameof(CodingToolAction.GitStatus)
                or nameof(CodingToolAction.GitDiff));

    private static bool IsPacketCloseoutReceipt(CodingReceipt receipt) =>
        receipt.Succeeded
        && receipt.Action is nameof(CodingToolAction.AdvanceRoadmapStep)
            or nameof(CodingToolAction.ShowRoadmapExecutionPacketProgress)
            or nameof(CodingToolAction.GenerateCodingReport)
            or nameof(CodingToolAction.GitAdd)
            or nameof(CodingToolAction.GitCommit);

    private static void AddUniqueCommands(List<string> lines, IEnumerable<string> commands)
    {
        var unique = commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unique.Count == 0)
        {
            lines.Add("- none");
            return;
        }

        lines.AddRange(unique.Select(command => $"- {command}"));
    }

    private static IReadOnlyList<PacketCommandItem> FlattenApprovedPacketCommands(ApprovedRoadmapExecutionPacket packet)
    {
        var items = new List<PacketCommandItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSection("Prep", packet.PrepCommands);
        AddSection("Execute", packet.ExecutionCommands);
        AddSection("Validate", packet.ValidationCommands);
        AddSection("Closeout", packet.CloseoutCommands);
        return items;

        void AddSection(string section, IEnumerable<string> commands)
        {
            foreach (var command in commands
                         .Where(command => !string.IsNullOrWhiteSpace(command))
                         .Select(command => command.Trim()))
            {
                if (!seen.Add(command))
                {
                    continue;
                }

                items.Add(new PacketCommandItem(items.Count + 1, section, command));
            }
        }
    }

    private static bool PacketCommandNeedsConfirmation(string command)
    {
        var normalized = command.Trim();
        if (normalized.StartsWith("confirm ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("confirmed ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("go ahead ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.StartsWith("apply ", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("restore ", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("run project", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("git add", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("git commit", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("git merge", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("git pull", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("git push", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("create file", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("append file", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("append to file", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("replace in file", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("replace text in file", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("mark roadmap step complete", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("advance roadmap step", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("finish roadmap", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("discard ", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildExecutionCandidateCommands(
        RoadmapExecutionState state,
        RoadmapNextActionRecommendation recommendation,
        string currentStep,
        string primaryTarget)
    {
        var commands = new List<string>();
        commands.AddRange(recommendation.Commands);

        if (MentionsAny(currentStep, "Clarify", "owner", "behavior", "non-goals"))
        {
            commands.Add($"explore build idea {state.Goal}");
            commands.Add($"draft implementation roadmap {state.Goal}");
        }
        else if (MentionsAny(currentStep, "Inspect", "architecture", "impact surface"))
        {
            commands.Add("inspect coding workspace");
            commands.Add("analyze solution architecture");
            commands.Add("list packages");
        }
        else if (MentionsAny(currentStep, "Plan", "exact next", "code", "package", "build action"))
        {
            commands.Add($"plan coding task {state.Goal}");
            commands.Add("show next coding action");
            if (MentionsAny(state.Goal, "package", "library", "dependency", "nuget", "install"))
            {
                commands.Add($"confirm check outdated packages \"{primaryTarget}\"");
            }
        }
        else if (MentionsAny(currentStep, "package", "library", "dependency", "nuget", "install"))
        {
            commands.Add("list packages");
            commands.Add($"confirm dotnet restore \"{primaryTarget}\"");
            commands.Add($"confirm dotnet add package \"Package.Id\" to \"{primaryTarget}\"");
        }
        else if (MentionsAny(currentStep, "Preview", "execute", "approved action", "patch"))
        {
            commands.Add("preview patch bundle");
            commands.Add("show pending patch preview");
            commands.Add("confirm apply last patch preview");
        }
        else if (MentionsAny(currentStep, "validation", "changed surface", "build", "test"))
        {
            commands.Add($"confirm dotnet build \"{primaryTarget}\"");
            commands.Add($"confirm dotnet test \"{primaryTarget}\"");
        }
        else if (MentionsAny(currentStep, "receipts", "decide", "complete"))
        {
            commands.Add("show coding receipts");
            commands.Add("show crash recovery status");
            commands.Add("mark roadmap step complete");
        }
        else if (MentionsAny(currentStep, "Stage", "commit", "completed phase"))
        {
            commands.Add("git status");
            commands.Add("git diff");
            commands.Add("confirm git add all");
            commands.Add("confirm git commit \"message\"");
        }

        return commands;
    }

    private static IReadOnlyList<string> BuildValidationCommands(
        RoadmapExecutionState state,
        string currentStep,
        string primaryTarget)
    {
        var commands = new List<string>
        {
            $"confirm dotnet build \"{primaryTarget}\""
        };
        if (MentionsAny(state.Goal, "test", "package", "library", "dependency", "nuget", "install")
            || MentionsAny(currentStep, "validation", "test", "changed surface"))
        {
            commands.Add($"confirm dotnet test \"{primaryTarget}\"");
        }

        commands.Add("diagnose last build failure");
        return commands;
    }

    private static IReadOnlyList<string> BuildCloseoutCommands(RoadmapExecutionState state, string currentStep)
    {
        var commands = new List<string>
        {
            "show coding receipts",
            "show crash recovery status"
        };
        if (MentionsAny(currentStep, "receipts", "decide", "complete")
            || MentionsAny(currentStep, "validation", "changed surface"))
        {
            commands.Add("mark roadmap step complete");
        }

        if (MentionsAny(currentStep, "Stage", "commit", "completed phase") || state.Finished)
        {
            commands.Add("git status");
            commands.Add("git diff");
            commands.Add("confirm git add all");
            commands.Add("confirm git commit \"message\"");
        }

        commands.Add("generate coding report");
        return commands;
    }

    private async Task<ApprovedRoadmapExecutionPacket> BuildApprovedRoadmapExecutionPacketAsync(
        RoadmapExecutionState state,
        CancellationToken cancellationToken)
    {
        var primaryTarget = Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : Policy.WorkspaceRoot;
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var recommendation = BuildNextRoadmapRecommendation(state, gitStatus, latestDotNetReceipt, primaryTarget);
        var currentStep = GetRoadmapCurrentStep(state);
        return new ApprovedRoadmapExecutionPacket(
            Goal: state.Goal,
            StepIndex: state.CurrentStepIndex,
            Step: currentStep,
            RoadmapUpdatedAt: state.UpdatedAt,
            ApprovedAt: DateTimeOffset.UtcNow,
            PrimaryTarget: primaryTarget,
            PacketStatus: DescribeExecutionPacketStatus(state, gitStatus, latestDotNetReceipt),
            RecommendedAction: recommendation.Action,
            Confidence: recommendation.Confidence,
            PrepCommands: ["show next coding action", "show crash recovery status", "show coding receipts"],
            ExecutionCommands: BuildExecutionCandidateCommands(state, recommendation, currentStep, primaryTarget),
            ValidationCommands: BuildValidationCommands(state, currentStep, primaryTarget),
            CloseoutCommands: BuildCloseoutCommands(state, currentStep));
    }

    private string FormatApprovedPacket(ApprovedRoadmapExecutionPacket packet, bool includePath)
    {
        var lines = new List<string>
        {
            "Approved execution packet:",
            $"Goal: {packet.Goal}",
            $"Step: {packet.StepIndex + 1}: {packet.Step}",
            $"Approved: {packet.ApprovedAt:u}",
            $"Primary target: {packet.PrimaryTarget}",
            $"Packet status: {packet.PacketStatus}",
            $"Recommended action: {packet.RecommendedAction}",
            $"Confidence: {packet.Confidence}",
            "Truth boundary: this is approved planning state only. Commands below still require their normal approval gates.",
            "Read-only prep:"
        };
        AddUniqueCommands(lines, packet.PrepCommands);
        lines.Add("Execution candidates:");
        AddUniqueCommands(lines, packet.ExecutionCommands);
        lines.Add("Validation commands:");
        AddUniqueCommands(lines, packet.ValidationCommands);
        lines.Add("Closeout commands:");
        AddUniqueCommands(lines, packet.CloseoutCommands);
        lines.Add("Next safe commands:");
        lines.Add("- show packet progress");
        lines.Add("- discard approved packet");
        if (includePath)
        {
            lines.Add($"Packet file: {_approvedPacketPath}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private bool TryGetActiveRoadmapForStepChange(
        out RoadmapExecutionState state,
        out CodingToolResult error)
    {
        state = _roadmapState!;
        error = CodingToolResult.NotHandled;
        if (_roadmapState is null)
        {
            error = new CodingToolResult(true, false, "No active roadmap state is available.", "Roadmap execution", Policy.WorkspaceRoot);
            return false;
        }

        state = _roadmapState;
        if (!state.Approved || !state.Started)
        {
            error = new CodingToolResult(true, false, "Roadmap must be approved and started before steps can advance.", "Roadmap execution", Policy.WorkspaceRoot);
            return false;
        }

        if (state.Paused)
        {
            error = new CodingToolResult(true, false, "Roadmap is paused. Use: resume roadmap", "Roadmap execution", Policy.WorkspaceRoot);
            return false;
        }

        if (state.Finished)
        {
            error = new CodingToolResult(true, true, "Roadmap is already finished.", "Roadmap execution", Policy.WorkspaceRoot);
            return false;
        }

        return true;
    }

    private static void AddBuildIdeaWorkspaceFit(List<string> lines, IReadOnlyList<ProjectSummary> summaries)
    {
        var roleCounts = summaries
            .GroupBy(summary => summary.ProjectRole, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToList();
        var packageCount = summaries.Sum(summary => summary.PackageReferences.Count);
        var projectReferenceCount = summaries.Sum(summary => summary.ProjectReferences.Count);

        lines.Add("Workspace fit:");
        lines.Add($"- Project roles already present: {string.Join(", ", roleCounts)}");
        lines.Add($"- Known package references: {packageCount}");
        lines.Add($"- Project-to-project references: {projectReferenceCount}");
        foreach (var summary in summaries.Take(6))
        {
            lines.Add($"- {summary.RelativePath}: {summary.ProjectRole}");
        }
    }

    private static void AddImplementationPaths(List<string> lines, string goal)
    {
        lines.Add("Possible implementation paths to compare:");
        lines.Add("- Path A: prototype inside the existing app with a narrow command, one service, and one testable workflow.");
        lines.Add("- Path B: create a separate library for the core logic, then call it from UI, CLI, or Visual Studio tooling.");
        lines.Add("- Path C: build a companion process or bridge only when the feature must talk to external software.");

        if (MentionsAny(goal, "solidworks", "cad", "drawing", "model", "assembly", "part", "bom"))
        {
            lines.Add("- CAD lane: start with a small SolidWorks macro/API proof before attempting a full add-in.");
        }

        if (MentionsAny(goal, "visual studio", "vsix", "ide", "extension", "tool window"))
        {
            lines.Add("- Visual Studio lane: keep Ali's existing guarded command surface and add a thin VS tool window or external-tool bridge.");
        }

        if (MentionsAny(goal, "web", "api", "dashboard", "site", "portal", "server"))
        {
            lines.Add("- Web/API lane: separate server endpoints from UI so auth, data, and tests stay inspectable.");
        }

        if (MentionsAny(goal, "ai", "assistant", "rag", "chat", "agent", "llm", "model"))
        {
            lines.Add("- AI lane: isolate prompts, retrieval, tool calls, and receipts so model output does not become hidden authority.");
        }

        if (MentionsAny(goal, "database", "data", "report", "inventory", "search", "history"))
        {
            lines.Add("- Data lane: decide early between local file storage, embedded storage, and a client/server storage service.");
        }
    }

    private static void AddLibraryExploration(List<string> lines, string goal)
    {
        lines.Add("Library/software areas to explore for approval:");
        lines.Add("- .NET app structure: Microsoft.Extensions.Hosting, dependency injection, logging, and options.");
        lines.Add("- Desktop UI: WPF, CommunityToolkit.Mvvm, and Windows App SDK/WinUI as a comparison path.");
        lines.Add("- Testing: xUnit/NUnit/MSTest, snapshot/golden-output tests, and small integration harnesses.");

        if (MentionsAny(goal, "solidworks", "cad", "drawing", "model", "assembly", "part", "bom"))
        {
            lines.Add("- SolidWorks: SOLIDWORKS API via COM interop, macro prototypes, add-in templates, and Document Manager API if licensing fits.");
            lines.Add("- CAD documents/data: STEP/DXF/export automation, BOM extraction, ClosedXML for spreadsheets, and PDF export tooling.");
        }

        if (MentionsAny(goal, "web", "api", "dashboard", "site", "portal", "server"))
        {
            lines.Add("- Web/API: ASP.NET Core, Minimal APIs, Blazor, OpenAPI/Swagger, and auth middleware.");
        }

        if (MentionsAny(goal, "database", "data", "report", "inventory", "search", "history"))
        {
            lines.Add("- Data: local file stores, lightweight embedded storage, object mapping, search indexes, and migration tooling.");
        }

        if (MentionsAny(goal, "ai", "assistant", "rag", "chat", "agent", "llm", "model"))
        {
            lines.Add("- AI/RAG: Semantic Kernel, Microsoft.Extensions.AI, local Ollama/OpenAI adapters, vector search, and prompt receipt logging.");
        }

        if (MentionsAny(goal, "pdf", "word", "excel", "document", "spreadsheet", "report"))
        {
            lines.Add("- Documents: Open XML SDK, ClosedXML, QuestPDF, PDF inspection/extraction libraries, and template-based report generation.");
        }

        lines.Add("- Version/source check: approve an internet or package-registry lookup before treating any library/version as current.");
    }

    private static void AddPackageLookupCandidateLanes(List<string> lines, string goal)
    {
        lines.Add("- Baseline .NET: Microsoft.Extensions.Hosting, Microsoft.Extensions.Logging, Microsoft.Extensions.Options.");
        lines.Add("- Testing: MSTest, xUnit, NUnit, FluentAssertions, Verify, or Microsoft.Playwright when UI checks matter.");
        lines.Add("- Serialization/data: System.Text.Json, LiteDB, JSON files, or CsvHelper depending on storage shape.");

        if (MentionsAny(goal, "visual studio", "vsix", "ide", "extension", "tool window"))
        {
            lines.Add("- Visual Studio: Microsoft.VisualStudio.SDK, Community.VisualStudio.Toolkit, and VSIX packaging tools.");
        }

        if (MentionsAny(goal, "solidworks", "cad", "drawing", "assembly", "part", "bom"))
        {
            lines.Add("- SolidWorks/CAD: SolidWorks COM interop/API samples first; add-in packaging only after a macro proof works.");
        }

        if (MentionsAny(goal, "pdf", "report", "document", "manual"))
        {
            lines.Add("- Documents/PDF: QuestPDF, PdfSharpCore, MigraDoc, or the existing local SimplePdfWriter for text-only output.");
        }

        if (MentionsAny(goal, "web", "api", "browser", "dashboard"))
        {
            lines.Add("- Web/API: ASP.NET Core minimal APIs, typed HttpClient, and a small browser UI before heavier frameworks.");
        }

        if (MentionsAny(goal, "voice", "audio", "stt", "tts", "piper", "whisper"))
        {
            lines.Add("- Voice/audio: NAudio, Piper/Whisper local process bridges, and explicit model-resource validation.");
        }
    }

    private static string BuildSafeScaffoldName(string goal)
    {
        var words = goal
            .Split([' ', '-', '_', '.', '/', '\\', ':', ';', ',', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .Take(4)
            .ToList();
        if (words.Count == 0)
        {
            return "Ali.Feature";
        }

        var name = string.Concat(words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        return name.StartsWith("Ali", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"Ali.{name}";
    }

    private string GetReadOnlyCommandWorkingDirectory() =>
        Directory.Exists(Policy.WorkspaceRoot)
            ? Policy.WorkspaceRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static ProcessSnapshot SnapshotProcess(Process process)
    {
        string? path = null;
        string startTimeText = "unavailable";
        try
        {
            path = process.MainModule?.FileName;
        }
        catch
        {
            path = null;
        }

        try
        {
            startTimeText = process.StartTime.ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            startTimeText = "unavailable";
        }

        long workingSet = 0;
        try
        {
            workingSet = process.WorkingSet64;
        }
        catch
        {
            workingSet = 0;
        }

        return new ProcessSnapshot(
            process.Id,
            process.ProcessName,
            path,
            startTimeText,
            workingSet);
    }

    private static ProcessSnapshot? TrySnapshotProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return SnapshotProcess(process);
        }
        catch
        {
            return null;
        }
    }

    private static bool ProcessSnapshotMatches(ProcessSnapshot snapshot, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return snapshot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
               || snapshot.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (snapshot.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static IReadOnlyList<NetstatPortOwner> ParseNetstatPortOwners(string output, int port)
    {
        var owners = new List<NetstatPortOwner>();
        foreach (var rawLine in output.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Proto", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            var protocol = parts[0];
            var localAddress = parts[1];
            var pidText = parts[^1];
            var state = parts.Length >= 5 ? parts[^2] : "n/a";
            if (!int.TryParse(pidText, out var pid) || !LocalAddressUsesPort(localAddress, port))
            {
                continue;
            }

            owners.Add(new NetstatPortOwner(protocol, localAddress, state, pid));
        }

        return owners
            .DistinctBy(owner => (owner.Protocol, owner.LocalAddress, owner.State, owner.ProcessId))
            .ToList();
    }

    private static bool LocalAddressUsesPort(string localAddress, int port)
    {
        var suffix = $":{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return localAddress.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               || localAddress.EndsWith($".{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyFailureMessage(string message)
    {
        if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("locked by", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MSB302", StringComparison.OrdinalIgnoreCase))
        {
            return "locked file";
        }

        if (message.Contains("NU", StringComparison.OrdinalIgnoreCase)
            || message.Contains("restore", StringComparison.OrdinalIgnoreCase)
            || message.Contains("package", StringComparison.OrdinalIgnoreCase))
        {
            return "restore/package";
        }

        if (message.Contains("CS", StringComparison.OrdinalIgnoreCase)
            || message.Contains("compiler", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error", StringComparison.OrdinalIgnoreCase) && message.Contains(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return "compiler";
        }

        if (message.Contains("test", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Assert", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Expected", StringComparison.OrdinalIgnoreCase))
        {
            return "test";
        }

        if (message.Contains("SDK", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is not recognized", StringComparison.OrdinalIgnoreCase))
        {
            return "missing sdk/tool";
        }

        return "unknown";
    }

    private static string CleanGoal(string? query, string fallback) =>
        string.IsNullOrWhiteSpace(query)
            ? fallback
            : query.Trim();

    private string? GetPrimaryTarget() =>
        Directory.Exists(Policy.WorkspaceRoot) && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : null;

    private IReadOnlyList<ProjectSummary> GetWorkspaceProjectSummaries()
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return [];
        }

        return EnumerateWorkspaceFiles()
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWorkspaceSummaryEntries)
            .Select(ReadProjectSummary)
            .ToList();
    }

    private static string ClassifyGoalType(string goal)
    {
        if (MentionsAny(goal, "visual studio", "vsix", "ide", "extension", "tool window"))
        {
            return "Visual Studio integration/tooling";
        }

        if (MentionsAny(goal, "solidworks", "cad", "drawing", "model", "assembly", "part", "bom"))
        {
            return "CAD/SolidWorks automation";
        }

        if (MentionsAny(goal, "web", "api", "dashboard", "site", "portal", "server"))
        {
            return "web/API workflow";
        }

        if (MentionsAny(goal, "ai", "assistant", "rag", "chat", "agent", "llm", "model", "vision", "screenshot"))
        {
            return "AI assistant workflow";
        }

        if (MentionsAny(goal, "package", "library", "dependency", "nuget", "sdk"))
        {
            return "dependency/library integration";
        }

        if (MentionsAny(goal, "pdf", "word", "excel", "document", "spreadsheet", "report"))
        {
            return "document/report automation";
        }

        return "application feature or tooling workflow";
    }

    private static void AddArchitectureRecommendationCards(
        List<string> lines,
        string goal,
        IReadOnlyList<ProjectSummary> summaries)
    {
        var hasApp = summaries.Any(summary => summary.ProjectRole.Contains("app", StringComparison.OrdinalIgnoreCase));
        var hasLibrary = summaries.Any(summary => summary.ProjectRole.Equals("library", StringComparison.OrdinalIgnoreCase));
        var hasTests = summaries.Any(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase));
        var isCad = MentionsAny(goal, "solidworks", "cad", "drawing", "model", "assembly", "part", "bom");
        var isVisualStudio = MentionsAny(goal, "visual studio", "vsix", "ide", "extension", "tool window");
        var isWeb = MentionsAny(goal, "web", "api", "dashboard", "site", "portal", "server");
        var isAi = MentionsAny(goal, "ai", "assistant", "rag", "chat", "agent", "llm", "model");

        lines.Add("Architecture recommendation cards:");
        lines.Add("- Card 1 - App shape:");
        if (isVisualStudio)
        {
            lines.Add("  Recommendation: thin Visual Studio surface over Ali's existing guarded bridge and local helper.");
        }
        else if (isCad)
        {
            lines.Add("  Recommendation: small external adapter or macro proof first, then decide whether a full add-in is justified.");
        }
        else if (isWeb)
        {
            lines.Add("  Recommendation: ASP.NET Core service boundary with a narrow UI/API slice and explicit auth/data decisions.");
        }
        else if (isAi)
        {
            lines.Add("  Recommendation: separate prompt/retrieval/tool orchestration from UI and persistence so receipts stay inspectable.");
        }
        else
        {
            lines.Add("  Recommendation: start with one owner-visible workflow inside the existing app before extracting libraries.");
        }

        lines.Add("- Card 2 - Local fit:");
        lines.Add($"  Existing app/UI project: {(hasApp ? "yes" : "not detected")}; library project: {(hasLibrary ? "yes" : "not detected")}; test project: {(hasTests ? "yes" : "not detected")}.");
        lines.Add(hasTests
            ? "  Fit note: add focused service/parser tests around the new behavior."
            : "  Fit note: include a small harness or test project before broadening the workflow.");

        lines.Add("- Card 3 - Candidate libraries/tools:");
        lines.Add("  Common: Microsoft.Extensions.Hosting, dependency injection, logging/options, and focused test harnesses.");
        if (isCad)
        {
            lines.Add("  CAD: SOLIDWORKS API via COM interop, macro prototypes, Document Manager API, ClosedXML, and PDF/export tooling.");
        }
        else if (isVisualStudio)
        {
            lines.Add("  Visual Studio: VSIX tool window, external tool bridge, loopback helper endpoints, and command context handoff.");
        }
        else if (isWeb)
        {
            lines.Add("  Web/API: ASP.NET Core Minimal APIs, OpenAPI/Swagger, Blazor or a small static UI, and auth middleware.");
        }
        else if (isAi)
        {
            lines.Add("  AI: local Ollama/OpenAI adapters, Semantic Kernel or Microsoft.Extensions.AI as candidates, vector search, and prompt receipt logging.");
        }
        else
        {
            lines.Add("  General: CommunityToolkit.Mvvm for WPF, Open XML/ClosedXML/QuestPDF for documents, and embedded/local stores when data is small.");
        }

        lines.Add("- Card 4 - First prototype path:");
        lines.Add("  1. Define one visible outcome and one non-goal.");
        lines.Add("  2. Map the smallest existing project/file surface.");
        lines.Add("  3. Add or reuse one service boundary.");
        lines.Add("  4. Validate with a focused build/test receipt before widening.");

        lines.Add("- Card 5 - Approval and risk:");
        lines.Add("  Approval needed before live lookup/install: package versions, internet sources, external SDKs, and tool downloads.");
        lines.Add("  Risk: do not claim external app integration, installed packages, or current library versions without receipts.");
    }

    private static void AddRoadmapArchitectureFit(List<string> lines, IReadOnlyList<ProjectSummary> summaries)
    {
        lines.Add("Current architecture fit:");
        if (summaries.Count == 0)
        {
            lines.Add("- No local project files were available for a fit check.");
            return;
        }

        var appProjects = summaries
            .Where(summary => summary.ProjectRole.Contains("app", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();
        var testProjects = summaries
            .Where(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();
        var libraryProjects = summaries
            .Where(summary => summary.ProjectRole.Equals("library", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();

        lines.Add($"- Projects scanned: {summaries.Count}");
        lines.Add($"- App/UI projects: {FormatRoadmapList(appProjects)}");
        lines.Add($"- Library projects: {FormatRoadmapList(libraryProjects)}");
        lines.Add($"- Test projects: {FormatRoadmapList(testProjects)}");
    }

    private static void AddRoadmapPhases(List<string> lines, string goal)
    {
        lines.Add("Recommended phase sequence:");
        lines.Add("1. Clarify owner-visible behavior and non-goals.");
        lines.Add("2. Inspect the smallest relevant files and project references.");
        lines.Add("3. Choose the core boundary: command, service, adapter, UI, or bridge.");
        lines.Add("4. Prototype the narrowest observable workflow behind existing permission gates.");
        lines.Add("5. Add focused parser/service tests before widening behavior.");
        lines.Add("6. Run confirmed build/test validation and record receipts.");
        lines.Add("7. Update owner docs with exact commands and truth boundaries.");

        if (MentionsAny(goal, "library", "package", "dependency", "nuget", "sdk"))
        {
            lines.Add("8. Only after approval, verify library candidates against current sources/package metadata.");
        }
    }

    private static void AddRoadmapImpactSurface(List<string> lines, string goal)
    {
        lines.Add("Likely impact surface:");
        lines.Add("- Parser: add deterministic phrases only when the action shape is clear.");
        lines.Add("- Policy: keep read-only planning allowed and writes/builds/package actions gated.");
        lines.Add("- Service: keep receipts and explicit truth boundaries in every tool result.");
        lines.Add("- Tests: cover parser routing, service output, and permission behavior.");
        lines.Add("- Docs: add owner commands and state what is not implemented yet.");

        if (MentionsAny(goal, "visual studio", "vsix", "ide", "extension", "tool window"))
        {
            lines.Add("- Visual Studio: preserve the existing bridge contract before adding VSIX-specific UI.");
        }

        if (MentionsAny(goal, "solidworks", "cad", "drawing", "model", "assembly", "part", "bom"))
        {
            lines.Add("- CAD tooling: separate SolidWorks automation adapters from Ali's core planning and permission logic.");
        }

        if (MentionsAny(goal, "voice", "piper", "microphone", "speech", "stt", "tts"))
        {
            lines.Add("- Voice: keep local-only STT/TTS settings and avoid mixing voice repair with coding tool authority.");
        }
    }

    private static void AddRoadmapTestStrategy(List<string> lines, string goal)
    {
        lines.Add("Test strategy:");
        lines.Add("- Parser tests for every new owner phrase.");
        lines.Add("- Service tests that assert output sections and no command runner calls for read-only planning.");
        lines.Add("- Policy tests when an action changes permission behavior.");
        lines.Add("- Full harness after command-surface changes.");

        if (MentionsAny(goal, "build", "test", "compile", "restore", "run"))
        {
            lines.Add("- Dotnet command tests should prove confirmation is required before execution.");
        }

        if (MentionsAny(goal, "patch", "edit", "file", "write", "apply"))
        {
            lines.Add("- Patch/edit tests should prove preview, stale-check, and explicit confirmation behavior.");
        }
    }

    private static void AddRoadmapRiskRegister(List<string> lines, string goal)
    {
        lines.Add("Risk register:");
        lines.Add("- Scope creep: keep each phase owner-visible and testable.");
        lines.Add("- False authority: do not claim installs, versions, IDE state, or external app state without receipts.");
        lines.Add("- Permission drift: do not let planning commands become execution commands.");

        if (MentionsAny(goal, "package", "library", "dependency", "nuget", "download", "install"))
        {
            lines.Add("- Dependency currency: source/package lookup needs approval before selecting versions.");
        }

        if (MentionsAny(goal, "solidworks", "cad", "drawing", "model", "assembly", "part", "bom"))
        {
            lines.Add("- External automation: SolidWorks control must be proven with a small adapter before broad workflow claims.");
        }
    }

    private static void AddRoadmapDefinitionOfDone(List<string> lines)
    {
        lines.Add("Definition of done:");
        lines.Add("- The command or workflow has an owner-facing phrase and predictable output.");
        lines.Add("- The implementation has focused tests.");
        lines.Add("- Build and full harness pass after changes.");
        lines.Add("- DevRun is refreshed when app behavior changes.");
        lines.Add("- Docs state the capability and the boundary.");
        lines.Add("- The owner can ask `show coding receipts` or `generate coding report` to review what actually happened.");
    }

    private static void AddRoadmapNextCommands(List<string> lines, string goal)
    {
        lines.Add("Safe next commands:");
        lines.Add($"- explore build idea {goal}");
        lines.Add("- analyze solution architecture");
        lines.Add("- show visual studio integration");
        lines.Add("- show coding receipts");
        lines.Add("- show execution packet");
    }

    private static string FormatRoadmapList(IReadOnlyList<string> values) =>
        values.Count == 0
            ? "none detected"
            : string.Join(", ", values.Take(6));

    private static void AddApprovalCheckpoints(List<string> lines)
    {
        lines.Add("Approval checkpoints:");
        lines.Add("1. Pick the path to prototype first.");
        lines.Add("2. Approve any internet/package lookup before Ali claims library currency.");
        lines.Add("3. Approve package restore/install commands before Ali changes dependencies.");
        lines.Add("4. Approve file edits or patch previews before Ali changes code.");
        lines.Add("5. Approve build/test/run commands before execution.");
    }

    private static void AddBuildIdeaNextCommands(List<string> lines, string goal)
    {
        lines.Add("Safe next commands:");
        lines.Add("- analyze solution architecture");
        lines.Add("- list packages");
        lines.Add($"- plan coding task {goal}");
        lines.Add("- show execution packet");
        lines.Add("- generate coding report");
    }

    private CodingToolResult ShowReceipts()
    {
        if (!File.Exists(_actionLogPath))
        {
            return new CodingToolResult(
                true,
                true,
                "No coding receipts have been recorded yet.",
                "Coding receipts",
                _actionLogPath);
        }

        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        if (receipts.Count == 0)
        {
            return new CodingToolResult(
                true,
                true,
                "No readable coding receipts were found.",
                "Coding receipts",
                _actionLogPath);
        }

        var lines = new List<string>
        {
            $"Recent coding receipts from: {_actionLogPath}"
        };
        foreach (var receipt in receipts)
        {
            var status = receipt.Succeeded ? "succeeded" : "failed";
            var target = string.IsNullOrWhiteSpace(receipt.TargetPath)
                ? string.Empty
                : $" target={receipt.TargetPath}";
            var exit = receipt.ExitCode is null
                ? string.Empty
                : $" exit={receipt.ExitCode.Value}";
            lines.Add($"- {receipt.Timestamp:u} {receipt.Action} {status}{exit}{target}");
        }

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Coding receipts",
            _actionLogPath);
    }

    private CodingToolResult ShowToolIntegrationStatus()
    {
        var notepadPlusPlus = CodingToolLocator.FindNotepadPlusPlus(_configuredNotepadPlusPlusPath);
        var visualStudio = CodingToolLocator.FindVisualStudio(_configuredVisualStudioPath);
        var hasWorkspace = Directory.Exists(Policy.WorkspaceRoot);
        var primarySolution = hasWorkspace && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var solutionOrProject)
            ? solutionOrProject
            : null;

        var lines = new List<string>
        {
            "Coding tool integration status:",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Workspace exists: {hasWorkspace}",
            $"Primary solution/project: {primarySolution ?? "not found"}",
            $"Visual Studio: {visualStudio ?? "not found"}",
            $"Notepad++: {notepadPlusPlus ?? "not found; Ali will fall back to Notepad for file open requests"}",
            "Visual Studio in-IDE panel: Ali Companion VSIX is included in this build.",
            "Current integration mode: Ali chat commands, Programming Companion WebHelper, Visual Studio External Tools bridge, and Ali Companion VSIX tool window.",
            "Permission gates:",
            $"- Explicit outside file open: {(Policy.AllowExplicitOutsideFileOpen ? "allowed" : "disabled")}",
            $"- Confirmed build/test/run: {(Policy.AllowConfirmedBuildTestRunInsideWorkspace ? "available with confirmation" : "disabled")}",
            $"- Confirmed edits: {(Policy.AllowConfirmedEditInsideWorkspace ? "available with confirmation" : "disabled")}",
            $"- Git read: {(Policy.AllowGitReadInsideWorkspace ? "allowed" : "disabled")}",
            $"- Git write: {(Policy.AllowConfirmedGitWriteInsideWorkspace ? "available with confirmation" : "disabled")}",
            $"- Git merge: {(Policy.AllowConfirmedGitMergeInsideWorkspace ? "available with confirmation" : "disabled")}",
            $"- Git pull/push: {(Policy.AllowGitNetworkOperations ? "available with confirmation" : "blocked")}",
            "Useful commands:",
            "- open solution",
            "- analyze solution architecture",
            "- interpret build goal <goal>",
            "- show architecture options <goal>",
            "- write acceptance criteria <goal>",
            "- suggest tests for <goal>",
            "- detect codebase patterns",
            "- plan feature files <goal>",
            "- show refactor safety checklist <goal>",
            "- show next coding action",
            "- show execution packet",
            "- approve execution packet",
            "- show packet commands",
            "- show packet ledger",
            "- resume build plan",
            "- plan package lookup <goal>",
            "- plan dependency install packet <goal>",
            "- preview project scaffold <goal>",
            "- plan scaffold apply <goal>",
            "- plan post edit validation",
            "- show coding skill command index",
            "- show coding session summary",
            "- show windows troubleshooting toolkit",
            "- plan rogue process hunt <target>",
            "- collect process evidence <name-or-pid>",
            "- diagnose port <port>",
            "- diagnose build lock",
            "- classify last build failure",
            "- show roadmap step checklist",
            "- show install doctor",
            "- generate visual studio integration plan",
            "- show coding receipts",
            "- generate coding report",
            "- generate morning report"
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Coding tool status",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult GenerateVisualStudioHandoff()
    {
        var notepadPlusPlus = CodingToolLocator.FindNotepadPlusPlus(_configuredNotepadPlusPlusPath);
        var visualStudio = CodingToolLocator.FindVisualStudio(_configuredVisualStudioPath);
        var hasWorkspace = Directory.Exists(Policy.WorkspaceRoot);
        var primarySolution = hasWorkspace && TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var solutionOrProject)
            ? solutionOrProject
            : null;
        var architecture = hasWorkspace
            ? AnalyzeArchitecture().Message
            : $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}";

        var lines = new List<string>
        {
            "Visual Studio integration handoff:",
            "Current truth: Ali Companion VSIX is included in this build. It uses native Visual Studio/WPF controls and routes commands through Ali's guarded loopback bridge.",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Workspace exists: {hasWorkspace}",
            $"Primary solution/project: {primarySolution ?? "not found"}",
            $"Visual Studio launcher: {visualStudio ?? "not found"}",
            $"File editor launcher: {notepadPlusPlus ?? "Notepad fallback"}",
            "Recommended phase shape:",
            "- Keep hardening the Ali Companion VSIX tool window around Ali's existing guarded command surface.",
            "- Add current solution/file/line handoff only through explicit owner-invoked commands.",
            "Current bridge surface:",
            "- GET /api/coding/status on the local web helper.",
            "- POST /api/coding/command on the local web helper.",
            "- Ali.App.VisualStudioBridge.exe can be configured as a Visual Studio External Tool and submits commands to the local helper.",
            "- Ali.App.VisualStudioExtension.vsix adds the Ali Companion tool window inside Visual Studio.",
            "- Bridge endpoints are loopback-only and still use Ali's coding parser, policy gates, and receipts.",
            "Minimum integration contract:",
            "- Show workspace root, primary solution/project, architecture summary, coding receipts, pending patch state, and last dotnet failure state.",
            "- Accept deterministic Ali coding commands: inspect workspace, analyze architecture, plan coding task, draft/show/approve/start roadmap, show crash recovery status, show receipts, preview patch, show pending patch, apply confirmed patch, and generate coding report.",
            "- Pass current solution/file/line as context only after the user invokes the command.",
            "- Route edits, builds, tests, run, restore, and Git writes through Ali's existing confirmation gates.",
            "- Keep Git pull/push blocked unless the configured Git network gate is deliberately enabled.",
            "Next implementation slices:",
            "1. Add VSIX buttons that pass current solution/file/line into the existing bridge.",
            "2. Add helper auto-start/status inside the VSIX tool window.",
            "3. Return tool results and receipts to the panel without granting direct IDE write authority.",
            "4. Add VSIX smoke validation for package load and menu command registration.",
            "Workspace architecture snapshot:",
            TrimForChat(architecture, 5_500)
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Visual Studio handoff",
            primarySolution ?? Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> GeneratePdfAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildGeneratedPdfPath(request.Path, out var pdfPath, out var pathError))
        {
            return new CodingToolResult(true, false, pathError, "PDF generator", _pdfWorkspaceRoot);
        }

        if (!ValidatePdfContent(request.Content, out var contentError))
        {
            return new CodingToolResult(true, false, contentError, "PDF generator", pdfPath);
        }

        Directory.CreateDirectory(_pdfWorkspaceRoot);
        var uniquePath = BuildUniquePath(pdfPath);
        var title = Path.GetFileNameWithoutExtension(uniquePath);
        var bytes = SimplePdfWriter.BuildTextPdf(title, request.Content!);
        await File.WriteAllBytesAsync(uniquePath, bytes, cancellationToken).ConfigureAwait(false);

        return new CodingToolResult(
            true,
            true,
            $"Generated PDF: {uniquePath}{Environment.NewLine}Wrote {bytes.Length} byte(s) from {request.Content!.Length} text character(s).",
            "PDF generator",
            uniquePath);
    }

    private async Task<CodingToolResult> GenerateCodingReportAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildGeneratedPdfPath(request.Path, out var pdfPath, out var pathError))
        {
            return new CodingToolResult(true, false, pathError, "Coding report", _pdfWorkspaceRoot);
        }

        var reportText = await BuildCodingSessionReportAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_pdfWorkspaceRoot);
        var uniquePath = BuildUniquePath(pdfPath);
        var title = Path.GetFileNameWithoutExtension(uniquePath);
        var bytes = SimplePdfWriter.BuildTextPdf(title, reportText);
        await File.WriteAllBytesAsync(uniquePath, bytes, cancellationToken).ConfigureAwait(false);

        return new CodingToolResult(
            true,
            true,
            $"Generated coding session report PDF: {uniquePath}{Environment.NewLine}Wrote {bytes.Length} byte(s).",
            "Coding report",
            uniquePath);
    }

    private async Task<CodingToolResult> GenerateMorningReportAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildGeneratedPdfPath(request.Path, out var pdfPath, out var pathError))
        {
            return new CodingToolResult(true, false, pathError, "Morning report", _pdfWorkspaceRoot);
        }

        var reportText = await BuildMorningReportAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_pdfWorkspaceRoot);
        var uniquePath = BuildUniquePath(pdfPath);
        var title = Path.GetFileNameWithoutExtension(uniquePath);
        var bytes = SimplePdfWriter.BuildTextPdf(title, reportText);
        await File.WriteAllBytesAsync(uniquePath, bytes, cancellationToken).ConfigureAwait(false);

        return new CodingToolResult(
            true,
            true,
            $"Generated morning build report PDF: {uniquePath}{Environment.NewLine}Wrote {bytes.Length} byte(s).",
            "Morning report",
            uniquePath);
    }

    private CodingToolResult ShowPdfToolStatus()
    {
        var lines = new List<string>
        {
            "Ali PDF tool status:",
            $"PDF workspace: {_pdfWorkspaceRoot}",
            $"Workspace exists: {Directory.Exists(_pdfWorkspaceRoot)}",
            "Permission gates:",
            $"- Inspect/extract/summarize: {(Policy.AllowPdfRead ? "allowed" : "disabled")}",
            $"- Create/export PDFs: {(Policy.AllowPdfCreate ? "allowed" : "disabled")}",
            $"- Combine/split/modify: {(Policy.AllowConfirmedPdfModify ? "available with confirmation" : "disabled")}",
            "Current capabilities:",
            "- Create polished text/report PDFs with title, section styling, wrapping, footer, and page numbers.",
            "- Inspect page markers, file size, metadata hints, text availability, and likely scanned/image-only PDFs.",
            "- Extract and summarize text from PDFs that expose simple text drawing commands.",
            "- Convert Markdown/text files into polished PDFs.",
            "- Combine/split by creating derived text-based PDFs when source text can be extracted.",
            "Truth boundary:",
            "- Ali is not a full Acrobat replacement yet.",
            "- Scanned PDFs, image-only PDFs, encrypted PDFs, complex forms, redaction, OCR, annotations, and layout-preserving visual edits are future work."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "PDF tool status", _pdfWorkspaceRoot);
    }

    private CodingToolResult ShowPdfCommandIndex()
        => new(true, true, CodingAbilityCatalog.BuildPdfCommandIndex(_pdfWorkspaceRoot), "PDF command index", _pdfWorkspaceRoot);

    private async Task<CodingToolResult> GenerateInstallReportAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        var doctor = ShowInstallDoctor();
        var body = string.Join(
            Environment.NewLine,
            [
                "Ali Install Readiness Report",
                $"Generated: {DateTimeOffset.Now:u}",
                $"Workspace root: {Policy.WorkspaceRoot}",
                $"PDF workspace: {_pdfWorkspaceRoot}",
                string.Empty,
                "Install Doctor",
                TrimForChat(doctor.Message, 12_000),
                string.Empty,
                "Manual Checks",
                "- Verify Visual Studio Community is installed before installing the VSIX.",
                "- Verify WebHelper is running before using the browser companion or Visual Studio tool window.",
                "- Verify model-backed chat separately from deterministic local tools.",
                "- Keep installer, repair, signing, trust-store, registry, firewall, and PATH changes owner-approved."
            ]);

        return await WriteGeneratedPdfAsync(request.Path, "Ali install report", "Install report", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingToolResult> GenerateTroubleshootingReportAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        var body = string.Join(
            Environment.NewLine,
            [
                "Ali Troubleshooting Report",
                $"Generated: {DateTimeOffset.Now:u}",
                $"Workspace root: {Policy.WorkspaceRoot}",
                $"PDF workspace: {_pdfWorkspaceRoot}",
                string.Empty,
                "Windows Troubleshooting Toolkit",
                TrimForChat(ShowWindowsTroubleshootingToolkit().Message, 12_000),
                string.Empty,
                "Install Doctor",
                TrimForChat(ShowInstallDoctor().Message, 8_000),
                string.Empty,
                "Safe Next Commands",
                "- collect process evidence <name-or-pid>",
                "- diagnose port <port>",
                "- diagnose build lock",
                "- inspect services and startup",
                "- triage event logs",
                "- show install doctor",
                string.Empty,
                "Approval Boundary",
                "- Read-only diagnostics first.",
                "- Process stop, service changes, startup changes, registry edits, firewall changes, PATH changes, repair actions, and deletes require explicit approval."
            ]);

        return await WriteGeneratedPdfAsync(request.Path, "Ali troubleshooting report", "Troubleshooting report", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingToolResult> InspectPdfAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolvePdfInputPath(request.Path, out var pdfPath, out var error))
        {
            return new CodingToolResult(true, false, error, "PDF inspector", _pdfWorkspaceRoot);
        }

        var bytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        var text = SimplePdfInspector.ExtractText(bytes);
        var info = SimplePdfInspector.Inspect(bytes, text);
        var lines = new List<string>
        {
            "PDF inspection:",
            $"Path: {pdfPath}",
            $"Size: {bytes.Length} byte(s)",
            $"Page count estimate: {info.PageCount}",
            $"PDF version: {info.Version}",
            $"Encrypted marker: {(info.HasEncryptMarker ? "yes" : "no")}",
            $"Form marker: {(info.HasAcroFormMarker ? "yes" : "no")}",
            $"Image marker: {(info.HasImageMarker ? "yes" : "no")}",
            $"Text characters extracted: {text.Length}",
            $"Likely scanned/image-only: {(info.LikelyImageOnly ? "yes" : "no")}",
            "Notes:",
            info.LikelyImageOnly
                ? "- This PDF does not expose enough text for Ali's current local extractor. OCR is future work."
                : "- This PDF exposes extractable text for summarize/extract commands.",
            "- This inspection is read-only."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "PDF inspector", pdfPath);
    }

    private async Task<CodingToolResult> ExtractPdfTextAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolvePdfInputPath(request.Path, out var pdfPath, out var error))
        {
            return new CodingToolResult(true, false, error, "PDF text extractor", _pdfWorkspaceRoot);
        }

        var text = SimplePdfInspector.ExtractText(await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CodingToolResult(
                true,
                false,
                "PDF text extraction found no readable text. This may be scanned, image-only, encrypted, or encoded in a complex font stream. OCR/advanced extraction is future work.",
                "PDF text extractor",
                pdfPath);
        }

        var outputPath = BuildUniquePath(Path.Combine(_pdfWorkspaceRoot, $"{Path.GetFileNameWithoutExtension(pdfPath)}-extracted.txt"));
        Directory.CreateDirectory(_pdfWorkspaceRoot);
        await File.WriteAllTextAsync(outputPath, text, cancellationToken).ConfigureAwait(false);
        return new CodingToolResult(
            true,
            true,
            $"Extracted PDF text: {outputPath}{Environment.NewLine}Characters: {text.Length}{Environment.NewLine}{TrimForChat(text, 2_000)}",
            "PDF text extractor",
            outputPath);
    }

    private async Task<CodingToolResult> SummarizePdfAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolvePdfInputPath(request.Path, out var pdfPath, out var error))
        {
            return new CodingToolResult(true, false, error, "PDF summary", _pdfWorkspaceRoot);
        }

        var text = SimplePdfInspector.ExtractText(await File.ReadAllBytesAsync(pdfPath, cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CodingToolResult(true, false, "PDF summary needs extractable text. This PDF may need OCR or advanced extraction.", "PDF summary", pdfPath);
        }

        var summary = BuildExtractiveSummary(text);
        return new CodingToolResult(
            true,
            true,
            $"PDF summary: {pdfPath}{Environment.NewLine}{summary}",
            "PDF summary",
            pdfPath);
    }

    private async Task<CodingToolResult> ConvertMarkdownToPdfAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveTextInputPath(request.Path, out var inputPath, out var error))
        {
            return new CodingToolResult(true, false, error, "Markdown to PDF", _pdfWorkspaceRoot);
        }

        var outputName = string.IsNullOrWhiteSpace(request.Query)
            ? $"{Path.GetFileNameWithoutExtension(inputPath)}.pdf"
            : request.Query;
        var content = await File.ReadAllTextAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (!ValidatePdfContent(content, out var contentError))
        {
            return new CodingToolResult(true, false, contentError, "Markdown to PDF", inputPath);
        }

        return await WriteGeneratedPdfAsync(outputName, Path.GetFileNameWithoutExtension(inputPath), "Markdown to PDF", content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingToolResult> CombinePdfsAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AdditionalPaths is null || request.AdditionalPaths.Count < 2)
        {
            return new CodingToolResult(true, false, "PDF combine needs at least two source PDFs and one output name.", "PDF combiner", _pdfWorkspaceRoot);
        }

        var sections = new List<string>();
        foreach (var source in request.AdditionalPaths)
        {
            if (!TryResolvePdfInputPath(source, out var sourcePath, out var error))
            {
                return new CodingToolResult(true, false, error, "PDF combiner", _pdfWorkspaceRoot);
            }

            var text = SimplePdfInspector.ExtractText(await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(text))
            {
                return new CodingToolResult(true, false, $"PDF combine blocked: {sourcePath} has no extractable text. Layout-preserving binary PDF merge is future work.", "PDF combiner", sourcePath);
            }

            sections.Add($"# {Path.GetFileName(sourcePath)}{Environment.NewLine}{text}");
        }

        return await WriteGeneratedPdfAsync(request.Path, "Combined PDF", "PDF combiner", string.Join(Environment.NewLine + Environment.NewLine, sections), cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingToolResult> SplitPdfAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolvePdfInputPath(request.Path, out var sourcePath, out var error))
        {
            return new CodingToolResult(true, false, error, "PDF splitter", _pdfWorkspaceRoot);
        }

        var text = SimplePdfInspector.ExtractText(await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CodingToolResult(true, false, "PDF split needs extractable text in this phase. Layout-preserving page split is future work.", "PDF splitter", sourcePath);
        }

        var outputName = string.IsNullOrWhiteSpace(request.Query)
            ? $"{Path.GetFileNameWithoutExtension(sourcePath)}-split.pdf"
            : request.Query;
        var body = $"# Split extract from {Path.GetFileName(sourcePath)}{Environment.NewLine}{text}";
        return await WriteGeneratedPdfAsync(outputName, "Split PDF extract", "PDF splitter", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildMorningReportAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "Ali Morning Build Report",
            $"Generated: {DateTimeOffset.Now:u}",
            $"Workspace root: {Policy.WorkspaceRoot}",
            string.Empty,
            "What Changed",
            "- Ali's coding assistant surface now has approved packet commands, a packet run ledger, dependency risk cards, scaffold previews, crash resume guidance, and a morning report export.",
            "- These features still use Ali's normal approval gates for edits, packages, builds, tests, run commands, and Git writes.",
            string.Empty,
            "Packet Console"
        };

        lines.Add(TrimForChat(ShowApprovedPacketCommands().Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Packet Ledger");
        var ledger = await ShowPacketRunLedgerAsync(cancellationToken).ConfigureAwait(false);
        lines.Add(TrimForChat(ledger.Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Resume Plan");
        var resume = await ResumeBuildPlanAsync(cancellationToken).ConfigureAwait(false);
        lines.Add(TrimForChat(resume.Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Dependency Planning");
        lines.Add(TrimForChat(PlanPackageLookup(new CodingToolRequest(CodingToolAction.PlanPackageLookup, null, Query: "current project")).Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Scaffold Planning");
        lines.Add(TrimForChat(PreviewProjectScaffold(new CodingToolRequest(CodingToolAction.PreviewProjectScaffold, null, Query: "next approved feature")).Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Install Readiness");
        lines.Add("- Build from source before refreshing DevRun.");
        lines.Add("- Keep Visual Studio Community installed for the Ali Companion VSIX.");
        lines.Add("- Start Ali.App.WebHelper before using the VS tool window or browser companion.");
        lines.Add("- Keep Ollama available only for model-backed chat; coding commands are deterministic local commands.");
        lines.Add("- Keep package installs, repair actions, signing, and trust-store changes owner-approved.");
        lines.Add(string.Empty);
        lines.Add("Next Safe Commands");
        lines.Add("- show packet commands");
        lines.Add("- show packet ledger");
        lines.Add("- resume build plan");
        lines.Add("- plan package lookup <goal>");
        lines.Add("- preview project scaffold <goal>");
        lines.Add("- generate coding report");
        lines.Add("- generate morning report");

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<string> BuildCodingSessionReportAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "Ali Coding Session Report",
            $"Generated: {DateTimeOffset.Now:u}",
            $"Workspace root: {Policy.WorkspaceRoot}",
            string.Empty,
            "Summary",
            "- This report is generated from Ali's local coding tool state.",
            "- Tool receipts show actions Ali actually handled.",
            "- It does not claim builds, tests, edits, or Git actions happened unless receipts or stored diagnostics show them.",
            string.Empty,
            "Workspace Inspection"
        };

        var inspection = InspectWorkspace();
        lines.Add(TrimForChat(inspection.Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Solution Architecture");
        var architecture = AnalyzeArchitecture();
        lines.Add(TrimForChat(architecture.Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Recent Coding Receipts");
        lines.Add(TrimForChat(ShowReceipts().Message, 8_000));
        lines.Add(string.Empty);
        lines.Add("Pending Patch Preview");
        if (_lastPatchPreviewRequest is null)
        {
            lines.Add("No pending patch preview is waiting to be applied.");
        }
        else
        {
            var preview = _lastPatchPreviewRequest.Action == CodingToolAction.PreviewPatchBundle
                ? await PreviewPatchBundleAsync(_lastPatchPreviewRequest, cancellationToken).ConfigureAwait(false)
                : await PreviewReplaceTextAsync(_lastPatchPreviewRequest, cancellationToken).ConfigureAwait(false);
            lines.Add(preview.Succeeded
                ? TrimForChat(preview.Message, 8_000)
                : $"Pending patch preview is stale or invalid: {TrimForChat(preview.Message, 4_000)}");
        }

        lines.Add(string.Empty);
        lines.Add("Implementation Roadmap");
        LoadRoadmapStateIfNeeded();
        if (_roadmapState is null)
        {
            lines.Add("No implementation roadmap is pending in this Ali session.");
        }
        else
        {
            lines.Add(TrimForChat(FormatRoadmapStatus(_roadmapState, includeRecoveryPath: true), 8_000));
            if (_lastRoadmapRequest is not null)
            {
                lines.Add(TrimForChat(DraftImplementationRoadmap(_lastRoadmapRequest).Message, 8_000));
            }
        }

        lines.Add(string.Empty);
        lines.Add("Last Dotnet Failure");
        if (_lastDotNetRequest is null || _lastDotNetResult is not { Succeeded: false } lastDotNetResult)
        {
            lines.Add("No failed dotnet command is stored in this Ali session.");
        }
        else
        {
            lines.Add($"Action: {_lastDotNetRequest.Action}");
            lines.Add($"Target: {_lastDotNetResult.TargetPath ?? _lastDotNetRequest.Path ?? Policy.WorkspaceRoot}");
            lines.Add(lastDotNetResult.ExitCode is null ? "Exit code: unavailable" : $"Exit code: {lastDotNetResult.ExitCode.Value}");
            lines.Add(TrimForChat(lastDotNetResult.Message, 8_000));
        }

        lines.Add(string.Empty);
        lines.Add("Next Safe Commands");
        lines.Add("- inspect coding workspace");
        lines.Add("- analyze solution architecture");
        lines.Add("- plan coding task <goal>");
        lines.Add("- draft implementation roadmap <goal>");
        lines.Add("- show pending roadmap");
        lines.Add("- show active roadmap step");
        lines.Add("- show next coding action");
        lines.Add("- show execution packet");
        lines.Add("- show packet commands");
        lines.Add("- run packet item 1");
        lines.Add("- confirm run packet item N");
        lines.Add("- show packet ledger");
        lines.Add("- resume build plan");
        lines.Add("- plan package lookup <goal>");
        lines.Add("- preview project scaffold <goal>");
        lines.Add("- recover roadmap state");
        lines.Add("- show crash recovery status");
        lines.Add("- approve last roadmap");
        lines.Add("- start approved roadmap");
        lines.Add("- mark roadmap step complete");
        lines.Add("- pause roadmap");
        lines.Add("- resume roadmap");
        lines.Add("- preview replace in file \"path\" \"old text\" with \"new text\"");
        lines.Add("- preview patch bundle");
        lines.Add("- show pending patch preview");
        lines.Add("- confirm apply last patch preview");
        lines.Add("- suggest patch from last failure");
        lines.Add("- confirm dotnet add package \"Package.Id\" to \"path\"");
        lines.Add("- confirm dotnet build \"path\"");
        lines.Add("- diagnose last build failure");
        lines.Add("- show crash recovery status");
        lines.Add("- confirm git add all");
        lines.Add("- confirm git commit \"message\"");
        lines.Add("- generate visual studio integration plan");
        lines.Add("- generate morning report");
        lines.Add("- show coding receipts");

        return string.Join(Environment.NewLine, lines);
    }

    private Task<CodingToolResult> OpenWorkspaceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Policy.WorkspaceRoot);
        _processLauncher.Start(
            "explorer.exe",
            [Policy.WorkspaceRoot],
            useShellExecute: false);

        return Task.FromResult(new CodingToolResult(
            true,
            true,
            $"Opened coding workspace: {Policy.WorkspaceRoot}",
            "Explorer",
            Policy.WorkspaceRoot));
    }

    private CodingToolResult ListWorkspace()
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}",
                "Workspace list",
                Policy.WorkspaceRoot);
        }

        var entries = Directory.EnumerateFileSystemEntries(Policy.WorkspaceRoot)
            .Where(path => !ShouldSkipPath(path))
            .OrderBy(path => Directory.Exists(path) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Take(MaxListedEntries + 1)
            .ToList();
        var listed = entries.Take(MaxListedEntries)
            .Select(path => Directory.Exists(path)
                ? $"[dir] {Path.GetFileName(path)}"
                : $"[file] {Path.GetFileName(path)}")
            .ToList();
        var truncated = entries.Count > MaxListedEntries
            ? $"{Environment.NewLine}...more entries omitted."
            : string.Empty;

        var body = listed.Count == 0
            ? "Workspace is empty."
            : string.Join(Environment.NewLine, listed);
        return new CodingToolResult(
            true,
            true,
            $"Coding workspace: {Policy.WorkspaceRoot}{Environment.NewLine}{body}{truncated}",
            "Workspace list",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult InspectWorkspace()
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}",
                "Workspace inspection",
                Policy.WorkspaceRoot);
        }

        var files = EnumerateWorkspaceFiles().Take(10_000).ToList();
        var solutions = files
            .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projects = files
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var entryPoints = files
            .Where(IsLikelyEntryPoint)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxWorkspaceSummaryEntries)
            .Select(RelativeToWorkspace)
            .ToList();
        var extensionCounts = files
            .Select(Path.GetExtension)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .GroupBy(extension => extension!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToList();

        var lines = new List<string>
        {
            $"Coding workspace inspection: {Policy.WorkspaceRoot}",
            $"Files scanned: {files.Count}",
            $"Solutions: {solutions.Count}",
            $"Projects: {projects.Count}"
        };

        AddPathSection(lines, "Solution files", solutions);
        AddProjectSection(lines, projects);
        if (entryPoints.Count > 0)
        {
            lines.Add("Likely entry/UI files:");
            lines.AddRange(entryPoints.Select(path => $"- {path}"));
        }

        if (extensionCounts.Count > 0)
        {
            lines.Add("Top file types:");
            lines.AddRange(extensionCounts.Select(item => $"- {item}"));
        }

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Workspace inspection",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult AnalyzeArchitecture()
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}",
                "Architecture analysis",
                Policy.WorkspaceRoot);
        }

        var files = EnumerateWorkspaceFiles().Take(10_000).ToList();
        var solutions = files
            .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projects = files
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var summaries = projects
            .Take(MaxWorkspaceSummaryEntries)
            .Select(ReadProjectSummary)
            .ToList();

        var lines = new List<string>
        {
            $"Solution architecture analysis: {Policy.WorkspaceRoot}",
            "No files were changed.",
            $"Solutions found: {solutions.Count}",
            $"Projects found: {projects.Count}",
            $"Files scanned: {files.Count}"
        };

        AddPathSection(lines, "Solution files", solutions);
        if (summaries.Count > 0)
        {
            lines.Add("Project map:");
            foreach (var summary in summaries)
            {
                lines.Add($"- {summary.RelativePath}");
                lines.Add($"  Role: {summary.ProjectRole}");
                lines.Add(summary.TargetFrameworks.Count == 0
                    ? "  Targets: not declared"
                    : $"  Targets: {string.Join(", ", summary.TargetFrameworks)}");
                lines.Add($"  Source files: {summary.CSharpSourceCount} C#, {summary.XamlFileCount} XAML, {summary.JsonFileCount} JSON/config");

                if (summary.ProjectReferences.Count > 0)
                {
                    lines.Add($"  Project references: {string.Join(", ", summary.ProjectReferences.Take(8))}");
                    if (summary.ProjectReferences.Count > 8)
                    {
                        lines.Add($"  ...{summary.ProjectReferences.Count - 8} more project reference(s) omitted.");
                    }
                }
                else
                {
                    lines.Add("  Project references: none declared");
                }

                if (summary.PackageReferences.Count > 0)
                {
                    lines.Add($"  Package references: {string.Join(", ", summary.PackageReferences.Take(8))}");
                    if (summary.PackageReferences.Count > 8)
                    {
                        lines.Add($"  ...{summary.PackageReferences.Count - 8} more package reference(s) omitted.");
                    }
                }
                else
                {
                    lines.Add("  Package references: none declared");
                }

                if (!string.IsNullOrWhiteSpace(summary.Warning))
                {
                    lines.Add($"  Warning: {summary.Warning}");
                }
            }
        }

        AddArchitectureDependencySections(lines, summaries);

        if (projects.Count > MaxWorkspaceSummaryEntries)
        {
            lines.Add($"...{projects.Count - MaxWorkspaceSummaryEntries} more project file(s) omitted.");
        }

        lines.Add("Suggested guarded next steps:");
        lines.Add("- open solution");
        lines.Add("- list packages");
        lines.Add("- confirm dotnet build \"path\"");
        lines.Add("- diagnose last build failure");
        lines.Add("- generate coding report");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Architecture analysis",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowProjectIntelligence()
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}",
                "Project intelligence",
                Policy.WorkspaceRoot);
        }

        var files = EnumerateWorkspaceFiles().Take(10_000).ToList();
        var solutions = files
            .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projects = files
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var summaries = projects
            .Take(MaxWorkspaceSummaryEntries)
            .Select(ReadProjectSummary)
            .ToList();
        var primaryTarget = TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out var primary)
            ? primary
            : null;
        var appProjects = summaries
            .Where(summary => summary.ProjectRole.Contains("app", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();
        var testProjects = summaries
            .Where(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();
        var roleSummary = summaries.Count == 0
            ? "none"
            : string.Join(", ", summaries
                .GroupBy(summary => summary.ProjectRole, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}: {group.Count()}"));
        var markers = FindProjectMarkers(files);
        var entryFiles = files
            .Where(IsLikelyEntryPoint)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(RelativeToWorkspace)
            .ToList();
        var stackSignals = DetectStackSignals(files, summaries);
        var styleSignals = DetectStyleSignals(files, summaries);
        var buildCommands = DiscoverBuildCommands(files, summaries, primaryTarget).ToList();
        var testCommands = DiscoverTestCommands(files, summaries, primaryTarget).ToList();

        var lines = new List<string>
        {
            $"Project intelligence scan: {Policy.WorkspaceRoot}",
            "No files were changed.",
            $"Shape: {solutions.Count} solution(s), {projects.Count} .NET project(s), {files.Count} file(s) scanned.",
            $"Detected stacks: {FormatInlineList(stackSignals)}",
            $"Style signals: {FormatInlineList(styleSignals)}",
            $"Project roles: {roleSummary}",
            primaryTarget is null
                ? "Primary target: not found"
                : $"Primary target: {RelativeToWorkspace(primaryTarget)}"
        };

        AddCompactList(lines, "Likely app/host projects", appProjects);
        AddCompactList(lines, "Likely test projects", testProjects);
        AddCompactList(lines, "Important entry/config files", entryFiles);
        AddCompactList(lines, "Other project markers", markers);

        lines.Add("Recommended commands:");
        if (buildCommands.Count > 0)
        {
            foreach (var command in buildCommands.Take(4))
            {
                lines.Add($"- Build: {command}");
            }
        }
        else
        {
            lines.Add("- Build: choose a solution, project, package, or script target first.");
        }

        if (testCommands.Count > 0)
        {
            foreach (var command in testCommands.Take(4))
            {
                lines.Add($"- Tests: {command}");
            }
        }
        else
        {
            lines.Add("- Tests: no test command detected; add or identify tests before risky changes.");
        }

        lines.Add("- Review: review current changes");
        lines.Add("- Plan: plan coding task <goal>");
        lines.Add("Risk notes:");
        if (solutions.Count > 1)
        {
            lines.Add("- Multiple solutions found; choose the intended solution before build/test work.");
        }

        if (testProjects.Count == 0)
        {
            lines.Add("- No obvious test project found; prefer small edits plus manual validation notes.");
        }

        if (appProjects.Count == 0 && projects.Count > 0)
        {
            lines.Add("- No obvious app/host project found; this may be a library or support package.");
        }

        if (projects.Count == 0 && markers.Count == 0)
        {
            lines.Add("- No common project files found; inspect the workspace before planning edits.");
        }

        lines.Add("Next - Use Plan, Build, Tests, or Review from the Programming dashboard.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Project intelligence",
            Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowRepoUnderstandingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var intelligence = ShowProjectIntelligence();
        var architecture = AnalyzeArchitecture();
        var patterns = DetectCodebasePatterns();
        var validation = await PlanPostEditValidationAsync(cancellationToken).ConfigureAwait(false);
        var safeCommit = await ShowSafeCommitCheckAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<string>
        {
            $"Repo understanding: {Policy.WorkspaceRoot}",
            "No files were changed.",
            "Project:",
        };
        AddSelectedLines(lines, intelligence.Message, 8,
            "Shape:",
            "Detected stacks:",
            "Style signals:",
            "Project roles:",
            "Primary target:",
            "Likely app",
            "Likely test",
            "Other project markers:");
        lines.Add("Architecture:");
        AddSelectedLines(lines, architecture.Message, 7,
            "Solutions found:",
            "Projects found:",
            "Project role summary:",
            "App/UI entry projects:",
            "Test projects:",
            "Estimated project build order:");
        lines.Add("Patterns:");
        AddSelectedLines(lines, patterns.Message, 6,
            "Detected stacks:",
            "Style signals:",
            "Build commands:",
            "Test commands:");
        lines.Add("Validation:");
        AddSelectedLines(lines, validation.Message, 5,
            "Git:",
            "Latest validation:",
            "- Build:",
            "- Tests:",
            "- Review:");
        lines.Add("Commit readiness:");
        AddSelectedLines(lines, safeCommit.Message, 6,
            "Safe to commit:",
            "Git:",
            "Validation:",
            "Pending patch preview:",
            "Required next step:");
        lines.Add("Next - Pick Plan, Build, Tests, Review, or Safe Commit from the Programming dashboard.");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Repo understanding", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowSafeCommitCheckAsync(CancellationToken cancellationToken)
    {
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestDotNetReceipt = receipts.LastOrDefault(IsDotNetReceipt);
        var hasPendingPatchPreview = _lastPatchPreviewRequest is not null;
        var hasSuccessfulValidation = latestDotNetReceipt?.Succeeded == true
            || _lastDotNetResult?.Succeeded == true;
        var blockers = new List<string>();
        if (!gitStatus.Available)
        {
            blockers.Add("Git status is unavailable.");
        }
        else if (gitStatus.Clean)
        {
            blockers.Add("Working tree is clean; there is nothing obvious to commit.");
        }

        if (!hasSuccessfulValidation)
        {
            blockers.Add("No successful build/test validation receipt is available in this session.");
        }

        if (hasPendingPatchPreview)
        {
            blockers.Add("A pending patch preview still exists; apply or discard it before committing.");
        }

        var safe = blockers.Count == 0;
        var lines = new List<string>
        {
            "Commit readiness check:",
            "No files were changed.",
            $"Safe to commit: {(safe ? "Yes" : "No")}",
            $"Git: {gitStatus.Summary}",
            latestDotNetReceipt is null
                ? "Validation: none found"
                : FormatReceiptSummary("Validation", latestDotNetReceipt),
            hasPendingPatchPreview
                ? "Pending patch preview: yes"
                : "Pending patch preview: none",
            "Decision factors:"
        };

        if (blockers.Count == 0)
        {
            lines.Add("- Git has changes and the latest validation is successful.");
            lines.Add("- Review the exact diff one final time before committing.");
        }
        else
        {
            lines.AddRange(blockers.Select(blocker => $"- {blocker}"));
        }

        lines.Add("Required next step:");
        lines.Add(safe
            ? "- Review current changes, then commit with an owner-approved message."
            : "- Run Review, Build, and Tests until the rows above are good.");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Commit readiness", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowWorkspaceHealthScoreAsync(CancellationToken cancellationToken)
    {
        var files = Directory.Exists(Policy.WorkspaceRoot)
            ? EnumerateWorkspaceFiles().Take(10_000).ToList()
            : [];
        var summaries = GetWorkspaceProjectSummaries();
        var gitStatus = await InspectGitWorkingTreeAsync(cancellationToken).ConfigureAwait(false);
        var primaryTarget = GetPrimaryTarget();
        var buildCommands = DiscoverBuildCommands(files, summaries, primaryTarget).ToList();
        var testCommands = DiscoverTestCommands(files, summaries, primaryTarget).ToList();
        var receipts = ReadRecentReceipts(MaxReceiptEntries);
        var latestValidation = receipts.LastOrDefault(IsDotNetReceipt);
        var score = 0;
        score += Directory.Exists(Policy.WorkspaceRoot) ? 15 : 0;
        score += primaryTarget is not null ? 15 : 0;
        score += summaries.Count > 0 ? 15 : 0;
        score += testCommands.Count > 0 ? 15 : 0;
        score += gitStatus.Available ? 10 : 0;
        score += gitStatus.Available && !gitStatus.HasUncommittedChanges ? 10 : 0;
        score += latestValidation?.Succeeded == true ? 20 : 0;

        var lines = new List<string>
        {
            "Workspace health score:",
            "No files were changed.",
            $"Score: {score}/100",
            $"Workspace: {(Directory.Exists(Policy.WorkspaceRoot) ? "Good" : "Bad - missing")}",
            $"Primary target: {(primaryTarget is null ? "Bad - not found" : "Good - " + primaryTarget)}",
            $"Projects: {(summaries.Count > 0 ? "Good - " + summaries.Count : "Bad - none found")}",
            $"Tests: {(testCommands.Count > 0 ? "Good - " + FormatInlineList(testCommands.Take(3)) : "Needs work - none detected")}",
            $"Git: {gitStatus.Summary}",
            latestValidation is null ? "Latest validation: none" : FormatReceiptSummary("Latest validation", latestValidation),
            $"Build commands: {FormatInlineList(buildCommands)}",
            $"Test commands: {FormatInlineList(testCommands)}",
            "Next - improve the weakest row first."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Workspace health score", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> DraftCommitMessageAsync(CancellationToken cancellationToken)
    {
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var summary = SummarizeChangedAreas(changedFiles);
        var message = changedFiles.Count == 0
            ? "No commit needed"
            : $"Update {summary}";
        var lines = new List<string>
        {
            "Commit message draft:",
            "No files were changed.",
            $"Changed files: {changedFiles.Count}",
            $"Suggested message: {message}",
            "Body bullets:"
        };
        if (changedFiles.Count == 0)
        {
            lines.Add("- Working tree appears clean.");
        }
        else
        {
            foreach (var area in changedFiles.Select(ClassifyChangedArea).Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
            {
                lines.Add($"- Updates {area}.");
            }
        }

        lines.Add("Next - run Safe Commit before using this message.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Commit message draft", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> DraftReleaseNotesAsync(CancellationToken cancellationToken)
    {
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "Release notes draft:",
            "No files were changed.",
            "Highlights:"
        };
        if (changedFiles.Count == 0)
        {
            lines.Add("- No uncommitted release-note items detected.");
        }
        else
        {
            foreach (var area in changedFiles.Select(ClassifyChangedArea).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            {
                lines.Add($"- {area}");
            }
        }

        lines.Add("Validation:");
        lines.Add("- Build passed: confirm from latest receipt.");
        lines.Add("- Tests passed: confirm from latest receipt.");
        lines.Add("- User-facing notes reviewed by owner.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Release notes draft", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowCodingSessionTimeline()
    {
        var receipts = ReadRecentReceipts(20);
        var lines = new List<string>
        {
            "Coding session timeline:",
            "No files were changed.",
            $"Receipts found: {receipts.Count}"
        };
        if (receipts.Count == 0)
        {
            lines.Add("- No coding receipts found yet.");
        }
        else
        {
            lines.AddRange(receipts.Select(receipt =>
                $"- {receipt.Timestamp.LocalDateTime:g}: {receipt.Action} {(receipt.Succeeded ? "Good" : "Bad")}{(receipt.ExitCode is null ? string.Empty : $" exit {receipt.ExitCode}")}"));
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Coding session timeline", Policy.WorkspaceRoot);
    }


    private async Task<CodingToolResult> ShowFullCodingReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var health = await ShowWorkspaceHealthScoreAsync(cancellationToken).ConfigureAwait(false);
        var safeCommit = await ShowSafeCommitCheckAsync(cancellationToken).ConfigureAwait(false);
        var xamlBindings = VerifyXamlBindings();
        var commandBindings = VerifyCommandBindings();
        var deadCommands = ScanDeadCommands();
        var symbolIndex = ShowCSharpSymbolIndex();
        var validationLedger = ShowValidationLedger();

        var lines = new List<string>
        {
            "Full coding readiness:",
            "No files were changed.",
            "Workspace:"
        };
        AddSelectedLines(lines, health.Message, 8, "Score:", "Workspace:", "Primary target:", "Projects:", "Tests:", "Git:", "Latest validation:");
        lines.Add("Bindings:");
        AddSelectedLines(lines, xamlBindings.Message, 5, "XAML files:", "Bindings found:", "Unknown bindings:");
        AddSelectedLines(lines, commandBindings.Message, 5, "Command bindings found:", "Missing command targets:");
        lines.Add("Command surface:");
        AddSelectedLines(lines, deadCommands.Message, 6, "Coding actions:", "Service handlers:", "Dashboard commands:", "Missing dashboard targets:");
        lines.Add("Symbol index:");
        AddSelectedLines(lines, symbolIndex.Message, 5, "C# files:", "Types:", "Methods:", "Properties:");
        lines.Add("Commit gate:");
        AddSelectedLines(lines, safeCommit.Message, 6, "Safe to commit:", "Git:", "Validation:", "Pending patch preview:", "Required next step:");
        lines.Add("Validation ledger:");
        AddSelectedLines(lines, validationLedger.Message, 6, "Receipts found:", "Latest validation:", "Latest edit:", "Latest git check:");
        lines.Add("Next - fix any Bad, missing, or unknown row, then run Build and Tests.");

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Full coding readiness", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowValidationLedger()
    {
        var receipts = ReadRecentReceipts(30);
        var latestValidation = receipts.LastOrDefault(IsDotNetReceipt);
        var latestEdit = receipts.LastOrDefault(receipt => receipt.Action is nameof(CodingToolAction.CreateFile)
            or nameof(CodingToolAction.AppendFile)
            or nameof(CodingToolAction.ReplaceText)
            or nameof(CodingToolAction.ApplyLastPatchPreview));
        var latestGit = receipts.LastOrDefault(receipt => receipt.Action.StartsWith("Git", StringComparison.OrdinalIgnoreCase)
            || receipt.Action is nameof(CodingToolAction.GitStatus) or nameof(CodingToolAction.GitDiff) or nameof(CodingToolAction.ReviewCurrentChanges));
        var lines = new List<string>
        {
            "Before/after validation ledger:",
            "No files were changed.",
            $"Receipts found: {receipts.Count}",
            latestValidation is null ? "Latest validation: none" : FormatReceiptSummary("Latest validation", latestValidation),
            latestEdit is null ? "Latest edit: none" : FormatReceiptSummary("Latest edit", latestEdit),
            latestGit is null ? "Latest git check: none" : FormatReceiptSummary("Latest git check", latestGit),
            "Recent validation receipts:"
        };
        var validationReceipts = receipts.Where(IsValidationReceipt).TakeLast(8).ToList();
        lines.AddRange(validationReceipts.Count == 0 ? ["- none found"] : validationReceipts.Select(receipt => FormatReceiptSummary("-", receipt)));
        lines.Add("Rule - every non-trivial edit should end with review, build, and test evidence before release.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Validation ledger", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowCSharpSymbolIndex()
    {
        var files = GetCSharpFiles();
        var symbols = BuildRoslynSymbolIndex(files, 160);
        var typeCount = symbols.Count(symbol => symbol.Kind is "class" or "record" or "interface" or "enum" or "struct");
        var methodCount = symbols.Count(symbol => symbol.Kind == "method");
        var propertyCount = symbols.Count(symbol => symbol.Kind == "property");
        var lines = new List<string>
        {
            "C# symbol index:",
            "No files were changed.",
            "Engine: Roslyn syntax tree",
            $"C# files: {files.Count}",
            $"Types: {typeCount}",
            $"Methods: {methodCount}",
            $"Properties: {propertyCount}",
            "Examples:"
        };
        lines.AddRange(symbols.Count == 0 ? ["- none found"] : symbols.Take(20).Select(symbol => $"- {symbol.Kind} {symbol.Name}: {RelativeToWorkspace(symbol.Path)}:{symbol.LineNumber}"));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "C# symbol index", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowCallGraph(CodingToolRequest request)
    {
        var query = CleanGoal(request.Query, string.Empty);
        var edges = BuildRoslynCallGraph(GetCSharpFiles(), 240);
        var filtered = string.IsNullOrWhiteSpace(query)
            ? edges.Take(30).ToList()
            : edges
                .Where(edge => edge.Caller.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || edge.Callee.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(30)
                .ToList();
        var lines = new List<string>
        {
            "Call graph:",
            "No files were changed.",
            "Engine: Roslyn invocation syntax scan",
            string.IsNullOrWhiteSpace(query) ? "Filter: none" : $"Filter: {query}",
            $"Edges found: {edges.Count}",
            $"Edges shown: {filtered.Count}"
        };
        lines.AddRange(filtered.Count == 0
            ? ["- none found"]
            : filtered.Select(edge => $"- {edge.Caller} -> {edge.Callee} ({RelativeToWorkspace(edge.Path)}:{edge.LineNumber})"));
        lines.Add("Note - this is syntax-level and does not resolve overloads, virtual dispatch, reflection, or generated code yet.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Call graph", Policy.WorkspaceRoot);
    }
    private CodingToolResult ResolveSemanticSymbol(CodingToolRequest request)
    {
        var query = CleanGoal(request.Query, string.Empty);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new CodingToolResult(true, false, "Semantic symbol resolver needs a symbol name.", "Semantic symbol resolver", Policy.WorkspaceRoot);
        }

        var workspace = BuildRoslynWorkspaceModel(GetCSharpFiles(), 2_000);
        var hits = FindSemanticSymbolHits(workspace, query, 40);
        var declarations = hits.Where(hit => hit.Source == "declaration").Take(12).ToList();
        var references = hits.Where(hit => hit.Source != "declaration").Take(12).ToList();
        var lines = new List<string>
        {
            "Semantic symbol resolver:",
            "No files were changed.",
            "Engine: Roslyn semantic model",
            $"Query: {query}",
            $"C# files: {workspace.Trees.Count}",
            $"Declarations found: {declarations.Count}",
            $"References found: {references.Count}",
            "Declarations:"
        };
        lines.AddRange(declarations.Count == 0 ? ["- none found"] : declarations.Select(FormatSemanticHit));
        lines.Add("References:");
        lines.AddRange(references.Count == 0 ? ["- none found"] : references.Select(FormatSemanticHit));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Semantic symbol resolver", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowImpactedTestsAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        var query = CleanGoal(request.Query, string.Empty);
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var workspace = BuildRoslynWorkspaceModel(GetCSharpFiles(), 2_000);
        var semanticHits = string.IsNullOrWhiteSpace(query) ? [] : FindSemanticSymbolHits(workspace, query, 60);
        var sourceFiles = semanticHits
            .Select(hit => hit.Path)
            .Where(path => !IsTestFile(path))
            .Concat(changedFiles.Where(IsSourceFile).Where(file => !IsTestFile(file)).Select(ToAbsoluteWorkspacePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var sourceTokens = sourceFiles
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var testFiles = GetCSharpFiles()
            .Where(IsTestFile)
            .Where(file => sourceTokens.Count == 0 || sourceTokens.Any(token => file.Contains(token, StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrWhiteSpace(query) && file.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        if (testFiles.Count == 0)
        {
            testFiles = GetCSharpFiles().Where(IsTestFile).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        }

        var summaries = GetWorkspaceProjectSummaries();
        var validationCommands = DiscoverTestCommands(GetCSharpFiles().Cast<string>().Concat(GetXamlFiles()).ToList(), summaries, GetPrimaryTarget()).Take(5).ToList();
        var lines = new List<string>
        {
            "Impacted tests:",
            "No files were changed.",
            "Engine: Roslyn semantic hits + workspace test naming",
            string.IsNullOrWhiteSpace(query) ? "Query: current changed files" : $"Query: {query}",
            $"Source files signaled: {sourceFiles.Count}",
            $"Likely test files: {testFiles.Count}",
            "Source signals:"
        };
        lines.AddRange(sourceFiles.Count == 0 ? ["- none found"] : sourceFiles.Select(file => $"- {RelativeToWorkspace(file)}"));
        lines.Add("Likely tests:");
        lines.AddRange(testFiles.Count == 0 ? ["- none found"] : testFiles.Select(file => $"- {RelativeToWorkspace(file)}"));
        lines.Add("Validation commands:");
        lines.AddRange(validationCommands.Count == 0 ? ["- No test command detected. Run project intelligence first."] : validationCommands.Select(command => $"- {command}"));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Impacted tests", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> PlanSemanticEditAsync(CodingToolRequest request, CancellationToken cancellationToken)
    {
        var goal = CleanGoal(request.Query, "current change");
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var workspace = BuildRoslynWorkspaceModel(GetCSharpFiles(), 2_000);
        var goalTerms = ExtractMeaningfulGoalTerms(goal).Take(8).ToList();
        var hits = goalTerms.SelectMany(term => FindSemanticSymbolHits(workspace, term, 12)).DistinctBy(hit => $"{hit.Display}|{hit.Path}|{hit.LineNumber}", StringComparer.Ordinal).Take(20).ToList();
        var candidateFiles = hits.Select(hit => hit.Path)
            .Concat(changedFiles.Select(ToAbsoluteWorkspacePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (candidateFiles.Count == 0)
        {
            candidateFiles = SuggestLikelyFilesForGoal(goal).Select(ToAbsoluteWorkspacePath).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).Take(8).ToList();
        }

        var lines = new List<string>
        {
            "Semantic edit plan:",
            "No files were changed.",
            "Engine: Roslyn semantic model + existing risk labels",
            $"Goal: {goal}",
            $"Candidate files: {candidateFiles.Count}",
            $"Candidate symbols: {hits.Count}",
            "Symbols to inspect:"
        };
        lines.AddRange(hits.Count == 0 ? ["- none found from goal terms"] : hits.Take(10).Select(FormatSemanticHit));
        lines.Add("Files to inspect first:");
        lines.AddRange(candidateFiles.Count == 0 ? ["- none found"] : candidateFiles.Select(file => $"- {RelativeToWorkspace(file)}: {ClassifyFileRisk(RelativeToWorkspace(file))}"));
        lines.Add("Edit guardrails:");
        lines.Add("- Prefer exact-symbol changes over broad text replacement.");
        lines.Add("- Update tests when source behavior, parser routing, permissions, or UI command bindings change.");
        lines.Add("- Run impacted tests, then build, then safe commit check.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Semantic edit plan", Policy.WorkspaceRoot);
    }

    private CodingToolResult MapCompilerDiagnostic(CodingToolRequest request)
    {
        var diagnostic = CleanGoal(request.Query, string.Empty);
        if (string.IsNullOrWhiteSpace(diagnostic) && _lastDotNetResult is not null)
        {
            diagnostic = _lastDotNetResult.Message;
        }

        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return new CodingToolResult(true, false, "Diagnostic mapper needs a compiler diagnostic or a recent build failure.", "Diagnostic mapper", Policy.WorkspaceRoot);
        }

        var parsed = ParseCompilerDiagnostic(diagnostic);
        var absolutePath = string.IsNullOrWhiteSpace(parsed.Path) ? null : ToAbsoluteWorkspacePath(parsed.Path);
        var symbolContext = absolutePath is not null && File.Exists(absolutePath)
            ? FindEnclosingSymbolAtLine(absolutePath, parsed.LineNumber)
            : null;
        var lines = new List<string>
        {
            "Compiler diagnostic mapper:",
            "No files were changed.",
            "Engine: diagnostic parser + Roslyn syntax context",
            string.IsNullOrWhiteSpace(parsed.Code) ? "Code: unknown" : $"Code: {parsed.Code}",
            absolutePath is null ? "File: unknown" : $"File: {RelativeToWorkspace(absolutePath)}",
            parsed.LineNumber is null ? "Line: unknown" : $"Line: {parsed.LineNumber}",
            symbolContext is null ? "Nearest symbol: unknown" : $"Nearest symbol: {symbolContext}",
            "Likely fix lane:"
        };
        AddKnownErrorGuidance(lines, string.IsNullOrWhiteSpace(parsed.Code) ? diagnostic : parsed.Code);
        lines.Add("Next - inspect the mapped file/symbol, make the smallest exact edit, then run impacted tests and build.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Diagnostic mapper", absolutePath ?? Policy.WorkspaceRoot, parsed.LineNumber);
    }
    private CodingToolResult VerifyXamlBindings()
    {
        var xamlFiles = GetXamlFiles();
        var symbols = BuildRoslynSymbolIndex(GetCSharpFiles(), 10_000);
        var symbolNames = symbols.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        var bindings = new List<(string Path, string Name)>();
        foreach (var file in xamlFiles)
        {
            foreach (var binding in ExtractXamlBindingNames(SafeReadText(file), commandOnly: false))
            {
                bindings.Add((file, binding));
            }
        }

        var unknown = bindings
            .Where(binding => !IsIgnoredBindingName(binding.Name))
            .Where(binding => !symbolNames.Contains(binding.Name))
            .DistinctBy(binding => RelativeToWorkspace(binding.Path) + "|" + binding.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var lines = new List<string>
        {
            "XAML binding check:",
            "No files were changed.",
            "Engine: XAML text scan + Roslyn C# symbol index",
            $"XAML files: {xamlFiles.Count}",
            $"Bindings found: {bindings.Count}",
            $"Unknown bindings: {unknown.Count}"
        };
        lines.AddRange(unknown.Count == 0 ? ["- Good - no unknown binding names found."] : unknown.Select(binding => $"- {RelativeToWorkspace(binding.Path)} -> {binding.Name}"));
        lines.Add("Note - dynamic DataContext, generated properties, and converter parameters may still need human review.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "XAML binding check", Policy.WorkspaceRoot);
    }

    private CodingToolResult VerifyCommandBindings()
    {
        var xamlFiles = GetXamlFiles();
        var symbols = BuildRoslynSymbolIndex(GetCSharpFiles(), 10_000);
        var symbolNames = symbols.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
        var commands = new List<(string Path, string Name)>();
        foreach (var file in xamlFiles)
        {
            foreach (var command in ExtractXamlBindingNames(SafeReadText(file), commandOnly: true))
            {
                commands.Add((file, command));
            }
        }

        var missing = commands
            .Where(command => !symbolNames.Contains(command.Name))
            .DistinctBy(command => RelativeToWorkspace(command.Path) + "|" + command.Name, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var lines = new List<string>
        {
            "Command binding check:",
            "No files were changed.",
            "Engine: XAML command scan + Roslyn C# symbol index",
            $"Command bindings found: {commands.Count}",
            $"Missing command targets: {missing.Count}"
        };
        lines.AddRange(missing.Count == 0 ? ["- Good - all command binding names were found in code symbols."] : missing.Select(command => $"- {RelativeToWorkspace(command.Path)} -> {command.Name}"));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Command binding check", Policy.WorkspaceRoot);
    }

    private CodingToolResult ScanDeadCommands()
    {
        var servicePath = Path.Combine(Policy.WorkspaceRoot, "src", "Ali.Infrastructure", "Coding", "LocalCodingToolService.cs");
        var serviceText = SafeReadText(servicePath);
        var actions = Enum.GetNames<CodingToolAction>();
        var missingHandlers = actions
            .Where(action => !serviceText.Contains($"CodingToolAction.{action}", StringComparison.Ordinal))
            .OrderBy(action => action, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var dashboardPath = Path.Combine(Policy.WorkspaceRoot, "src", "Ali.App.Wpf", "ProgrammingDashboardWindow.xaml");
        var dashboardCommands = ExtractXamlBindingNames(SafeReadText(dashboardPath), commandOnly: true).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var viewModelSymbols = BuildRoslynSymbolIndex([Path.Combine(Policy.WorkspaceRoot, "src", "Ali.App.Wpf", "ViewModels", "MainWindowViewModel.cs")], 10_000)
            .Select(symbol => symbol.Name)
            .ToHashSet(StringComparer.Ordinal);
        var missingDashboardTargets = dashboardCommands
            .Where(command => !viewModelSymbols.Contains(command))
            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var lines = new List<string>
        {
            "Dead command scan:",
            "No files were changed.",
            "Engine: enum/service text scan + Roslyn view-model symbol index",
            $"Coding actions: {actions.Length}",
            $"Service handlers: {(missingHandlers.Count == 0 ? "Good" : "Needs review - " + missingHandlers.Count + " action(s) not referenced in service text")}",
            $"Dashboard commands: {dashboardCommands.Count}",
            $"Missing dashboard targets: {missingDashboardTargets.Count}",
            "Service actions needing review:"
        };
        lines.AddRange(missingHandlers.Count == 0 ? ["- none"] : missingHandlers.Select(action => $"- {action}"));
        lines.Add("Dashboard bindings needing review:");
        lines.AddRange(missingDashboardTargets.Count == 0 ? ["- none"] : missingDashboardTargets.Select(command => $"- {command}"));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Dead command scan", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowRollbackPlanAsync(CancellationToken cancellationToken)
    {
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "Rollback plan:",
            "No files were changed.",
            $"Changed files detected: {changedFiles.Count}",
            "Safe rollback approach:"
        };
        if (changedFiles.Count == 0)
        {
            lines.Add("- No working-tree changes detected.");
        }
        else
        {
            lines.Add("- Review each changed file and decide whether to keep, edit, or revert.");
            lines.Add("- Prefer an explicit reverse patch for Ali-made changes.");
            lines.Add("- Use Git restore/reset only after the owner explicitly requests that destructive action.");
            lines.Add("Files to review:");
            lines.AddRange(changedFiles.Take(12).Select(file => $"- {file}"));
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Rollback plan", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowUiChangeChecklist(CodingToolRequest request)
    {
        var goal = CleanGoal(request.Query, "current UI change");
        var lines = new List<string>
        {
            "UI change checklist:",
            $"Goal: {goal}",
            "No files were changed.",
            "Before edit:",
            "- Identify XAML/view, view model, command, and test touch points.",
            "- Check disabled/busy states and error text.",
            "- Keep button labels short and output human-readable.",
            "After edit:",
            "- Build the app.",
            "- Smoke check the window at normal and narrow sizes.",
            "- Confirm text does not overlap and scroll areas still work.",
            "- Add or update parser/service tests when the button triggers a deterministic command."
        };

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "UI change checklist", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ComposeTypedPatchAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        var goal = CleanGoal(request.Query, "current change");
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var files = changedFiles.Count > 0
            ? changedFiles.Take(MaxPatchBundleEdits).ToList()
            : SuggestLikelyFilesForGoal(goal).Take(MaxPatchBundleEdits).ToList();
        var lines = new List<string>
        {
            "Typed patch composer:",
            $"Goal: {goal}",
            "No files were changed.",
            $"Candidate edits: {files.Count}",
            "Patch bundle template:"
        };

        if (files.Count == 0)
        {
            lines.Add("- No candidate files found yet. Run project intelligence or plan feature files first.");
        }
        else
        {
            foreach (var file in files)
            {
                lines.Add($"- File: {file}");
                lines.Add($"  Risk: {ClassifyFileRisk(file)}");
                lines.Add("  Edit type: replace exact old text with reviewed new text");
                lines.Add("  Needs: old text, new text, validation command");
            }
        }

        lines.Add("Next - turn each item into preview patch bundle lines, then review before applying.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Typed patch composer", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowFileRiskLabelsAsync(CancellationToken cancellationToken)
    {
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "File risk labels:",
            "No files were changed.",
            $"Changed files: {changedFiles.Count}"
        };
        if (changedFiles.Count == 0)
        {
            lines.Add("- No changed files detected.");
        }
        else
        {
            foreach (var file in changedFiles.Take(20))
            {
                lines.Add($"- {file}: {ClassifyFileRisk(file)}");
            }
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "File risk labels", Policy.WorkspaceRoot);
    }

    private CodingToolResult FindSymbol(CodingToolRequest request)
    {
        var symbol = CleanGoal(request.Query, string.Empty);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return new CodingToolResult(true, false, "Symbol finder needs a symbol name.", "Symbol finder", Policy.WorkspaceRoot);
        }

        var matches = FindSymbolMatches(symbol, declarationOnly: true, MaxSearchMatches);
        if (matches.Count == 0)
        {
            matches = FindSymbolMatches(symbol, declarationOnly: false, MaxSearchMatches);
        }

        var lines = new List<string>
        {
            "Symbol finder:",
            $"Symbol: {symbol}",
            "No files were changed.",
            $"Matches: {matches.Count}"
        };
        lines.AddRange(matches.Count == 0
            ? ["- No matches found."]
            : matches.Select(match => $"- {match}"));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Symbol finder", Policy.WorkspaceRoot);
    }

    private CodingToolResult ShowCrossReferenceMap(CodingToolRequest request)
    {
        var symbol = CleanGoal(request.Query, string.Empty);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return new CodingToolResult(true, false, "Cross-reference map needs a symbol name.", "Cross-reference map", Policy.WorkspaceRoot);
        }

        var declarations = FindSymbolMatches(symbol, declarationOnly: true, 10);
        var references = FindSymbolMatches(symbol, declarationOnly: false, MaxSearchMatches)
            .Where(match => !declarations.Contains(match, StringComparer.OrdinalIgnoreCase))
            .Take(20)
            .ToList();
        var lines = new List<string>
        {
            "Cross-reference map:",
            $"Symbol: {symbol}",
            "No files were changed.",
            "Declarations:"
        };
        lines.AddRange(declarations.Count == 0 ? ["- none found"] : declarations.Select(match => $"- {match}"));
        lines.Add("References:");
        lines.AddRange(references.Count == 0 ? ["- none found"] : references.Select(match => $"- {match}"));
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Cross-reference map", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowTestGapReportAsync(CancellationToken cancellationToken)
    {
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var sourceFiles = changedFiles
            .Where(IsSourceFile)
            .Where(file => !IsTestFile(file))
            .ToList();
        var testFiles = changedFiles.Where(IsTestFile).ToList();
        var lines = new List<string>
        {
            "Test gap report:",
            "No files were changed.",
            $"Changed source files: {sourceFiles.Count}",
            $"Changed test files: {testFiles.Count}"
        };

        if (sourceFiles.Count > 0 && testFiles.Count == 0)
        {
            lines.Add("Gap: source files changed without obvious test file changes.");
        }
        else
        {
            lines.Add("Gap: no obvious source/test mismatch detected.");
        }

        foreach (var file in sourceFiles.Take(12))
        {
            lines.Add($"- Source: {file}");
            lines.Add($"  Suggested test area: {SuggestTestArea(file)}");
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Test gap report", Policy.WorkspaceRoot);
    }

    private CodingToolResult ExplainKnownError(CodingToolRequest request)
    {
        var query = CleanGoal(request.Query, "last error");
        var lines = new List<string>
        {
            "Known error guidance:",
            $"Error: {query}",
            "No files were changed."
        };
        AddKnownErrorGuidance(lines, query);
        lines.Add("Next - diagnose last build failure, then preview an exact patch only when the old text is known.");
        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Known error guidance", Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> PreviewRollbackPatchAsync(CancellationToken cancellationToken)
    {
        var changedFiles = await ReadChangedFilesAsync(cancellationToken).ConfigureAwait(false);
        var diff = await _commandRunner.RunAsync(
            "git",
            ["diff", "--stat", "HEAD"],
            Policy.WorkspaceRoot,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            "Rollback patch preview:",
            "No files were changed.",
            $"Changed files detected: {changedFiles.Count}",
            "Rollback source: current git diff against HEAD.",
            "Safe application rule: use an explicit owner-approved reverse patch or confirmed git restore only after reviewing the files."
        };
        if (changedFiles.Count == 0)
        {
            lines.Add("- No rollback patch needed.");
        }
        else
        {
            lines.Add("Files that would be affected:");
            lines.AddRange(changedFiles.Take(20).Select(file => $"- {file}: {ClassifyFileRisk(file)}"));
            lines.Add("Diff stat:");
            lines.Add(TrimForChat(MergeCommandOutput(diff), 1_500));
        }

        return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "Rollback patch preview", Policy.WorkspaceRoot);
    }

    private CodingToolResult ListPackages(CodingToolRequest request)
    {
        if (!ResolveProjectReportTargets(request, out var projectFiles, out var targetPath, out var error))
        {
            return new CodingToolResult(true, false, error, "Package references", request.Path ?? Policy.WorkspaceRoot);
        }

        var lines = new List<string>
        {
            $"Package references for: {targetPath}",
            $"Projects checked: {projectFiles.Count}"
        };

        var totalPackages = 0;
        foreach (var summary in projectFiles.Take(MaxWorkspaceSummaryEntries).Select(ReadProjectSummary))
        {
            lines.Add($"- {summary.RelativePath}");
            lines.Add($"  Role: {summary.ProjectRole}");
            if (summary.TargetFrameworks.Count > 0)
            {
                lines.Add($"  Target: {string.Join(", ", summary.TargetFrameworks)}");
            }

            if (summary.PackageReferences.Count == 0)
            {
                lines.Add("  Packages: none declared");
            }
            else
            {
                totalPackages += summary.PackageReferences.Count;
                lines.AddRange(summary.PackageReferences.Take(12).Select(package => $"  Package: {package}"));
                if (summary.PackageReferences.Count > 12)
                {
                    lines.Add($"  ...{summary.PackageReferences.Count - 12} more package reference(s) omitted.");
                }
            }

            if (!string.IsNullOrWhiteSpace(summary.Warning))
            {
                lines.Add($"  Warning: {summary.Warning}");
            }
        }

        if (projectFiles.Count > MaxWorkspaceSummaryEntries)
        {
            lines.Add($"...{projectFiles.Count - MaxWorkspaceSummaryEntries} more project file(s) omitted.");
        }

        lines.Add($"Total package references listed: {totalPackages}");
        lines.Add("Outdated/vulnerable package checks are not run by this read-only report. Use a confirmed dotnet package command for live NuGet checks.");

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Package references",
            targetPath);
    }

    private CodingToolResult SearchWorkspace(CodingToolRequest request)
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}",
                "Workspace search",
                Policy.WorkspaceRoot);
        }

        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new CodingToolResult(true, false, "Coding search needs a query.", "Workspace search", Policy.WorkspaceRoot);
        }

        var matches = new List<string>();
        foreach (var file in EnumerateWorkspaceFiles().Take(5_000))
        {
            if (matches.Count >= MaxSearchMatches)
            {
                break;
            }

            if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add($"{Path.GetRelativePath(Policy.WorkspaceRoot, file)}: file name match");
                continue;
            }

            SearchFile(file, query, matches);
        }

        var message = matches.Count == 0
            ? $"No workspace matches found for: {query}"
            : $"Workspace matches for \"{query}\":{Environment.NewLine}{string.Join(Environment.NewLine, matches)}";
        return new CodingToolResult(
            true,
            true,
            message,
            "Workspace search",
            Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ReadFileAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CodingWorkspacePolicy.TryNormalizePath(request.Path ?? string.Empty, out var fullPath))
        {
            return new CodingToolResult(true, false, "Coding tool blocked: invalid file path.", "File read");
        }

        if (!File.Exists(fullPath))
        {
            return new CodingToolResult(true, false, $"Coding tool could not find file: {fullPath}", "File read", fullPath);
        }

        var content = await ReadFilePreviewAsync(fullPath, request.LineNumber, cancellationToken).ConfigureAwait(false);
        return new CodingToolResult(
            true,
            true,
            $"Read file: {fullPath}{Environment.NewLine}{content}",
            "File read",
            fullPath,
            request.LineNumber);
    }

    private async Task<CodingToolResult> EditFileAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CodingWorkspacePolicy.TryNormalizePath(request.Path ?? string.Empty, out var fullPath))
        {
            return new CodingToolResult(true, false, "Coding tool blocked: invalid edit file path.", "File edit");
        }

        if (!Policy.IsInsideWorkspace(fullPath) && !Policy.AllowConfirmedOutsideEditRun)
        {
            return new CodingToolResult(
                true,
                false,
                "Coding tool blocked: edit target must be inside the approved coding workspace.",
                "File edit",
                fullPath);
        }

        if (!LooksTextReadable(fullPath))
        {
            return new CodingToolResult(
                true,
                false,
                "Coding tool blocked: only text-like coding files can be edited.",
                "File edit",
                fullPath);
        }

        return request.Action switch
        {
            CodingToolAction.CreateFile => await CreateFileAsync(fullPath, request.Content, cancellationToken).ConfigureAwait(false),
            CodingToolAction.AppendFile => await AppendFileAsync(fullPath, request.Content, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ReplaceText => await ReplaceTextAsync(fullPath, request.Content, request.Replacement, cancellationToken).ConfigureAwait(false),
            _ => new CodingToolResult(true, false, "Coding tool blocked: unsupported file edit action.", "File edit", fullPath)
        };
    }

    private async Task<CodingToolResult> PreviewReplaceTextAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CodingWorkspacePolicy.TryNormalizePath(request.Path ?? string.Empty, out var fullPath))
        {
            return new CodingToolResult(true, false, "Coding tool blocked: invalid preview file path.", "Patch preview");
        }

        if (!Policy.IsInsideWorkspace(fullPath) && !Policy.AllowConfirmedOutsideEditRun)
        {
            return new CodingToolResult(
                true,
                false,
                "Coding tool blocked: patch preview target must be inside the approved coding workspace.",
                "Patch preview",
                fullPath);
        }

        if (!LooksTextReadable(fullPath))
        {
            return new CodingToolResult(
                true,
                false,
                "Coding tool blocked: only text-like coding files can be previewed.",
                "Patch preview",
                fullPath);
        }

        return await PreviewReplaceTextAsync(fullPath, request.Content, request.Replacement, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodingToolResult> PreviewPatchBundleAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizePatchBundle(request, out var edits, out var error))
        {
            return error;
        }

        var prepared = await PreparePatchBundleAsync(edits, "Patch bundle preview", cancellationToken).ConfigureAwait(false);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        var lines = new List<string>
        {
            "Patch bundle preview:",
            "No files were changed.",
            $"Edits: {prepared.Edits.Count}",
            "To apply this exact bundle, use: confirm apply last patch preview"
        };

        for (var index = 0; index < prepared.Edits.Count; index++)
        {
            var edit = prepared.Edits[index];
            lines.Add(string.Empty);
            lines.Add($"Edit {index + 1}: {edit.FullPath}");
            lines.Add("Before:");
            lines.Add(edit.BeforeSnippet);
            lines.Add("After:");
            lines.Add(edit.AfterSnippet);
        }

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Patch bundle preview",
            prepared.Edits.Count == 1 ? prepared.Edits[0].FullPath : Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ApplyLastPatchPreviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastPatchPreviewRequest is null)
        {
            return new CodingToolResult(
                true,
                false,
                "No patch preview is waiting to be applied. Preview the exact patch first.",
                "Patch preview apply");
        }

        if (_lastPatchPreviewRequest.Action == CodingToolAction.PreviewPatchBundle)
        {
            var patchBundleRequest = _lastPatchPreviewRequest;
            _lastPatchPreviewRequest = null;
            return await ApplyPatchBundlePreviewAsync(patchBundleRequest, cancellationToken).ConfigureAwait(false);
        }

        var applyRequest = _lastPatchPreviewRequest with
        {
            Action = CodingToolAction.ReplaceText,
            UserConfirmed = true
        };
        _lastPatchPreviewRequest = null;

        var permission = Policy.Evaluate(applyRequest);
        if (permission.Kind != CodingToolPermissionKind.Allow)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: {permission.Reason}",
                "Patch preview apply",
                applyRequest.Path);
        }

        var result = await EditFileAsync(applyRequest, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result with
            {
                Message = $"Applied last patch preview.{Environment.NewLine}{result.Message}",
                ToolName = "Patch preview apply"
            }
            : result with
            {
                Message = $"Last patch preview was not applied.{Environment.NewLine}{result.Message}",
                ToolName = "Patch preview apply"
            };
    }

    private async Task<CodingToolResult> ApplyPatchBundlePreviewAsync(
        CodingToolRequest previewRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalizePatchBundle(previewRequest, out var edits, out var error))
        {
            return error with
            {
                Message = $"Last patch preview was not applied. No files were changed.{Environment.NewLine}{error.Message}",
                ToolName = "Patch preview apply"
            };
        }

        foreach (var edit in edits)
        {
            var applyRequest = new CodingToolRequest(
                CodingToolAction.ReplaceText,
                edit.FullPath,
                ExplicitUserPath: false,
                UserConfirmed: true,
                Content: edit.OldText,
                Replacement: edit.NewText);
            var permission = Policy.Evaluate(applyRequest);
            if (permission.Kind != CodingToolPermissionKind.Allow)
            {
                return new CodingToolResult(
                    true,
                    false,
                    $"Coding tool blocked: {permission.Reason}",
                    "Patch preview apply",
                    edit.FullPath);
            }
        }

        var prepared = await PreparePatchBundleAsync(edits, "Patch preview apply", cancellationToken).ConfigureAwait(false);
        if (prepared.Error is not null)
        {
            return prepared.Error with
            {
                Message = $"Last patch preview was not applied. No files were changed.{Environment.NewLine}{prepared.Error.Message}",
                ToolName = "Patch preview apply"
            };
        }

        var finalEditsByFile = prepared.Edits
            .GroupBy(edit => edit.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        foreach (var edit in finalEditsByFile)
        {
            await File.WriteAllTextAsync(edit.FullPath, edit.UpdatedText, cancellationToken).ConfigureAwait(false);
        }

        var changedFiles = finalEditsByFile
            .Select(edit => edit.FullPath)
            .ToList();
        var lines = new List<string>
        {
            "Applied last patch preview bundle.",
            $"Applied {prepared.Edits.Count} edit(s) across {changedFiles.Count} file(s)."
        };
        lines.AddRange(changedFiles.Select(path => $"- {path}"));

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Patch preview apply",
            changedFiles.Count == 1 ? changedFiles[0] : Policy.WorkspaceRoot);
    }

    private async Task<CodingToolResult> ShowLastPatchPreviewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastPatchPreviewRequest is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No patch preview is waiting to be applied.",
                "Pending patch preview");
        }

        var preview = _lastPatchPreviewRequest.Action == CodingToolAction.PreviewPatchBundle
            ? await PreviewPatchBundleAsync(_lastPatchPreviewRequest, cancellationToken).ConfigureAwait(false)
            : await PreviewReplaceTextAsync(_lastPatchPreviewRequest, cancellationToken).ConfigureAwait(false);
        if (!preview.Succeeded)
        {
            _lastPatchPreviewRequest = null;
            return preview with
            {
                Message = $"Pending patch preview is no longer valid and was discarded.{Environment.NewLine}{preview.Message}",
                ToolName = "Pending patch preview"
            };
        }

        return preview with
        {
            Message = $"Pending patch preview is still valid.{Environment.NewLine}{preview.Message}",
            ToolName = "Pending patch preview"
        };
    }

    private CodingToolResult DiscardLastPatchPreview()
    {
        if (_lastPatchPreviewRequest is null)
        {
            return new CodingToolResult(
                true,
                true,
                "No pending patch preview was waiting to be discarded.",
                "Pending patch preview");
        }

        var path = _lastPatchPreviewRequest.Path
                   ?? $"{_lastPatchPreviewRequest.PatchEdits?.Count ?? 0} bundled edit(s)";
        _lastPatchPreviewRequest = null;
        return new CodingToolResult(
            true,
            true,
            $"Discarded pending patch preview. No files were changed.{Environment.NewLine}Target: {path}",
            "Pending patch preview",
            path);
    }

    private bool TryNormalizePatchBundle(
        CodingToolRequest request,
        out IReadOnlyList<NormalizedPatchEdit> edits,
        out CodingToolResult error)
    {
        edits = [];
        error = CodingToolResult.NotHandled;
        if (request.PatchEdits is null || request.PatchEdits.Count == 0)
        {
            error = new CodingToolResult(
                true,
                false,
                "Coding tool blocked: patch bundle preview needs at least one file edit.",
                "Patch bundle preview",
                Policy.WorkspaceRoot);
            return false;
        }

        if (request.PatchEdits.Count > MaxPatchBundleEdits)
        {
            error = new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: patch bundle can preview at most {MaxPatchBundleEdits} edit(s).",
                "Patch bundle preview",
                Policy.WorkspaceRoot);
            return false;
        }

        var normalized = new List<NormalizedPatchEdit>();
        foreach (var edit in request.PatchEdits)
        {
            if (!CodingWorkspacePolicy.TryNormalizePath(edit.Path, out var fullPath))
            {
                error = new CodingToolResult(
                    true,
                    false,
                    "Coding tool blocked: every patch bundle target must be a valid local path.",
                    "Patch bundle preview",
                    edit.Path);
                return false;
            }

            if (!Policy.IsInsideWorkspace(fullPath) && !Policy.AllowConfirmedOutsideEditRun)
            {
                error = new CodingToolResult(
                    true,
                    false,
                    "Coding tool blocked: patch bundle targets must be inside the approved coding workspace.",
                    "Patch bundle preview",
                    fullPath);
                return false;
            }

            if (!LooksTextReadable(fullPath))
            {
                error = new CodingToolResult(
                    true,
                    false,
                    "Coding tool blocked: only text-like coding files can be included in a patch bundle.",
                    "Patch bundle preview",
                    fullPath);
                return false;
            }

            normalized.Add(new NormalizedPatchEdit(fullPath, edit.OldText, edit.NewText));
        }

        edits = normalized;
        return true;
    }

    private static async Task<PatchBundlePreparation> PreparePatchBundleAsync(
        IReadOnlyList<NormalizedPatchEdit> edits,
        string toolName,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedPatchEdit>();
        var currentTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var result = await PreparePatchEditAsync(edit, currentTexts, toolName, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                return new PatchBundlePreparation([], result.Error);
            }

            prepared.Add(result.Edit!);
        }

        return new PatchBundlePreparation(prepared, null);
    }

    private static async Task<PatchEditPreparation> PreparePatchEditAsync(
        NormalizedPatchEdit edit,
        Dictionary<string, string> currentTexts,
        string toolName,
        CancellationToken cancellationToken)
    {
        if (!ValidateEditContent(edit.OldText, "Text to replace", out var oldTextError))
        {
            return new PatchEditPreparation(null, new CodingToolResult(true, false, oldTextError, toolName, edit.FullPath));
        }

        if (edit.OldText.Length == 0)
        {
            return new PatchEditPreparation(null, new CodingToolResult(true, false, "Coding tool blocked: text to replace cannot be empty.", toolName, edit.FullPath));
        }

        if (!ValidateEditContent(edit.NewText, "Replacement text", out var newTextError))
        {
            return new PatchEditPreparation(null, new CodingToolResult(true, false, newTextError, toolName, edit.FullPath));
        }

        if (!File.Exists(edit.FullPath))
        {
            return new PatchEditPreparation(
                null,
                new CodingToolResult(
                    true,
                    false,
                    $"Coding tool blocked: patch bundle target does not exist: {edit.FullPath}",
                    toolName,
                    edit.FullPath));
        }

        var fileInfo = new FileInfo(edit.FullPath);
        if (fileInfo.Length > MaxReplaceFileCharacters)
        {
            return new PatchEditPreparation(
                null,
                new CodingToolResult(
                    true,
                    false,
                    $"Coding tool blocked: patch bundle target is too large for a safe literal patch ({fileInfo.Length} bytes).",
                    toolName,
                    edit.FullPath));
        }

        if (!currentTexts.TryGetValue(edit.FullPath, out var existing))
        {
            existing = await File.ReadAllTextAsync(edit.FullPath, cancellationToken).ConfigureAwait(false);
        }

        var count = CountOrdinalOccurrences(existing, edit.OldText);
        if (count != 1)
        {
            return new PatchEditPreparation(
                null,
                new CodingToolResult(
                    true,
                    false,
                    $"Coding tool blocked: patch bundle expected exactly one match in {edit.FullPath} but found {count}.",
                    toolName,
                    edit.FullPath));
        }

        var index = existing.IndexOf(edit.OldText, StringComparison.Ordinal);
        var updated = existing.Remove(index, edit.OldText.Length).Insert(index, edit.NewText);
        currentTexts[edit.FullPath] = updated;
        return new PatchEditPreparation(
            new PreparedPatchEdit(
                edit.FullPath,
                updated,
                BuildSnippet(existing, index, edit.OldText.Length),
                BuildSnippet(updated, index, edit.NewText.Length)),
            null);
    }

    private static async Task<CodingToolResult> CreateFileAsync(
        string fullPath,
        string? content,
        CancellationToken cancellationToken)
    {
        if (!ValidateEditContent(content, "New file content", out var error))
        {
            return new CodingToolResult(true, false, error, "File create", fullPath);
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: create file will not overwrite an existing path: {fullPath}",
                "File create",
                fullPath);
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new CodingToolResult(true, false, "Coding tool blocked: create file needs a parent directory.", "File create", fullPath);
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, content!, cancellationToken).ConfigureAwait(false);
        return new CodingToolResult(
            true,
            true,
            $"Created file: {fullPath}{Environment.NewLine}Wrote {content!.Length} character(s).",
            "File create",
            fullPath);
    }

    private static async Task<CodingToolResult> AppendFileAsync(
        string fullPath,
        string? content,
        CancellationToken cancellationToken)
    {
        if (!ValidateEditContent(content, "Append content", out var error))
        {
            return new CodingToolResult(true, false, error, "File append", fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: append target does not exist. Create the file first: {fullPath}",
                "File append",
                fullPath);
        }

        await File.AppendAllTextAsync(fullPath, content!, cancellationToken).ConfigureAwait(false);
        return new CodingToolResult(
            true,
            true,
            $"Appended to file: {fullPath}{Environment.NewLine}Added {content!.Length} character(s).",
            "File append",
            fullPath);
    }

    private static async Task<CodingToolResult> PreviewReplaceTextAsync(
        string fullPath,
        string? oldText,
        string? newText,
        CancellationToken cancellationToken)
    {
        if (!ValidateEditContent(oldText, "Text to replace", out var oldTextError))
        {
            return new CodingToolResult(true, false, oldTextError, "Patch preview", fullPath);
        }

        if (oldText!.Length == 0)
        {
            return new CodingToolResult(true, false, "Coding tool blocked: text to replace cannot be empty.", "Patch preview", fullPath);
        }

        if (!ValidateEditContent(newText, "Replacement text", out var newTextError))
        {
            return new CodingToolResult(true, false, newTextError, "Patch preview", fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: preview target does not exist: {fullPath}",
                "Patch preview",
                fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxReplaceFileCharacters)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: preview target is too large for a safe literal patch ({fileInfo.Length} bytes).",
                "Patch preview",
                fullPath);
        }

        var existing = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var count = CountOrdinalOccurrences(existing, oldText);
        if (count != 1)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: patch preview expected exactly one match but found {count}.",
                "Patch preview",
                fullPath);
        }

        var index = existing.IndexOf(oldText, StringComparison.Ordinal);
        var updated = existing.Remove(index, oldText.Length).Insert(index, newText!);
        var before = BuildSnippet(existing, index, oldText.Length);
        var after = BuildSnippet(updated, index, newText!.Length);
        var message = string.Join(
            Environment.NewLine,
            $"Patch preview for: {fullPath}",
            "No files were changed.",
            "To apply this exact change, use the confirmed replace command.",
            "Before:",
            before,
            "After:",
            after);
        return new CodingToolResult(
            true,
            true,
            message,
            "Patch preview",
            fullPath);
    }

    private static async Task<CodingToolResult> ReplaceTextAsync(
        string fullPath,
        string? oldText,
        string? newText,
        CancellationToken cancellationToken)
    {
        if (!ValidateEditContent(oldText, "Text to replace", out var oldTextError))
        {
            return new CodingToolResult(true, false, oldTextError, "File replace", fullPath);
        }

        if (oldText!.Length == 0)
        {
            return new CodingToolResult(true, false, "Coding tool blocked: text to replace cannot be empty.", "File replace", fullPath);
        }

        if (!ValidateEditContent(newText, "Replacement text", out var newTextError))
        {
            return new CodingToolResult(true, false, newTextError, "File replace", fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: replace target does not exist: {fullPath}",
                "File replace",
                fullPath);
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxReplaceFileCharacters)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: replace target is too large for a safe literal edit ({fileInfo.Length} bytes).",
                "File replace",
                fullPath);
        }

        var existing = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var count = CountOrdinalOccurrences(existing, oldText);
        if (count != 1)
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding tool blocked: literal replacement expected exactly one match but found {count}.",
                "File replace",
                fullPath);
        }

        var index = existing.IndexOf(oldText, StringComparison.Ordinal);
        var updated = existing.Remove(index, oldText.Length).Insert(index, newText!);
        await File.WriteAllTextAsync(fullPath, updated, cancellationToken).ConfigureAwait(false);
        return new CodingToolResult(
            true,
            true,
            $"Replaced text in file: {fullPath}{Environment.NewLine}Changed one literal match.",
            "File replace",
            fullPath);
    }

    private Task<CodingToolResult> OpenFileAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CodingWorkspacePolicy.TryNormalizePath(request.Path ?? string.Empty, out var fullPath))
        {
            return Task.FromResult(new CodingToolResult(true, false, "Coding tool blocked: invalid file path."));
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult(new CodingToolResult(true, false, $"Coding tool could not find file: {fullPath}"));
        }

        var notepadPlusPlus = CodingToolLocator.FindNotepadPlusPlus(_configuredNotepadPlusPlusPath);
        if (notepadPlusPlus is not null)
        {
            var arguments = request.LineNumber is > 0
                ? new[] { fullPath, $"-n{request.LineNumber.Value}" }
                : [fullPath];
            _processLauncher.Start(notepadPlusPlus, arguments, useShellExecute: false);
            return Task.FromResult(new CodingToolResult(
                true,
                true,
                BuildOpenedMessage("Opened file in Notepad++", fullPath, request.LineNumber),
                "Notepad++",
                fullPath,
                request.LineNumber));
        }

        _processLauncher.Start("notepad.exe", [fullPath], useShellExecute: false);
        return Task.FromResult(new CodingToolResult(
            true,
            true,
            BuildOpenedMessage("Opened file in Notepad", fullPath, request.LineNumber),
            "Notepad",
            fullPath,
            request.LineNumber));
    }

    private async Task<CodingToolResult> OpenLastDiagnosticAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastDotNetResult is not { Succeeded: false } lastDotNetResult)
        {
            return new CodingToolResult(
                true,
                false,
                "No failed dotnet command is available yet. Run a confirmed build or test first.",
                "Last diagnostic",
                Policy.WorkspaceRoot);
        }

        var diagnostic = ExtractDiagnosticFileReferences(lastDotNetResult.Message)
            .FirstOrDefault(reference => File.Exists(reference.Path) && Policy.IsInsideWorkspace(reference.Path));
        if (diagnostic is null)
        {
            return new CodingToolResult(
                true,
                false,
                "The last failed dotnet command did not include an openable diagnostic file inside the approved workspace.",
                "Last diagnostic",
                Policy.WorkspaceRoot);
        }

        var result = await OpenFileAsync(
            new CodingToolRequest(
                CodingToolAction.OpenFile,
                diagnostic.Path,
                diagnostic.LineNumber,
                ExplicitUserPath: false,
                UserConfirmed: true),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result with
            {
                Message = $"Opened last diagnostic file from the failed dotnet command.{Environment.NewLine}{result.Message}",
                ToolName = "Last diagnostic"
            }
            : result with
            {
                Message = $"Could not open the last diagnostic file.{Environment.NewLine}{result.Message}",
                ToolName = "Last diagnostic"
            };
    }

    private async Task<CodingToolResult> DiagnoseLastFailureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastDotNetRequest is null || _lastDotNetResult is not { Succeeded: false } lastDotNetResult)
        {
            return new CodingToolResult(
                true,
                false,
                "No failed dotnet command is available yet. Run a confirmed build, test, restore, or run command first.",
                "Last failure diagnosis",
                Policy.WorkspaceRoot);
        }

        var target = string.IsNullOrWhiteSpace(lastDotNetResult.TargetPath)
            ? _lastDotNetRequest.Path ?? Policy.WorkspaceRoot
            : lastDotNetResult.TargetPath;
        var lines = new List<string>
        {
            "Last dotnet failure diagnosis:",
            "No files were changed by this diagnostic command.",
            $"Action: {_lastDotNetRequest.Action}",
            $"Target: {target}",
            lastDotNetResult.ExitCode is null
                ? "Exit code: unavailable"
                : $"Exit code: {lastDotNetResult.ExitCode.Value}",
            "Stored command result:",
            TrimForChat(lastDotNetResult.Message, 6_000)
        };

        await AddDiagnosticFileExcerptsAsync(lines, lastDotNetResult.Message, cancellationToken).ConfigureAwait(false);

        var openableDiagnostic = ExtractDiagnosticFileReferences(lastDotNetResult.Message)
            .FirstOrDefault(reference => File.Exists(reference.Path) && Policy.IsInsideWorkspace(reference.Path));
        lines.Add("Next guarded commands:");
        if (openableDiagnostic is not null)
        {
            lines.Add("- open build error");
        }

        lines.Add("- plan fix <short description>");
        lines.Add("- suggest patch from last failure");
        lines.Add("- preview replace in file \"path\" \"old text\" with \"new text\"");
        lines.Add("- confirm apply last patch preview");
        if (!string.IsNullOrWhiteSpace(target))
        {
            lines.Add($"- confirm dotnet build \"{target}\"");
        }

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Last failure diagnosis",
            target);
    }

    private async Task<CodingToolResult> SuggestLastFailurePatchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastDotNetResult is not { Succeeded: false } lastDotNetResult)
        {
            return new CodingToolResult(
                true,
                false,
                "No failed dotnet command is available yet. Run a confirmed build or test first.",
                "Last failure patch suggestion",
                Policy.WorkspaceRoot);
        }

        var diagnostic = ExtractDiagnosticFileReferences(lastDotNetResult.Message)
            .FirstOrDefault(reference => reference.LineNumber is > 0
                                         && File.Exists(reference.Path)
                                         && Policy.IsInsideWorkspace(reference.Path));
        if (diagnostic is null)
        {
            return new CodingToolResult(
                true,
                false,
                "No deterministic patch suggestion is available because the last failure did not include a source file and line inside the approved workspace.",
                "Last failure patch suggestion",
                Policy.WorkspaceRoot);
        }

        var lines = await File.ReadAllLinesAsync(diagnostic.Path, cancellationToken).ConfigureAwait(false);
        if (diagnostic.LineNumber is null || diagnostic.LineNumber.Value > lines.Length)
        {
            return new CodingToolResult(
                true,
                false,
                "No deterministic patch suggestion is available because the diagnostic line is outside the current file.",
                "Last failure patch suggestion",
                diagnostic.Path,
                diagnostic.LineNumber);
        }

        if (!TryBuildDeterministicFailurePatch(
                lastDotNetResult.Message,
                lines,
                diagnostic.LineNumber.Value,
                out var diagnosticLabel,
                out var oldText,
                out var newText,
                out var refusal))
        {
            return new CodingToolResult(
                true,
                false,
                refusal,
                "Last failure patch suggestion",
                diagnostic.Path,
                diagnostic.LineNumber);
        }

        var previewRequest = new CodingToolRequest(
            CodingToolAction.PreviewReplaceText,
            diagnostic.Path,
            diagnostic.LineNumber,
            ExplicitUserPath: false,
            UserConfirmed: false,
            Content: oldText,
            Replacement: newText);
        var preview = await PreviewReplaceTextAsync(previewRequest, cancellationToken).ConfigureAwait(false);
        if (preview.Succeeded)
        {
            _lastPatchPreviewRequest = previewRequest;
        }

        return preview with
        {
            Message = preview.Succeeded
                ? $"Suggested patch from last failure. No files were changed.{Environment.NewLine}Diagnostic: {diagnosticLabel}{Environment.NewLine}To apply this pending preview after review, use: confirm apply last patch preview{Environment.NewLine}{preview.Message}"
                : $"No deterministic patch suggestion was stored. No files were changed.{Environment.NewLine}{preview.Message}",
            ToolName = "Last failure patch suggestion"
        };
    }

    private static bool TryBuildDeterministicFailurePatch(
        string diagnosticText,
        IReadOnlyList<string> lines,
        int lineNumber,
        out string diagnosticLabel,
        out string oldText,
        out string newText,
        out string refusal)
    {
        diagnosticLabel = string.Empty;
        oldText = string.Empty;
        newText = string.Empty;
        refusal = "No deterministic patch suggestion is available for this diagnostic yet. Ali can currently preview simple CS1002 semicolon and CS1513 closing-brace fixes only.";

        var lineIndex = lineNumber - 1;
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            refusal = "No deterministic patch suggestion is available because the diagnostic line is outside the current file.";
            return false;
        }

        if (diagnosticText.Contains("CS1002", StringComparison.OrdinalIgnoreCase)
            && diagnosticText.Contains("; expected", StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildMissingSemicolonPatch(lines[lineIndex], out diagnosticLabel, out oldText, out newText, out refusal);
        }

        if (diagnosticText.Contains("CS1513", StringComparison.OrdinalIgnoreCase)
            && diagnosticText.Contains("} expected", StringComparison.OrdinalIgnoreCase))
        {
            return TryBuildMissingClosingBracePatch(lines, lineIndex, out diagnosticLabel, out oldText, out newText, out refusal);
        }

        return false;
    }

    private static bool TryBuildMissingSemicolonPatch(
        string oldLine,
        out string diagnosticLabel,
        out string oldText,
        out string newText,
        out string refusal)
    {
        diagnosticLabel = "CS1002 ; expected";
        oldText = oldLine;
        newText = string.Empty;
        refusal = string.Empty;
        var trimmedEnd = oldLine.TrimEnd();
        if (trimmedEnd.EndsWith(";", StringComparison.Ordinal)
            || trimmedEnd.EndsWith("{", StringComparison.Ordinal)
            || trimmedEnd.EndsWith("}", StringComparison.Ordinal))
        {
            refusal = "No deterministic patch suggestion is available because the diagnostic line does not look like a simple missing semicolon case.";
            return false;
        }

        var trailingWhitespace = oldLine[trimmedEnd.Length..];
        newText = trimmedEnd + ";" + trailingWhitespace;
        return true;
    }

    private static bool TryBuildMissingClosingBracePatch(
        IReadOnlyList<string> lines,
        int lineIndex,
        out string diagnosticLabel,
        out string oldText,
        out string newText,
        out string refusal)
    {
        diagnosticLabel = "CS1513 } expected";
        oldText = lines[lineIndex];
        newText = string.Empty;
        refusal = string.Empty;

        var lastNonEmptyIndex = lines
            .Select((line, index) => new { line, index })
            .LastOrDefault(item => !string.IsNullOrWhiteSpace(item.line))
            ?.index;
        if (lastNonEmptyIndex != lineIndex)
        {
            refusal = "No deterministic patch suggestion is available because the closing-brace diagnostic is not on the last non-empty line.";
            return false;
        }

        var openBraces = lines.Sum(line => line.Count(character => character == '{'));
        var closeBraces = lines.Sum(line => line.Count(character => character == '}'));
        if (openBraces - closeBraces != 1)
        {
            refusal = "No deterministic patch suggestion is available because the file does not appear to be missing exactly one closing brace.";
            return false;
        }

        if (oldText.Trim().Equals("}", StringComparison.Ordinal))
        {
            refusal = "No deterministic patch suggestion is available because the diagnostic line already contains only a closing brace.";
            return false;
        }

        var indent = oldText[..(oldText.Length - oldText.TrimStart().Length)];
        newText = oldText + Environment.NewLine + indent + "}";
        return true;
    }

    private Task<CodingToolResult> OpenSolutionAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath;
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            if (!Directory.Exists(Policy.WorkspaceRoot))
            {
                return Task.FromResult(new CodingToolResult(
                    true,
                    false,
                    $"Coding workspace does not exist yet: {Policy.WorkspaceRoot}",
                    "Visual Studio",
                    Policy.WorkspaceRoot));
            }

            if (!TryFindPrimaryProjectOrSolution(Policy.WorkspaceRoot, out fullPath))
            {
                return Task.FromResult(new CodingToolResult(
                    true,
                    false,
                    $"Coding tool could not find a solution or project under: {Policy.WorkspaceRoot}",
                    "Visual Studio",
                    Policy.WorkspaceRoot));
            }
        }
        else if (!CodingWorkspacePolicy.TryNormalizePath(request.Path, out var normalizedPath))
        {
            return Task.FromResult(new CodingToolResult(true, false, "Coding tool blocked: invalid solution path."));
        }
        else
        {
            fullPath = normalizedPath;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return Task.FromResult(new CodingToolResult(true, false, $"Coding tool could not find solution target: {fullPath}"));
        }

        var visualStudio = CodingToolLocator.FindVisualStudio(_configuredVisualStudioPath);
        if (visualStudio is not null)
        {
            _processLauncher.Start(visualStudio, [fullPath], useShellExecute: false);
            return Task.FromResult(new CodingToolResult(
                true,
                true,
                $"Opened solution in Visual Studio: {fullPath}",
                "Visual Studio",
                fullPath));
        }

        _processLauncher.Start(fullPath, [], useShellExecute: true);
        return Task.FromResult(new CodingToolResult(
            true,
            true,
            $"Opened solution with the default Windows handler: {fullPath}",
            "Windows default app",
            fullPath));
    }

    private async Task<CodingToolResult> RunDotNetCommandAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!ResolveDotNetTarget(request, out var targetPath, out var workingDirectory, out var error))
        {
            return new CodingToolResult(true, false, error, "dotnet", request.Path);
        }

        if (request.Action == CodingToolAction.AddPackage && !TryValidatePackageInstall(request, out var packageError))
        {
            return new CodingToolResult(true, false, packageError, "dotnet", targetPath);
        }

        var arguments = BuildDotNetArguments(request, targetPath, workingDirectory);
        var run = await _commandRunner.RunAsync(
            "dotnet",
            arguments,
            workingDirectory,
            DotNetCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var verb = request.Action switch
        {
            CodingToolAction.Build => "Build",
            CodingToolAction.Test => "Test",
            CodingToolAction.Restore => "Restore",
            CodingToolAction.ListOutdatedPackages => "Package update check",
            CodingToolAction.AddPackage => "Package install",
            _ => "Run"
        };
        var output = MergeCommandOutput(run);
        var status = run.TimedOut
            ? $"{verb} timed out after {DotNetCommandTimeout.TotalSeconds:0} seconds."
            : run.ExitCode == 0
                ? $"{verb} passed."
                : $"{verb} failed with exit code {run.ExitCode}.";
        var commandLine = $"dotnet {string.Join(" ", arguments)}";
        var diagnosticSummary = BuildDotNetDiagnosticSummary(request.Action, run, output);
        var outputBlock = string.IsNullOrWhiteSpace(diagnosticSummary)
            ? TrimForChat(output, MaxCommandOutputCharacters)
            : $"{diagnosticSummary}{Environment.NewLine}{TrimForChat(output, MaxCommandOutputCharacters)}";
        var result = new CodingToolResult(
            true,
            run.ExitCode == 0 && !run.TimedOut,
            $"{status}{Environment.NewLine}Command: {commandLine}{Environment.NewLine}Working directory: {workingDirectory}{Environment.NewLine}{outputBlock}",
            "dotnet",
            targetPath,
            ExitCode: run.ExitCode);
        StoreLastDotNetResult(request, result);
        return result;
    }

    private async Task<CodingToolResult> RunGitCommandAsync(
        CodingToolRequest request,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Policy.WorkspaceRoot;
        if (!Directory.Exists(workingDirectory))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {workingDirectory}",
                "git",
                workingDirectory);
        }

        if (!TryBuildGitArguments(request, out var arguments, out var validationError))
        {
            return new CodingToolResult(true, false, validationError, "git", workingDirectory);
        }

        if (request.Action == CodingToolAction.GitMerge)
        {
            var mergeGuard = await EnsureCleanGitWorkingTreeAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (mergeGuard is not null)
            {
                return mergeGuard;
            }
        }

        var run = await _commandRunner.RunAsync(
            "git",
            arguments,
            workingDirectory,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var output = MergeCommandOutput(run);
        var actionName = request.Action.ToString();
        var status = run.TimedOut
            ? $"{actionName} timed out after {GitCommandTimeout.TotalSeconds:0} seconds."
            : run.ExitCode == 0
                ? $"{actionName} completed."
                : $"{actionName} failed with exit code {run.ExitCode}.";
        return new CodingToolResult(
            true,
            run.ExitCode == 0 && !run.TimedOut,
            $"{status}{Environment.NewLine}Command: git {string.Join(" ", arguments)}{Environment.NewLine}Working directory: {workingDirectory}{Environment.NewLine}{TrimForChat(output, MaxCommandOutputCharacters)}",
            "git",
            workingDirectory,
            ExitCode: run.ExitCode);
    }

    private async Task<CodingToolResult> ReviewCurrentChangesAsync(CancellationToken cancellationToken)
    {
        var workingDirectory = Policy.WorkspaceRoot;
        if (!Directory.Exists(workingDirectory))
        {
            return new CodingToolResult(
                true,
                false,
                $"Coding workspace does not exist yet: {workingDirectory}",
                "git",
                workingDirectory);
        }

        var status = await _commandRunner.RunAsync(
            "git",
            ["status", "--short", "--branch"],
            workingDirectory,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var nameStatus = await _commandRunner.RunAsync(
            "git",
            ["diff", "--name-status", "HEAD"],
            workingDirectory,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var stat = await _commandRunner.RunAsync(
            "git",
            ["diff", "--stat", "HEAD"],
            workingDirectory,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var check = await _commandRunner.RunAsync(
            "git",
            ["diff", "--check", "HEAD"],
            workingDirectory,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);

        var statusText = MergeCommandOutput(status);
        if (status.ExitCode != 0 || status.TimedOut)
        {
            return new CodingToolResult(
                true,
                false,
                $"Change review could not read git status.{Environment.NewLine}{TrimForChat(statusText, MaxCommandOutputCharacters)}",
                "git",
                workingDirectory,
                ExitCode: status.ExitCode);
        }

        var statusLines = SplitNonEmptyLines(status.StandardOutput);
        var branch = statusLines.FirstOrDefault(line => line.StartsWith("##", StringComparison.Ordinal)) ?? "## unknown";
        var changeLines = statusLines
            .Where(line => !line.StartsWith("##", StringComparison.Ordinal))
            .ToList();
        var changedFiles = ExtractChangedFilePaths(changeLines, nameStatus.StandardOutput);
        var stagedCount = changeLines.Count(line => line.Length >= 2 && line[0] != ' ' && line[0] != '?');
        var unstagedCount = changeLines.Count(line => line.Length >= 2
            && line[1] != ' '
            && !line.StartsWith("??", StringComparison.Ordinal));
        var untrackedCount = changeLines.Count(line => line.StartsWith("??", StringComparison.Ordinal));
        var deletedCount = changeLines.Count(line => line.StartsWith(" D", StringComparison.Ordinal) || line.StartsWith("D", StringComparison.Ordinal));
        var renamedCount = SplitNonEmptyLines(nameStatus.StandardOutput)
            .Count(line => line.StartsWith("R", StringComparison.OrdinalIgnoreCase));
        var projectFiles = changedFiles
            .Where(IsProjectOrDependencyFile)
            .ToList();
        var sourceFiles = changedFiles
            .Where(IsSourceFile)
            .ToList();
        var testFiles = changedFiles
            .Where(IsTestFile)
            .ToList();

        var lines = new List<string>
        {
            "Current Changes Review",
            $"Workspace: {workingDirectory}",
            $"Branch: {branch.TrimStart('#', ' ')}",
            $"Changed files: {changedFiles.Count}",
            $"Staged: {stagedCount}",
            $"Unstaged: {unstagedCount}",
            $"Untracked: {untrackedCount}"
        };

        if (changedFiles.Count == 0)
        {
            lines.Add("Status: clean");
            lines.Add("Next: no commit is needed unless generated receipts or settings changed outside Git.");
            return new CodingToolResult(true, true, string.Join(Environment.NewLine, lines), "git", workingDirectory, ExitCode: 0);
        }

        lines.Add(string.Empty);
        lines.Add("Files");
        foreach (var path in changedFiles.Take(20))
        {
            lines.Add($"- {path}");
        }

        if (changedFiles.Count > 20)
        {
            lines.Add($"- ...and {changedFiles.Count - 20} more");
        }

        lines.Add(string.Empty);
        lines.Add("Risk Checks");
        lines.Add(check.ExitCode == 0 && !check.TimedOut
            ? "- Diff check: Good"
            : $"- Diff check: Needs attention - {TrimForChat(MergeCommandOutput(check), 800)}");

        if (projectFiles.Count > 0)
        {
            lines.Add($"- Project/dependency files changed: {string.Join(", ", projectFiles.Take(6))}");
        }

        if (sourceFiles.Count > 0 && testFiles.Count == 0)
        {
            lines.Add("- Source files changed without obvious test file changes.");
        }

        if (deletedCount > 0)
        {
            lines.Add($"- Deleted files detected: {deletedCount}");
        }

        if (renamedCount > 0)
        {
            lines.Add($"- Renamed files detected: {renamedCount}");
        }

        if (changedFiles.Count > 12)
        {
            lines.Add("- Large change set: review by feature area before commit.");
        }

        if (sourceFiles.Count == 0 && projectFiles.Count == 0)
        {
            lines.Add("- No source or project files detected; this looks like docs, config, receipts, or assets.");
        }

        lines.Add(string.Empty);
        lines.Add("Diff Stat");
        lines.Add(string.IsNullOrWhiteSpace(stat.StandardOutput)
            ? "No diff stat output."
            : TrimForChat(stat.StandardOutput.Trim(), 1_500));

        lines.Add(string.Empty);
        lines.Add("Next");
        lines.Add(projectFiles.Count > 0
            ? "- Run restore/build/tests before commit because project/dependency files changed."
            : "- Run build/tests that match the changed source area before commit.");
        lines.Add("- Use git diff for line-level review, then commit only after validation passes.");

        var succeeded = nameStatus.ExitCode == 0
            && stat.ExitCode == 0
            && check.ExitCode == 0
            && !nameStatus.TimedOut
            && !stat.TimedOut
            && !check.TimedOut;
        return new CodingToolResult(
            true,
            succeeded,
            string.Join(Environment.NewLine, lines),
            "git",
            workingDirectory,
            ExitCode: succeeded ? 0 : check.ExitCode);
    }

    private static IReadOnlyList<string> SplitNonEmptyLines(string text) =>
        text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private static IReadOnlyList<string> ExtractChangedFilePaths(
        IReadOnlyList<string> statusLines,
        string nameStatusOutput)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitNonEmptyLines(nameStatusOutput))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                paths.Add(parts[^1]);
            }
        }

        foreach (var line in statusLines)
        {
            if (line.Length < 4)
            {
                continue;
            }

            var path = line[3..].Trim();
            var renameArrow = path.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (renameArrow >= 0)
            {
                path = path[(renameArrow + 4)..].Trim();
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path.Trim('"'));
            }
        }

        return paths.ToList();
    }

    private static bool IsProjectOrDependencyFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("packages.config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestFile(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}test", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.AltDirectorySeparatorChar}test", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase);

    private async Task AppendLogAsync(
        CodingToolRequest request,
        CodingToolResult result,
        CodingToolPermission permission,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_actionLogPath)!);
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            action = request.Action.ToString(),
            request.Path,
            request.LineNumber,
            request.ExplicitUserPath,
            request.UserConfirmed,
            request.Query,
            contentLength = request.Content?.Length,
            replacementLength = request.Replacement?.Length,
            patchEditCount = request.PatchEdits?.Count,
            permission = permission.Kind.ToString(),
            permission.Reason,
            result.Succeeded,
            result.ToolName,
            result.TargetPath,
            result.ExitCode,
            result.Message
        });
        await File.AppendAllTextAsync(_actionLogPath, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildOpenedMessage(string prefix, string path, int? lineNumber) =>
        lineNumber is > 0
            ? $"{prefix}: {path} at line {lineNumber.Value}"
            : $"{prefix}: {path}";

    private IEnumerable<string> EnumerateWorkspaceFiles()
    {
        var pending = new Queue<string>();
        pending.Enqueue(Policy.WorkspaceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory);
                files = Directory.EnumerateFiles(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                if (!ShouldSkipPath(childDirectory))
                {
                    pending.Enqueue(childDirectory);
                }
            }

            foreach (var file in files)
            {
                if (!ShouldSkipPath(file))
                {
                    yield return file;
                }
            }
        }
    }

    private void AddPathSection(List<string> lines, string title, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        lines.Add($"{title}:");
        lines.AddRange(paths
            .Take(MaxWorkspaceSummaryEntries)
            .Select(path => $"- {RelativeToWorkspace(path)}"));
        if (paths.Count > MaxWorkspaceSummaryEntries)
        {
            lines.Add($"- ...{paths.Count - MaxWorkspaceSummaryEntries} more omitted.");
        }
    }

    private void AddProjectSection(List<string> lines, IReadOnlyList<string> projectFiles)
    {
        if (projectFiles.Count == 0)
        {
            return;
        }

        lines.Add("Project files:");
        foreach (var summary in projectFiles.Take(MaxWorkspaceSummaryEntries).Select(ReadProjectSummary))
        {
            lines.Add($"- {summary.RelativePath}");
            lines.Add($"  Role: {summary.ProjectRole}");
            if (summary.TargetFrameworks.Count > 0)
            {
                lines.Add($"  Target: {string.Join(", ", summary.TargetFrameworks)}");
            }

            if (summary.PackageReferences.Count > 0)
            {
                lines.Add($"  Packages: {string.Join(", ", summary.PackageReferences.Take(8))}");
            }

            if (summary.ProjectReferences.Count > 0)
            {
                lines.Add($"  Project refs: {string.Join(", ", summary.ProjectReferences.Take(8))}");
            }

            if (!string.IsNullOrWhiteSpace(summary.Warning))
            {
                lines.Add($"  Warning: {summary.Warning}");
            }
        }

        if (projectFiles.Count > MaxWorkspaceSummaryEntries)
        {
            lines.Add($"- ...{projectFiles.Count - MaxWorkspaceSummaryEntries} more omitted.");
        }
    }

    private ProjectSummary ReadProjectSummary(string projectFile)
    {
        var relativePath = RelativeToWorkspace(projectFile);
        try
        {
            var document = XDocument.Load(projectFile);
            var targetFrameworks = document
                .Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(element => SplitSemicolonList(element.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var packageReferences = document
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(FormatPackageReference)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projectReferences = document
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => FormatProjectReference(projectFile, element))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var projectDirectory = Path.GetDirectoryName(projectFile) ?? Policy.WorkspaceRoot;
            var csharpSourceCount = CountProjectFiles(projectDirectory, ".cs");
            var xamlFileCount = CountProjectFiles(projectDirectory, ".xaml");
            var jsonFileCount = CountProjectFiles(projectDirectory, ".json");
            var outputTypes = document
                .Descendants()
                .Where(element => element.Name.LocalName == "OutputType")
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var useWpf = ReadBooleanProperty(document, "UseWPF");
            var useWindowsForms = ReadBooleanProperty(document, "UseWindowsForms");
            var projectRole = ClassifyProjectRole(
                relativePath,
                outputTypes,
                packageReferences,
                useWpf,
                useWindowsForms,
                xamlFileCount);

            return new ProjectSummary(
                relativePath,
                projectRole,
                targetFrameworks,
                packageReferences,
                projectReferences,
                csharpSourceCount,
                xamlFileCount,
                jsonFileCount,
                Warning: null);
        }
        catch (IOException ex)
        {
            return ProjectSummary.WithWarning(relativePath, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ProjectSummary.WithWarning(relativePath, ex.Message);
        }
        catch (System.Xml.XmlException ex)
        {
            return ProjectSummary.WithWarning(relativePath, ex.Message);
        }
    }

    private string RelativeToWorkspace(string path) =>
        Path.GetRelativePath(Policy.WorkspaceRoot, path);

    private static void AddArchitectureDependencySections(List<string> lines, IReadOnlyList<ProjectSummary> summaries)
    {
        if (summaries.Count == 0)
        {
            return;
        }

        var knownProjects = summaries
            .Select(summary => summary.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = summaries
            .SelectMany(summary => summary.ProjectReferences
                .Where(reference => knownProjects.Contains(reference))
                .Select(reference => new ProjectDependency(summary.RelativePath, reference)))
            .OrderBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.To, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lines.Add("Project dependency graph:");
        if (edges.Count == 0)
        {
            lines.Add("- No project-to-project references found among listed projects.");
        }
        else
        {
            lines.AddRange(edges.Take(MaxWorkspaceSummaryEntries).Select(edge => $"- {edge.From} -> {edge.To}"));
            if (edges.Count > MaxWorkspaceSummaryEntries)
            {
                lines.Add($"- ...{edges.Count - MaxWorkspaceSummaryEntries} more dependency edge(s) omitted.");
            }
        }

        var buildOrder = EstimateBuildOrder(summaries, edges);
        if (buildOrder.Count > 0)
        {
            lines.Add("Estimated project build order:");
            lines.AddRange(buildOrder.Take(MaxWorkspaceSummaryEntries).Select((project, index) => $"{index + 1}. {project}"));
            if (buildOrder.Count > MaxWorkspaceSummaryEntries)
            {
                lines.Add($"...{buildOrder.Count - MaxWorkspaceSummaryEntries} more project(s) omitted.");
            }
        }

        var roleCounts = summaries
            .GroupBy(summary => summary.ProjectRole, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToList();
        lines.Add($"Project role summary: {string.Join(", ", roleCounts)}");

        var appProjects = summaries
            .Where(summary => summary.ProjectRole.Contains("app", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();
        var testProjects = summaries
            .Where(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase))
            .Select(summary => summary.RelativePath)
            .ToList();
        if (appProjects.Count > 0)
        {
            lines.Add($"App/UI entry projects: {string.Join(", ", appProjects.Take(6))}");
        }

        if (testProjects.Count > 0)
        {
            lines.Add($"Test projects: {string.Join(", ", testProjects.Take(6))}");
        }
    }

    private List<string> FindProjectMarkers(IReadOnlyList<string> files)
    {
        var markerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "README.md",
            "package.json",
            "pnpm-lock.yaml",
            "yarn.lock",
            "package-lock.json",
            "pyproject.toml",
            "requirements.txt",
            "Dockerfile",
            "docker-compose.yml",
            "Directory.Build.props",
            "global.json"
        };

        return files
            .Where(file => markerNames.Contains(Path.GetFileName(file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(RelativeToWorkspace)
            .ToList();
    }

    private List<string> DetectStackSignals(
        IReadOnlyList<string> files,
        IReadOnlyList<ProjectSummary> summaries)
    {
        var signals = new List<string>();
        if (summaries.Count > 0)
        {
            signals.Add(".NET/C#");
        }

        if (summaries.Any(summary => summary.ProjectRole.Contains("desktop", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("WPF/desktop UI");
        }

        if (summaries.Any(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add(".NET tests");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("Node/JavaScript");
        }

        if (files.Any(file => file.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                              || file.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("React-style UI");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(file).Equals("requirements.txt", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("Python");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(file).Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("Docker");
        }

        if (files.Any(file => file.Contains($"{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}workflows{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("GitHub Actions CI");
        }

        return signals
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(signal => signal, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> DetectStyleSignals(
        IReadOnlyList<string> files,
        IReadOnlyList<ProjectSummary> summaries)
    {
        var signals = new List<string>();
        if (files.Any(file => Path.GetFileName(file).EndsWith("ViewModel.cs", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("MVVM naming");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("App.xaml", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("WPF app startup");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("central MSBuild props");
        }

        if (summaries.SelectMany(summary => summary.PackageReferences).Any(package => package.StartsWith("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("CommunityToolkit MVVM");
        }

        if (files.Any(file => file.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("editorconfig style rules");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("README.md", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("README-guided project");
        }

        return signals
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(signal => signal, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> DiscoverBuildCommands(
        IReadOnlyList<string> files,
        IReadOnlyList<ProjectSummary> summaries,
        string? primaryTarget)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(primaryTarget))
        {
            commands.Add($"confirm dotnet build \"{primaryTarget}\"");
        }
        else if (summaries.Count > 0)
        {
            commands.Add($"confirm dotnet build \"{Path.Combine(Policy.WorkspaceRoot, summaries[0].RelativePath)}\"");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("npm run build");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("python -m build");
        }

        return commands.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> DiscoverTestCommands(
        IReadOnlyList<string> files,
        IReadOnlyList<ProjectSummary> summaries,
        string? primaryTarget)
    {
        var commands = new List<string>();
        if (summaries.Any(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add(!string.IsNullOrWhiteSpace(primaryTarget)
                ? $"confirm dotnet test \"{primaryTarget}\""
                : $"confirm dotnet test \"{Path.Combine(Policy.WorkspaceRoot, summaries.First(summary => summary.ProjectRole.Contains("test", StringComparison.OrdinalIgnoreCase)).RelativePath)}\"");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("npm test");
        }

        if (files.Any(file => Path.GetFileName(file).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(file).Equals("pytest.ini", StringComparison.OrdinalIgnoreCase)))
        {
            commands.Add("pytest");
        }

        return commands.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatInlineList(IEnumerable<string> items)
    {
        var list = items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        return list.Count == 0 ? "none detected" : string.Join(", ", list);
    }

    private static void AddSelectedLines(
        List<string> target,
        string text,
        int limit,
        params string[] prefixes)
    {
        var selected = SplitNonEmptyLines(text)
            .Where(line => prefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();
        if (selected.Count == 0)
        {
            target.Add("- no matching summary lines");
            return;
        }

        target.AddRange(selected.Select(line => "- " + line.TrimStart('-', ' ')));
    }

    private async Task<IReadOnlyList<string>> ReadChangedFilesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return [];
        }

        var status = await _commandRunner.RunAsync(
            "git",
            ["diff", "--name-only", "HEAD"],
            Policy.WorkspaceRoot,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0 || status.TimedOut)
        {
            return [];
        }

        return SplitNonEmptyLines(status.StandardOutput)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    private RoslynWorkspaceModel BuildRoslynWorkspaceModel(IReadOnlyList<string> files, int maxFiles)
    {
        var trees = new List<SyntaxTree>();
        foreach (var file in files.Where(File.Exists).Take(maxFiles))
        {
            var text = SafeReadText(file);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            trees.Add(CSharpSyntaxTree.ParseText(text, path: file));
        }

        var compilation = CSharpCompilation.Create(
            "AliSemanticWorkspace",
            trees,
            GetTrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new RoslynWorkspaceModel(compilation, trees);
    }

    private static IReadOnlyList<MetadataReference> GetTrustedPlatformReferences()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            return [];
        }

        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
    }

    private List<SemanticSymbolHit> FindSemanticSymbolHits(RoslynWorkspaceModel workspace, string query, int limit)
    {
        var hits = new List<SemanticSymbolHit>();
        foreach (var tree in workspace.Trees)
        {
            if (hits.Count >= limit)
            {
                break;
            }

            var semanticModel = workspace.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                if (hits.Count >= limit)
                {
                    break;
                }

                if (TryCreateSemanticDeclarationHit(semanticModel, node, query, out var declarationHit))
                {
                    hits.Add(declarationHit);
                    continue;
                }

                if (node is InvocationExpressionSyntax invocation)
                {
                    var symbol = semanticModel.GetSymbolInfo(invocation).Symbol;
                    if (symbol is not null && SymbolMatches(symbol, query))
                    {
                        hits.Add(CreateSemanticHit(symbol, invocation, "reference"));
                    }
                }
                else if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;
                    if (symbol is not null && SymbolMatches(symbol, query))
                    {
                        hits.Add(CreateSemanticHit(symbol, identifier, "reference"));
                    }
                }
            }
        }

        return hits
            .DistinctBy(hit => $"{hit.Source}|{hit.Display}|{hit.Path}|{hit.LineNumber}", StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static bool TryCreateSemanticDeclarationHit(SemanticModel semanticModel, SyntaxNode node, string query, out SemanticSymbolHit hit)
    {
        hit = new SemanticSymbolHit(string.Empty, string.Empty, string.Empty, string.Empty, 0, string.Empty);
        ISymbol? symbol = node switch
        {
            ClassDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            RecordDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            InterfaceDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            EnumDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            StructDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            MethodDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            ConstructorDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            PropertyDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration),
            FieldDeclarationSyntax declaration => declaration.Declaration.Variables.Select(variable => semanticModel.GetDeclaredSymbol(variable)).FirstOrDefault(symbol => symbol is not null && SymbolMatches(symbol, query)),
            _ => null
        };

        if (symbol is null || !SymbolMatches(symbol, query))
        {
            return false;
        }

        hit = CreateSemanticHit(symbol, node, "declaration");
        return true;
    }

    private static bool SymbolMatches(ISymbol symbol, string query) =>
        symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat).Contains(query, StringComparison.OrdinalIgnoreCase);

    private static SemanticSymbolHit CreateSemanticHit(ISymbol symbol, SyntaxNode node, string source)
    {
        var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return new SemanticSymbolHit(
            symbol.Name,
            symbol.Kind.ToString(),
            display,
            node.SyntaxTree.FilePath,
            GetLineNumber(node),
            source);
    }

    private static string FormatSemanticHit(SemanticSymbolHit hit) =>
        $"- {hit.Kind} {hit.Display} ({hit.Source}) at {Path.GetFileName(hit.Path)}:{hit.LineNumber}";

    private static int GetLineNumber(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    private string? ToAbsoluteWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidate = Path.IsPathFullyQualified(path)
            ? path
            : Path.Combine(Policy.WorkspaceRoot, path);
        try
        {
            var fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
            return Policy.IsInsideWorkspace(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractMeaningfulGoalTerms(string goal)
    {
        return goal.Split(ContextTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .Where(term => !ContextStopWords.Contains(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CompilerDiagnosticInfo ParseCompilerDiagnostic(string diagnostic)
    {
        var line = SplitNonEmptyLines(diagnostic).FirstOrDefault(candidate => candidate.Contains(" CS", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains(": error ", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
            ?? diagnostic;
        string? path = null;
        int? lineNumber = null;
        var pathMarker = line.IndexOf(".cs(", StringComparison.OrdinalIgnoreCase);
        if (pathMarker >= 0)
        {
            path = line[..(pathMarker + 3)].Trim();
            var close = line.IndexOf(')', pathMarker);
            if (close > pathMarker)
            {
                var location = line[(pathMarker + 4)..close];
                var first = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (int.TryParse(first, out var parsedLine))
                {
                    lineNumber = parsedLine;
                }
            }
        }

        string? code = ExtractCSharpDiagnosticCode(line);

        return new CompilerDiagnosticInfo(path, lineNumber, code);
    }

    private static string? ExtractCSharpDiagnosticCode(string line)
    {
        for (var index = 0; index < line.Length - 5; index++)
        {
            if ((line[index] == 'C' || line[index] == 'c')
                && (line[index + 1] == 'S' || line[index + 1] == 's')
                && char.IsDigit(line[index + 2])
                && char.IsDigit(line[index + 3])
                && char.IsDigit(line[index + 4])
                && char.IsDigit(line[index + 5]))
            {
                return line.Substring(index, 6).ToUpperInvariant();
            }
        }

        return null;
    }

    private string? FindEnclosingSymbolAtLine(string file, int? lineNumber)
    {
        if (lineNumber is null || !File.Exists(file))
        {
            return null;
        }

        var text = SafeReadText(file);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tree = CSharpSyntaxTree.ParseText(text, path: file);
        var root = tree.GetRoot();
        var matchingMember = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(member =>
            {
                var span = tree.GetLineSpan(member.Span);
                var start = span.StartLinePosition.Line + 1;
                var end = span.EndLinePosition.Line + 1;
                return lineNumber.Value >= start && lineNumber.Value <= end;
            })
            .OrderBy(member => member.Span.Length)
            .FirstOrDefault();
        return matchingMember switch
        {
            MethodDeclarationSyntax method => $"method {method.Identifier.ValueText}",
            ConstructorDeclarationSyntax constructor => $"constructor {constructor.Identifier.ValueText}",
            PropertyDeclarationSyntax property => $"property {property.Identifier.ValueText}",
            ClassDeclarationSyntax type => $"class {type.Identifier.ValueText}",
            RecordDeclarationSyntax type => $"record {type.Identifier.ValueText}",
            _ => null
        };
    }
    private IReadOnlyList<string> GetCSharpFiles() => Directory.Exists(Policy.WorkspaceRoot)
        ? EnumerateWorkspaceFiles().Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(5_000).ToList()
        : [];

    private IReadOnlyList<string> GetXamlFiles() => Directory.Exists(Policy.WorkspaceRoot)
        ? EnumerateWorkspaceFiles().Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).Take(1_000).ToList()
        : [];

    private List<CSharpCallEdge> BuildRoslynCallGraph(IReadOnlyList<string> files, int limit)
    {
        var edges = new List<CSharpCallEdge>();
        foreach (var file in files.Where(File.Exists))
        {
            if (edges.Count >= limit)
            {
                break;
            }

            var text = SafeReadText(file);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(text).GetRoot();
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (edges.Count >= limit)
                {
                    break;
                }

                var caller = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
                    ?? invocation.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
                    ?? "<initializer>";
                var callee = ExtractInvocationName(invocation.Expression);
                if (string.IsNullOrWhiteSpace(callee))
                {
                    continue;
                }

                var lineNumber = text[..Math.Min(invocation.SpanStart, text.Length)].Count(character => character == '\n') + 1;
                edges.Add(new CSharpCallEdge(caller, callee, file, lineNumber));
            }
        }

        return edges
            .DistinctBy(edge => $"{edge.Caller}|{edge.Callee}|{edge.Path}|{edge.LineNumber}", StringComparer.Ordinal)
            .ToList();
    }

    private static string ExtractInvocationName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => expression.ToString()
    };
    private List<CSharpSymbolInfo> BuildRoslynSymbolIndex(IReadOnlyList<string> files, int limit)
    {
        var symbols = new List<CSharpSymbolInfo>();
        foreach (var file in files.Where(File.Exists))
        {
            if (symbols.Count >= limit)
            {
                break;
            }

            var text = SafeReadText(file);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(text).GetRoot();
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var node in root.DescendantNodes())
            {
                if (symbols.Count >= limit)
                {
                    break;
                }

                if (TryCreateRoslynSymbol(file, text, node, out var symbol))
                {
                    symbols.Add(symbol);
                }
            }
        }

        return symbols;
    }

    private static bool TryCreateRoslynSymbol(string file, string text, SyntaxNode node, out CSharpSymbolInfo symbol)
    {
        symbol = new CSharpSymbolInfo(string.Empty, string.Empty, file, 0);
        string kind;
        string name;
        SyntaxToken identifier;
        switch (node)
        {
            case ClassDeclarationSyntax declaration:
                kind = "class";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            case RecordDeclarationSyntax declaration:
                kind = "record";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            case InterfaceDeclarationSyntax declaration:
                kind = "interface";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            case EnumDeclarationSyntax declaration:
                kind = "enum";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            case StructDeclarationSyntax declaration:
                kind = "struct";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            case MethodDeclarationSyntax declaration:
                kind = "method";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            case PropertyDeclarationSyntax declaration:
                kind = "property";
                name = declaration.Identifier.ValueText;
                identifier = declaration.Identifier;
                break;
            default:
                return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lineNumber = text[..Math.Min(identifier.SpanStart, text.Length)].Count(character => character == '\n') + 1;
        symbol = new CSharpSymbolInfo(kind, name, file, lineNumber);
        return true;
    }

    private static List<string> ExtractXamlBindingNames(string text, bool commandOnly)
    {
        var names = new List<string>();
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var bindingIndex = text.IndexOf("{Binding", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (bindingIndex < 0)
            {
                break;
            }

            var endIndex = text.IndexOf('}', bindingIndex);
            if (endIndex < 0)
            {
                break;
            }

            var prefixStart = Math.Max(0, bindingIndex - 48);
            var prefix = text[prefixStart..bindingIndex];
            var bindingText = text[(bindingIndex + "{Binding".Length)..endIndex].Trim();
            var name = CleanBindingName(bindingText);
            if (!string.IsNullOrWhiteSpace(name)
                && (!commandOnly || prefix.Contains("Command=\"", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Command", StringComparison.Ordinal)))
            {
                names.Add(name);
            }

            searchIndex = endIndex + 1;
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string CleanBindingName(string bindingText)
    {
        if (string.IsNullOrWhiteSpace(bindingText))
        {
            return string.Empty;
        }

        var path = bindingText;
        if (path.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
        {
            path = path[5..];
        }

        var comma = path.IndexOf(',');
        if (comma >= 0)
        {
            path = path[..comma];
        }

        path = path.Trim().Trim('"', '\'', '{', '}');
        if (path.Length == 0 || path.Contains("RelativeSource", StringComparison.OrdinalIgnoreCase) || path.Contains("ElementName", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? path : parts[^1];
    }

    private static bool IsIgnoredBindingName(string name) =>
        name is "DataContext" or "ActualWidth" or "ActualHeight" or "SelectedItem" or "PlacementTarget" or "Tag";

    private static string SafeReadText(string file)
    {
        try
        {
            return File.Exists(file) && LooksTextReadable(file)
                ? File.ReadAllText(file)
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }    private IReadOnlyList<string> SuggestLikelyFilesForGoal(string goal)
    {
        var files = Directory.Exists(Policy.WorkspaceRoot)
            ? EnumerateWorkspaceFiles().Take(10_000).Select(RelativeToWorkspace).ToList()
            : [];
        var selected = new List<string>();
        if (MentionsAny(goal, "command", "parser", "coding", "patch", "symbol", "reference", "test gap"))
        {
            AddIfExists(selected, files, "src/Ali.Core/Coding/CodingToolContracts.cs");
            AddIfExists(selected, files, "src/Ali.Core/Coding/CodingToolRequestParser.cs");
            AddIfExists(selected, files, "src/Ali.Core/Coding/CodingWorkspacePolicy.cs");
            AddIfExists(selected, files, "src/Ali.Infrastructure/Coding/LocalCodingToolService.cs");
            AddIfExists(selected, files, "tests/Ali.Tests/Program.cs");
        }

        if (MentionsAny(goal, "button", "dashboard", "wpf", "ui", "screen"))
        {
            AddIfExists(selected, files, "src/Ali.App.Wpf/ProgrammingDashboardWindow.xaml");
            AddIfExists(selected, files, "src/Ali.App.Wpf/ViewModels/MainWindowViewModel.cs");
        }

        return selected.Count == 0
            ? files.Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(6).ToList()
            : selected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddIfExists(List<string> selected, IReadOnlyList<string> files, string normalizedPath)
    {
        var match = files.FirstOrDefault(file => file.Replace('\\', '/').Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            selected.Add(match);
        }
    }

    private static string ClassifyFileRisk(string file)
    {
        var normalized = file.Replace('\\', '/');
        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("package.json", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("installer", StringComparison.OrdinalIgnoreCase))
        {
            return "High - dependency, build, or installer behavior";
        }

        if (normalized.Contains("/Coding/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CodingToolRequestParser", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CodingWorkspacePolicy", StringComparison.OrdinalIgnoreCase))
        {
            return "High - command or permission behavior";
        }

        if (normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ViewModel", StringComparison.OrdinalIgnoreCase))
        {
            return "Medium - user interface behavior";
        }

        if (IsTestFile(normalized))
        {
            return "Low - test coverage";
        }

        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            return "Low - documentation";
        }

        return "Medium - application code";
    }

    private List<string> FindSymbolMatches(string symbol, bool declarationOnly, int limit)
    {
        var matches = new List<string>();
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return matches;
        }

        foreach (var file in EnumerateWorkspaceFiles().Take(5_000))
        {
            if (matches.Count >= limit)
            {
                break;
            }

            if (!LooksTextReadable(file))
            {
                continue;
            }

            var lineNumber = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (!line.Contains(symbol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (declarationOnly && !LooksLikeSymbolDeclaration(line, symbol))
                    {
                        continue;
                    }

                    matches.Add($"{RelativeToWorkspace(file)}:{lineNumber}: {TrimForChat(line.Trim(), 180)}");
                    if (matches.Count >= limit)
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return matches;
    }

    private static bool LooksLikeSymbolDeclaration(string line, string symbol)
    {
        var trimmed = line.Trim();
        return trimmed.Contains($"class {symbol}", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains($"record {symbol}", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains($"interface {symbol}", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains($"enum {symbol}", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains($"struct {symbol}", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains($" {symbol}(", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith($"{symbol}(", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains($" {symbol} {{", StringComparison.OrdinalIgnoreCase);
    }

    private static string SuggestTestArea(string file)
    {
        var normalized = file.Replace('\\', '/');
        if (normalized.Contains("/Coding/", StringComparison.OrdinalIgnoreCase))
        {
            return "Ali.Tests coding parser/service/policy tests";
        }

        if (normalized.Contains("App.Wpf", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return "WPF command binding and compact-output smoke tests";
        }

        if (normalized.Contains("/Sources/", StringComparison.OrdinalIgnoreCase))
        {
            return "curated source retriever and planner tests";
        }

        return "focused regression test near the changed behavior";
    }

    private static void AddKnownErrorGuidance(List<string> lines, string query)
    {
        if (query.Contains("CS0246", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("- Pattern: C# type or namespace not found.");
            lines.Add("- Check: missing using, missing project reference, package reference, or renamed type.");
            return;
        }

        if (query.Contains("CS0103", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("- Pattern: name does not exist in current context.");
            lines.Add("- Check: missing helper method, wrong scope, typo, or stale generated code.");
            return;
        }

        if (query.Contains("CS1061", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("- Pattern: member not found on type.");
            lines.Add("- Check: record/property name mismatch, extension namespace, or API version drift.");
            return;
        }

        if (query.Contains("NETSDK", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("- Pattern: SDK/project configuration failure.");
            lines.Add("- Check: target framework, workload, runtime identifier, restore state, or SDK version.");
            return;
        }

        if (query.Contains("NU", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("- Pattern: NuGet restore/package failure.");
            lines.Add("- Check: package id/version, source availability, lock file, and network/source permissions.");
            return;
        }

        if (query.Contains("xaml", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("- Pattern: XAML parse/binding/build issue.");
            lines.Add("- Check: property names, command bindings, resource keys, namespace mappings, and x:Class.");
            return;
        }

        lines.Add("- Pattern: no specific known-error card matched.");
        lines.Add("- Check: run diagnose last build failure and map the first compiler/runtime error to the smallest file.");
    }

    private static string SummarizeChangedAreas(IReadOnlyList<string> changedFiles)
    {
        var areas = changedFiles
            .Select(ClassifyChangedArea)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        return areas.Count == 0 ? "working tree" : string.Join(", ", areas).ToLowerInvariant();
    }

    private static string ClassifyChangedArea(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
        {
            return "tests";
        }

        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            return "documentation";
        }

        if (normalized.Contains("/Coding/", StringComparison.OrdinalIgnoreCase))
        {
            return "coding assistant behavior";
        }

        if (normalized.Contains("App.Wpf", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return "WPF interface";
        }

        if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return "project/dependency setup";
        }

        if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
        {
            return "application code";
        }

        return "project files";
    }

    private static void AddCompactList(List<string> lines, string title, IReadOnlyList<string> items)
    {
        lines.Add($"{title}: {(items.Count == 0 ? "none found" : string.Join(", ", items.Take(6)))}");
        if (items.Count > 6)
        {
            lines.Add($"- ...{items.Count - 6} more {title.ToLowerInvariant()} omitted.");
        }
    }

    private static IReadOnlyList<string> EstimateBuildOrder(
        IReadOnlyList<ProjectSummary> summaries,
        IReadOnlyList<ProjectDependency> edges)
    {
        var remaining = summaries
            .Select(summary => summary.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(project => edges
                    .Where(edge => edge.From.Equals(project, StringComparison.OrdinalIgnoreCase))
                    .All(edge => !remaining.Contains(edge.To)))
                .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ready.Count == 0)
            {
                ordered.AddRange(remaining.OrderBy(project => project, StringComparer.OrdinalIgnoreCase));
                break;
            }

            foreach (var project in ready)
            {
                ordered.Add(project);
                remaining.Remove(project);
            }
        }

        return ordered;
    }

    private static bool ReadBooleanProperty(XDocument document, string propertyName)
    {
        var value = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static string ClassifyProjectRole(
        string relativePath,
        IReadOnlyList<string> outputTypes,
        IReadOnlyList<string> packageReferences,
        bool useWpf,
        bool useWindowsForms,
        int xamlFileCount)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var isTest = name.Contains("test", StringComparison.OrdinalIgnoreCase)
                     || relativePath.Contains($"{Path.DirectorySeparatorChar}test", StringComparison.OrdinalIgnoreCase)
                     || packageReferences.Any(package =>
                         package.StartsWith("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                         || package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                         || package.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
                         || package.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase));
        if (isTest)
        {
            return "test";
        }

        if (useWpf || useWindowsForms || xamlFileCount > 0)
        {
            return "desktop app/UI";
        }

        if (outputTypes.Any(outputType =>
                outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
        {
            return "app/host";
        }

        return "library";
    }

    private static bool IsLikelyEntryPoint(string file)
    {
        var name = Path.GetFileName(file);
        return name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
               || name.Equals("App.xaml", StringComparison.OrdinalIgnoreCase)
               || name.Equals("MainWindow.xaml", StringComparison.OrdinalIgnoreCase)
               || name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitSemicolonList(string value) =>
        value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? FormatPackageReference(XElement element)
    {
        var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var version = element.Attribute("Version")?.Value
                      ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;
        return string.IsNullOrWhiteSpace(version)
            ? id.Trim()
            : $"{id.Trim()} {version.Trim()}";
    }

    private string? FormatProjectReference(string projectFile, XElement element)
    {
        var include = element.Attribute("Include")?.Value;
        if (string.IsNullOrWhiteSpace(include))
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(projectFile) ?? Policy.WorkspaceRoot;
        try
        {
            var referencedPath = Path.GetFullPath(Path.Combine(projectDirectory, include.Trim()));
            return Policy.IsInsideWorkspace(referencedPath)
                ? RelativeToWorkspace(referencedPath)
                : include.Trim();
        }
        catch
        {
            return include.Trim();
        }
    }

    private static int CountProjectFiles(string projectDirectory, string extension)
    {
        try
        {
            return Directory.EnumerateFiles(projectDirectory, "*" + extension, SearchOption.AllDirectories)
                .Count(file => !HasSkippedPathSegment(file));
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void SearchFile(string file, string query, List<string> matches)
    {
        if (!LooksTextReadable(file))
        {
            return;
        }

        try
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add($"{file}:{lineNumber}: {TrimForChat(line.Trim(), 180)}");
                if (matches.Count >= MaxSearchMatches)
                {
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<string> ReadFilePreviewAsync(
        string file,
        int? lineNumber,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
        if (lineNumber is > 0)
        {
            var start = Math.Max(0, lineNumber.Value - 11);
            var end = Math.Min(lines.Length, start + 80);
            return FormatNumberedLines(lines, start, end);
        }

        var preview = FormatNumberedLines(lines, 0, Math.Min(lines.Length, 220));
        return TrimForChat(preview, MaxReadCharacters);
    }

    private static string FormatNumberedLines(string[] lines, int start, int end)
    {
        var formatted = new List<string>();
        for (var i = start; i < end; i++)
        {
            formatted.Add($"{i + 1}: {lines[i]}");
        }

        if (end < lines.Length)
        {
            formatted.Add($"...{lines.Length - end} more line(s) omitted.");
        }

        return string.Join(Environment.NewLine, formatted);
    }

    private bool ResolveDotNetTarget(
        CodingToolRequest request,
        out string targetPath,
        out string workingDirectory,
        out string error)
    {
        error = string.Empty;
        targetPath = string.IsNullOrWhiteSpace(request.Path)
            ? Policy.WorkspaceRoot
            : request.Path;
        workingDirectory = Policy.WorkspaceRoot;
        if (!CodingWorkspacePolicy.TryNormalizePath(targetPath, out var fullPath))
        {
            error = "Coding tool blocked: invalid dotnet target path.";
            return false;
        }

        targetPath = fullPath;
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            error = $"Coding tool could not find dotnet target: {targetPath}";
            return false;
        }

        if (request.Action == CodingToolAction.ListOutdatedPackages && Directory.Exists(targetPath))
        {
            if (!TryFindPrimaryProjectOrSolution(targetPath, out var packageTarget))
            {
                error = $"Coding tool could not find a project or solution under: {targetPath}";
                return false;
            }

            targetPath = packageTarget;
        }

        if (request.Action == CodingToolAction.AddPackage)
        {
            if (Directory.Exists(targetPath))
            {
                var packageDirectory = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var projectTarget = EnumerateWorkspaceFiles()
                    .Where(file => file.StartsWith(packageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (projectTarget is null)
                {
                    error = $"Coding tool could not find a project under: {targetPath}";
                    return false;
                }

                targetPath = projectTarget;
            }

            if (!targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                error = "Package install target must be a .csproj file or a folder containing a .csproj file.";
                return false;
            }
        }

        workingDirectory = Directory.Exists(targetPath)
            ? targetPath
            : Path.GetDirectoryName(targetPath) ?? Policy.WorkspaceRoot;
        return true;
    }

    private bool ResolveProjectReportTargets(
        CodingToolRequest request,
        out IReadOnlyList<string> projectFiles,
        out string targetPath,
        out string error)
    {
        projectFiles = [];
        error = string.Empty;
        targetPath = string.IsNullOrWhiteSpace(request.Path)
            ? Policy.WorkspaceRoot
            : request.Path;

        if (!CodingWorkspacePolicy.TryNormalizePath(targetPath, out var fullPath))
        {
            error = "Coding tool blocked: invalid package target path.";
            return false;
        }

        if (!Policy.IsInsideWorkspace(fullPath))
        {
            error = "Package inspection is limited to the approved coding workspace.";
            return false;
        }

        targetPath = fullPath;
        if (File.Exists(targetPath))
        {
            if (!targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !targetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                && !targetPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                error = "Package inspection target must be a project, solution, or folder.";
                return false;
            }

            projectFiles = targetPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                ? [targetPath]
                : FindProjectsNearSolution(targetPath);
            return true;
        }

        if (!Directory.Exists(targetPath))
        {
            error = $"Coding tool could not find package target: {targetPath}";
            return false;
        }

        var normalizedTargetPath = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        projectFiles = EnumerateWorkspaceFiles()
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                           && file.StartsWith(normalizedTargetPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    private IReadOnlyList<string> FindProjectsNearSolution(string solutionPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath) ?? Policy.WorkspaceRoot;
        return EnumerateWorkspaceFiles()
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                           && file.StartsWith(solutionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryFindPrimaryProjectOrSolution(string directory, out string targetPath)
    {
        var normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = EnumerateWorkspaceFiles()
            .Where(file => file.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
        targetPath = candidates.FirstOrDefault() ?? string.Empty;
        return targetPath.Length > 0;
    }

    private static IReadOnlyList<string> BuildDotNetArguments(
        CodingToolRequest request,
        string targetPath,
        string workingDirectory)
    {
        if (request.Action == CodingToolAction.AddPackage)
        {
            var arguments = new List<string> { "add", targetPath, "package", request.Query!.Trim() };
            if (!string.IsNullOrWhiteSpace(request.Replacement))
            {
                arguments.Add("--version");
                arguments.Add(request.Replacement.Trim());
            }

            return arguments;
        }

        return request.Action switch
        {
            CodingToolAction.Build => ["build", targetPath, "--no-restore"],
            CodingToolAction.Test => ["test", targetPath, "--no-restore"],
            CodingToolAction.Restore => ["restore", targetPath],
            CodingToolAction.ListOutdatedPackages => ["list", targetPath, "package", "--outdated"],
            CodingToolAction.RunProject when Directory.Exists(targetPath) => ["run", "--no-restore"],
            CodingToolAction.RunProject => ["run", "--no-restore", "--project", targetPath],
            _ => ["--info"]
        };
    }

    private static bool TryValidatePackageInstall(CodingToolRequest request, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            error = "Package install needs a package ID.";
            return false;
        }

        var packageId = request.Query.Trim();
        if (packageId.Length > 128 || packageId.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            error = "Package install package ID can only contain letters, digits, dots, dashes, and underscores.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Replacement))
        {
            var version = request.Replacement.Trim();
            if (version.Length > 80 || version.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+')))
            {
                error = "Package install version can only contain letters, digits, dots, dashes, underscores, and plus signs.";
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildGitArguments(
        CodingToolRequest request,
        out IReadOnlyList<string> arguments,
        out string error)
    {
        arguments = [];
        error = string.Empty;
        var query = request.Query?.Trim();
        switch (request.Action)
        {
            case CodingToolAction.GitStatus:
                arguments = ["status", "--short", "--branch"];
                return true;

            case CodingToolAction.GitDiff:
                arguments = ["diff"];
                return true;

            case CodingToolAction.GitLog:
                arguments = ["log", "--oneline", "-10"];
                return true;

            case CodingToolAction.GitAdd:
                if (string.IsNullOrWhiteSpace(query))
                {
                    error = "Git add needs a target. Use: confirm git add all";
                    return false;
                }

                if (query.Equals("all", StringComparison.OrdinalIgnoreCase) || query.Equals(".", StringComparison.Ordinal))
                {
                    arguments = ["add", "--all"];
                    return true;
                }

                if (!IsSafeRelativeGitPath(query))
                {
                    error = "Git add target must be a relative workspace path or 'all'.";
                    return false;
                }

                arguments = ["add", "--", query];
                return true;

            case CodingToolAction.GitCommit:
                if (string.IsNullOrWhiteSpace(query) || query.Contains('\n') || query.Contains('\r'))
                {
                    error = "Git commit needs a one-line commit message.";
                    return false;
                }

                arguments = ["commit", "-m", query];
                return true;

            case CodingToolAction.GitMerge:
                if (!IsSafeGitRef(query))
                {
                    error = "Git merge needs a safe branch or ref name.";
                    return false;
                }

                arguments = ["merge", query!];
                return true;

            case CodingToolAction.GitPull:
                if (!string.IsNullOrWhiteSpace(query))
                {
                    error = "Git pull with custom arguments is not supported yet.";
                    return false;
                }

                arguments = ["pull"];
                return true;

            case CodingToolAction.GitPush:
                if (!string.IsNullOrWhiteSpace(query))
                {
                    error = "Git push with custom arguments is not supported yet.";
                    return false;
                }

                arguments = ["push"];
                return true;

            default:
                error = "Unsupported Git action.";
                return false;
        }
    }

    private async Task<CodingToolResult?> EnsureCleanGitWorkingTreeAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var status = await _commandRunner.RunAsync(
            "git",
            ["status", "--porcelain"],
            workingDirectory,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0 || status.TimedOut)
        {
            return new CodingToolResult(
                true,
                false,
                $"Git merge blocked: could not verify a clean working tree.{Environment.NewLine}{TrimForChat(MergeCommandOutput(status), MaxCommandOutputCharacters)}",
                "git",
                workingDirectory,
                ExitCode: status.ExitCode);
        }

        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return new CodingToolResult(
                true,
                false,
                $"Git merge blocked: working tree has uncommitted changes.{Environment.NewLine}{TrimForChat(status.StandardOutput.Trim(), MaxCommandOutputCharacters)}",
                "git",
                workingDirectory,
                ExitCode: status.ExitCode);
        }

        return null;
    }

    private async Task<GitWorkingTreeStatus> InspectGitWorkingTreeAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Policy.WorkspaceRoot))
        {
            return new GitWorkingTreeStatus(false, false, false, $"workspace does not exist: {Policy.WorkspaceRoot}", []);
        }

        var status = await _commandRunner.RunAsync(
            "git",
            ["status", "--short", "--branch"],
            Policy.WorkspaceRoot,
            GitCommandTimeout,
            cancellationToken).ConfigureAwait(false);
        var output = MergeCommandOutput(status);
        if (status.ExitCode != 0 || status.TimedOut)
        {
            return new GitWorkingTreeStatus(
                false,
                false,
                false,
                $"could not read git status{(status.TimedOut ? " before timeout" : string.Empty)}: {TrimForChat(output, 1_000)}",
                []);
        }

        var entries = status.StandardOutput
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !line.StartsWith("##", StringComparison.Ordinal))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (entries.Count == 0)
        {
            return new GitWorkingTreeStatus(true, true, false, "clean", []);
        }

        return new GitWorkingTreeStatus(
            true,
            false,
            true,
            $"{entries.Count} uncommitted change(s) detected",
            entries);
    }

    private static string MergeCommandOutput(CodingCommandRun run)
    {
        var output = string.Join(
            Environment.NewLine,
            new[] { run.StandardOutput, run.StandardError }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(output)
            ? "No command output."
            : output.Trim();
    }

    private static CodingReceipt? ParseReceiptLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestamp = root.TryGetProperty("timestamp", out var timestampElement)
                            && timestampElement.TryGetDateTimeOffset(out var parsedTimestamp)
                ? parsedTimestamp
                : DateTimeOffset.MinValue;
            var action = ReadString(root, "action") ?? "UnknownAction";
            var succeeded = ReadBool(root, "Succeeded") ?? ReadBool(root, "succeeded") ?? false;
            var targetPath = ReadString(root, "TargetPath") ?? ReadString(root, "targetPath");
            var exitCode = ReadInt(root, "ExitCode") ?? ReadInt(root, "exitCode");
            return new CodingReceipt(timestamp, action, succeeded, targetPath, exitCode);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyList<CodingReceipt> ReadRecentReceipts(int count)
    {
        if (!File.Exists(_actionLogPath))
        {
            return [];
        }

        return File.ReadLines(_actionLogPath)
            .TakeLast(count)
            .Select(ParseReceiptLine)
            .Where(receipt => receipt is not null)
            .Select(receipt => receipt!)
            .ToList();
    }

    private static bool IsDotNetReceipt(CodingReceipt receipt) =>
        receipt.Action is nameof(CodingToolAction.Build)
            or nameof(CodingToolAction.Test)
            or nameof(CodingToolAction.Restore)
            or nameof(CodingToolAction.ListOutdatedPackages)
            or nameof(CodingToolAction.AddPackage)
            or nameof(CodingToolAction.RunProject);

    private static bool IsValidationReceipt(CodingReceipt receipt) =>
        IsDotNetReceipt(receipt)
        || receipt.Action is nameof(CodingToolAction.CreateFile)
            or nameof(CodingToolAction.AppendFile)
            or nameof(CodingToolAction.ReplaceText)
            or nameof(CodingToolAction.ApplyLastPatchPreview)
            or nameof(CodingToolAction.GitStatus)
            or nameof(CodingToolAction.GitDiff)
            or nameof(CodingToolAction.GitLog)
            or nameof(CodingToolAction.ReviewCurrentChanges)
            or nameof(CodingToolAction.GitAdd)
            or nameof(CodingToolAction.GitCommit);

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
    }

    private void StoreLastDotNetResult(CodingToolRequest request, CodingToolResult result)
    {
        if (request.Action is not (CodingToolAction.Build
            or CodingToolAction.Test
            or CodingToolAction.Restore
            or CodingToolAction.ListOutdatedPackages
            or CodingToolAction.AddPackage
            or CodingToolAction.RunProject))
        {
            return;
        }

        if (result.Succeeded)
        {
            _lastDotNetRequest = null;
            _lastDotNetResult = null;
            return;
        }

        _lastDotNetRequest = request;
        _lastDotNetResult = result;
    }

    private void StoreLastPatchPreview(CodingToolRequest request, CodingToolResult result)
    {
        if (request.Action is not (CodingToolAction.PreviewReplaceText or CodingToolAction.PreviewPatchBundle))
        {
            return;
        }

        _lastPatchPreviewRequest = result.Succeeded
            ? request
            : null;
    }

    private void StoreLastRoadmap(CodingToolRequest request, CodingToolResult result)
    {
        if (request.Action != CodingToolAction.DraftImplementationRoadmap)
        {
            return;
        }

        if (!result.Succeeded)
        {
            ClearRoadmapState();
            return;
        }

        _lastRoadmapRequest = request;
        _lastRoadmapApproved = false;
        _approvedRoadmapStarted = false;
        var goal = request.Query ?? "unspecified implementation";
        _roadmapState = CreateRoadmapState(goal);
        _roadmapStateLoaded = true;
        SaveRoadmapState();
    }

    private bool ShouldBuildCodingContext(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var text = userText.Trim();
        if (CodingToolRequestParser.TryParse(text, out _))
        {
            return false;
        }

        if (_lastDotNetResult is { Succeeded: false } && MentionsLastFailureFollowUp(text))
        {
            return true;
        }

        if (text.Contains("c#", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".xaml", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".csproj", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".sln", StringComparison.OrdinalIgnoreCase)
            || text.Contains("visual studio", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tokens = ExtractContextSearchTerms(text);
        return tokens.Any(token => CodingContextTerms.Contains(token));
    }

    private static bool MentionsLastFailureFollowUp(string text)
    {
        var tokens = text
            .Split(ContextTokenSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens.Contains("fix")
               || tokens.Contains("why")
               || tokens.Contains("error")
               || tokens.Contains("errors")
               || tokens.Contains("failed")
               || tokens.Contains("failure")
               || tokens.Contains("build")
               || tokens.Contains("test")
               || tokens.Contains("it")
               || tokens.Contains("that")
               || tokens.Contains("this");
    }

    private static bool MentionsAny(string text, params string[] terms)
    {
        foreach (var term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddContextSection(
        List<string> lines,
        string title,
        string content,
        int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        lines.Add($"{title}:");
        lines.Add(TrimForChat(content.Trim(), maxCharacters));
    }

    private static IReadOnlyList<string> ExtractContextSearchTerms(string text)
    {
        var terms = new List<string>();
        foreach (var rawToken in text.Split(ContextTokenSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var token = new string(rawToken.Where(char.IsLetterOrDigit).ToArray());
            if (token.Length < 3
                || token.All(char.IsDigit)
                || ContextStopWords.Contains(token)
                || terms.Any(existing => existing.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            terms.Add(token);
            if (terms.Count >= 6)
            {
                break;
            }
        }

        return terms;
    }

    private IReadOnlyList<string> FindContextMatches(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return [];
        }

        var matches = new List<string>();
        foreach (var term in terms)
        {
            foreach (var file in EnumerateWorkspaceFiles().Take(5_000))
            {
                if (matches.Count >= MaxContextSearchMatches)
                {
                    return matches;
                }

                var relativePath = RelativeToWorkspace(file);
                if (Path.GetFileName(file).Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    AddUniqueMatch(matches, $"{relativePath}: file name match");
                    continue;
                }

                SearchContextFile(file, term, matches);
            }
        }

        return matches;
    }

    private void SearchContextFile(string file, string term, List<string> matches)
    {
        if (!LooksTextReadable(file))
        {
            return;
        }

        try
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!line.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddUniqueMatch(
                    matches,
                    $"{RelativeToWorkspace(file)}:{lineNumber}: {TrimForChat(line.Trim(), 180)}");
                return;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddUniqueMatch(List<string> matches, string match)
    {
        if (matches.Any(existing => existing.Equals(match, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        matches.Add(match);
    }

    private async Task AddDiagnosticFileExcerptsAsync(
        List<string> lines,
        string diagnosticText,
        CancellationToken cancellationToken)
    {
        var references = ExtractDiagnosticFileReferences(diagnosticText).Take(3).ToList();
        if (references.Count == 0)
        {
            return;
        }

        lines.Add("Diagnostic file excerpts:");
        foreach (var reference in references)
        {
            if (!File.Exists(reference.Path) || !Policy.IsInsideWorkspace(reference.Path))
            {
                continue;
            }

            var preview = await ReadFilePreviewAsync(reference.Path, reference.LineNumber, cancellationToken).ConfigureAwait(false);
            lines.Add($"File: {reference.Path} at line {reference.LineNumber}");
            lines.Add(TrimForChat(preview, 3_500));
        }
    }

    private static IReadOnlyList<DiagnosticFileReference> ExtractDiagnosticFileReferences(string diagnosticText)
    {
        var references = new List<DiagnosticFileReference>();
        foreach (var line in diagnosticText.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryExtractDiagnosticFileReference(line, out var reference))
            {
                continue;
            }

            if (references.Any(existing =>
                    existing.Path.Equals(reference.Path, StringComparison.OrdinalIgnoreCase)
                    && existing.LineNumber == reference.LineNumber))
            {
                continue;
            }

            references.Add(reference);
        }

        return references;
    }

    private static bool TryExtractDiagnosticFileReference(
        string line,
        out DiagnosticFileReference reference)
    {
        reference = new DiagnosticFileReference(string.Empty, null);
        var extensionEnd = FindDiagnosticExtensionEnd(line);
        if (extensionEnd < 0)
        {
            return false;
        }

        var start = FindDrivePathStart(line, extensionEnd);
        if (start < 0)
        {
            return false;
        }

        var path = line[start..extensionEnd];
        if (!CodingWorkspacePolicy.TryNormalizePath(path, out var fullPath))
        {
            return false;
        }

        reference = new DiagnosticFileReference(fullPath, TryReadDiagnosticLineNumber(line, extensionEnd));
        return true;
    }

    private static int FindDiagnosticExtensionEnd(string line)
    {
        foreach (var extension in new[] { ".cs(", ".xaml(", ".csproj(", ".props(", ".targets(" })
        {
            var index = line.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return index + extension.Length - 1;
            }
        }

        return -1;
    }

    private static int FindDrivePathStart(string line, int beforeIndex)
    {
        for (var i = 0; i < beforeIndex - 1; i++)
        {
            if (char.IsLetter(line[i]) && line[i + 1] == ':')
            {
                return i;
            }
        }

        return -1;
    }

    private static int? TryReadDiagnosticLineNumber(string line, int extensionEnd)
    {
        if (extensionEnd >= line.Length || line[extensionEnd] != '(')
        {
            return null;
        }

        var numberStart = extensionEnd + 1;
        var numberEnd = numberStart;
        while (numberEnd < line.Length && char.IsDigit(line[numberEnd]))
        {
            numberEnd++;
        }

        return numberEnd > numberStart && int.TryParse(line[numberStart..numberEnd], out var lineNumber)
            ? lineNumber
            : null;
    }

    private static string BuildDotNetDiagnosticSummary(
        CodingToolAction action,
        CodingCommandRun run,
        string output)
    {
        if (run.ExitCode == 0 && !run.TimedOut)
        {
            return string.Empty;
        }

        var diagnostics = new List<string>();
        foreach (var line in output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!LooksLikeDotNetDiagnostic(action, line))
            {
                continue;
            }

            var trimmed = TrimForChat(line.Trim(), 360);
            if (diagnostics.Any(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            diagnostics.Add(trimmed);
            if (diagnostics.Count >= MaxDiagnosticLines)
            {
                break;
            }
        }

        if (diagnostics.Count == 0)
        {
            return "Diagnostic summary: No structured diagnostic lines were detected. Raw command output follows.";
        }

        return "Diagnostic summary:"
               + Environment.NewLine
               + string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"- {diagnostic}"));
    }

    private static bool LooksLikeDotNetDiagnostic(CodingToolAction action, string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Contains(": error ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("error ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("warning ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (action != CodingToolAction.Test)
        {
            return false;
        }

        return trimmed.StartsWith("Failed ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Error Message:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("Stack Trace:", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains(" Assert.", StringComparison.OrdinalIgnoreCase)
               || trimmed.Contains("Exception:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipPath(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return IgnoredDirectoryNames.Any(ignored => ignored.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSkippedPathSegment(string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => IgnoredDirectoryNames.Any(ignored => ignored.Equals(segment, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool LooksTextReadable(string file)
    {
        var extension = Path.GetExtension(file);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return true;
        }

        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContextRelevantFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateEditContent(string? content, string label, out string error)
    {
        error = string.Empty;
        if (content is null)
        {
            error = $"Coding tool blocked: {label} is required.";
            return false;
        }

        if (content.Length > MaxEditContentCharacters)
        {
            error = $"Coding tool blocked: {label} is too large for a single safe edit ({content.Length} character(s)).";
            return false;
        }

        return true;
    }

    private bool TryBuildGeneratedPdfPath(string? requestedName, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            error = "PDF generator needs a file name like \"owner-demo.pdf\".";
            return false;
        }

        var fileName = Path.GetFileName(requestedName.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "PDF generator blocked: PDF file name is not valid.";
            return false;
        }

        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".pdf";
        }

        path = Path.Combine(_pdfWorkspaceRoot, fileName);
        return true;
    }

    private static bool ValidatePdfContent(string? content, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = "PDF generator needs text content.";
            return false;
        }

        if (content.Length > MaxPdfTextCharacters)
        {
            error = $"PDF generator blocked: text is too large for a single simple PDF ({content.Length} character(s)).";
            return false;
        }

        return true;
    }

    private async Task<CodingToolResult> WriteGeneratedPdfAsync(
        string? requestedName,
        string title,
        string toolName,
        string body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBuildGeneratedPdfPath(requestedName, out var pdfPath, out var pathError))
        {
            return new CodingToolResult(true, false, pathError, toolName, _pdfWorkspaceRoot);
        }

        if (!ValidatePdfContent(body, out var contentError))
        {
            return new CodingToolResult(true, false, contentError, toolName, pdfPath);
        }

        Directory.CreateDirectory(_pdfWorkspaceRoot);
        var uniquePath = BuildUniquePath(pdfPath);
        var document = new SimplePdfDocument(
            title,
            $"Generated by Ali on {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
            body,
            DateTimeOffset.Now,
            "Ali PDF");
        var bytes = SimplePdfWriter.BuildTextPdf(document);
        await File.WriteAllBytesAsync(uniquePath, bytes, cancellationToken).ConfigureAwait(false);

        return new CodingToolResult(
            true,
            true,
            $"{toolName}: {uniquePath}{Environment.NewLine}Wrote {bytes.Length} byte(s).",
            toolName,
            uniquePath);
    }

    private bool TryResolvePdfInputPath(string? requestedPath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            error = "PDF command needs a PDF file name or full path.";
            return false;
        }

        if (!TryResolveDocumentPath(requestedPath, ".pdf", out fullPath, out error))
        {
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"PDF command blocked: file was not found: {fullPath}";
            return false;
        }

        if (!fullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            error = "PDF command blocked: target must be a .pdf file.";
            return false;
        }

        return true;
    }

    private bool TryResolveTextInputPath(string? requestedPath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            error = "Markdown to PDF needs a .md or .txt file name or full path.";
            return false;
        }

        if (!TryResolveDocumentPath(requestedPath, null, out fullPath, out error))
        {
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"Markdown to PDF blocked: file was not found: {fullPath}";
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            error = "Markdown to PDF blocked: input must be .md, .markdown, or .txt.";
            return false;
        }

        return true;
    }

    private bool TryResolveDocumentPath(string requestedPath, string? defaultExtension, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;
        var cleaned = requestedPath.Trim().Trim('"');
        try
        {
            if (Path.IsPathFullyQualified(cleaned))
            {
                fullPath = Path.GetFullPath(cleaned);
            }
            else
            {
                var fileName = Path.GetFileName(cleaned);
                if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    error = "Document command blocked: file name is not valid.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(defaultExtension)
                    && string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
                {
                    fileName += defaultExtension;
                }

                fullPath = Path.Combine(_pdfWorkspaceRoot, fileName);
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Document command blocked: path is not a valid local path.";
            return false;
        }
    }

    private static string BuildExtractiveSummary(string text)
    {
        var normalized = text.ReplaceLineEndings("\n").Trim();
        var sentences = normalized
            .Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(sentence => sentence.Length > 20)
            .Take(6)
            .Select(sentence => "- " + TrimForChat(sentence, 260))
            .ToList();

        if (sentences.Count == 0)
        {
            return TrimForChat(normalized, 1_500);
        }

        var lines = new List<string>
        {
            "Extractive summary:",
            "This is a deterministic text summary from extractable PDF text; no model interpretation was used."
        };
        lines.AddRange(sentences);
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildUniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find an available generated document path for: {path}");
    }

    private static int CountOrdinalOccurrences(string text, string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var index = text.IndexOf(value, searchIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            count++;
            searchIndex = index + value.Length;
        }

        return count;
    }

    private static string TrimForChat(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        return text[..maxCharacters] + $"{Environment.NewLine}...output truncated.";
    }

    private static string BuildSnippet(string text, int changeIndex, int changeLength)
    {
        var start = Math.Max(0, changeIndex - 180);
        var end = Math.Min(text.Length, changeIndex + Math.Max(changeLength, 1) + 180);
        var prefix = start > 0 ? "... " : string.Empty;
        var suffix = end < text.Length ? " ..." : string.Empty;
        return prefix + text[start..end].Trim() + suffix;
    }

    private static bool IsSafeGitRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return value.All(character =>
            char.IsLetterOrDigit(character)
            || character is '_' or '-' or '.' or '/');
    }

    private static bool IsSafeRelativeGitPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathFullyQualified(value)
            || value.StartsWith("-", StringComparison.Ordinal)
            || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private sealed record NormalizedPatchEdit(
        string FullPath,
        string OldText,
        string NewText);

    private sealed record PreparedPatchEdit(
        string FullPath,
        string UpdatedText,
        string BeforeSnippet,
        string AfterSnippet);

    private sealed record PatchEditPreparation(
        PreparedPatchEdit? Edit,
        CodingToolResult? Error);

    private sealed record PatchBundlePreparation(
        IReadOnlyList<PreparedPatchEdit> Edits,
        CodingToolResult? Error);

    private sealed record ProjectSummary(
        string RelativePath,
        string ProjectRole,
        IReadOnlyList<string> TargetFrameworks,
        IReadOnlyList<string> PackageReferences,
        IReadOnlyList<string> ProjectReferences,
        int CSharpSourceCount,
        int XamlFileCount,
        int JsonFileCount,
        string? Warning)
    {
        public static ProjectSummary WithWarning(string relativePath, string warning) =>
            new(relativePath, "unknown", [], [], [], 0, 0, 0, warning);
    }

    private sealed record ProjectDependency(
        string From,
        string To);

    private sealed record DiagnosticFileReference(
        string Path,
        int? LineNumber);

    private sealed record RoadmapNextActionRecommendation(
        string Action,
        string Confidence,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<string> Commands);

    private sealed record PacketCommandItem(
        int Number,
        string Section,
        string Command);

    private sealed record ProcessSnapshot(
        int Id,
        string Name,
        string? Path,
        string StartTimeText,
        long WorkingSetBytes)
    {
        public double WorkingSetMegabytes => WorkingSetBytes / 1024d / 1024d;
    }

    private sealed record NetstatPortOwner(
        string Protocol,
        string LocalAddress,
        string State,
        int ProcessId);

    private sealed record ApprovedRoadmapExecutionPacket(
        string Goal,
        int StepIndex,
        string Step,
        DateTimeOffset RoadmapUpdatedAt,
        DateTimeOffset ApprovedAt,
        string PrimaryTarget,
        string PacketStatus,
        string RecommendedAction,
        string Confidence,
        IReadOnlyList<string> PrepCommands,
        IReadOnlyList<string> ExecutionCommands,
        IReadOnlyList<string> ValidationCommands,
        IReadOnlyList<string> CloseoutCommands);

    private sealed record RoadmapExecutionState(
        string Goal,
        bool Approved,
        bool Started,
        bool Paused,
        bool Finished,
        int CurrentStepIndex,
        IReadOnlyList<string> Steps,
        string LastReceiptSummary,
        DateTimeOffset UpdatedAt);

    private sealed record RoslynWorkspaceModel(
        CSharpCompilation Compilation,
        IReadOnlyList<SyntaxTree> Trees);

    private sealed record SemanticSymbolHit(
        string Name,
        string Kind,
        string Display,
        string Path,
        int LineNumber,
        string Source);

    private sealed record CompilerDiagnosticInfo(
        string? Path,
        int? LineNumber,
        string? Code);

    private sealed record CSharpCallEdge(
        string Caller,
        string Callee,
        string Path,
        int LineNumber);

    private sealed record CSharpSymbolInfo(
        string Kind,
        string Name,
        string Path,
        int LineNumber);

    private sealed record CodingReceipt(
        DateTimeOffset Timestamp,
        string Action,
        bool Succeeded,
        string? TargetPath,
        int? ExitCode);

    private sealed record GitWorkingTreeStatus(
        bool Available,
        bool Clean,
        bool HasUncommittedChanges,
        string Summary,
        IReadOnlyList<string> Entries);
}

public interface ICodingProcessLauncher
{
    void Start(
        string fileName,
        IReadOnlyList<string> arguments,
        bool useShellExecute);
}

public interface ICodingCommandRunner
{
    Task<CodingCommandRun> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed record CodingCommandRun(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public sealed class CodingCommandRunner : ICodingCommandRunner
{
    public async Task<CodingCommandRun> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new CodingCommandRun(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false),
                TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            var stdout = await ReadCompletedOrEmptyAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadCompletedOrEmptyAsync(stderrTask).ConfigureAwait(false);
            return new CodingCommandRun(-1, stdout, stderr, TimedOut: true);
        }
    }

    private static async Task<string> ReadCompletedOrEmptyAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}

public sealed class CodingProcessLauncher : ICodingProcessLauncher
{
    public void Start(
        string fileName,
        IReadOnlyList<string> arguments,
        bool useShellExecute)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = useShellExecute
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
    }
}

internal static class SimplePdfInspector
{
    public static SimplePdfInfo Inspect(byte[] bytes, string extractedText)
    {
        var raw = Encoding.ASCII.GetString(bytes);
        var pageCount = CountMarkers(raw, "/Type /Page");
        if (pageCount > 0 && raw.Contains("/Type /Pages", StringComparison.Ordinal))
        {
            pageCount--;
        }

        var version = raw.StartsWith("%PDF-", StringComparison.Ordinal)
            ? raw.Split('\n', 2)[0].Trim()
            : "unknown";
        var hasImage = raw.Contains("/Subtype /Image", StringComparison.Ordinal)
            || raw.Contains("/Image", StringComparison.Ordinal);
        var hasTextOperators = raw.Contains(" Tj", StringComparison.Ordinal)
            || raw.Contains(" TJ", StringComparison.Ordinal);
        var hasExtractedText = !string.IsNullOrWhiteSpace(extractedText);
        return new SimplePdfInfo(
            version,
            Math.Max(pageCount, 1),
            raw.Contains("/Encrypt", StringComparison.Ordinal),
            raw.Contains("/AcroForm", StringComparison.Ordinal),
            hasImage,
            hasImage && (!hasTextOperators || !hasExtractedText));
    }

    public static string ExtractText(byte[] bytes)
    {
        var raw = Encoding.ASCII.GetString(bytes);
        var builder = new StringBuilder();
        var index = 0;
        while (index < raw.Length)
        {
            var open = raw.IndexOf('(', index);
            if (open < 0)
            {
                break;
            }

            if (!LooksLikeTextOperand(raw, open))
            {
                index = open + 1;
                continue;
            }

            if (!TryReadPdfLiteral(raw, open, out var literal, out var end))
            {
                index = open + 1;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(literal))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(literal.Trim());
            }

            index = end + 1;
        }

        return builder.ToString().Trim();
    }

    private static bool LooksLikeTextOperand(string raw, int openIndex)
    {
        var close = FindLiteralClose(raw, openIndex);
        if (close < 0)
        {
            return false;
        }

        var after = raw.Substring(close + 1, Math.Min(16, raw.Length - close - 1));
        return after.Contains("Tj", StringComparison.Ordinal)
            || after.Contains("'", StringComparison.Ordinal)
            || after.Contains("\"", StringComparison.Ordinal);
    }

    private static bool TryReadPdfLiteral(string raw, int openIndex, out string literal, out int endIndex)
    {
        literal = string.Empty;
        endIndex = -1;
        var builder = new StringBuilder();
        var escaped = false;
        var depth = 0;
        for (var i = openIndex + 1; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (escaped)
            {
                builder.Append(ch switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    _ => ch
                });
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '(')
            {
                depth++;
                builder.Append(ch);
                continue;
            }

            if (ch == ')')
            {
                if (depth == 0)
                {
                    endIndex = i;
                    literal = NormalizeExtractedText(builder.ToString());
                    return true;
                }

                depth--;
                builder.Append(ch);
                continue;
            }

            builder.Append(ch);
        }

        return false;
    }

    private static int FindLiteralClose(string raw, int openIndex)
    {
        if (!TryReadPdfLiteral(raw, openIndex, out _, out var endIndex))
        {
            return -1;
        }

        return endIndex;
    }

    private static string NormalizeExtractedText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                >= ' ' and <= '~' => ch,
                '\n' or '\r' or '\t' => ' ',
                _ => ' '
            });
        }

        return builder.ToString().Trim();
    }

    private static int CountMarkers(string text, string marker)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(marker, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            count++;
            index = found + marker.Length;
        }

        return count;
    }
}

internal sealed record SimplePdfInfo(
    string Version,
    int PageCount,
    bool HasEncryptMarker,
    bool HasAcroFormMarker,
    bool HasImageMarker,
    bool LikelyImageOnly);
