namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionProviderFailureReceipt(
    string ProviderIdentity,
    string ProviderVersion,
    string ProviderAssemblySha256,
    string ExceptionType,
    string MessageSha256);
