using System.Diagnostics;
using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

public sealed record KittenCliTextToSpeechOptions(
    string? ExecutablePath,
    string? ModelPath,
    string VoiceId,
    string ArgumentsTemplate,
    string OutputDirectory)
{
    public static KittenCliTextToSpeechOptions FromEnvironment(string dataRoot) =>
        new(
            Environment.GetEnvironmentVariable("ALI_KITTEN_EXE"),
            Environment.GetEnvironmentVariable("ALI_KITTEN_MODEL"),
            Environment.GetEnvironmentVariable("ALI_KITTEN_VOICE") ?? KittenVoiceCatalog.DefaultVoiceId,
            Environment.GetEnvironmentVariable("ALI_KITTEN_ARGS") ?? "\"{script}\" --model \"{model}\" --voice \"{voice}\" --output \"{output}\" --rate \"{rate}\"",
            Path.Combine(dataRoot, "SessionSpeech", DateTimeOffset.Now.ToString("yyyyMMdd")));
}

public sealed class KittenCliTextToSpeechProvider(KittenCliTextToSpeechOptions options) : ITextToSpeechProvider
{
    public string ProviderName => "Local KittenTTS";

    public string VoiceId => options.VoiceId;

    public string ModelPath => options.ModelPath ?? string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.ExecutablePath)
        && !string.IsNullOrWhiteSpace(options.ModelPath)
        && File.Exists(options.ExecutablePath)
        && (File.Exists(options.ModelPath) || Directory.Exists(options.ModelPath))
        && !LocalSpeechToolPolicy.ContainsCloudReference(
            options.ExecutablePath,
            options.ModelPath,
            options.ArgumentsTemplate);

    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        var scriptPath = LocalVoiceResourceLocator.FindKittenScript(AppContext.BaseDirectory);
        LocalSpeechToolPolicy.EnsureLocalOnly(
            "Text-to-speech",
            options.ExecutablePath,
            options.ModelPath,
            options.ArgumentsTemplate,
            scriptPath);

        if (string.IsNullOrWhiteSpace(options.ExecutablePath) || !File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("Local KittenTTS Python executable was not found.", options.ExecutablePath);
        }

        if (string.IsNullOrWhiteSpace(options.ModelPath)
            || (!File.Exists(options.ModelPath) && !Directory.Exists(options.ModelPath)))
        {
            throw new FileNotFoundException("Local KittenTTS model path was not found.", options.ModelPath);
        }

        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Local KittenTTS bridge script was not found.", scriptPath);
        }

        if (!KittenVoiceCatalog.IsKnownVoice(settings.VoiceId))
        {
            throw new InvalidOperationException($"Selected KittenTTS voice '{settings.VoiceId}' is not available.");
        }

        if (!string.Equals(settings.VoiceId, options.VoiceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Selected TTS voice '{settings.VoiceId}' does not match configured KittenTTS voice '{options.VoiceId}'.");
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
            settings.Rate,
            scriptPath);

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
                $"Local KittenTTS process failed with exit code {process.ExitCode}. {TrimForUser(stderr)}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("Local KittenTTS did not create an output WAV file.");
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
        double rate,
        string scriptPath) =>
        template
            .Replace("{script}", scriptPath, StringComparison.OrdinalIgnoreCase)
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
