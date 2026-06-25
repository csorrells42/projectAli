namespace Ali.Core.Coding;

public static class CodingToolRequestParser
{
    private static readonly string[] ConfirmationPrefixes =
    [
        "confirm ",
        "confirmed ",
        "yes confirm ",
        "yes, confirm ",
        "go ahead and ",
        "go ahead, "
    ];

    private static readonly string[] OpenPrefixes =
    [
        "open ",
        "open file ",
        "open this file ",
        "open in notepad++ ",
        "open with notepad++ ",
        "open in notepad ",
        "open with notepad ",
        "open in visual studio ",
        "open with visual studio ",
        "debug solution ",
        "start debugging "
    ];

    private static readonly string[] SolutionPrefixes =
    [
        "open solution ",
        "open sln ",
        "open this solution ",
        "open project solution ",
        "open in visual studio ",
        "debug solution ",
        "start debugging "
    ];

    private static readonly string[] OpenSolutionRequests =
    [
        "open solution",
        "open sln",
        "open project solution",
        "open coding solution",
        "open visual studio",
        "open project in visual studio",
        "start visual studio",
        "debug solution",
        "start debugging"
    ];

    private static readonly string[] ReadPrefixes =
    [
        "read file ",
        "read this file ",
        "show file ",
        "show this file ",
        "inspect file ",
        "inspect this file "
    ];

    private static readonly string[] CreateFilePrefixes =
    [
        "create file ",
        "create new file ",
        "write file ",
        "write new file "
    ];

    private static readonly string[] AppendFilePrefixes =
    [
        "append to file ",
        "append file "
    ];

    private static readonly string[] ReplaceTextPrefixes =
    [
        "replace in file ",
        "replace text in file ",
        "replace literal in file "
    ];

    private static readonly string[] PreviewReplaceTextPrefixes =
    [
        "preview replace in file ",
        "preview replace text in file ",
        "preview replace literal in file ",
        "preview patch in file ",
        "dry run replace in file ",
        "dry-run replace in file "
    ];

    private static readonly string[] PreviewPatchBundlePrefixes =
    [
        "preview patch bundle",
        "preview patch set",
        "preview multi-file patch",
        "preview multifile patch",
        "preview multiple file patch",
        "dry run patch bundle",
        "dry-run patch bundle"
    ];

    private static readonly string[] GeneratePdfPrefixes =
    [
        "create pdf ",
        "create a pdf ",
        "generate pdf ",
        "generate a pdf ",
        "make pdf ",
        "make a pdf ",
        "write pdf ",
        "write a pdf "
    ];

    private static readonly string[] ApplyLastPatchPreviewRequests =
    [
        "apply last patch preview",
        "apply the last patch preview",
        "apply patch preview",
        "apply the patch preview",
        "apply last preview",
        "apply the last preview",
        "apply preview",
        "apply the preview"
    ];

    private static readonly string[] ShowLastPatchPreviewRequests =
    [
        "show last patch preview",
        "show the last patch preview",
        "show pending patch preview",
        "show the pending patch preview",
        "show pending patch",
        "show the pending patch",
        "what patch is pending",
        "what is the pending patch"
    ];

    private static readonly string[] DiscardLastPatchPreviewRequests =
    [
        "discard last patch preview",
        "discard the last patch preview",
        "discard pending patch preview",
        "discard the pending patch preview",
        "clear last patch preview",
        "clear the last patch preview",
        "clear pending patch",
        "clear the pending patch"
    ];

    private static readonly string[] SearchPrefixes =
    [
        "search workspace for ",
        "search coding workspace for ",
        "search code for ",
        "find in workspace ",
        "find in coding workspace "
    ];

    private static readonly string[] InspectWorkspaceRequests =
    [
        "inspect workspace",
        "inspect coding workspace",
        "analyze workspace",
        "analyze coding workspace",
        "summarize workspace",
        "summarize coding workspace",
        "show project map",
        "show coding project map",
        "list solutions",
        "list projects"
    ];

