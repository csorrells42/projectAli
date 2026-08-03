using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ali.Modules.EngineeringCertification;

internal sealed class EngineeringCertificationRunStorage
{
    internal const int MaximumRawEvidenceCharacters = 65_536;
    internal const int MaximumStoredEvidenceBytes = 524_288;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _root;

    internal EngineeringCertificationRunStorage(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    internal string GetRunDirectory(string runId) =>
        Path.Combine(_root, SafeSegment(runId));

    internal async Task<EngineeringCertificationRunInitialization> InitializeRunAsync(
        EngineeringCertificationRunRequest request,
        string suiteDigest,
        EngineeringCandidateDiscoveryResult discovery,
        CancellationToken cancellationToken)
    {
        request.Validate();
        RequireDigest(suiteDigest);
        var runDirectory = GetRunDirectory(request.RunId);
        Directory.CreateDirectory(runDirectory);
        EnsureManagedDirectory(runDirectory, GetRunDirectory(request.RunId));

        var suiteSnapshot = new SuiteSnapshot(
            request.Suite.Version,
            suiteDigest,
            request.Suite.Tasks,
            "Every discovered candidate receives this exact ordered task set. Task definitions are evaluation fixtures, not request-routing rules.");
        var suitePath = Path.Combine(runDirectory, "suite.json");
        if (File.Exists(suitePath))
        {
            var existing = await ReadBoundedJsonAsync<SuiteSnapshot>(suitePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(existing.Version, request.Suite.Version, StringComparison.Ordinal)
                || !string.Equals(existing.Digest, suiteDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Resume refused because the stored run uses a different certification suite.");
            }
        }
        else
        {
            await WriteJsonAtomicAsync(suitePath, suiteSnapshot, cancellationToken).ConfigureAwait(false);
        }

        var inventoryPath = Path.Combine(runDirectory, "candidate-inventory.json");
        EngineeringCandidateDiscoveryResult frozenDiscovery;
        if (File.Exists(inventoryPath))
        {
            frozenDiscovery = await ReadBoundedJsonAsync<EngineeringCandidateDiscoveryResult>(
                inventoryPath,
                cancellationToken).ConfigureAwait(false);
            var frozenBindings = frozenDiscovery.Candidates
                .Select(candidate => candidate.BindingDigest)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var currentBindings = discovery.Candidates
                .Select(candidate => candidate.BindingDigest)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!frozenBindings.SequenceEqual(currentBindings, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "Resume refused because the dynamically discovered candidate inventory changed. Start a new run id for the new inventory.");
            }
        }
        else
        {
            frozenDiscovery = discovery;
            await WriteJsonAtomicAsync(
                inventoryPath,
                frozenDiscovery,
                cancellationToken).ConfigureAwait(false);
        }
        return new EngineeringCertificationRunInitialization(runDirectory, frozenDiscovery);
    }

    internal int GetNextAttempt(
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task)
    {
        var taskDirectory = GetTaskDirectory(runDirectory, candidate, task);
        if (!Directory.Exists(taskDirectory))
        {
            return 1;
        }

        var attempts = Directory.EnumerateDirectories(taskDirectory, "a*")
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name?[1..], out var value) ? value : 0)
            .DefaultIfEmpty(0)
            .Max();
        return checked(attempts + 1);
    }

    internal async Task<string> PrepareIsolatedWorkspaceAsync(
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }
        task.Validate();
        var workspace = Path.Combine(
            GetTaskDirectory(runDirectory, candidate, task),
            $"a{attempt}",
            "w");
        if (Directory.Exists(workspace))
        {
            throw new IOException($"Certification workspace attempt {attempt} already exists.");
        }

        Directory.CreateDirectory(workspace);
        EnsureManagedDirectory(workspace, runDirectory);
        foreach (var fixtureFile in task.FixtureFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveFixturePath(workspace, fixtureFile.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                fixtureFile.Content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
        }

        var fixtureDigest = ComputeFixtureDigest(task);
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".certification-fixture.sha256"),
            fixtureDigest + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
        return workspace;
    }

    internal async Task<EngineeringCertificationTaskEvidence?> TryReadEvidenceAsync(
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task,
        string suiteDigest,
        CancellationToken cancellationToken)
    {
        var path = GetEvidencePath(runDirectory, candidate, task);
        if (!File.Exists(path))
        {
            return null;
        }
        var evidence = await ReadBoundedJsonAsync<EngineeringCertificationTaskEvidence>(
            path,
            cancellationToken).ConfigureAwait(false);
        return string.Equals(evidence.SuiteDigest, suiteDigest, StringComparison.Ordinal)
               && string.Equals(evidence.CandidateBindingDigest, candidate.BindingDigest, StringComparison.Ordinal)
               && string.Equals(evidence.TaskId, task.Id, StringComparison.Ordinal)
            ? evidence
            : throw new InvalidDataException("Stored certification evidence does not match the resumed task binding.");
    }

    internal async Task<EngineeringCertificationTaskEvidence> SaveEvidenceAsync(
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task,
        EngineeringCertificationTaskEvidence evidence,
        CancellationToken cancellationToken)
    {
        var evidenceDirectory = Path.Combine(GetTaskDirectory(runDirectory, candidate, task), "e");
        Directory.CreateDirectory(evidenceDirectory);
        var rawPath = Path.Combine(evidenceDirectory, "raw-evidence.txt");
        var boundedAgent = Bound(evidence.Agent.RawEvidence);
        var boundedBaseline = Bound(evidence.Baseline.RawEvidence);
        var boundedVerifier = Bound(evidence.Verification.RawEvidence);
        var raw = string.Join(
            Environment.NewLine,
            "=== AUTHORITATIVE AGENT LOOP ===",
            boundedAgent,
            "=== PRE-EXECUTION ROSLYN BASELINE ===",
            boundedBaseline,
            "=== INDEPENDENT VERIFIER ===",
            boundedVerifier);
        await File.WriteAllTextAsync(rawPath, raw, cancellationToken).ConfigureAwait(false);

        var stored = evidence with
        {
            Agent = evidence.Agent with { RawEvidence = boundedAgent },
            Baseline = evidence.Baseline with { RawEvidence = boundedBaseline },
            Verification = evidence.Verification with { RawEvidence = boundedVerifier },
            RawEvidencePath = rawPath
        };
        await WriteJsonAtomicAsync(
            GetEvidencePath(runDirectory, candidate, task),
            stored,
            cancellationToken).ConfigureAwait(false);
        return stored;
    }

    internal async Task<IReadOnlyList<EngineeringCertificationTaskEvidence>> ReadAllEvidenceAsync(
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var files = Directory.Exists(runDirectory)
            ? Directory.EnumerateFiles(runDirectory, "result.json", SearchOption.AllDirectories)
                .Take(OpenAiCertificationCandidateSource.MaximumCandidates
                      * EngineeringCertificationSuite.MaximumTaskCount + 1)
                .ToArray()
            : [];
        if (files.Length > OpenAiCertificationCandidateSource.MaximumCandidates
            * EngineeringCertificationSuite.MaximumTaskCount)
        {
            throw new InvalidDataException("Certification result storage exceeded its bounded result count.");
        }

        var evidence = new List<EngineeringCertificationTaskEvidence>(files.Length);
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            evidence.Add(await ReadBoundedJsonAsync<EngineeringCertificationTaskEvidence>(
                file,
                cancellationToken).ConfigureAwait(false));
        }
        return evidence;
    }

    internal Task WriteComparisonJsonAsync(
        string runDirectory,
        EngineeringCertificationComparisonReport report,
        CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(Path.Combine(runDirectory, "comparison.json"), report, cancellationToken);

    internal Task WriteComparisonMarkdownAsync(
        string runDirectory,
        string markdown,
        CancellationToken cancellationToken) =>
        WriteTextAtomicAsync(Path.Combine(runDirectory, "comparison.md"), markdown, cancellationToken);

    internal Task WriteCandidateReportAsync(
        string runDirectory,
        EngineeringCertificationCandidateReport report,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(runDirectory, "r", SafeSegment(report.CandidateId));
        Directory.CreateDirectory(directory);
        return WriteJsonAtomicAsync(Path.Combine(directory, "report.json"), report, cancellationToken);
    }

    internal Task WriteCandidateMarkdownAsync(
        string runDirectory,
        EngineeringCertificationCandidateReport report,
        string markdown,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(runDirectory, "r", SafeSegment(report.CandidateId));
        Directory.CreateDirectory(directory);
        return WriteTextAtomicAsync(Path.Combine(directory, "report.md"), markdown, cancellationToken);
    }

    private static string GetEvidencePath(
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task) =>
        Path.Combine(GetTaskDirectory(runDirectory, candidate, task), "e", "result.json");

    private static string GetTaskDirectory(
        string runDirectory,
        EngineeringCertificationCandidate candidate,
        EngineeringCertificationTask task) =>
        Path.Combine(
            Path.GetFullPath(runDirectory),
            "c",
            SafeSegment(candidate.CandidateId),
            "t",
            SafeSegment(task.Id));

    private static string ResolveFixturePath(string workspace, string relativePath)
    {
        var root = Path.GetFullPath(workspace);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Fixture path '{relativePath}' escapes its isolated workspace.");
        }
        return path;
    }

    private static string ComputeFixtureDigest(EngineeringCertificationTask task)
    {
        var canonical = string.Join(
            "\n",
            task.FixtureFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => $"{file.RelativePath}\n{file.Content}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SafeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var readable = new string(value
            .Take(24)
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_')
            .ToArray());
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8]
            .ToLowerInvariant();
        return $"{readable}-{digest}";
    }

    private static void EnsureManagedDirectory(string path, string boundary)
    {
        var fullPath = Path.GetFullPath(path);
        var fullBoundary = Path.GetFullPath(boundary);
        if (!fullPath.Equals(fullBoundary, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(fullBoundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Certification storage escaped its exact run boundary.");
        }
        var current = new DirectoryInfo(fullPath);
        while (current is not null
               && current.FullName.StartsWith(fullBoundary, StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Certification storage refused reparse-point directory '{current.FullName}'.");
            }
            if (current.FullName.Equals(fullBoundary, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent;
        }
    }

    private static async Task<T> ReadBoundedJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumStoredEvidenceBytes)
        {
            throw new InvalidDataException($"Certification evidence '{path}' exceeds the bounded file size.");
        }
        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException($"Certification evidence '{path}' is empty.");
    }

    private static Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        WriteTextAtomicAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private static async Task WriteTextAtomicAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, value, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Bound(string value) =>
        value.Length <= MaximumRawEvidenceCharacters
            ? value
            : value[..MaximumRawEvidenceCharacters];

    private static void RequireDigest(string digest)
    {
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Certification suite digest is invalid.");
        }
    }

    private sealed record SuiteSnapshot(
        string Version,
        string Digest,
        IReadOnlyList<EngineeringCertificationTask> Tasks,
        string Scope);
}
