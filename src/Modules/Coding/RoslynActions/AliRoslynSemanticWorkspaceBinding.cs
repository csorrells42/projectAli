using Ali.Modules.Coding.Execution;
using Ali.Modules.Orchestration;
using Ali.Modules.Orchestration.Work;

namespace Ali.Modules.Coding.RoslynActions;

internal sealed record AliRoslynStaticWorkspaceTarget(
    AliResolvedCodingTarget Target,
    string? DocumentPhysicalPath);

/// <summary>
/// Binds a broker decision to a bounded, no-follow snapshot of the selected target and its
/// ordinary source tree. Static capture never evaluates MSBuild or opens an MSBuildWorkspace.
/// After that durable authorization exists, the loaded Roslyn solution receives a separate,
/// retained semantic fingerprint for action identity, preview, verification, and drift checks.
/// </summary>
internal sealed class AliRoslynSemanticWorkspaceBinding(
    AliRoslynWorkspaceLoader workspaceLoader,
    AliRoslynSolutionFingerprint fingerprint)
{
    internal const string StaticSourceRevisionVersionKey = "staticSourceRevision";

    private readonly AliRoslynWorkspaceLoader _workspaceLoader =
        workspaceLoader ?? throw new ArgumentNullException(nameof(workspaceLoader));
    private readonly AliRoslynSolutionFingerprint _fingerprint =
        fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));

    internal IReadOnlyDictionary<string, string> Capture(
        AliResolvedCodingTarget expectedTarget,
        string? documentPhysicalPath,
        IReadOnlyDictionary<string, string>? additionalVersions = null)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        ValidateRequestedDocument(expectedTarget, documentPhysicalPath);
        return CapturePhysicalInputs(expectedTarget, additionalVersions);
    }

    internal void RequireStaticGrantVersion(
        AliExecutionGrant grant,
        AliResolvedCodingTarget expectedTarget,
        string? documentPhysicalPath,
        IReadOnlyDictionary<string, string>? additionalVersions = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var currentDigest = TargetVersionDigest(Capture(
            expectedTarget,
            documentPhysicalPath,
            additionalVersions));
        if (!string.Equals(
                currentDigest,
                grant.TargetVersionDigest,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Roslyn static source revision changed after the broker decision was prepared.");
        }
    }

    internal async Task<AliRoslynSolutionFingerprintSnapshot> BindLoadedAsync(
        AliRoslynWorkspaceSession session,
        AliResolvedCodingTarget expectedTarget,
        string? documentPhysicalPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectedTarget);
        RequireSameTarget(session.Target, expectedTarget);
        await AliRoslynWorkspaceLoader.RequireExactLoadAsync(session, cancellationToken)
            .ConfigureAwait(false);
        if (documentPhysicalPath is not null)
        {
            RequireLoadedDocument(session, documentPhysicalPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var semanticFingerprint = await _fingerprint.CaptureAsync(
                session.Solution,
                cancellationToken)
            .ConfigureAwait(false);
        await AliRoslynWorkspaceLoader.RequireExactLoadAsync(session, cancellationToken)
            .ConfigureAwait(false);
        session.BindSemanticFingerprint(semanticFingerprint);
        return semanticFingerprint;
    }

    internal async Task RequireBoundSemanticFingerprintAsync(
        AliRoslynWorkspaceSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var expected = session.SemanticFingerprint;
        var current = await _fingerprint.CaptureAsync(
                session.Solution,
                cancellationToken)
            .ConfigureAwait(false);
        await AliRoslynWorkspaceLoader.RequireExactLoadAsync(session, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                current.Sha256,
                expected.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The loaded Roslyn semantic workspace changed after its exact fingerprint was bound.");
        }
    }

    internal static string TargetVersionDigest(
        IReadOnlyDictionary<string, string> versions) =>
        WorkIdentityCanonicalizer.MapDigest(
            "action-target-versions-v1",
            versions);

    private static IReadOnlyDictionary<string, string> CapturePhysicalInputs(
        AliResolvedCodingTarget expectedTarget,
        IReadOnlyDictionary<string, string>? additionalVersions)
    {
        var selectedTarget = AliCodingExecutionAssetFingerprint.CaptureRequiredFile(
            expectedTarget.PhysicalPath,
            "The selected Roslyn target");
        var staticInputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["selectedTarget"] = selectedTarget.Identity,
            ["sourceTree"] = AliCodingInputFingerprint.CaptureTree(
                expectedTarget.RootDirectory),
            ["generatedOutputLayout"] = AliGeneratedOutputLayoutFingerprint.Capture(
                expectedTarget.RootDirectory)
        };
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StaticSourceRevisionVersionKey] = WorkIdentityCanonicalizer.MapDigest(
                "ali-roslyn-static-source-revision-v1",
                staticInputs)
        };
        foreach (var pair in additionalVersions
                     ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!versions.TryAdd(pair.Key, pair.Value))
            {
                throw new InvalidDataException(
                    "A semantic workspace version key was duplicated.");
            }
        }
        return versions;
    }

    private static void ValidateRequestedDocument(
        AliResolvedCodingTarget expectedTarget,
        string? documentPhysicalPath)
    {
        if (documentPhysicalPath is null)
        {
            return;
        }
        var document = Path.GetFullPath(documentPhysicalPath);
        AliCodingProjectResolver.RejectReparsePoints(expectedTarget.MountRoot, document);
        if (!File.Exists(document))
        {
            throw new FileNotFoundException(
                "The exact requested Roslyn document does not exist.",
                document);
        }
    }

    private static void RequireSameTarget(
        AliResolvedCodingTarget loaded,
        AliResolvedCodingTarget expected)
    {
        var loadedPath = Path.GetFullPath(loaded.PhysicalPath);
        var expectedPath = Path.GetFullPath(expected.PhysicalPath);
        if (!PathComparer.Equals(loadedPath, expectedPath)
            || loaded.IsSolution != expected.IsSolution)
        {
            throw new InvalidOperationException(
                "Roslyn loaded a different target than the exact brokered target.");
        }
    }

    private static void RequireLoadedDocument(
        AliRoslynWorkspaceSession session,
        string documentPhysicalPath)
    {
        var expectedPath = Path.GetFullPath(documentPhysicalPath);
        var loaded = session.Solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => document.FilePath is not null
                && PathComparer.Equals(Path.GetFullPath(document.FilePath), expectedPath))
            .Take(2)
            .ToArray();
        if (loaded.Length != 1)
        {
            throw new InvalidOperationException(
                loaded.Length == 0
                    ? "Roslyn did not load the exact requested document into the semantic workspace."
                    : "The requested physical document is shared by more than one Roslyn project; an exact project identity is required.");
        }
    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