    private static readonly string[] PlanTaskPrefixes =
    [
        "plan coding task",
        "plan this coding task",
        "plan code task",
        "plan code change",
        "plan coding change",
        "make a coding plan",
        "make coding plan",
        "draft coding plan",
        "plan the fix",
        "plan fix"
    ];

    private static readonly string[] ReceiptRequests =
    [
        "show coding receipts",
        "show code receipts",
        "show recent coding receipts",
        "show recent code receipts",
        "show coding actions",
        "show recent coding actions",
        "coding receipts",
        "coding status",
        "what did you do in coding"
    ];

    private static readonly string[] OpenLastDiagnosticRequests =
    [
        "open last diagnostic",
        "open last diagnostic file",
        "open first diagnostic",
        "open first diagnostic file",
        "open last build error",
        "open build error",
        "open last error file",
        "open failing file",
        "open compiler error",
        "open last compiler error"
    ];

    private static readonly string[] DiagnoseLastFailureRequests =
    [
        "diagnose last failure",
        "diagnose last build failure",
        "diagnose last test failure",
        "diagnose last dotnet failure",
        "explain last failure",
        "explain last build error",
        "explain last compiler error",
        "show last failure",
        "show last build failure",
        "show last dotnet failure",
        "summarize last failure",
        "summarize last build error",
        "what failed last"
    ];

    private static readonly string[] PackagePrefixes =
    [
        "list packages",
        "list package references",
        "list dependencies",
        "inspect packages",
        "inspect dependencies",
        "show packages",
        "show dependencies"
    ];

    private static readonly string[] OutdatedPackagePrefixes =
    [
        "dotnet list package --outdated",
        "list outdated packages",
        "check outdated packages",
        "inspect outdated packages",
        "check package updates",
        "check dependency updates"
    ];

    private static readonly string[] BuildPrefixes =
    [
        "dotnet build",
        "build workspace",
        "build coding workspace",
        "build solution",
        "build project"
    ];

    private static readonly string[] TestPrefixes =
    [
        "dotnet test",
        "test workspace",
        "test coding workspace",
        "test solution",
        "test project",
        "run tests"
    ];

    private static readonly string[] RestorePrefixes =
    [
        "dotnet restore",
        "restore packages",
        "restore project",
        "restore solution",
        "restore workspace"
    ];

    private static readonly string[] RunPrefixes =
    [
        "dotnet run",
        "run project",
        "run app",
        "run application"
    ];

    private static readonly string[] GitPrefixes =
    [
        "git status",
        "git diff",
        "git log",
        "git add",
        "git commit",
        "git merge",
        "git pull",
        "git push"
    ];

