namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionListResult(
    bool Success,
    string TargetName,
    string DocumentRelativePath,
    int Line,
    int Column,
    string? SolutionFingerprintSha256,
    IReadOnlyList<AliRoslynActionDescriptor> Actions,
    IReadOnlyList<AliRoslynActionProviderFailureReceipt> ProviderFailures,
    bool Truncated,
    string Summary,
    string? FailureCode)
{
    internal static AliRoslynActionListResult Failed(
        string targetName,
        string documentName,
        int line,
        int column,
        string failureCode,
        string summary) =>
        new(false, targetName, documentName, line, column, null, [], [], false, summary, failureCode);
}
