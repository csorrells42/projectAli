using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Ali.Modules.Coding.RoslynActions;

/// <summary>
/// Adapts one explicitly supplied public CodeFixProvider instance. No MEF lookup,
/// provider construction, reflection invocation, or internal Roslyn service is used.
/// </summary>
internal sealed class AliRoslynCodeFixProviderBridge : IAliRoslynActionProvider
{
    private const int MaximumFixableDiagnosticIds = 4_096;
    private const int MaximumCandidateDiagnostics = 256;

    private readonly CodeFixProvider _provider;
    private readonly AliRoslynProviderIdentity _identity;

    internal AliRoslynCodeFixProviderBridge(CodeFixProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identity = AliRoslynProviderIdentity.Create(provider, "roslyn-codefix");
    }

    public string ProviderIdentity => _identity.StableIdentity;
    public string ProviderVersion => _identity.AssemblyVersion;
    public string ProviderAssemblySha256 => _identity.AssemblyFileSha256;

    public async Task<IReadOnlyList<AliRoslynProviderAction>> DiscoverAsync(
        AliRoslynActionDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var fixableIds = (_provider.FixableDiagnosticIds.IsDefault
                ? ImmutableArray<string>.Empty
                : _provider.FixableDiagnosticIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (fixableIds.Length > MaximumFixableDiagnosticIds)
        {
            throw new InvalidDataException("A trusted CodeFixProvider advertised too many diagnostic IDs.");
        }
        if (fixableIds.Length == 0)
        {
            return [];
        }

        var diagnostics = await CaptureCandidateDiagnosticsAsync(
                context,
                fixableIds.ToHashSet(StringComparer.Ordinal),
                cancellationToken)
            .ConfigureAwait(false);
        var registrations = new List<AliRoslynRegisteredCodeAction>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callbackGate = new object();
            var callbackActions = new List<AliRoslynRegisteredCodeAction>();
            var fixContext = new CodeFixContext(
                context.Document,
                diagnostic,
                (action, applicableDiagnostics) =>
                {
                    ArgumentNullException.ThrowIfNull(action);
                    var ids = (applicableDiagnostics.IsDefault
                            ? ImmutableArray<Diagnostic>.Empty
                            : applicableDiagnostics)
                        .Select(item => item.Id)
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Append(diagnostic.Id)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    lock (callbackGate)
                    {
                        callbackActions.Add(new(action, ids));
                    }
                },
                cancellationToken);
            await _provider.RegisterCodeFixesAsync(fixContext).ConfigureAwait(false);
            lock (callbackGate)
            {
                registrations.AddRange(callbackActions);
            }
            if (registrations.Count > AliRoslynActionDiscovery.MaximumActions)
            {
                throw new InvalidDataException("A trusted CodeFixProvider registered too many root actions.");
            }
        }

        return AliRoslynCodeActionFlattener.Flatten(registrations);
    }

    private static async Task<IReadOnlyList<Diagnostic>> CaptureCandidateDiagnosticsAsync(
        AliRoslynActionDiscoveryContext context,
        IReadOnlySet<string> fixableIds,
        CancellationToken cancellationToken)
    {
        var syntaxTree = await context.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The exact CodeFixProvider document has no syntax tree.");
        var compilation = await context.Document.Project.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn could not compile the exact CodeFixProvider project.");
        var analyzers = context.Document.Project.AnalyzerReferences
            .SelectMany(reference => reference.GetAnalyzers(context.Document.Project.Language))
            .Distinct()
            .ToImmutableArray();
        var allDiagnostics = analyzers.IsDefaultOrEmpty
            ? compilation.GetDiagnostics(cancellationToken)
            : await compilation.WithAnalyzers(analyzers)
                .GetAllDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        var matches = allDiagnostics
            .Where(diagnostic =>
                fixableIds.Contains(diagnostic.Id)
                && diagnostic.Location.IsInSource
                && diagnostic.Location.SourceTree == syntaxTree
                && IsRelevant(diagnostic.Location.SourceSpan, context.Span))
            .OrderBy(diagnostic => diagnostic.Location.SourceSpan.Start)
            .ThenBy(diagnostic => diagnostic.Location.SourceSpan.Length)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.GetMessage(), StringComparer.Ordinal)
            .Take(MaximumCandidateDiagnostics + 1)
            .ToArray();
        if (matches.Length > MaximumCandidateDiagnostics)
        {
            throw new InvalidDataException("The exact CodeFixProvider position has too many candidate diagnostics.");
        }
        return matches;
    }

    private static bool IsRelevant(TextSpan diagnosticSpan, TextSpan requestedSpan) =>
        requestedSpan.Length == 0
            ? diagnosticSpan.Contains(requestedSpan.Start)
              || diagnosticSpan.Length == 0 && diagnosticSpan.Start == requestedSpan.Start
            : diagnosticSpan.IntersectsWith(requestedSpan);
}

