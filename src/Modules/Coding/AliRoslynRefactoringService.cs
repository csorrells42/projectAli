using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace Ali.Modules.Coding;

public sealed record RoslynTextChange(
    string File,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string OldText,
    string NewText);

public sealed record RoslynRenameResult(
    bool Success,
    bool Applied,
    string TargetPath,
    string DocumentPath,
    string? OriginalSymbol,
    string NewName,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<RoslynTextChange> Changes,
    IReadOnlyList<string> WorkspaceWarnings);

/// <summary>
/// Performs Roslyn's semantic solution-wide rename with an exact dry-run preview.
/// Application writes only already-approved source documents beneath the target root.
/// </summary>
internal sealed class AliRoslynRefactoringService(AliRoslynWorkspaceLoader loader)
{
    private const int MaximumReportedChanges = 300;

    public Task<RoslynRenameResult> PreviewRenameAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        string newName,
        CancellationToken cancellationToken) =>
        RenameAsync(targetPath, documentPath, line, column, newName, apply: false, cancellationToken);

    public Task<RoslynRenameResult> ApplyRenameAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        string newName,
        CancellationToken cancellationToken) =>
        RenameAsync(targetPath, documentPath, line, column, newName, apply: true, cancellationToken);

    private async Task<RoslynRenameResult> RenameAsync(
        string targetPath,
        string documentPath,
        int line,
        int column,
        string newName,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new ArgumentException("The replacement must be a valid C# identifier.", nameof(newName));
        }

        using var session = await loader.LoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var (document, position) = await loader.ResolvePositionAsync(
            session,
            documentPath,
            line,
            column,
            cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);
        if (symbol is null)
        {
            return new RoslynRenameResult(
                false,
                false,
                targetPath,
                documentPath,
                null,
                newName,
                "Roslyn found no renameable symbol at the requested position.",
                [],
                [],
                session.Warnings);
        }

        var options = new SymbolRenameOptions(
            RenameOverloads: false,
            RenameInStrings: false,
            RenameInComments: false,
            RenameFile: false);
        var renamed = await Renamer.RenameSymbolAsync(
            session.Solution,
            symbol,
            options,
            newName,
            cancellationToken).ConfigureAwait(false);
        var (changedFiles, changes) = await DescribeChangesAsync(
            session.Solution,
            renamed,
            session.Target,
            cancellationToken).ConfigureAwait(false);

        if (apply)
        {
            await ApplyChangedDocumentsAsync(session.Solution, renamed, session.Target, cancellationToken).ConfigureAwait(false);
        }

        var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return new RoslynRenameResult(
            true,
            apply,
            targetPath,
            documentPath,
            display,
            newName,
            apply
                ? $"Roslyn renamed {display} to {newName} across {changedFiles.Count} file(s)."
                : $"Roslyn previewed renaming {display} to {newName} across {changedFiles.Count} file(s).",
            changedFiles,
            changes,
            session.Warnings);
    }

    private async Task<(IReadOnlyList<string> Files, IReadOnlyList<RoslynTextChange> Changes)> DescribeChangesAsync(
        Solution original,
        Solution changed,
        AliResolvedCodingTarget target,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var changes = new List<RoslynTextChange>();
        foreach (var projectChange in changed.GetChanges(original).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var oldDocument = original.GetDocument(documentId);
                var newDocument = changed.GetDocument(documentId);
                if (oldDocument?.FilePath is null || newDocument is null)
                {
                    continue;
                }

                var physicalPath = ValidateChangedPath(target, oldDocument.FilePath);
                files.Add(Path.GetRelativePath(target.RootDirectory, physicalPath));
                var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var newText = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var textChange in newText.GetTextChanges(oldText))
                {
                    if (changes.Count >= MaximumReportedChanges)
                    {
                        break;
                    }

                    var start = oldText.Lines.GetLinePosition(textChange.Span.Start);
                    var end = oldText.Lines.GetLinePosition(textChange.Span.End);
                    changes.Add(new RoslynTextChange(
                        Path.GetRelativePath(target.RootDirectory, physicalPath),
                        start.Line + 1,
                        start.Character + 1,
                        end.Line + 1,
                        end.Character + 1,
                        oldText.ToString(textChange.Span),
                        textChange.NewText ?? string.Empty));
                }
            }
        }

        return (files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), changes);
    }

    private async Task ApplyChangedDocumentsAsync(
        Solution original,
        Solution changed,
        AliResolvedCodingTarget target,
        CancellationToken cancellationToken)
    {
        foreach (var projectChange in changed.GetChanges(original).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var oldDocument = original.GetDocument(documentId);
                var newDocument = changed.GetDocument(documentId);
                if (oldDocument?.FilePath is null || newDocument is null)
                {
                    continue;
                }

                var physicalPath = ValidateChangedPath(target, oldDocument.FilePath);
                var text = await newDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var encoding = text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                await File.WriteAllTextAsync(physicalPath, text.ToString(), encoding, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string ValidateChangedPath(AliResolvedCodingTarget target, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var targetPrefix = Path.TrimEndingDirectorySeparator(target.RootDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Roslyn attempted to change a document outside the approved target root.");
        }

        AliCodingProjectResolver.RejectReparsePoints(target.MountRoot, fullPath);
        return fullPath;
    }
}
