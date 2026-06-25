namespace Ali.Core.Coding;

public enum CodingToolAction
{
    OpenFile,
    OpenSolution,
    OpenWorkspace,
    ListWorkspace,
    InspectWorkspace,
    ListPackages,
    ListOutdatedPackages,
    SearchWorkspace,
    ReadFile,
    CreateFile,
    AppendFile,
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
    string? Replacement = null);

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

public interface ILocalCodingTool
{
    CodingWorkspacePolicy Policy { get; }

    Task<CodingToolResult> TryHandleAsync(
        string userText,
        CancellationToken cancellationToken);
}
