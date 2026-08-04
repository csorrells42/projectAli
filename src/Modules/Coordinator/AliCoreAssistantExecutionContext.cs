using System.Collections.Concurrent;

namespace Ali.Modules.Coordinator;

/// <summary>
/// Marks the temporary journal-free assistant path. Workspace validation remains authoritative;
/// durable execution grants are intentionally bypassed while the core functionality gate is active.
/// </summary>
internal static class AliCoreAssistantExecutionContext
{
    private static readonly ConcurrentDictionary<string, CoreAssistantFileBaseline> FileBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly AsyncLocal<string?> ActiveProjectPath = new();
    private static int _activeScopes;

    internal static bool IsActive => Volatile.Read(ref _activeScopes) > 0;

    internal static CoreAssistantFileBaseline CaptureFileBaseline(
        string physicalPath,
        string currentContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentNullException.ThrowIfNull(currentContent);
        var baseline = new CoreAssistantFileBaseline(
            currentContent.Length,
            currentContent.Count(character => character == '\n') + 1);
        return FileBaselines.GetOrAdd(Path.GetFullPath(physicalPath), baseline);
    }

    internal static void BindActiveProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var normalized = projectPath
            .Trim()
            .Trim('"', '\'', '`')
            .Replace('\\', '/')
            .Trim('/');
        var separator = normalized.LastIndexOf('/');
        if (separator > 0)
        {
            ActiveProjectPath.Value = normalized[..separator];
        }
    }

    internal static string RebaseToActiveProject(string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        var activeProject = ActiveProjectPath.Value;
        var normalized = modelPath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(activeProject)
            || string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathFullyQualified(normalized)
            || normalized.Equals(activeProject, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(activeProject + "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        const string workspacePrefix = "Workspace/";
        if (normalized.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalized[workspacePrefix.Length..];
            return relative.Contains('/')
                ? normalized
                : activeProject + "/" + relative;
        }

        var projectName = activeProject[(activeProject.LastIndexOf('/') + 1)..];
        return normalized.StartsWith(projectName + "/", StringComparison.OrdinalIgnoreCase)
            ? workspacePrefix + normalized
            : activeProject + "/" + normalized;
    }

    internal static IDisposable Enter()
    {
        var previousProjectPath = ActiveProjectPath.Value;
        ActiveProjectPath.Value = null;
        if (Interlocked.Increment(ref _activeScopes) == 1)
        {
            FileBaselines.Clear();
        }
        return new Scope(previousProjectPath);
    }

    private sealed class Scope(string? previousProjectPath) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ActiveProjectPath.Value = previousProjectPath;
            if (Interlocked.Decrement(ref _activeScopes) == 0)
            {
                FileBaselines.Clear();
            }
        }
    }
}

internal readonly record struct CoreAssistantFileBaseline(int CharacterLength, int LineCount);
