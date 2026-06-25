using System.Diagnostics.CodeAnalysis;

namespace Ali.Core.Coding;

public sealed class CodingWorkspacePolicy
{
    public CodingWorkspacePolicy(
        string workspaceRoot,
        bool allowExplicitOutsideFileOpen = true,
        bool allowConfirmedBuildTestRunInsideWorkspace = true,
        bool allowGitReadInsideWorkspace = true,
        bool allowConfirmedGitWriteInsideWorkspace = true,
        bool allowConfirmedGitMergeInsideWorkspace = true,
        bool allowGitNetworkOperations = false)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));
        }

        WorkspaceRoot = NormalizeDirectory(workspaceRoot);
        AllowExplicitOutsideFileOpen = allowExplicitOutsideFileOpen;
        AllowConfirmedBuildTestRunInsideWorkspace = allowConfirmedBuildTestRunInsideWorkspace;
        AllowGitReadInsideWorkspace = allowGitReadInsideWorkspace;
        AllowConfirmedGitWriteInsideWorkspace = allowConfirmedGitWriteInsideWorkspace;
        AllowConfirmedGitMergeInsideWorkspace = allowConfirmedGitMergeInsideWorkspace;
        AllowGitNetworkOperations = allowGitNetworkOperations;
    }

    public string WorkspaceRoot { get; }

    public bool AllowExplicitOutsideFileOpen { get; }

    public bool AllowConfirmedBuildTestRunInsideWorkspace { get; }

    public bool AllowGitReadInsideWorkspace { get; }

    public bool AllowConfirmedGitWriteInsideWorkspace { get; }

    public bool AllowConfirmedGitMergeInsideWorkspace { get; }

    public bool AllowGitNetworkOperations { get; }

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

        if (request.Action == CodingToolAction.SearchWorkspace && string.IsNullOrWhiteSpace(request.Path))
        {
            return CodingToolPermissionKind.Allow.AsPermission("Searching the approved coding workspace is allowed.");
        }

        if (IsBuildTestRun(request.Action) && string.IsNullOrWhiteSpace(request.Path))
        {
            return EvaluateBuildTestRun(WorkspaceRoot, request);
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
            return CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "Build, test, and run actions are limited to the approved coding workspace.");
        }

        if (!AllowConfirmedBuildTestRunInsideWorkspace)
        {
            return CodingToolPermissionKind.Deny.AsPermission(
                "Build, test, and run actions are disabled in coding permissions.");
        }

        return request.UserConfirmed
            ? CodingToolPermissionKind.Allow.AsPermission("Confirmed build/test/run action inside the approved coding workspace is allowed.")
            : CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "Build, test, and run actions need an explicit confirmation phrase before execution.");
    }

    private static bool IsBuildTestRun(CodingToolAction action) =>
        action is CodingToolAction.Build or CodingToolAction.Test or CodingToolAction.RunProject;

    private CodingToolPermission EvaluateGit(string fullPath, CodingToolRequest request)
    {
        if (!IsInsideWorkspace(fullPath))
        {
            return CodingToolPermissionKind.RequireConfirmation.AsPermission(
                "Git actions are limited to the approved coding workspace.");
        }

        return request.Action switch
        {
            CodingToolAction.GitStatus or CodingToolAction.GitDiff or CodingToolAction.GitLog
                when AllowGitReadInsideWorkspace =>
                CodingToolPermissionKind.Allow.AsPermission("Read-only Git inspection inside the approved coding workspace is allowed."),

            CodingToolAction.GitStatus or CodingToolAction.GitDiff or CodingToolAction.GitLog =>
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
