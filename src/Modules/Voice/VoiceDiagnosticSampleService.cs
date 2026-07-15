using Ali.Modules.Voice;

namespace Ali.Modules.Voice;

public sealed class VoiceDiagnosticSampleService(
    IVoiceRecorder recorder,
    ISpeechPlayer speechPlayer,
    Func<string, int, string, VoiceCaptureDiagnostics>? analyzeWaveAudio = null)
{
    private readonly Func<string, int, string, VoiceCaptureDiagnostics> _analyzeWaveAudio =
        analyzeWaveAudio ?? VoiceAudioFileAnalyzer.AnalyzeWaveAudio;

    public async Task<VoiceDiagnosticSample> RecordSampleAsync(
        string outputDirectory,
        TimeSpan duration,
        int inputDeviceNumber,
        string inputDeviceName,
        InputChannelMode channelMode,
        string inputPreset,
        double extraGainDb,
        bool normalizeBeforeStt,
        bool retainDebugAudio,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Sample duration must be positive.");
        }

        await recorder.StartAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
        VoiceAudioInput? audioInput = null;
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            audioInput = await recorder.StopAsync(cancellationToken).ConfigureAwait(false);
            if (normalizeBeforeStt)
            {
                VoiceAudioNormalizer.NormalizePcm16WaveInPlace(audioInput.FilePath);
            }

            var diagnostics = _analyzeWaveAudio(audioInput.FilePath, inputDeviceNumber, inputDeviceName);
            var retainedAudio = audioInput with { RetainAudio = retainDebugAudio };
            return new VoiceDiagnosticSample(
                retainedAudio,
                diagnostics,
                inputDeviceNumber,
                inputDeviceName,
                channelMode,
                InputChannelModeCatalog.ToLabel(channelMode),
                inputPreset,
                extraGainDb,
                normalizeBeforeStt,
                retainDebugAudio);
        }
        catch
        {
            recorder.Cancel();
            if (audioInput is not null && !retainDebugAudio)
            {
                DeleteSample(audioInput.FilePath);
            }

            throw;
        }
    }

    public Task PlaySampleAsync(VoiceDiagnosticSample sample, CancellationToken cancellationToken) =>
        speechPlayer.PlayAsync(sample.AudioInput.FilePath, cancellationToken);

    public void DeleteSample(VoiceDiagnosticSample? sample, bool force = false)
    {
        if (sample is null || (sample.RetainDebugAudio && !force))
        {
            return;
        }

        DeleteSample(sample.AudioInput.FilePath);
    }

    private static void DeleteSample(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Temporary diagnostic audio cleanup is best-effort.
        }
    }
}
