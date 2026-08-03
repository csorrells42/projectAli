namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionDescriptor(
    string IdentitySha256,
    string SolutionFingerprintSha256,
    string DocumentTextSha256,
    string ProviderIdentity,
    string ProviderVersion,
    string ProviderAssemblySha256,
    string EquivalenceKey,
    string NestedActionPath,
    string Title,
    IReadOnlyList<string> DiagnosticIds,
    string DocumentRelativePath,
    int Line,
    int Column);
