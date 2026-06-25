using System.Diagnostics;
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
    private const int MaxDiagnosticLines = 12;
    private const int MaxEditContentCharacters = 20_000;
    private const int MaxReplaceFileCharacters = 500_000;
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
    private CodingToolRequest? _lastDotNetRequest;
    private CodingToolResult? _lastDotNetResult;
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
            CodingToolAction.PlanTask => await PlanTaskAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.ListPackages => ListPackages(request),
            CodingToolAction.SearchWorkspace => SearchWorkspace(request),
            CodingToolAction.ReadFile => await ReadFileAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.CreateFile or CodingToolAction.AppendFile or CodingToolAction.ReplaceText =>
                await EditFileAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.OpenSolution => await OpenSolutionAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.Build or CodingToolAction.Test or CodingToolAction.Restore
                or CodingToolAction.ListOutdatedPackages or CodingToolAction.RunProject =>
                await RunDotNetCommandAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.GitStatus or CodingToolAction.GitDiff or CodingToolAction.GitLog
                or CodingToolAction.GitAdd or CodingToolAction.GitCommit or CodingToolAction.GitMerge
                or CodingToolAction.GitPull or CodingToolAction.GitPush =>
                await RunGitCommandAsync(request, cancellationToken).ConfigureAwait(false),
            _ => await OpenFileAsync(request, cancellationToken).ConfigureAwait(false)
        };

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

        lines.Add("Permission gates:");
        lines.Add("- Read/open/search/inspect inside the approved workspace can proceed as read-only actions.");
        lines.Add("- File writes require an explicit confirmation phrase before Ali changes files.");
        lines.Add("- Build, test, restore, and run require confirmation before execution.");
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

        var arguments = BuildDotNetArguments(request.Action, targetPath, workingDirectory);
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
            if (summary.TargetFrameworks.Count > 0)
            {
                lines.Add($"  Target: {string.Join(", ", summary.TargetFrameworks)}");
            }

            if (summary.PackageReferences.Count > 0)
            {
                lines.Add($"  Packages: {string.Join(", ", summary.PackageReferences.Take(8))}");
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

            return new ProjectSummary(relativePath, targetFrameworks, packageReferences, Warning: null);
        }
        catch (IOException ex)
        {
            return new ProjectSummary(relativePath, [], [], ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new ProjectSummary(relativePath, [], [], ex.Message);
        }
        catch (System.Xml.XmlException ex)
        {
            return new ProjectSummary(relativePath, [], [], ex.Message);
        }
    }

    private string RelativeToWorkspace(string path) =>
        Path.GetRelativePath(Policy.WorkspaceRoot, path);

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
        CodingToolAction action,
        string targetPath,
        string workingDirectory)
    {
        return action switch
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

    private void StoreLastDotNetResult(CodingToolRequest request, CodingToolResult result)
    {
        if (request.Action is not (CodingToolAction.Build
            or CodingToolAction.Test
            or CodingToolAction.Restore
            or CodingToolAction.ListOutdatedPackages
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

    private sealed record ProjectSummary(
        string RelativePath,
        IReadOnlyList<string> TargetFrameworks,
        IReadOnlyList<string> PackageReferences,
        string? Warning);

    private sealed record DiagnosticFileReference(
        string Path,
        int? LineNumber);
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
