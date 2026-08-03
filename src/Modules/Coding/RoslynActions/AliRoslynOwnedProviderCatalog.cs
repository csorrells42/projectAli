using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// The complete production-owned provider set. Callers opt into this exact catalog;
/// no assembly scanning, MEF lookup, or implicit provider construction occurs.
/// </summary>
internal sealed record AliRoslynOwnedProviderCatalog(
    IReadOnlyList<CodeFixProvider> CodeFixProviders,
    IReadOnlyList<CodeRefactoringProvider> RefactoringProviders)
{
    internal static AliRoslynOwnedProviderCatalog CreateDefault() => new(
        [new AliRoslynUnambiguousNamespaceImportCodeFixProvider()],
        [new AliRoslynFormatDocumentRefactoringProvider()]);

    internal AliRoslynActionDiscovery CreateDiscovery() => new(
        trustedCodeFixProviders: CodeFixProviders,
        trustedRefactoringProviders: RefactoringProviders);
}

/// <summary>
/// Offers CS0246 namespace imports only when one namespace makes the exact syntax
/// node bind to one accessible top-level type. Ambiguous candidates register nothing.
/// </summary>
internal sealed class AliRoslynUnambiguousNamespaceImportCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "CS0246";
    private const int MaximumNamespacesInspected = 65_536;

    public override ImmutableArray<string> FixableDiagnosticIds => [DiagnosticId];

    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var cancellationToken = context.CancellationToken;
        var diagnostic = context.Diagnostics.SingleOrDefault();
        if (diagnostic is null || !string.Equals(diagnostic.Id, DiagnosticId, StringComparison.Ordinal))
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null)
        {
            return;
        }
        var simpleName = root.FindToken(diagnostic.Location.SourceSpan.Start)
            .Parent?
            .AncestorsAndSelf()
            .OfType<SimpleNameSyntax>()
            .FirstOrDefault(node => node.Span.Contains(diagnostic.Location.SourceSpan));
        if (simpleName is null)
        {
            return;
        }

        var identifier = simpleName.Identifier.ValueText;
        var arity = simpleName is GenericNameSyntax generic
            ? generic.TypeArgumentList.Arguments.Count
            : 0;
        var candidateNamespaces = FindCandidateNamespaces(
            semanticModel.Compilation,
            identifier,
            arity,
            cancellationToken);
        if (candidateNamespaces.Length != 1)
        {
            return;
        }

        var namespaceName = candidateNamespaces[0];
        var changedDocument = await AddImportAndVerifyAsync(
                context.Document,
                simpleName,
                namespaceName,
                cancellationToken)
            .ConfigureAwait(false);
        if (changedDocument is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add namespace import '{namespaceName}'",
                _ => Task.FromResult(changedDocument),
                "ali.add-unambiguous-namespace:" + namespaceName),
            diagnostic);
    }

    private static string[] FindCandidateNamespaces(
        Compilation compilation,
        string identifier,
        int arity,
        CancellationToken cancellationToken)
    {
        var matches = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<INamespaceSymbol>();
        pending.Push(compilation.GlobalNamespace);
        var inspected = 0;
        while (pending.Count > 0 && matches.Count < 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++inspected > MaximumNamespacesInspected)
            {
                throw new InvalidDataException(
                    "The exact CS0246 namespace search exceeded its semantic graph bound.");
            }
            var current = pending.Pop();
            foreach (var symbol in current.GetTypeMembers(identifier, arity))
            {
                if (symbol.ContainingType is null
                    && !symbol.ContainingNamespace.IsGlobalNamespace
                    && (symbol.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public
                        || SymbolEqualityComparer.Default.Equals(
                            symbol.ContainingAssembly,
                            compilation.Assembly)))
                {
                    matches.Add(symbol.ContainingNamespace.ToDisplayString());
                }
            }
            foreach (var child in current.GetNamespaceMembers()
                         .OrderByDescending(item => item.Name, StringComparer.Ordinal))
            {
                pending.Push(child);
            }
        }
        return matches.Take(2).ToArray();
    }

    private static async Task<Document?> AddImportAndVerifyAsync(
        Document document,
        SimpleNameSyntax simpleName,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }
        var annotation = new SyntaxAnnotation("ali-cs0246-target");
        var annotatedRoot = root.ReplaceNode(simpleName, simpleName.WithAdditionalAnnotations(annotation));
        var generator = SyntaxGenerator.GetGenerator(document);
        var importedRoot = generator.AddNamespaceImports(
            annotatedRoot,
            generator.NamespaceImportDeclaration(namespaceName));
        var changedDocument = document.WithSyntaxRoot(importedRoot);
        var changedModel = await changedDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var changedRoot = await changedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var changedNode = changedRoot?.GetAnnotatedNodes(annotation).SingleOrDefault();
        if (changedModel is null
            || changedNode is null
            || changedModel.GetSymbolInfo(changedNode, cancellationToken).Symbol is not INamedTypeSymbol resolved
            || !string.Equals(
                resolved.ContainingNamespace.ToDisplayString(),
                namespaceName,
                StringComparison.Ordinal))
        {
            return null;
        }
        return changedDocument;
    }
}

/// <summary>Offers exact whole-document Roslyn formatting only when text would change.</summary>
internal sealed class AliRoslynFormatDocumentRefactoringProvider : CodeRefactoringProvider
{
    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var changedDocument = await FormatWithPublicEditorAsync(
                context.Document,
                context.CancellationToken)
            .ConfigureAwait(false);
        var before = await context.Document.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
        var after = await changedDocument.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
        if (before.ContentEquals(after))
        {
            return;
        }

        context.RegisterRefactoring(CodeAction.Create(
            "Format exact Roslyn document",
            _ => Task.FromResult(changedDocument),
            "ali.format-exact-document.v1"));
    }

    private static async Task<Document> FormatWithPublicEditorAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var originalRoot = editor.OriginalRoot;
        var formattedRoot = Formatter.Format(
            originalRoot,
            document.Project.Solution.Workspace,
            cancellationToken: cancellationToken);
        editor.ReplaceNode(originalRoot, formattedRoot);
        return editor.GetChangedDocument();
    }
}
