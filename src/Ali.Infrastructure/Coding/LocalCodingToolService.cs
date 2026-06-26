using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Ali.Core.Coding;

namespace Ali.Infrastructure.Coding;

public sealed class LocalCodingToolService(
    CodingWorkspacePolicy policy,
    string dataRoot,
    ICodingProcessLauncher? processLauncher = null,
    ICodingCommandRunner? commandRunner = null,
    string? configuredNotepadPlusPlusPath = null,
    string? configuredVisualStudioPath = null) : ILocalCodingTool
{
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
    private readonly string _generatedDocumentsRoot = Path.Combine(dataRoot, "GeneratedDocuments");
    private CodingToolRequest? _lastDotNetRequest;
    private CodingToolResult? _lastDotNetResult;
    private CodingToolRequest? _lastPatchPreviewRequest;
    private CodingToolRequest? _lastRoadmapRequest;
    private bool _lastRoadmapApproved;
    private bool _approvedRoadmapStarted;
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
            CodingToolAction.PlanTask => await PlanTaskAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ExploreBuildIdea => ExploreBuildIdea(request),
            CodingToolAction.DraftImplementationRoadmap => DraftImplementationRoadmap(request),
            CodingToolAction.ShowLastRoadmap => ShowLastRoadmap(),
            CodingToolAction.DiscardLastRoadmap => DiscardLastRoadmap(),
            CodingToolAction.ApproveLastRoadmap => ApproveLastRoadmap(),
            CodingToolAction.StartApprovedRoadmap => StartApprovedRoadmap(),
            CodingToolAction.ShowReceipts => ShowReceipts(),
            CodingToolAction.ShowToolIntegrationStatus => ShowToolIntegrationStatus(),
            CodingToolAction.GenerateVisualStudioHandoff => GenerateVisualStudioHandoff(),
            CodingToolAction.GeneratePdf => await GeneratePdfAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GenerateCodingReport => await GenerateCodingReportAsync(request, cancellationToken).ConfigureAwait(false),
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
        var status = _lastRoadmapApproved
            ? _approvedRoadmapStarted
                ? "Roadmap status: approved and started."
                : "Roadmap status: approved and ready to start."
            : "Roadmap status: pending approval.";
        var next = _lastRoadmapApproved
            ? "Next command: start approved roadmap"
            : "Next command: approve last roadmap";

        return roadmap with
        {
            Message = $"{status}{Environment.NewLine}{next}{Environment.NewLine}{Environment.NewLine}{roadmap.Message}",
            ToolName = "Implementation roadmap"
        };
    }

    private CodingToolResult DiscardLastRoadmap()
    {
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
        _lastRoadmapRequest = null;
        _lastRoadmapApproved = false;
        _approvedRoadmapStarted = false;

        return new CodingToolResult(
            true,
            true,
            $"Discarded implementation roadmap. No files were changed.{Environment.NewLine}Goal: {goal}",
            "Implementation roadmap",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult ApproveLastRoadmap()
    {
        if (_lastRoadmapRequest is null)
        {
            return new CodingToolResult(
                true,
                false,
                "No implementation roadmap is pending. Draft one first with: draft implementation roadmap <goal>",
                "Implementation roadmap",
                Policy.WorkspaceRoot);
        }

        _lastRoadmapApproved = true;
        _approvedRoadmapStarted = false;
        var goal = _lastRoadmapRequest.Query ?? "unspecified implementation";
        return new CodingToolResult(
            true,
            true,
            $"Approved implementation roadmap. No files were changed.{Environment.NewLine}Goal: {goal}{Environment.NewLine}Next command: start approved roadmap",
            "Implementation roadmap",
            Policy.WorkspaceRoot);
    }

    private CodingToolResult StartApprovedRoadmap()
    {
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
        var lines = new List<string>
        {
            "Approved roadmap execution started:",
            $"Goal: {goal}",
            "No files were changed.",
            "Current execution mode: guided phase loop. Ali will propose the next safe action, then stop at write/build/package/Git approval boundaries.",
            "Phase 1: clarify owner-visible behavior and non-goals.",
            "Phase 1 checklist:",
            "- Restate the exact behavior to build.",
            "- Name what is intentionally out of scope for this pass.",
            "- Identify the first read-only inspection command.",
            "Recommended next Ali commands:",
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
            "- Git write/network actions remain behind Git permission gates."
        };

        return new CodingToolResult(
            true,
            true,
            string.Join(Environment.NewLine, lines),
            "Roadmap execution",
            Policy.WorkspaceRoot);
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

        var receipts = File.ReadLines(_actionLogPath)
            .TakeLast(MaxReceiptEntries)
            .Select(ParseReceiptLine)
            .Where(receipt => receipt is not null)
            .Select(receipt => receipt!)
            .ToList();
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
            "Visual Studio in-IDE panel: not installed in this build.",
            "Current integration mode: Ali chat commands plus external Visual Studio/Notepad++ launch.",
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
            "- generate visual studio integration plan",
            "- show coding receipts",
            "- generate coding report"
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
            "Current truth: no Visual Studio extension or in-IDE panel is installed in this build.",
            $"Workspace root: {Policy.WorkspaceRoot}",
            $"Workspace exists: {hasWorkspace}",
            $"Primary solution/project: {primarySolution ?? "not found"}",
            $"Visual Studio launcher: {visualStudio ?? "not found"}",
            $"File editor launcher: {notepadPlusPlus ?? "Notepad fallback"}",
            "Recommended phase shape:",
            "- Option A: Visual Studio extension/tool window that calls Ali's existing guarded command surface.",
            "- Option B: Local companion window that follows the active solution and sends context to Ali.",
            "Current bridge surface:",
            "- GET /api/coding/status on the local web helper.",
            "- POST /api/coding/command on the local web helper.",
            "- Ali.App.VisualStudioBridge.exe can be configured as a Visual Studio External Tool and submits commands to the local helper.",
            "- Bridge endpoints are loopback-only and still use Ali's coding parser, policy gates, and receipts.",
            "Minimum integration contract:",
            "- Show workspace root, primary solution/project, architecture summary, coding receipts, pending patch state, and last dotnet failure state.",
            "- Accept deterministic Ali coding commands: inspect workspace, analyze architecture, plan coding task, draft/show/approve/start roadmap, show receipts, preview patch, show pending patch, apply confirmed patch, and generate coding report.",
            "- Pass current solution/file/line as context only after the user invokes the command.",
            "- Route edits, builds, tests, run, restore, and Git writes through Ali's existing confirmation gates.",
            "- Keep Git pull/push blocked unless the configured Git network gate is deliberately enabled.",
            "First implementation slice:",
            "1. Add a local loopback or named-pipe command endpoint in Ali with explicit local-only binding.",
            "2. Build a VS tool window or companion window that displays status and submits deterministic commands.",
            "3. Return tool results and receipts to the panel without granting direct IDE write authority.",
            "4. Add tests for command parsing, policy enforcement, endpoint authorization, and stale patch prevention.",
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
            return new CodingToolResult(true, false, pathError, "PDF generator", _generatedDocumentsRoot);
        }

        if (!ValidatePdfContent(request.Content, out var contentError))
        {
            return new CodingToolResult(true, false, contentError, "PDF generator", pdfPath);
        }

        Directory.CreateDirectory(_generatedDocumentsRoot);
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
            return new CodingToolResult(true, false, pathError, "Coding report", _generatedDocumentsRoot);
        }

        var reportText = await BuildCodingSessionReportAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(_generatedDocumentsRoot);
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
        if (_lastRoadmapRequest is null)
        {
            lines.Add("No implementation roadmap is pending in this Ali session.");
        }
        else
        {
            lines.Add(_lastRoadmapApproved
                ? _approvedRoadmapStarted
                    ? "Status: approved and started."
                    : "Status: approved and ready to start."
                : "Status: pending approval.");
            lines.Add(TrimForChat(DraftImplementationRoadmap(_lastRoadmapRequest).Message, 8_000));
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
        lines.Add("- approve last roadmap");
        lines.Add("- start approved roadmap");
        lines.Add("- preview replace in file \"path\" \"old text\" with \"new text\"");
        lines.Add("- preview patch bundle");
        lines.Add("- show pending patch preview");
        lines.Add("- confirm apply last patch preview");
        lines.Add("- suggest patch from last failure");
        lines.Add("- confirm dotnet add package \"Package.Id\" to \"path\"");
        lines.Add("- confirm dotnet build \"path\"");
        lines.Add("- diagnose last build failure");
        lines.Add("- confirm git add all");
        lines.Add("- confirm git commit \"message\"");
        lines.Add("- generate visual studio integration plan");
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

        if (!Policy.IsInsideWorkspace(fullPath))
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

        if (!Policy.IsInsideWorkspace(fullPath))
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

            if (!Policy.IsInsideWorkspace(fullPath))
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

        if (!lastDotNetResult.Message.Contains("CS1002", StringComparison.OrdinalIgnoreCase)
            || !lastDotNetResult.Message.Contains("; expected", StringComparison.OrdinalIgnoreCase))
        {
            return new CodingToolResult(
                true,
                false,
                "No deterministic patch suggestion is available for this diagnostic yet. Ali can currently preview simple CS1002 semicolon fixes only.",
                "Last failure patch suggestion",
                diagnostic.Path,
                diagnostic.LineNumber);
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

        var oldLine = lines[diagnostic.LineNumber.Value - 1];
        var trimmedEnd = oldLine.TrimEnd();
        if (trimmedEnd.EndsWith(";", StringComparison.Ordinal)
            || trimmedEnd.EndsWith("{", StringComparison.Ordinal)
            || trimmedEnd.EndsWith("}", StringComparison.Ordinal))
        {
            return new CodingToolResult(
                true,
                false,
                "No deterministic patch suggestion is available because the diagnostic line does not look like a simple missing semicolon case.",
                "Last failure patch suggestion",
                diagnostic.Path,
                diagnostic.LineNumber);
        }

        var trailingWhitespace = oldLine[trimmedEnd.Length..];
        var newLine = trimmedEnd + ";" + trailingWhitespace;
        var previewRequest = new CodingToolRequest(
            CodingToolAction.PreviewReplaceText,
            diagnostic.Path,
            diagnostic.LineNumber,
            ExplicitUserPath: false,
            UserConfirmed: false,
            Content: oldLine,
            Replacement: newLine);
        var preview = await PreviewReplaceTextAsync(previewRequest, cancellationToken).ConfigureAwait(false);
        if (preview.Succeeded)
        {
            _lastPatchPreviewRequest = previewRequest;
        }

        return preview with
        {
            Message = preview.Succeeded
                ? $"Suggested patch from last failure. No files were changed.{Environment.NewLine}Diagnostic: CS1002 ; expected{Environment.NewLine}To apply this pending preview after review, use: confirm apply last patch preview{Environment.NewLine}{preview.Message}"
                : $"No deterministic patch suggestion was stored. No files were changed.{Environment.NewLine}{preview.Message}",
            ToolName = "Last failure patch suggestion"
        };
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
            _lastRoadmapRequest = null;
            _lastRoadmapApproved = false;
            _approvedRoadmapStarted = false;
            return;
        }

        _lastRoadmapRequest = request;
        _lastRoadmapApproved = false;
        _approvedRoadmapStarted = false;
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

        path = Path.Combine(_generatedDocumentsRoot, fileName);
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

    private sealed record CodingReceipt(
        DateTimeOffset Timestamp,
        string Action,
        bool Succeeded,
        string? TargetPath,
        int? ExitCode);
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
