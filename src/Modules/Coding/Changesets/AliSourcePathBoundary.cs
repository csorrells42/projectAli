using System.Text;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Changesets;

internal sealed partial class AliSourceChangeSetStore
{
    internal static string NormalizeRelativePath(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        if (candidate.Length > MaximumRelativePathCharacters
            || Path.IsPathRooted(candidate)
            || candidate.IndexOf('\0') >= 0
            || candidate.EndsWith('/')
            || candidate.EndsWith('\\'))
        {
            throw new ArgumentException("A source changeset path must be a bounded relative path.", nameof(candidate));
        }

        var normalized = candidate.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => !IsSafePathSegment(segment)))
        {
            throw new ArgumentException("A source changeset path contains an unsafe component.", nameof(candidate));
        }
        return string.Join('/', segments);
    }

    internal static string ResolveContainedPath(string canonicalRoot, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A source changeset path escaped its approved root.");
        }
        return fullPath;
    }

    private static async Task<byte[]> ReadBoundedStoreFileAsync(
        string path,
        int maximumBytes,
        string invalidMessage,
        CancellationToken cancellationToken)
    {
        var bytes = await WindowsBoundedFileReader.TryReadExactlyAsync(
            WindowsOrchestrationFileBoundary.ToExtendedLengthWin32Path(path),
            minimumLength: 1,
            maximumBytes,
            invalidMessage,
            invalidMessage,
            invalidMessage,
            cancellationToken).ConfigureAwait(false);
        return bytes ?? throw new FileNotFoundException(invalidMessage, path);
    }

    internal static async Task WriteFileDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        bool replaceExisting)
    {
        // This path-addressed helper is restricted to the same-user protected transaction store.
        // Canonical publication and staged-tree materialization never call it.
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("A durable source file path has no parent directory.");
        WindowsOrchestrationFileBoundary.ValidateRegularDirectoryPath(
            directory,
            "A durable source file parent is not a regular local directory.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = WindowsOrchestrationFileBoundary.OpenRegularFile(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             writeThrough: true,
                             "A durable source temporary file is not a regular local file."))
            {
                if (!content.IsEmpty)
                {
                    await stream.WriteAsync(content, CancellationToken.None).ConfigureAwait(false);
                }
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            WindowsOrchestrationFileBoundary.MoveRegularFile(
                temporaryPath,
                fullPath,
                replaceExisting,
                "A durable source file is not a regular local file.");
        }
        finally
        {
            _ = WindowsOrchestrationFileBoundary.DeleteRegularFileNoFollow(
                temporaryPath,
                "A durable source temporary file is not a regular local file.");
        }
    }

    private static bool IsSafePathSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.StartsWith(' ')
            || segment.EndsWith(' ')
            || segment.EndsWith('.')
            || segment.Contains(':')
            || segment.Any(char.IsControl)
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        var compatibilityName = segment.Normalize(NormalizationForm.FormKC);
        var stem = compatibilityName.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        return stem is not (
            "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
    }

}
