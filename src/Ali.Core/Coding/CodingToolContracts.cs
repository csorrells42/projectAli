namespace Ali.Core.Coding;

public enum CodingToolAction
{
    OpenFile,
    OpenSolution,
    OpenWorkspace,
    ListWorkspace,
    InspectWorkspace,
    PlanTask,
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

public interface ILocalCodingTool
{
    CodingWorkspacePolicy Policy { get; }

    Task<CodingToolResult> TryHandleAsync(
        string userText,
        CancellationToken cancellationToken);

    Task<CodingContextPack> BuildContextPackAsync(
        string userText,
        CancellationToken cancellationToken);

    Task<CodingTaskPlan> BuildTaskPlanAsync(
        string userText,
        CodingContextPack contextPack,
        CancellationToken cancellationToken);
}
