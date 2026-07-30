using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ali.Modules.Coding.Languages;

namespace Ali.Modules.Coding.Operations;

public sealed record AliCodeContextSnippet(string File, int Line, string Text, int Score);

public sealed record AliCodeContextReport(
    bool Success,
    string Summary,
    string Provider,
    int FilesConsidered,
    int FilesSelected,
    int CharactersReturned,
    bool Truncated,
    IReadOnlyList<AliCodeContextSnippet> Snippets);

public sealed record AliExternalServiceProbeResult(
    bool Success,
    string Summary,
    int? StatusCode,
    string MediaType,
    long DurationMilliseconds,
    bool Truncated,
    string ResponsePreview);

public sealed record AliRuntimeProcessSnapshot(
    bool Success,
    string Summary,
    int ProcessId,
    string ProcessName,
    DateTimeOffset? StartedAt,
    double TotalProcessorSeconds,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    bool Responding);

/// <summary>
/// Shared, bounded operations used by every language provider. This module narrows large
/// repositories into evidence-sized context, performs explicit read-only HTTP probes, and
/// captures live process facts without exposing a general shell.
/// </summary>
internal sealed class AliCodingOperations : IDisposable
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".py", ".pyi", ".js", ".jsx", ".mjs", ".cjs",
        ".ts", ".tsx", ".mts", ".cts", ".html", ".htm", ".css", ".scss", ".java", ".c",
        ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".ixx", ".cppm", ".json",
        ".xml", ".toml", ".gradle", ".kts", ".ino", ".yaml", ".yml"
    };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".ali", "bin", "obj", "node_modules", ".venv", "venv",
        "target", "build", "dist", "out", ".gradle"
    };
    private readonly AliLanguageProjectResolver _resolver;
    private readonly AliLanguageProviderRegistry _providers;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public AliCodingOperations(AliLanguageProjectResolver resolver, AliLanguageProviderRegistry providers)
        : this(resolver, providers, CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal AliCodingOperations(
        AliLanguageProjectResolver resolver,
        AliLanguageProviderRegistry providers,
        HttpClient httpClient,
        bool ownsHttpClient = false)
    {
        _resolver = resolver;
        _providers = providers;
        _http = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<AliCodeContextReport> BuildContextAsync(
        string targetPath,
        string query,
        int? maximumFiles,
        int? maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var project = _resolver.Resolve(targetPath);
        var provider = _providers.Resolve(project);
        var fileLimit = Math.Clamp(maximumFiles ?? 12, 1, 40);
        var characterLimit = Math.Clamp(maximumCharacters ?? 24_000, 2_000, 80_000);
        var terms = query.Split([' ', '\t', '\r', '\n', '.', ':', '/', '\\', '(', ')', '[', ']', '{', '}', '-', '_'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
        var candidates = Directory.EnumerateFiles(project.ProjectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => SourceExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(IgnoredDirectories.Contains))
            .Take(20_000)
            .ToArray();
        var ranked = new List<(string Path, string[] Lines, int Score)>();
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > 1_000_000) continue;
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var relative = Path.GetRelativePath(project.ProjectDirectory, path);
            var score = terms.Sum(term =>
                (relative.Contains(term, StringComparison.OrdinalIgnoreCase) ? 12 : 0)
                + Math.Min(20, CountOccurrences(text, term)));
            if (score > 0) ranked.Add((path, text.ReplaceLineEndings("\n").Split('\n'), score));
        }

        var snippets = new List<AliCodeContextSnippet>();
        var characters = 0;
        var truncated = false;
        foreach (var item in ranked.OrderByDescending(item => item.Score).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(fileLimit))
        {
            var lineIndex = FindBestLine(item.Lines, terms);
            var first = Math.Max(0, lineIndex - 3);
            var last = Math.Min(item.Lines.Length - 1, lineIndex + 5);
            var builder = new StringBuilder();
            for (var index = first; index <= last; index++) builder.AppendLine($"{index + 1}: {item.Lines[index]}");
            var text = builder.ToString().TrimEnd();
            if (characters + text.Length > characterLimit)
            {
                truncated = true;
                break;
            }
            characters += text.Length;
            snippets.Add(new(Path.GetRelativePath(project.ProjectDirectory, item.Path), first + 1, text, item.Score));
        }
        if (ranked.Count > snippets.Count) truncated = true;
        return new(
            true,
            snippets.Count == 0
                ? "No source snippet matched the requested focus. Refine the query or inspect the structural index."
                : $"Selected {snippets.Count} evidence-sized source snippet(s) for {provider.DisplayName} without loading the whole project into model context.",
            provider.Id,
            candidates.Length,
            snippets.Count,
            characters,
            truncated,
            snippets);
    }

    public async Task<AliExternalServiceProbeResult> ProbeHttpAsync(
        string targetPath,
        string url,
        string? method,
        CancellationToken cancellationToken)
    {
        _ = _resolver.Resolve(targetPath);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("The service URL must be an absolute HTTP or HTTPS URL without embedded credentials.", nameof(url));
        }
        var verb = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Head : HttpMethod.Get;
        using var request = new HttpRequestMessage(verb, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var timer = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var bytes = verb == HttpMethod.Head
            ? Array.Empty<byte>()
            : await ReadBoundedAsync(response.Content, 65_536, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
        var preview = bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
        var declaredLength = response.Content.Headers.ContentLength;
        var truncated = declaredLength.HasValue && declaredLength.Value > bytes.Length;
        return new(
            response.IsSuccessStatusCode,
            response.IsSuccessStatusCode
                ? "The approved external service responded successfully."
                : $"The approved external service returned HTTP {(int)response.StatusCode}.",
            (int)response.StatusCode,
            mediaType,
            timer.ElapsedMilliseconds,
            truncated,
            preview);
    }

    public AliRuntimeProcessSnapshot InspectProcess(string targetPath, int processId)
    {
        _ = _resolver.Resolve(targetPath);
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            DateTimeOffset? started = null;
            try { started = process.StartTime; } catch { }
            bool responding;
            try { responding = process.Responding; } catch { responding = true; }
            return new(
                true,
                "Captured a live, point-in-time process snapshot. This is runtime evidence, not a profiler trace.",
                process.Id,
                process.ProcessName,
                started,
                process.TotalProcessorTime.TotalSeconds,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.Threads.Count,
                responding);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, $"The process could not be inspected: {ex.Message}", processId, string.Empty, null, 0, 0, 0, 0, false);
        }
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while (count < 20 && (index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    private static int FindBestLine(IReadOnlyList<string> lines, IReadOnlyList<string> terms)
    {
        var best = 0;
        var bestScore = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var score = terms.Count(term => lines[index].Contains(term, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore) { bestScore = score; best = index; }
        }
        return best;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8_192];
        while (output.Length < maximum)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
