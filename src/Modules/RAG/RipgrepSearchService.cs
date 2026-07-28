using System.Diagnostics;
using System.Text.Json;
using Ali.Modules.Internet;

namespace Ali.Modules.RAG;

public sealed class RipgrepSearchService
{
    private const int MaximumLineCharacters = 1_200;

    public string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "dependencies", "ripgrep", "rg.exe");

    public bool IsAvailable => File.Exists(ExecutablePath);

    public async Task<IReadOnlyList<SourceExcerpt>> SearchAsync(
        string searchRoot,
        IEnumerable<string> terms,
        IReadOnlyList<string> allowedExtensions,
        int maximumResults,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable || (!Directory.Exists(searchRoot) && !File.Exists(searchRoot)))
        {
            return [];
        }

        var patterns = terms
            .Select(term => term.Trim())
            .Where(term => term.Length >= 2 && term.Length <= 160)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (patterns.Length == 0)
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[]
                 {
                     "--json", "--no-config", "--no-ignore", "--fixed-strings", "--smart-case",
                     "--line-number", "--column", "--color", "never", "--threads", "2",
                     "--max-count", "2", "--max-filesize", "1M"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var extension in allowedExtensions.Select(value => value.TrimStart('.')).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--glob");
            startInfo.ArgumentList.Add($"*.{extension}");
        }
        foreach (var pattern in patterns)
        {
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(pattern);
        }
        startInfo.ArgumentList.Add(searchRoot);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start Ali's bundled ripgrep process.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var errorsTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        var results = new List<SourceExcerpt>();
        var stoppedAtLimit = false;
        try
        {
            while (results.Count < Math.Max(1, maximumResults))
            {
                var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (TryParseMatch(line, results.Count + 1) is { } match)
                {
                    results.Add(match);
                }
            }

            if (!process.HasExited && results.Count >= Math.Max(1, maximumResults))
            {
                stoppedAtLimit = true;
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var error = (await errorsTask.ConfigureAwait(false)).ReplaceLineEndings(" ").Trim();
            if (process.ExitCode > 1 && !stoppedAtLimit)
            {
                throw new InvalidOperationException($"ripgrep exited with code {process.ExitCode}. {error}");
            }
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
            return results;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!process.HasExited) process.WaitForExit(2_000);
        }
    }

    private static SourceExcerpt? TryParseMatch(string json, int index)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "match"
                || !root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("path", out var pathElement)
                || !pathElement.TryGetProperty("text", out var pathText)
                || !data.TryGetProperty("lines", out var linesElement)
                || !linesElement.TryGetProperty("text", out var linesText))
            {
                return null;
            }

            var path = Path.GetFullPath(pathText.GetString() ?? string.Empty);
            var lineNumber = data.TryGetProperty("line_number", out var line) ? line.GetInt32() : 0;
            var content = (linesText.GetString() ?? string.Empty).Trim();
            if (content.Length > MaximumLineCharacters) content = content[..MaximumLineCharacters];
            return new SourceExcerpt(
                index,
                "local_documents",
                $"{Path.GetFileName(path)}:{lineNumber}",
                new Uri(path).AbsoluteUri,
                DateTimeOffset.UtcNow,
                content);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }
}
