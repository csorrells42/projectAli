namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynTargetInspection(
    bool Success,
    string TargetName,
    string? SolutionFingerprintSha256,
    int ProjectCount,
    int DocumentCount,
    int ErrorCount,
    int WarningCount,
    int WorkspaceWarningCount,
    string Summary,
    string? FailureCode)
{
    internal static AliRoslynTargetInspection Failed(
        string targetName,
        string failureCode,
        string summary) =>
        new(false, targetName, null, 0, 0, 0, 0, 0, summary, failureCode);
}
