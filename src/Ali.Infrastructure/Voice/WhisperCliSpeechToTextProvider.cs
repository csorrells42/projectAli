using System.Diagnostics;
using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

public sealed record WhisperCliSpeechToTextOptions(
    string? ExecutablePath,
    string? ModelPath,
    string ArgumentsTemplate,
    string OutputTextSuffix)
{
    public static WhisperCliSpeechToTextOptions FromEnvironment() =>
        new(
            Environment.GetEnvironmentVariable("ALI_WHISPER_EXE"),
            Environment.GetEnvironmentVariable("ALI_WHISPER_MODEL"),
            Environment.GetEnvironmentVariable("ALI_WHISPER_ARGS")
                ?? "-m \"{model}\" -f \"{audio}\" -otxt -of \"{outputBase}\"",
            Environment.GetEnvironmentVariable("ALI_WHISPER_OUTPUT_SUFFIX") ?? ".txt");
}

public sealed record WhisperCliDebugInfo(
    string? ExecutablePath,
    string? ModelPath,
    string InputAudioPath,
    string ArgumentsTemplate,
    int? ExitCode,
    string Transcript,
    string StandardError,
    TimeSpan Elapsed,
    DateTimeOffset CreatedAt);

public sealed class WhisperCliSpeechToTextProvider(WhisperCliSpeechToTextOptions options) : ISpeechToTextProvider
{
    public string ProviderName => "Local Whisper CLI";

    public string Mode => "local-cli";

    public string ModelPath => options.ModelPath ?? string.Empty;

    public WhisperCliDebugInfo? LastDebugInfo { get; private set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ExecutablePath)
        && File.Exists(options.ExecutablePath)
        && (!ArgumentsTemplateRequiresModelPath() || LocalPathExists(options.ModelPath))
        && !LocalSpeechToolPolicy.ContainsCloudReference(
            options.ExecutablePath,
            options.ModelPath,
            options.ArgumentsTemplate);

    public async Task<SpeechTranscript> TranscribeAsync(
        VoiceAudioInput audioInput,
        CancellationToken cancellationToken)
    {
        LocalSpeechToolPolicy.EnsureLocalOnly(
            "Speech-to-text",
            options.ExecutablePath,
            options.ModelPath,
            options.ArgumentsTemplate);

        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new InvalidOperationException(
                "Local STT is not configured. Set ALI_WHISPER_EXE and, if needed, ALI_WHISPER_MODEL.");
        }

        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("Local STT executable was not found.", options.ExecutablePath);
        }

        if (ArgumentsTemplateRequiresModelPath() && string.IsNullOrWhiteSpace(options.ModelPath))
        {
            throw new InvalidOperationException("Local STT model path is required by the configured Whisper arguments.");
        }

        if (ArgumentsTemplateRequiresModelPath() && !LocalPathExists(options.ModelPath))
        {
            throw new FileNotFoundException("Local STT model path was not found.", options.ModelPath);
        }

        if (!File.Exists(audioInput.FilePath))
        {
            throw new FileNotFoundException("Recorded audio file was not found.", audioInput.FilePath);
        }

        var outputBase = Path.Combine(
            Path.GetDirectoryName(audioInput.FilePath) ?? Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(audioInput.FilePath)}.transcript");
        var outputTextPath = outputBase + options.OutputTextSuffix;
        if (File.Exists(outputTextPath))
        {
            File.Delete(outputTextPath);
        }

        var arguments = RenderTemplate(
            options.ArgumentsTemplate,
            audioInput.FilePath,
            outputBase,
            options.ModelPath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        if (process.ExitCode != 0)
        {
            LastDebugInfo = CreateDebugInfo(
                audioInput.FilePath,
                process.ExitCode,
                transcript: string.Empty,
                stderr,
                stopwatch.Elapsed,
                startedAt);
            throw new InvalidOperationException(
                $"Local STT process failed with exit code {process.ExitCode}. {TrimForUser(stderr)}");
        }

        var text = File.Exists(outputTextPath)
            ? await File.ReadAllTextAsync(outputTextPath, cancellationToken).ConfigureAwait(false)
            : stdout;

        text = text.Trim();
        LastDebugInfo = CreateDebugInfo(
            audioInput.FilePath,
            process.ExitCode,
            text,
            stderr,
            stopwatch.Elapsed,
            startedAt);
        if (string.IsNullOrWhiteSpace(text))
        {
            var metadataTextPath = outputBase + ".json";
            var metadataSummary = File.Exists(metadataTextPath)
                ? $" Metadata: {TrimForUser(await File.ReadAllTextAsync(metadataTextPath, cancellationToken).ConfigureAwait(false))}"
                : string.Empty;
            throw new InvalidOperationException($"Local STT returned an empty transcript.{metadataSummary}");
        }

        return new SpeechTranscript(text, ProviderName, Mode, DateTimeOffset.UtcNow);
    }

    private WhisperCliDebugInfo CreateDebugInfo(
        string inputAudioPath,
        int? exitCode,
        string transcript,
        string standardError,
        TimeSpan elapsed,
        DateTimeOffset createdAt) =>
        new(
            options.ExecutablePath,
            options.ModelPath,
            inputAudioPath,
            options.ArgumentsTemplate,
            exitCode,
            TrimForUser(transcript),
            TrimForUser(standardError),
            elapsed,
            createdAt);

    private static string RenderTemplate(
        string template,
        string audioPath,
        string outputBase,
        string? modelPath) =>
        template
            .Replace("{audio}", audioPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{outputBase}", outputBase, StringComparison.OrdinalIgnoreCase)
            .Replace("{model}", modelPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private bool ArgumentsTemplateRequiresModelPath() =>
        options.ArgumentsTemplate.Contains("{model}", StringComparison.OrdinalIgnoreCase);

    private static bool LocalPathExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cancellation cleanup should not mask the original cancellation.
        }
    }

    private static string TrimForUser(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }
}
