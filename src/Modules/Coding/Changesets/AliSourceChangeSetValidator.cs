using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ali.Modules.Coding.Changesets;

internal sealed record AliSourceValidationResult(
    bool Valid,
    ImmutableArray<string> Errors,
    ImmutableArray<string> Files);

internal sealed class AliSourceChangeSetValidator(AliSourceChangeSetStore store)
{
    private const int MaximumErrors = 128;
    private const int MaximumErrorCharacters = 1024;
    private readonly AliSourceChangeSetStore _store = store
        ?? throw new ArgumentNullException(nameof(store));

    internal async Task<AliSourceValidationResult> ValidateAsync(
        AliSourceChangeSet suppliedChangeSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppliedChangeSet);
        var changeSet = await _store.LoadAsync(suppliedChangeSet.Id, cancellationToken).ConfigureAwait(false);
        AliSourceChangeSetStore.RequireExactManifest(suppliedChangeSet, changeSet);
        var errors = new List<string>();
        foreach (var operation in changeSet.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (errors.Count >= MaximumErrors)
            {
                break;
            }

            byte[]? postimage = null;
            string? validationPath = null;
            try
            {
                if (operation.Kind is AliSourceChangeKind.Add or AliSourceChangeKind.Replace)
                {
                    postimage = await _store.ReadContentAsync(changeSet, operation, cancellationToken)
                        .ConfigureAwait(false);
                    validationPath = operation.SourceRelativePath;
                }
                else if (operation.Kind == AliSourceChangeKind.Rename)
                {
                    postimage = await _store.ReadBackupAsync(changeSet, operation, cancellationToken)
                        .ConfigureAwait(false);
                    validationPath = operation.DestinationRelativePath;
                }

                if (postimage is not null && validationPath is not null)
                {
                    ValidateSyntax(
                        validationPath,
                        postimage,
                        operation.Encoding,
                        errors,
                        cancellationToken);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException
                                       or DecoderFallbackException
                                       or JsonException
                                       or XmlException)
            {
                AddError(errors, validationPath ?? operation.SourceRelativePath, ex.Message);
            }
            finally
            {
                if (postimage is not null)
                {
                    CryptographicOperations.ZeroMemory(postimage);
                }
            }
        }

        var files = changeSet.Operations
            .SelectMany(operation => operation.DestinationRelativePath is null
                ? [operation.SourceRelativePath]
                : new[] { operation.SourceRelativePath, operation.DestinationRelativePath })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new AliSourceValidationResult(errors.Count == 0, errors.ToImmutableArray(), files);
    }

    private static void ValidateSyntax(
        string relativePath,
        ReadOnlySpan<byte> bytes,
        AliSourceEncodingMetadata? encoding,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(relativePath);
        if (!RequiresTextValidation(extension))
        {
            return;
        }

        var content = AliSourceTextEncoding.Decode(bytes, encoding);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: cancellationToken);
            foreach (var diagnostic in tree.GetDiagnostics(cancellationToken)
                         .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                         .Take(MaximumErrors - errors.Count))
            {
                AddError(errors, relativePath, diagnostic.GetMessage());
            }
            return;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(
                    content,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 256
                    });
            }
            catch (JsonException ex)
            {
                AddError(errors, relativePath, ex.Message);
            }
            return;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(content),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    Async = false,
                    MaxCharactersInDocument = AliSourceChangeSetStore.MaximumFileBytes,
                    MaxCharactersFromEntities = 0
                });
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (XmlException ex)
        {
            AddError(errors, relativePath, ex.Message);
        }
    }

    private static bool RequiresTextValidation(string extension) =>
        extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase);

    private static void AddError(ICollection<string> errors, string relativePath, string message)
    {
        if (errors.Count >= MaximumErrors)
        {
            return;
        }
        var normalized = string.IsNullOrWhiteSpace(message) ? "Validation failed." : message.Trim();
        if (normalized.Length > MaximumErrorCharacters)
        {
            normalized = normalized[..MaximumErrorCharacters] + " [bounded]";
        }
        errors.Add($"{relativePath}: {normalized}");
    }
}
