using System.Security.Cryptography;
using Ali.Modules.Orchestration.Evidence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynResolvedMetadataReference(
    string PhysicalPath,
    string Sha256,
    MetadataReferenceProperties Properties,
    PortableExecutableReference Reference);

internal sealed record AliRoslynResolvedAnalyzerReference(
    string PhysicalPath,
    string Sha256,
    AnalyzerReference Reference);

internal sealed record AliRoslynResolvedTargetReferences(
    IReadOnlyList<AliRoslynResolvedMetadataReference> MetadataReferences,
    IReadOnlyList<AliRoslynResolvedAnalyzerReference> AnalyzerReferences);

/// <summary>
/// Rehydrates the exact references already selected by the canonical
/// MSBuildWorkspace. It never manufactures references from assemblies loaded in
/// Ali's process.
/// </summary>
internal sealed class AliRoslynTargetReferenceResolver
{
    private const long MaximumReferenceBytes = 1024L * 1024 * 1024;

    public async Task<AliRoslynResolvedTargetReferences> ResolveAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var metadata = new List<AliRoslynResolvedMetadataReference>();
        foreach (var reference in project.MetadataReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference is not PortableExecutableReference portable)
            {
                throw new InvalidOperationException(
                    $"Project '{project.Name}' contains an unresolved or non-file metadata reference '{reference.Display ?? "<unknown>"}'.");
            }

            var candidate = portable.FilePath ?? portable.Display;
            var path = RequireExistingAbsoluteFile(candidate, "metadata", project.Name);
            await using var stream = OpenReferenceFile(path, "metadata", project.Name);
            var sha256 = await HashOpenFileAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var cloned = MetadataReference.CreateFromStream(
                stream,
                portable.Properties,
                documentation: null,
                filePath: path);
            try
            {
                _ = cloned.GetMetadata();
            }
            catch (Exception exception) when (IsRecoverableReferenceFailure(exception))
            {
                throw new InvalidOperationException(
                    $"Project '{project.Name}' metadata reference '{path}' could not be loaded exactly.",
                    exception);
            }

            metadata.Add(new(path, sha256, portable.Properties, cloned));
        }

        var analyzers = await ResolveAnalyzersAsync(
                project.AnalyzerReferences,
                $"project '{project.Name}'",
                cancellationToken)
            .ConfigureAwait(false);
        return new(metadata, analyzers);
    }

    public Task<IReadOnlyList<AliRoslynResolvedAnalyzerReference>> ResolveSolutionAnalyzersAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return ResolveAnalyzersAsync(solution.AnalyzerReferences, "solution", cancellationToken);
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            $"A Roslyn semantic input must be a regular no-follow file: {path}");
        RequireBoundedReference(stream, "semantic input", path);
        return await HashOpenFileAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AliRoslynResolvedAnalyzerReference>> ResolveAnalyzersAsync(
        IEnumerable<AnalyzerReference> references,
        string owner,
        CancellationToken cancellationToken)
    {
        var resolved = new List<AliRoslynResolvedAnalyzerReference>();
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = RequireExistingAbsoluteFile(reference.FullPath, "analyzer", owner);
            await using var stream = OpenReferenceFile(path, "analyzer", owner);
            var sha256 = await HashOpenFileAsync(stream, cancellationToken).ConfigureAwait(false);
            resolved.Add(new(path, sha256, reference));
        }

        return resolved;
    }

    private static FileStream OpenReferenceFile(
        string path,
        string kind,
        string owner)
    {
        var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            writeThrough: false,
            $"The {owner} {kind} reference must be a regular no-follow file.");
        try
        {
            RequireBoundedReference(stream, kind, path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void RequireBoundedReference(
        FileStream stream,
        string kind,
        string path)
    {
        if (stream.Length is <= 0 or > MaximumReferenceBytes)
        {
            throw new InvalidOperationException(
                $"The Roslyn {kind} reference is empty or outside the bounded size policy: {path}");
        }
    }

    private static async Task<string> HashOpenFileAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(digest);
    }

    private static string RequireExistingAbsoluteFile(string? candidate, string kind, string owner)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
        {
            throw new InvalidOperationException(
                $"The {owner} contains an unresolved {kind} reference without an exact physical path.");
        }

        var path = Path.GetFullPath(candidate);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The {owner} {kind} reference is missing: {path}");
        }
        return path;
    }

    private static bool IsRecoverableReferenceFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
}
