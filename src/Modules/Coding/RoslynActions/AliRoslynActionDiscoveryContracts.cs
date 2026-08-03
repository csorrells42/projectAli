using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynActionDiscoveryContext(
    Solution Solution,
    Document Document,
    TextSpan Span);

internal sealed record AliRoslynProviderAction(
    string? EquivalenceKey,
    string Title,
    IReadOnlyList<string> DiagnosticIds,
    IReadOnlyList<AliRoslynActionPathSegment> Path,
    Func<string, CancellationToken, Task<ImmutableArray<CodeActionOperation>>> ExecuteAsync);

internal sealed record AliRoslynActionPathSegment(
    int Ordinal,
    string? EquivalenceKey,
    string Title);

internal interface IAliRoslynActionProvider
{
    string ProviderIdentity { get; }
    string ProviderVersion { get; }
    string ProviderAssemblySha256 { get; }

    Task<IReadOnlyList<AliRoslynProviderAction>> DiscoverAsync(
        AliRoslynActionDiscoveryContext context,
        CancellationToken cancellationToken);
}

internal sealed record AliRoslynDiscoveredAction(
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
    string ProjectIdentity,
    string DocumentIdentity,
    string? DocumentPath,
    int SpanStart,
    int SpanLength,
    Func<string, CancellationToken, Task<ImmutableArray<CodeActionOperation>>> ExecuteAsync);

internal sealed record AliRoslynActionProviderFailure(
    string ProviderIdentity,
    string ProviderVersion,
    string ProviderAssemblySha256,
    string ExceptionType,
    string MessageSha256);

internal sealed record AliRoslynActionDiscoveryResult(
    IReadOnlyList<AliRoslynDiscoveredAction> Actions,
    IReadOnlyList<AliRoslynActionProviderFailure> ProviderFailures,
    bool Truncated);

internal sealed class AliRoslynProviderExecutionException : Exception
{
    internal AliRoslynProviderExecutionException(string failureCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}
