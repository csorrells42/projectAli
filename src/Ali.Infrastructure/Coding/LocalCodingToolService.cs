using System.Diagnostics;
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
    private const int MaxEditContentCharacters = 20_000;
    private const int MaxReplaceFileCharacters = 500_000;
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

    private readonly ICodingProcessLauncher _processLauncher = processLauncher ?? new CodingProcessLauncher();
    private readonly ICodingCommandRunner _commandRunner = commandRunner ?? new CodingCommandRunner();
    private readonly string _actionLogPath = Path.Combine(dataRoot, "coding-tool-actions.jsonl");
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
            CodingToolAction.SearchWorkspace => SearchWorkspace(request),
            CodingToolAction.ReadFile => await ReadFileAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.CreateFile or CodingToolAction.AppendFile or CodingToolAction.ReplaceText =>
                await EditFileAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.OpenSolution => await OpenSolutionAsync(request, cancellationToken).ConfigureAwait(false),
            CodingToolAction.Build or CodingToolAction.Test or CodingToolAction.RunProject =>
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
        if (!CodingWorkspacePolicy.TryNormalizePath(request.Path ?? string.Empty, out var fullPath))
        {
            return Task.FromResult(new CodingToolResult(true, false, "Coding tool blocked: invalid solution path."));
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
            _ => "Run"
        };
        var output = MergeCommandOutput(run);
        var status = run.TimedOut
            ? $"{verb} timed out after {DotNetCommandTimeout.TotalSeconds:0} seconds."
            : run.ExitCode == 0
                ? $"{verb} passed."
                : $"{verb} failed with exit code {run.ExitCode}.";
        var commandLine = $"dotnet {string.Join(" ", arguments)}";
        return new CodingToolResult(
            true,
            run.ExitCode == 0 && !run.TimedOut,
            $"{status}{Environment.NewLine}Command: {commandLine}{Environment.NewLine}Working directory: {workingDirectory}{Environment.NewLine}{TrimForChat(output, MaxCommandOutputCharacters)}",
            "dotnet",
            targetPath,
            ExitCode: run.ExitCode);
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

        workingDirectory = Directory.Exists(targetPath)
            ? targetPath
            : Path.GetDirectoryName(targetPath) ?? Policy.WorkspaceRoot;
        return true;
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
