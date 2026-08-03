using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ali.Modules.Coding.Changesets;

internal sealed record AliSourceValidationResult(
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Files);

internal sealed class AliSourceChangeSetValidator
{
    public async Task<AliSourceValidationResult> ValidateAsync(
        AliSourceChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var errors = new List<string>();
        foreach (var change in changeSet.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await File.ReadAllBytesAsync(change.FilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(AliSourceChangeSetStore.Hash(current), change.ExpectedSha256, StringComparison.Ordinal))
            {
                errors.Add($"{change.FilePath}: the source changed after the preview was created.");
                continue;
            }

            ValidateSyntax(change.FilePath, change.NewContent, errors, cancellationToken);
        }

        return new AliSourceValidationResult(
            errors.Count == 0,
            errors,
            changeSet.Files.Select(change => change.FilePath).ToArray());
    }

    private static void ValidateSyntax(
        string filePath,
        string content,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: cancellationToken);
            foreach (var diagnostic in tree.GetDiagnostics(cancellationToken)
                         .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                errors.Add($"{filePath}: {diagnostic.GetMessage()}");
            }
            return;
        }

        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = XmlReader.Create(
                    new StringReader(content),
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        Async = false
                    });
                while (reader.Read())
                {
                }
            }
            catch (XmlException ex)
            {
                errors.Add($"{filePath}: {ex.Message}");
            }
        }
    }
}
