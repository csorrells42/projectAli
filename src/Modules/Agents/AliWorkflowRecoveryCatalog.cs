using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Reads Agent Framework's durable JSON checkpoints without inventing a second
/// workflow state format. Only checkpoints whose stable executor identities
/// match the current workflow graph are offered for recovery.
/// </summary>
internal sealed class AliWorkflowRecoveryCatalog(string checkpointPath)
{
    private readonly string _checkpointPath = Path.GetFullPath(checkpointPath);

    public AliRecoverableWorkflowReport Inspect(
        IReadOnlyCollection<AliWorkflowRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        Directory.CreateDirectory(_checkpointPath);

        var latestBySession = new Dictionary<string, CheckpointSnapshot>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_checkpointPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!TryParseCheckpointFileName(Path.GetFileName(file), out var sessionId, out var checkpointId))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var step = root.TryGetProperty("stepNumber", out var stepElement)
                    && stepElement.TryGetInt32(out var parsedStep)
                    ? parsedStep
                    : -1;
                var snapshot = new CheckpointSnapshot(
                    sessionId,
                    checkpointId,
                    step,
                    File.GetLastWriteTimeUtc(file),
                    IsPending(root),
                    ExtractObjective(root),
                    ExtractExecutorIds(root),
                    ExtractStartExecutorId(root));
                if (!latestBySession.TryGetValue(sessionId, out var current)
                    || snapshot.StepNumber > current.StepNumber
                    || (snapshot.StepNumber == current.StepNumber && snapshot.UpdatedAtUtc > current.UpdatedAtUtc))
                {
                    latestBySession[sessionId] = snapshot;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A process can stop between creating and finishing one JSON file.
                // Ignore that incomplete file; the preceding checkpoint remains valid.
            }
        }

        var recoverable = latestBySession.Values
            .Where(snapshot => snapshot.IsPending)
            .Select(snapshot => Match(snapshot, registrations))
            .Where(run => run is not null)
            .Cast<AliRecoverableWorkflowRun>()
            .OrderByDescending(run => run.UpdatedAt)
            .ToArray();
        return new AliRecoverableWorkflowReport(
            recoverable.Length == 0
                ? "No interrupted Agent Framework workflows are waiting for recovery."
                : $"{recoverable.Length} interrupted Agent Framework workflow(s) can be resumed from their latest local checkpoint.",
            recoverable);
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
        var decoded = Uri.UnescapeDataString(fileName);
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
    IReadOnlyList<AliRecoverableWorkflowRun> Workflows);

public sealed record AliWorkflowResumeResult(
    bool Success,
    string Summary,
    string SessionId,
    string WorkflowName,
    string Output);
