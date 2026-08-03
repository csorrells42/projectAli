using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>Discovers model-selectable actions while isolating every provider failure.</summary>
internal sealed class AliRoslynActionDiscovery
{
    internal const int MaximumProviders = 64;
    internal const int MaximumActions = 256;
    internal const int MaximumDiagnosticIdsPerAction = 256;
    internal const int MaximumIdentityCharacters = 2_048;
    internal const int MaximumFailureTextCharacters = 2_048;

    private readonly IReadOnlyList<IAliRoslynActionProvider> _providers;

    public AliRoslynActionDiscovery(
        IEnumerable<IAliRoslynActionProvider>? ownedProviders = null,
        IEnumerable<CodeFixProvider>? trustedCodeFixProviders = null,
        IEnumerable<CodeRefactoringProvider>? trustedRefactoringProviders = null)
    {
        var owned = MaterializeNonNull(ownedProviders, nameof(ownedProviders));
        var codeFixes = MaterializeNonNull(trustedCodeFixProviders, nameof(trustedCodeFixProviders));
        var refactorings = MaterializeNonNull(
            trustedRefactoringProviders,
            nameof(trustedRefactoringProviders));
        if (1 + owned.Length + codeFixes.Length + refactorings.Length > MaximumProviders)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownedProviders),
                $"At most {MaximumProviders - 1} explicitly registered providers are supported.");
        }

        var providers = new List<IAliRoslynActionProvider>(
            1 + owned.Length + codeFixes.Length + refactorings.Length)
        {
            new AliRoslynSemanticRenameActionProvider()
        };
        providers.AddRange(owned);
        providers.AddRange(codeFixes.Select(provider => new AliRoslynCodeFixProviderBridge(provider)));
        providers.AddRange(refactorings.Select(provider => new AliRoslynCodeRefactoringProviderBridge(provider)));
        var duplicates = providers
            .GroupBy(
                provider => provider.ProviderIdentity
                            + "\n"
                            + provider.ProviderVersion
                            + "\n"
                            + provider.ProviderAssemblySha256,
                StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicates is not null)
        {
            throw new ArgumentException(
                "Each explicitly registered Roslyn provider must have a unique concrete type and assembly identity.",
                nameof(ownedProviders));
        }

        _providers = providers;
    }

    public async Task<AliRoslynActionDiscoveryResult> DiscoverAsync(
        Solution solution,
        DocumentId documentId,
        TextSpan span,
        string solutionFingerprintSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(documentId);
        ValidateSha256(solutionFingerprintSha256, nameof(solutionFingerprintSha256));
        var document = solution.GetDocument(documentId)
            ?? throw new InvalidOperationException(
                "The requested Roslyn action document is not part of the exact solution.");
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (span.Start < 0 || span.End > text.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(span),
                "The requested action span is outside the exact document.");
        }
        var documentTextSha256 = ComputeTextSha256(text);
        var projectIdentity = ProjectIdentity(document.Project);
        var documentIdentity = DocumentIdentity(document);

        var context = new AliRoslynActionDiscoveryContext(solution, document, span);
        var actions = new List<AliRoslynDiscoveredAction>();
        var failures = new List<AliRoslynActionProviderFailure>();
        var truncated = false;
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerStart = actions.Count;
            var providerIdentity = BoundedFallback(provider.GetType().FullName, "provider");
            var providerVersion = BoundedFallback(
                provider.GetType().Assembly.GetName().Version?.ToString(),
                "unknown");
            var providerAssemblySha256 = new string('0', 64);
            try
            {
                providerIdentity = RequireBoundedIdentity(provider.ProviderIdentity, "provider identity");
                providerVersion = RequireBoundedIdentity(provider.ProviderVersion, "provider version");
                providerAssemblySha256 = provider.ProviderAssemblySha256;
                ValidateSha256(providerAssemblySha256, "providerAssemblySha256");
                var provided = await provider.DiscoverAsync(context, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The provider returned a null action collection.");

                foreach (var action in provided)
                {
                    if (actions.Count >= MaximumActions)
                    {
                        truncated = true;
                        break;
                    }
                    if (action is null)
                    {
                        throw new InvalidDataException("The provider returned a null action.");
                    }

                    var equivalenceKey = BoundedOptionalIdentity(action.EquivalenceKey, "equivalence key");
                    var title = RequireBoundedIdentity(action.Title, "action title");
                    var diagnostics = NormalizeDiagnostics(action.DiagnosticIds);
                    var path = ValidatePath(action.Path);
                    var nestedActionPath = FormatPath(path);
                    var digest = ComputeIdentity(
                        solutionFingerprintSha256,
                        documentTextSha256,
                        providerIdentity,
                        providerVersion,
                        providerAssemblySha256,
                        action.EquivalenceKey,
                        title,
                        diagnostics,
                        projectIdentity,
                        documentIdentity,
                        span,
                        path);
                    actions.Add(new(
                        digest,
                        solutionFingerprintSha256,
                        documentTextSha256,
                        providerIdentity,
                        providerVersion,
                        providerAssemblySha256,
                        equivalenceKey,
                        nestedActionPath,
                        title,
                        diagnostics,
                        projectIdentity,
                        documentIdentity,
                        document.FilePath,
                        span.Start,
                        span.Length,
                        action.ExecuteAsync
                        ?? throw new InvalidDataException("The provider action has no exact executor.")));
                }
            }
            catch (Exception exception) when (IsIsolatedProviderFailure(exception))
            {
                if (actions.Count > providerStart)
                {
                    actions.RemoveRange(providerStart, actions.Count - providerStart);
                }
                failures.Add(new(
                    providerIdentity,
                    providerVersion,
                    providerAssemblySha256,
                    BoundedFallback(exception.GetType().FullName, exception.GetType().Name),
                    HashFailureMessage(exception.Message)));
                continue;
            }

            if (truncated)
            {
                break;
            }
        }

        var unique = actions
            .GroupBy(action => action.IdentitySha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(action => action.ProviderIdentity, StringComparer.Ordinal)
            .ThenBy(action => action.NestedActionPath, StringComparer.Ordinal)
            .ThenBy(action => action.EquivalenceKey, StringComparer.Ordinal)
            .ThenBy(action => action.Title, StringComparer.Ordinal)
            .ToArray();
        return new(unique, failures, truncated);
    }

    private static string ComputeIdentity(
        string solutionFingerprintSha256,
        string documentTextSha256,
        string providerIdentity,
        string providerVersion,
        string providerAssemblySha256,
        string? equivalenceKey,
        string title,
        IReadOnlyList<string> diagnosticIds,
        string projectIdentity,
        string documentIdentity,
        TextSpan span,
        IReadOnlyList<AliRoslynActionPathSegment> path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "ali-roslyn-action-identity-v2");
        Add(hash, solutionFingerprintSha256);
        Add(hash, documentTextSha256);
        Add(hash, providerIdentity);
        Add(hash, providerVersion);
        Add(hash, providerAssemblySha256);
        AddOptional(hash, equivalenceKey);
        Add(hash, title);
        Add(hash, diagnosticIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var diagnosticId in diagnosticIds)
        {
            Add(hash, diagnosticId);
        }
        Add(hash, projectIdentity);
        Add(hash, documentIdentity);
        Add(hash, span.Start.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(hash, path.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var segment in path)
        {
            Add(hash, segment.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddOptional(hash, segment.EquivalenceKey);
            Add(hash, segment.Title);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeTextSha256(SourceText text)
    {
        using var algorithm = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true);
        using (var writer = new StreamWriter(
                   stream,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                   bufferSize: 4096,
                   leaveOpen: true))
        {
            text.Write(writer);
        }
        stream.FlushFinalBlock();
        return Convert.ToHexString(
            algorithm.Hash
            ?? throw new CryptographicException("Roslyn could not hash the exact document text."));
    }

    private static string[] NormalizeDiagnostics(IReadOnlyList<string>? supplied)
    {
        var diagnostics = (supplied ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => RequireBoundedIdentity(id, "diagnostic ID"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (diagnostics.Length > MaximumDiagnosticIdsPerAction)
        {
            throw new InvalidDataException("A Roslyn action provider returned too many diagnostic IDs.");
        }
        return diagnostics;
    }

    private static IReadOnlyList<AliRoslynActionPathSegment> ValidatePath(
        IReadOnlyList<AliRoslynActionPathSegment>? supplied)
    {
        if (supplied is null || supplied.Count == 0 || supplied.Count > 32)
        {
            throw new InvalidDataException("A Roslyn action provider returned an invalid nested action path.");
        }
        var path = supplied.ToArray();
        foreach (var segment in path)
        {
            if (segment is null || segment.Ordinal < 0 || segment.Ordinal >= MaximumActions)
            {
                throw new InvalidDataException("A Roslyn action provider returned an invalid action ordinal.");
            }
            _ = BoundedOptionalIdentity(segment.EquivalenceKey, "nested equivalence key");
            _ = RequireBoundedIdentity(segment.Title, "nested action title");
        }
        return path;
    }

    private static string FormatPath(IReadOnlyList<AliRoslynActionPathSegment> path) =>
        string.Join(
            "/",
            path.Select(segment =>
                segment.Ordinal.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)
                + "@"
                + HashPathSegment(segment)[..16]));

    private static string HashPathSegment(AliRoslynActionPathSegment segment)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddOptional(hash, segment.EquivalenceKey);
        Add(hash, segment.Title);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value?.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Roslyn action discovery requires an exact SHA-256 identity.",
                parameterName);
        }
    }

    private static string ProjectIdentity(Project project) =>
        NormalizePath(project.FilePath) + "|" + project.Name + "|" + project.AssemblyName + "|" + project.Language;

    private static string DocumentIdentity(Document document) =>
        NormalizePath(document.FilePath) + "|" + string.Join("/", document.Folders) + "|" + document.Name;

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        var normalized = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static T[] MaterializeNonNull<T>(IEnumerable<T>? supplied, string parameterName)
        where T : class
    {
        var values = supplied?.ToArray() ?? [];
        if (values.Any(value => value is null))
        {
            throw new ArgumentException("Roslyn provider registrations cannot contain null entries.", parameterName);
        }
        return values;
    }

    private static string RequireBoundedIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentityCharacters)
        {
            throw new InvalidDataException($"A Roslyn action provider returned an invalid {name}.");
        }
        return value;
    }

    private static string BoundedOptionalIdentity(string? value, string name)
    {
        if (value is not null && value.Length > MaximumIdentityCharacters)
        {
            throw new InvalidDataException($"A Roslyn action provider returned an invalid {name}.");
        }
        return value ?? string.Empty;
    }

    private static string BoundedFallback(string? value, string fallback)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return selected.Length <= MaximumFailureTextCharacters
            ? selected
            : selected[..MaximumFailureTextCharacters];
    }

    private static string HashFailureMessage(string? message)
    {
        var bounded = BoundedFallback(message, "provider failure");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bounded)));
    }

    private static void AddOptional(IncrementalHash hash, string? value)
    {
        Add(hash, value is null ? "0" : "1");
        if (value is not null)
        {
            Add(hash, value);
        }
    }

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool IsIsolatedProviderFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
}
