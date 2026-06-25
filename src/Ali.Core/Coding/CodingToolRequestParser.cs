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

    private static readonly string[] ReadPrefixes =
    [
        "read file ",
        "read this file ",
        "show file ",
        "show this file ",
        "inspect file ",
        "inspect this file "
    ];

    private static readonly string[] SearchPrefixes =
    [
        "search workspace for ",
        "search coding workspace for ",
        "search code for ",
        "find in workspace ",
        "find in coding workspace "
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

        if (TryParseSearch(trimmed, userConfirmed, out request))
        {
            return true;
        }

        if (TryParseRead(trimmed, userConfirmed, out request))
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

    private static bool TryParseBuildTestRun(string text, bool userConfirmed, out CodingToolRequest request)
    {
        request = new CodingToolRequest(CodingToolAction.OpenFile, null);
        if (TryParseWorkspaceCommand(text, BuildPrefixes, CodingToolAction.Build, userConfirmed, out request)
            || TryParseWorkspaceCommand(text, TestPrefixes, CodingToolAction.Test, userConfirmed, out request)
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
