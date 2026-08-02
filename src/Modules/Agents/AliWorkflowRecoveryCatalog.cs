using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Reads Agent Framework's durable JSON checkpoints without inventing a second
/// workflow state format. Only checkpoints whose stable executor identities
/// match the current workflow graph are offered for recovery.
/// </summary>
internal sealed class AliWorkflowRecoveryCatalog(
    AliWorkflowCheckpointOwnership ownership)
{
    internal const int MaximumCheckpointFilesToInspect = 256;
    internal const int MaximumCheckpointDirectoryEntriesToScan = 1024;
    internal const int MaximumCheckpointFileBytes = 4 * 1024 * 1024;
    internal const int MaximumCheckpointBytesToInspect = 32 * 1024 * 1024;
    internal const int MaximumRecoverableWorkflows = 16;

    private readonly AliWorkflowCheckpointOwnership _ownership =
        ownership ?? throw new ArgumentNullException(nameof(ownership));

    public AliRecoverableWorkflowReport Inspect(
        IReadOnlyCollection<AliWorkflowRegistration> registrations,
        AliWorkflowCheckpointOwner owner)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(owner);
        var checkpointPath = _ownership.GetCheckpointDirectory(owner);
        if (!Directory.Exists(checkpointPath))
        {
            return EmptyReport();
        }

        var candidates = new SortedSet<CheckpointFileCandidate>(CheckpointFileCandidateComparer.Instance);
        var boundedSkipCount = 0;
        var scannedDirectoryEntries = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(checkpointPath, "*", SearchOption.TopDirectoryOnly))
            {
                scannedDirectoryEntries++;
                if (scannedDirectoryEntries > MaximumCheckpointDirectoryEntriesToScan)
                {
                    boundedSkipCount++;
                    break;
                }

                if (!TryParseCheckpointFileName(Path.GetFileName(file), out var sessionId, out var checkpointId))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(file);
                    candidates.Add(new CheckpointFileCandidate(
                        file,
                        sessionId,
                        checkpointId,
                        info.Length,
                        info.LastWriteTimeUtc));
                    if (candidates.Count > MaximumCheckpointFilesToInspect)
                    {
                        candidates.Remove(candidates.Max!);
                        boundedSkipCount++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A checkpoint can disappear while the bounded candidate set is built.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep already collected candidates, but make the bounded inspection visible.
            boundedSkipCount++;
        }

        var latestBySession = new Dictionary<string, CheckpointSnapshot>(StringComparer.Ordinal);
        long inspectedBytes = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Length is <= 0 or > MaximumCheckpointFileBytes
                || inspectedBytes + candidate.Length > MaximumCheckpointBytesToInspect)
            {
                boundedSkipCount++;
                continue;
            }

            inspectedBytes += candidate.Length;
            try
            {
                using var document = ReadBoundedCheckpoint(
                    candidate.Path,
                    candidate.Length,
                    out var exceededBound);
                if (document is null)
                {
                    if (exceededBound)
                    {
                        boundedSkipCount++;
                    }
                    continue;
                }
                var root = document.RootElement;
                if (!_ownership.IsOwnedBy(root, owner))
                {
                    continue;
                }
                var step = root.TryGetProperty("stepNumber", out var stepElement)
                    && stepElement.TryGetInt32(out var parsedStep)
                    ? parsedStep
                    : -1;
                var snapshot = new CheckpointSnapshot(
                    candidate.SessionId,
                    candidate.CheckpointId,
                    step,
                    candidate.UpdatedAtUtc,
                    IsPending(root),
                    ExtractObjective(root),
                    ExtractExecutorIds(root),
                    ExtractStartExecutorId(root));
                if (!latestBySession.TryGetValue(candidate.SessionId, out var current)
                    || snapshot.StepNumber > current.StepNumber
                    || (snapshot.StepNumber == current.StepNumber && snapshot.UpdatedAtUtc > current.UpdatedAtUtc))
                {
                    latestBySession[candidate.SessionId] = snapshot;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A process can stop between creating and finishing one JSON file.
                // Ignore that incomplete file; the preceding checkpoint remains valid.
            }
        }

        var allRecoverable = latestBySession.Values
            .Where(snapshot => snapshot.IsPending)
            .Select(snapshot => Match(snapshot, registrations))
            .Where(run => run is not null)
            .Cast<AliRecoverableWorkflowRun>()
            .OrderByDescending(run => run.UpdatedAt)
            .ThenBy(run => run.SessionId, StringComparer.Ordinal)
            .ToArray();
        var recoverable = allRecoverable
            .Take(MaximumRecoverableWorkflows)
            .ToArray();
        var isTruncated = boundedSkipCount > 0
            || allRecoverable.Length > recoverable.Length;
        var summary = recoverable.Length == 0
            ? "No interrupted Agent Framework workflows are waiting for recovery."
            : $"{recoverable.Length} interrupted Agent Framework workflow(s) can be resumed from their latest local checkpoint.";
        if (isTruncated)
        {
            summary += " Recovery inspection was bounded; older, oversized, or excess checkpoint records were left untouched and not exposed.";
        }
        return new AliRecoverableWorkflowReport(
            summary,
            recoverable,
            isTruncated,
            boundedSkipCount);
    }

    private static AliRecoverableWorkflowReport EmptyReport() =>
        new(
            "No interrupted Agent Framework workflows are waiting for recovery.",
            []);

    private static JsonDocument? ReadBoundedCheckpoint(
        string path,
        long expectedLength,
        out bool exceededBound)
    {
        exceededBound = false;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length is <= 0 or > MaximumCheckpointFileBytes)
        {
            exceededBound = true;
            return null;
        }
        if (length != expectedLength)
        {
            return null;
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        stream.ReadExactly(bytes);
        if (stream.ReadByte() >= 0 || stream.Length != length)
        {
            return null;
        }

        return JsonDocument.Parse(bytes);
    }

    private static AliRecoverableWorkflowRun? Match(
        CheckpointSnapshot snapshot,
        IEnumerable<AliWorkflowRegistration> registrations)
    {
        var registration = registrations.FirstOrDefault(candidate =>
            (candidate.StartExecutorId is null
             || ExecutorIdMatches(snapshot.StartExecutorId, candidate.StartExecutorId))
            && candidate.RequiredExecutorIds.All(requiredId =>
                snapshot.ExecutorIds.Any(actualId => ExecutorIdMatches(actualId, requiredId))));
        return registration is null
            ? null
            : new AliRecoverableWorkflowRun(
                snapshot.SessionId,
                registration.Kind,
                registration.DisplayName,
                snapshot.Objective,
                snapshot.StepNumber,
                new DateTimeOffset(snapshot.UpdatedAtUtc, TimeSpan.Zero),
                snapshot.CheckpointId);
    }

    // Agent Framework prefixes an agent's stable Id with its display Name when
    // it materializes the workflow graph (for example,
    // "SoftwareEngineer_ali-specialist-software-engineer"). Match the exact
    // graph executor or that stable suffix so a renamed display label does not
    // strand a durable checkpoint.
    private static bool ExecutorIdMatches(string? actualId, string requiredId) =>
        !string.IsNullOrWhiteSpace(actualId)
        && (string.Equals(actualId, requiredId, StringComparison.Ordinal)
            || NormalizeExecutorId(actualId).EndsWith(
                "_" + NormalizeExecutorId(requiredId),
                StringComparison.Ordinal));

    private static string NormalizeExecutorId(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    private static bool IsPending(JsonElement root)
    {
        if (!root.TryGetProperty("runnerData", out var runnerData))
        {
            return false;
        }

        return HasEntries(runnerData, "queuedMessages")
               || HasEntries(runnerData, "outstandingRequests");
    }

    private static bool HasEntries(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => false
        };
    }

    private static HashSet<string> ExtractExecutorIds(JsonElement root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("workflow", out var workflow)
            && workflow.TryGetProperty("executors", out var executors)
            && executors.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in executors.EnumerateObject())
            {
                ids.Add(property.Name);
            }
        }

        return ids;
    }

    private static string? ExtractStartExecutorId(JsonElement root) =>
        root.TryGetProperty("workflow", out var workflow)
        && workflow.TryGetProperty("startExecutorId", out var start)
        && start.ValueKind == JsonValueKind.String
            ? start.GetString()
            : null;

    private static string ExtractObjective(JsonElement root)
    {
        var text = FindFirstUserText(root);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Interrupted workflow objective was preserved in the checkpoint.";
        }

        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 320 ? compact : compact[..320] + "...";
    }

    private static string? FindFirstUserText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("role", out var role)
                && string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase)
                && element.TryGetProperty("contents", out var contents))
            {
                var text = FindTextContent(contents);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindFirstUserText(property.Value);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindFirstUserText(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? FindTextContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindTextContent(child);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var found = FindTextContent(property.Value);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool TryParseCheckpointFileName(
        string fileName,
        out string sessionId,
        out string checkpointId)
    {
        sessionId = string.Empty;
        checkpointId = string.Empty;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(fileName);
        }
        catch (UriFormatException)
        {
            return false;
        }
        if (!decoded.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = decoded[..^5];
        var separator = stem.IndexOf('_');
        if (separator <= 0 || separator >= stem.Length - 1)
        {
            return false;
        }

        sessionId = stem[..separator];
        checkpointId = stem[(separator + 1)..];
        return sessionId.Length > 0 && checkpointId.Length > 0;
    }

    private sealed record CheckpointSnapshot(
        string SessionId,
        string CheckpointId,
        int StepNumber,
        DateTime UpdatedAtUtc,
        bool IsPending,
        string Objective,
        HashSet<string> ExecutorIds,
        string? StartExecutorId);

    private sealed record CheckpointFileCandidate(
        string Path,
        string SessionId,
        string CheckpointId,
        long Length,
        DateTime UpdatedAtUtc);

    private sealed class CheckpointFileCandidateComparer : IComparer<CheckpointFileCandidate>
    {
        public static CheckpointFileCandidateComparer Instance { get; } = new();

        public int Compare(CheckpointFileCandidate? left, CheckpointFileCandidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return 1;
            }
            if (right is null)
            {
                return -1;
            }

            var updated = right.UpdatedAtUtc.CompareTo(left.UpdatedAtUtc);
            return updated != 0
                ? updated
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        }
    }
}

internal sealed record AliWorkflowRegistration(
    string Kind,
    string DisplayName,
    Workflow Workflow,
    string? StartExecutorId,
    IReadOnlyList<string> RequiredExecutorIds);

public sealed record AliRecoverableWorkflowRun(
    string SessionId,
    string WorkflowKind,
    string WorkflowName,
    string Objective,
    int CompletedStep,
    DateTimeOffset UpdatedAt,
    string CheckpointId);

public sealed record AliRecoverableWorkflowReport(
    string Summary,
    IReadOnlyList<AliRecoverableWorkflowRun> Workflows,
    bool IsTruncated = false,
    int SkippedCheckpointFiles = 0);

public sealed record AliWorkflowResumeResult(
    bool Success,
    string Summary,
    string SessionId,
    string WorkflowName,
    string Output);
