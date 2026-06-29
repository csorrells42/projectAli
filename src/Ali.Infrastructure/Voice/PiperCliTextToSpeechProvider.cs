using System.Diagnostics;
using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

public sealed record PiperCliTextToSpeechOptions(
    string? ExecutablePath,
    string? ModelPath,
    string VoiceId,
    string ArgumentsTemplate,
    string OutputDirectory)
{
    public static PiperCliTextToSpeechOptions FromEnvironment(string dataRoot) =>
        new(
            Environment.GetEnvironmentVariable("ALI_PIPER_EXE"),
            Environment.GetEnvironmentVariable("ALI_PIPER_MODEL"),
            Environment.GetEnvironmentVariable("ALI_PIPER_VOICE") ?? "default",
            Environment.GetEnvironmentVariable("ALI_PIPER_ARGS") ?? "--model \"{model}\" --output_file \"{output}\"",
            Path.Combine(dataRoot, "SessionSpeech", DateTimeOffset.Now.ToString("yyyyMMdd")));
}

public sealed class PiperCliTextToSpeechProvider(PiperCliTextToSpeechOptions options) : ITextToSpeechProvider
{
    public string ProviderName => "Local Piper CLI";

    public string VoiceId => options.VoiceId;

    public string ModelPath => options.ModelPath ?? string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ExecutablePath)
        && !string.IsNullOrWhiteSpace(options.ModelPath)
        && File.Exists(options.ExecutablePath)
        && File.Exists(options.ModelPath)
        && !LocalSpeechToolPolicy.ContainsCloudReference(
            options.ExecutablePath,
            options.ModelPath,
            options.ArgumentsTemplate);

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        LocalSpeechToolPolicy.EnsureLocalOnly(
            "Text-to-speech",
            options.ExecutablePath,
            options.ModelPath,
            options.ArgumentsTemplate);

        if (string.IsNullOrWhiteSpace(options.ExecutablePath) || string.IsNullOrWhiteSpace(options.ModelPath))
        {
            throw new InvalidOperationException(
                "Local TTS is not configured. Set ALI_PIPER_EXE and ALI_PIPER_MODEL.");
        }

        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("Local TTS executable was not found.", options.ExecutablePath);
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException("Local TTS voice model was not found.", options.ModelPath);
        }

        if (!string.Equals(settings.VoiceId, options.VoiceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Selected TTS voice '{settings.VoiceId}' does not match configured Piper voice '{options.VoiceId}'.");
        }

        var spokenText = SpeechOutputCleaner.Clean(text);
        if (string.IsNullOrWhiteSpace(spokenText))
        {
            throw new InvalidOperationException("There is no speakable text after cleaning.");
        }

        Directory.CreateDirectory(options.OutputDirectory);
        var outputPath = Path.Combine(options.OutputDirectory, $"speech_{Guid.NewGuid():N}.wav");
        var arguments = RenderTemplate(
            options.ArgumentsTemplate,
            options.ModelPath,
            outputPath,
            settings.VoiceId,
            settings.Rate);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.Start();
        await process.StandardInput.WriteAsync(spokenText.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

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

        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Local TTS process failed with exit code {process.ExitCode}. {TrimForUser(stderr)}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("Local TTS did not create an output WAV file.");
        }

        return new SpeechSynthesisResult(
            outputPath,
            ProviderName,
            settings.VoiceId,
            settings.RetainAudio,
            DateTimeOffset.UtcNow);
    }

    private static string RenderTemplate(
        string template,
        string modelPath,
        string outputPath,
        string voiceId,
        double rate) =>
        template
            .Replace("{model}", modelPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{output}", outputPath, StringComparison.OrdinalIgnoreCase)
            .Replace("{voice}", voiceId, StringComparison.OrdinalIgnoreCase)
            .Replace("{rate}", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

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
