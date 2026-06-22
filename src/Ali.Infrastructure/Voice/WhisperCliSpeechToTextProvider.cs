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

public sealed class WhisperCliSpeechToTextProvider(WhisperCliSpeechToTextOptions options) : ISpeechToTextProvider
{
    public string ProviderName => "Local Whisper CLI";

    public string Mode => "local-cli";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ExecutablePath)
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

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Local STT process failed with exit code {process.ExitCode}. {TrimForUser(stderr)}");
        }

        var text = File.Exists(outputTextPath)
            ? await File.ReadAllTextAsync(outputTextPath, cancellationToken).ConfigureAwait(false)
            : stdout;

        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Local STT returned an empty transcript.");
        }

        return new SpeechTranscript(text, ProviderName, Mode, DateTimeOffset.UtcNow);
    }

    private static string RenderTemplate(
        string template,
        string audioPath,
        string outputBase,
        string? modelPath) =>
        template
            .Replace("{audio}", audioPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{outputBase}", outputBase, StringComparison.OrdinalIgnoreCase)
            .Replace("{model}", modelPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);

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