    public static bool TryParse(string userText, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var trimmed = userText.Trim();
        var userConfirmed = StripConfirmationPrefix(ref trimmed);
        if (IsWorkspaceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.OpenWorkspace, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsListWorkspaceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ListWorkspace, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsInspectWorkspaceRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.InspectWorkspace, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsOpenSolutionRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.OpenSolution, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParsePlanTask(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsReceiptRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowReceipts, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsOpenLastDiagnosticRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.OpenLastDiagnostic, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiagnoseLastFailureRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiagnoseLastFailure, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseSearch(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParsePackages(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseRead(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseGeneratePdf(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParsePatchBundle(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (IsShowLastPatchPreviewRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ShowLastPatchPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsDiscardLastPatchPreviewRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.DiscardLastPatchPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (IsApplyLastPatchPreviewRequest(trimmed))
        {
            request = new CodingToolRequest(CodingToolAction.ApplyLastPatchPreview, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (TryParseFileEdit(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseBuildTestRun(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseGit(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (!HasOpenIntent(trimmed))
        {
            return false;
        }

        if (!TryExtractPath(trimmed, OpenPrefixes, out var path, out var lineNumber))
        {
            return false;
        }

        var action = LooksLikeSolutionRequest(trimmed, path)
            ? CodingToolAction.OpenSolution
            : CodingToolAction.OpenFile;
        request = new CodingToolRequest(action, path, lineNumber, ExplicitUserPath: true, UserConfirmed: userConfirmed);
        return true;
    }

    private static bool IsWorkspaceRequest(string text) =>
        text.Equals("open coding workspace", StringComparison.OrdinalIgnoreCase)
        || text.Equals("open programming projects", StringComparison.OrdinalIgnoreCase)
        || text.Equals("open ali coding workspace", StringComparison.OrdinalIgnoreCase);

    private static bool IsListWorkspaceRequest(string text) =>
        text.Equals("list workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("list coding workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("show workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("show coding workspace files", StringComparison.OrdinalIgnoreCase)
        || text.Equals("list programming projects", StringComparison.OrdinalIgnoreCase);

    private static bool IsInspectWorkspaceRequest(string text) =>
        InspectWorkspaceRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenSolutionRequest(string text) =>
        OpenSolutionRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsReceiptRequest(string text) =>
        ReceiptRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenLastDiagnosticRequest(string text) =>
        OpenLastDiagnosticRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiagnoseLastFailureRequest(string text) =>
        DiagnoseLastFailureRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsShowLastPatchPreviewRequest(string text) =>
        ShowLastPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsDiscardLastPatchPreviewRequest(string text) =>
        DiscardLastPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool IsApplyLastPatchPreviewRequest(string text) =>
        ApplyLastPatchPreviewRequests.Any(request => text.Equals(request, StringComparison.OrdinalIgnoreCase));

    private static bool TryParsePlanTask(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = PlanTaskPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var query = text[prefix.Length..].Trim().Trim(':', '-', ' ', '"');
        request = new CodingToolRequest(
            CodingToolAction.PlanTask,
            null,
            UserConfirmed: userConfirmed,
            Query: string.IsNullOrWhiteSpace(query) ? null : query);
        return true;
    }

    private static bool HasOpenIntent(string text)
    {
        foreach (var prefix in OpenPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSearch(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        foreach (var prefix in SearchPrefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var query = text[prefix.Length..].Trim().Trim('"');
            if (query.Length == 0)
            {
                return false;
            }

            request = new CodingToolRequest(
                CodingToolAction.SearchWorkspace,
                null,
                ExplicitUserPath: false,
                UserConfirmed: userConfirmed,
                Query: query);
            return true;
        }

        return false;
    }

    private static bool TryParsePackages(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!TryParseWorkspaceCommand(text, PackagePrefixes, CodingToolAction.ListPackages, userConfirmed, out request))
        {
            return TryParseWorkspaceCommand(text, OutdatedPackagePrefixes, CodingToolAction.ListOutdatedPackages, userConfirmed, out request);
        }

        return true;
    }

    private static bool TryParseRead(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, ReadPrefixes))
        {
            return false;
        }

        if (!TryExtractPath(text, ReadPrefixes, out var path, out var lineNumber))
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.ReadFile,
            path,
            lineNumber,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed);
        return true;
    }

    private static bool TryParseGeneratePdf(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, GeneratePdfPrefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 2 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.GeneratePdf,
            segments[0].Trim(),
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed,
            Content: segments[1]);
        return true;
    }

    private static bool TryParsePatchBundle(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = PreviewPatchBundlePrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var body = text[prefix.Length..].Trim();
        if (body.Length == 0)
        {
            return false;
        }

        var edits = new List<CodingPatchEdit>();
        foreach (var line in SplitPatchBundleLines(body))
        {
            var segments = ExtractQuotedSegments(line);
            if (segments.Count < 3 || string.IsNullOrWhiteSpace(segments[0]))
            {
                return false;
            }

            edits.Add(new CodingPatchEdit(
                segments[0].Trim(),
                segments[1],
                segments[2]));
        }

        if (edits.Count == 0)
        {
            return false;
        }

        request = new CodingToolRequest(
            CodingToolAction.PreviewPatchBundle,
            null,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            PatchEdits: edits);
        return true;
    }

    private static IReadOnlyList<string> SplitPatchBundleLines(string text)
    {
        var lines = new List<string>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Equals("```", StringComparison.Ordinal)
                || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            line = line.TrimStart('-', '*', ' ');
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static bool TryParseFileEdit(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (TryParseTextWrite(text, CreateFilePrefixes, CodingToolAction.CreateFile, userConfirmed, out request)
            || TryParseTextWrite(text, AppendFilePrefixes, CodingToolAction.AppendFile, userConfirmed, out request))
        {
            return true;
        }

        var isPreview = StartsWithAny(text, PreviewReplaceTextPrefixes);
        if (!isPreview && !StartsWithAny(text, ReplaceTextPrefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 3 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            isPreview ? CodingToolAction.PreviewReplaceText : CodingToolAction.ReplaceText,
            segments[0].Trim(),
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            Content: segments[1],
            Replacement: segments[2]);
        return true;
    }

    private static bool TryParseTextWrite(
        string text,
        IReadOnlyList<string> prefixes,
        CodingToolAction action,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (!StartsWithAny(text, prefixes))
        {
            return false;
        }

        var segments = ExtractQuotedSegments(text);
        if (segments.Count < 2 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return false;
        }

        request = new CodingToolRequest(
            action,
            segments[0].Trim(),
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed,
            Content: segments[1]);
        return true;
    }

    private static bool TryParseBuildTestRun(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (TryParseWorkspaceCommand(text, BuildPrefixes, CodingToolAction.Build, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, TestPrefixes, CodingToolAction.Test, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, RestorePrefixes, CodingToolAction.Restore, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, RunPrefixes, CodingToolAction.RunProject, userConfirmed, out request))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseGit(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = GitPrefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var action = prefix.ToLowerInvariant() switch
        {
            "git status" => CodingToolAction.GitStatus,
            "git diff" => CodingToolAction.GitDiff,
            "git log" => CodingToolAction.GitLog,
            "git add" => CodingToolAction.GitAdd,
            "git commit" => CodingToolAction.GitCommit,
            "git merge" => CodingToolAction.GitMerge,
            "git pull" => CodingToolAction.GitPull,
            "git push" => CodingToolAction.GitPush,
            _ => CodingToolAction.GitStatus
        };
        var remainder = text[prefix.Length..].Trim();
        request = new CodingToolRequest(
            action,
            null,
            ExplicitUserPath: false,
            UserConfirmed: userConfirmed,
            Query: NormalizeGitRemainder(action, remainder));
        return true;
    }

    private static bool TryParseWorkspaceCommand(
        string text,
        IReadOnlyList<string> prefixes,
        CodingToolAction action,
        bool userConfirmed,
        out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        var prefix = prefixes
            .OrderByDescending(prefix => prefix.Length)
            .FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefix is null)
        {
            return false;
        }

        var remainder = text[prefix.Length..].Trim();
        if (remainder.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[3..].Trim();
        }

        if (remainder.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[3..].Trim();
        }

        if (remainder.StartsWith("for ", StringComparison.OrdinalIgnoreCase))
        {
            remainder = remainder[4..].Trim();
        }

        if (remainder.Length == 0)
        {
            request = new CodingToolRequest(action, null, UserConfirmed: userConfirmed);
            return true;
        }

        if (!TryExtractPathFromRemainder(remainder, out var path, out var lineNumber))
        {
            return false;
        }

        request = new CodingToolRequest(
            action,
            path,
            lineNumber,
            ExplicitUserPath: true,
            UserConfirmed: userConfirmed);
        return true;
    }

    private static bool LooksLikeSolutionRequest(string text, string path)
    {
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var prefix in SolutionPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractPath(string text, IReadOnlyList<string> prefixes, out string path, out int? lineNumber)
    {
        path = string.Empty;
        lineNumber = null;

        var working = StripKnownPrefix(text, prefixes);
        if (TryExtractTrailingLineNumber(working, out var withoutLineNumber, out var parsedLineNumber))
        {
            working = withoutLineNumber;
            lineNumber = parsedLineNumber;
        }

        working = working.Trim();
        if (working.Length == 0)
        {
            return false;
        }

        if (TryExtractQuotedPath(working, out var quotedPath))
        {
            path = quotedPath;
            return true;
        }

        return TryExtractPathFromRemainder(working, out path, out _);
    }

    private static bool TryExtractPathFromRemainder(string text, out string path, out int? lineNumber)
    {
        path = string.Empty;
        lineNumber = null;
        var working = text.Trim();
        if (TryExtractTrailingLineNumber(working, out var withoutLineNumber, out var parsedLineNumber))
        {
            working = withoutLineNumber;
            lineNumber = parsedLineNumber;
        }

        if (TryExtractQuotedPath(working, out var quotedPath))
        {
            path = quotedPath;
            return true;
        }

        var driveIndex = FindDrivePathStart(working);
        if (driveIndex < 0)
        {
            return false;
        }

        path = working[driveIndex..].Trim().TrimEnd('.', ',', ';');
        return path.Length > 0;
    }

    private static string StripKnownPrefix(string text, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[prefix.Length..];
            }
        }

        return text;
    }

    private static string? NormalizeGitRemainder(CodingToolAction action, string remainder)
    {
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return null;
        }

        var normalized = remainder.Trim();
        if (action == CodingToolAction.GitCommit)
        {
            if (normalized.StartsWith("-m ", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[3..].Trim();
            }

            if (TryExtractQuotedPath(normalized, out var quotedMessage))
            {
                return quotedMessage;
            }
        }

        return normalized.Trim('"');
    }

    private static bool TryExtractQuotedPath(string text, out string path)
    {
        path = string.Empty;
        var firstQuote = text.IndexOf('"');
        if (firstQuote < 0)
        {
            return false;
        }

        var secondQuote = text.IndexOf('"', firstQuote + 1);
        if (secondQuote <= firstQuote)
        {
            return false;
        }

        path = text.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
        return path.Length > 0;
    }

    private static IReadOnlyList<string> ExtractQuotedSegments(string text)
    {
        var segments = new List<string>();
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var firstQuote = text.IndexOf('"', searchIndex);
            if (firstQuote < 0)
            {
                break;
            }

            var secondQuote = text.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
            {
                break;
            }

            segments.Add(text.Substring(firstQuote + 1, secondQuote - firstQuote - 1));
            searchIndex = secondQuote + 1;
        }

        return segments;
    }

    private static int FindDrivePathStart(string text)
    {
        for (var i = 0; i < text.Length - 2; i++)
        {
            if (char.IsLetter(text[i]) && text[i + 1] == ':' && (text[i + 2] == '\\' || text[i + 2] == '/'))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryExtractTrailingLineNumber(string text, out string withoutLineNumber, out int? lineNumber)
    {
        withoutLineNumber = text;
        lineNumber = null;

        var markerIndex = LastIndexOfLineMarker(text, " at line ");
        if (markerIndex < 0)
        {
            markerIndex = LastIndexOfLineMarker(text, " line ");
        }
        if (markerIndex < 0)
        {
            return false;
        }

        var marker = text[markerIndex..];
        var digits = new string(marker.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var parsed) || parsed < 1)
        {
            return false;
        }

        withoutLineNumber = text[..markerIndex].TrimEnd();
        lineNumber = parsed;
        return true;
    }

    private static int LastIndexOfLineMarker(string text, string marker) =>
        text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string text, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StripConfirmationPrefix(ref string text)
    {
        foreach (var prefix in ConfirmationPrefixes.OrderByDescending(prefix => prefix.Length))
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            text = text[prefix.Length..].Trim();
            return true;
        }

        return false;
    }
}
