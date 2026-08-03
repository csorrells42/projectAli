using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>Built-in semantic rename discovery; it never interprets request text.</summary>
internal sealed class AliRoslynSemanticRenameActionProvider : IAliRoslynActionProvider
{
    private const string RenameEquivalenceKey = "microsoft.codeanalysis.semantic-rename";
    private readonly AliRoslynProviderIdentity _identity;

    internal AliRoslynSemanticRenameActionProvider()
    {
        _identity = AliRoslynProviderIdentity.Create(this, "ali-owned-action");
    }

    public string ProviderIdentity => _identity.StableIdentity;
    public string ProviderVersion => _identity.AssemblyVersion;
    public string ProviderAssemblySha256 => _identity.AssemblyFileSha256;

    public async Task<IReadOnlyList<AliRoslynProviderAction>> DiscoverAsync(
        AliRoslynActionDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
                context.Document,
                context.Span.Start,
                cancellationToken)
            .ConfigureAwait(false);
        if (symbol is null || symbol.IsImplicitlyDeclared)
        {
            return [];
        }

        var model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var diagnosticIds = model is null
            ? []
            : model.GetDiagnostics(context.Span, cancellationToken)
                .Select(diagnostic => diagnostic.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        var title = $"Rename {symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}";
        return
        [
            new AliRoslynProviderAction(
                RenameEquivalenceKey,
                title,
                diagnosticIds,
                [new(0, RenameEquivalenceKey, title)],
                async (requestedValue, executionCancellationToken) =>
                {
                    if (requestedValue.Length > 512 || !SyntaxFacts.IsValidIdentifier(requestedValue))
                    {
                        throw new AliRoslynProviderExecutionException(
                            "invalid-rename-identifier",
                            "The semantic rename value is not a valid bounded C# identifier.");
                    }

                    var codeAction = CodeAction.Create(
                        title,
                        token => Renamer.RenameSymbolAsync(
                            context.Solution,
                            symbol,
                            new SymbolRenameOptions(
                                RenameOverloads: false,
                                RenameInStrings: false,
                                RenameInComments: false,
                                RenameFile: false),
                            requestedValue,
                            token),
                        RenameEquivalenceKey);
                    return await codeAction.GetOperationsAsync(executionCancellationToken)
                        .ConfigureAwait(false);
                })
        ];
    }
}