/// <summary>
/// Adapts one explicitly supplied public CodeRefactoringProvider instance.
/// </summary>
internal sealed class AliRoslynCodeRefactoringProviderBridge : IAliRoslynActionProvider
{
    private readonly CodeRefactoringProvider _provider;
    private readonly AliRoslynProviderIdentity _identity;

    internal AliRoslynCodeRefactoringProviderBridge(CodeRefactoringProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _identity = AliRoslynProviderIdentity.Create(provider, "roslyn-refactoring");
    }

    public string ProviderIdentity => _identity.StableIdentity;
    public string ProviderVersion => _identity.AssemblyVersion;
    public string ProviderAssemblySha256 => _identity.AssemblyFileSha256;

    public async Task<IReadOnlyList<AliRoslynProviderAction>> DiscoverAsync(
        AliRoslynActionDiscoveryContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var callbackGate = new object();
        var registrations = new List<AliRoslynRegisteredCodeAction>();
        var refactoringContext = new CodeRefactoringContext(
            context.Document,
            context.Span,
            action =>
            {
                ArgumentNullException.ThrowIfNull(action);
                lock (callbackGate)
                {
                    registrations.Add(new(action, []));
                }
            },
            cancellationToken);
        await _provider.ComputeRefactoringsAsync(refactoringContext).ConfigureAwait(false);
        lock (callbackGate)
        {
            if (registrations.Count > AliRoslynActionDiscovery.MaximumActions)
            {
                throw new InvalidDataException(
                    "A trusted CodeRefactoringProvider registered too many root actions.");
            }
            return AliRoslynCodeActionFlattener.Flatten(registrations.ToArray());
        }
    }
}

internal sealed record AliRoslynRegisteredCodeAction(
    CodeAction Action,
    IReadOnlyList<string> DiagnosticIds);

internal static class AliRoslynCodeActionFlattener
{
    private const int MaximumNestedDepth = 32;

    internal static IReadOnlyList<AliRoslynProviderAction> Flatten(
        IReadOnlyList<AliRoslynRegisteredCodeAction> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var flattened = new List<AliRoslynProviderAction>();
        for (var rootOrdinal = 0; rootOrdinal < registrations.Count; rootOrdinal++)
        {
            var registration = registrations[rootOrdinal]
                ?? throw new InvalidDataException("A trusted Roslyn provider registered a null action.");
            FlattenAction(
                registration.Action,
                registration.DiagnosticIds,
                [],
                rootOrdinal,
                new HashSet<CodeAction>(ReferenceEqualityComparer.Instance),
                flattened);
        }
        return flattened;
    }

    private static void FlattenAction(
        CodeAction action,
        IReadOnlyList<string> diagnosticIds,
        IReadOnlyList<AliRoslynActionPathSegment> parentPath,
        int ordinal,
        ISet<CodeAction> ancestors,
        ICollection<AliRoslynProviderAction> flattened)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (parentPath.Count >= MaximumNestedDepth)
        {
            throw new InvalidDataException("A trusted Roslyn provider exceeded the nested action depth bound.");
        }
        if (!ancestors.Add(action))
        {
            throw new InvalidDataException("A trusted Roslyn provider returned a cyclic nested action graph.");
        }
        try
        {
            var nested = action.NestedActions;
            // Roslyn's convenience factory may synthesize a process-local equivalence
            // key for a grouping action. A group is not executable, so bind its stable
            // ordinal and title; preserve the provider equivalence key on executable leaves.
            var path = new AliRoslynActionPathSegment[parentPath.Count + 1];
            for (var index = 0; index < parentPath.Count; index++)
            {
                path[index] = parentPath[index];
            }
            path[^1] = new(
                ordinal,
                nested.IsDefaultOrEmpty ? action.EquivalenceKey : null,
                action.Title);
            if (!nested.IsDefaultOrEmpty)
            {
                for (var childOrdinal = 0; childOrdinal < nested.Length; childOrdinal++)
                {
                    var child = nested[childOrdinal]
                        ?? throw new InvalidDataException(
                            "A trusted Roslyn provider returned a null nested action.");
                    FlattenAction(
                        child,
                        diagnosticIds,
                        path,
                        childOrdinal,
                        ancestors,
                        flattened);
                }
                return;
            }

            if (flattened.Count >= AliRoslynActionDiscovery.MaximumActions)
            {
                throw new InvalidDataException("A trusted Roslyn provider exceeded the flattened action bound.");
            }
            flattened.Add(new(
                action.EquivalenceKey,
                action.Title,
                diagnosticIds,
                path,
                (_, cancellationToken) => action.GetOperationsAsync(cancellationToken)));
        }
        finally
        {
            ancestors.Remove(action);
        }
    }
}
