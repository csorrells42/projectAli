using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding;

public sealed record RoslynDocumentSymbol(
    string Name,
    string Kind,
    string Display,
    int Line,
    int Column);

public sealed record RoslynClassifiedSpan(
    string Classification,
    string Text,
    int Line,
    int Column);

public sealed record RoslynDocumentResult(
    bool Success,
    string TargetPath,
    string DocumentPath,
    string Summary,
    IReadOnlyList<RoslynDocumentSymbol> Outline,
    IReadOnlyList<RoslynDiagnosticItem> Diagnostics,
    IReadOnlyList<RoslynClassifiedSpan> Classifications,
    IReadOnlyList<string> WorkspaceWarnings);

public sealed record RoslynPositionResult(
    bool Success,
    string TargetPath,
    string DocumentPath,
    int Line,
    int Column,
    string Summary,
    string? Symbol,
    string? QuickInfo,
    IReadOnlyList<string> Signatures,
    IReadOnlyList<RoslynReferenceLocation> Definitions,
    IReadOnlyList<string> WorkspaceWarnings);

/// <summary>
/// Editor-grade Roslyn services for document outline, live diagnostics, semantic
/// classification, hover information, definitions, and invocation signatures.
/// </summary>
internal sealed class AliRoslynDocumentIntelligence(AliRoslynWorkspaceLoader loader)
{
    private const int MaximumClassifications = 500;
    private const int MaximumSignatures = 50;

    public async Task<RoslynDocumentResult> InspectDocumentAsync(
        string targetPath,
        string documentPath,
        CancellationToken cancellationToken)
    {
        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var (document, _) = await loader.ResolvePositionAsync(session, documentPath, 1, 1, cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn could not parse the requested document.");
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn could not create a semantic model for the requested document.");

        var outline = root.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Select(node => (Node: node, Symbol: model.GetDeclaredSymbol(node, cancellationToken)))
            .Where(item => item.Symbol is not null)
            .Select(item =>
            {
                var position = text.Lines.GetLinePosition(item.Node.SpanStart);
                return new RoslynDocumentSymbol(
                    item.Symbol!.Name,
                    item.Symbol.Kind.ToString(),
                    item.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    position.Line + 1,
                    position.Character + 1);
            })
            .ToList();

        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(ToDiagnosticItem)
            .ToList();
        var classifiedSpans = await Classifier.GetClassifiedSpansAsync(
            document,
            new TextSpan(0, text.Length),
            cancellationToken).ConfigureAwait(false);
        var classifications = classifiedSpans
            .Take(MaximumClassifications)
            .Select(span =>
            {
                var position = text.Lines.GetLinePosition(span.TextSpan.Start);
                return new RoslynClassifiedSpan(
                    span.ClassificationType,
                    text.ToString(span.TextSpan),
                    position.Line + 1,
                    position.Character + 1);
            })
            .ToList();

        return new RoslynDocumentResult(
            diagnostics.All(diagnostic => diagnostic.Severity != "Error"),
            targetPath,
            documentPath,
            $"Roslyn outlined {outline.Count} declaration(s), found {diagnostics.Count} live diagnostic(s), and classified {classifications.Count} source span(s).",
            outline,
            diagnostics,
            classifications,
            session.Warnings);
    }

    public async Task<RoslynPositionResult> InspectPositionAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var (document, position) = await loader.ResolvePositionAsync(
            session,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn could not create a semantic model for the requested document.");
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        var quickInfoService = QuickInfoService.GetService(document);
        var quickInfo = quickInfoService is null
            ? null
            : await quickInfoService.GetQuickInfoAsync(document, position, cancellationToken).ConfigureAwait(false);
        var quickInfoText = quickInfo is null
            ? null
            : string.Join(Environment.NewLine, quickInfo.Sections.Select(section => string.Concat(section.TaggedParts.Select(part => part.Text))));

        var definitions = symbol?.Locations
            .Where(location => location.IsInSource)
            .Select(location => ToDefinition(symbol, location, session.Target.RootDirectory))
            .ToList() ?? [];
        var signatures = GetInvocationSignatures(model, position, cancellationToken);
        var display = symbol?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        return new RoslynPositionResult(
            symbol is not null || quickInfo is not null || signatures.Count > 0,
            targetPath,
            documentPath,
            line,
            column,
            symbol is null
                ? "Roslyn found no semantic symbol at this position."
                : $"Roslyn resolved {symbol.Kind} {display}.",
            display,
            quickInfoText,
            signatures,
            definitions,
            session.Warnings);
    }

    private static IReadOnlyList<string> GetInvocationSignatures(
        SemanticModel model,
        int position,
        CancellationToken cancellationToken)
    {
        var root = model.SyntaxTree.GetRoot(cancellationToken);
        var token = root.FindToken(Math.Clamp(position, 0, Math.Max(0, root.FullSpan.End - 1)));
        var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null)
        {
            return [];
        }

        return model.GetMemberGroup(invocation.Expression, cancellationToken)
            .OfType<IMethodSymbol>()
            .Select(method => method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumSignatures)
            .ToList();
    }

    private static RoslynReferenceLocation ToDefinition(ISymbol symbol, Location location, string rootDirectory)
    {
        var span = location.GetLineSpan();
        return new RoslynReferenceLocation(
            "Definition",
            symbol.Name,
            Path.IsPathFullyQualified(span.Path) ? Path.GetRelativePath(rootDirectory, span.Path) : span.Path,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    }

    private static RoslynDiagnosticItem ToDiagnosticItem(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan() : default;
        return new RoslynDiagnosticItem(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            diagnostic.Location.IsInSource ? span.Path : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? span.StartLinePosition.Character + 1 : null);
    }
}
