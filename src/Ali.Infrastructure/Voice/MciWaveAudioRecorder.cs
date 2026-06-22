using System.Runtime.InteropServices;
using System.Text;
using Ali.Core.Voice;

namespace Ali.Infrastructure.Voice;

public sealed class MciWaveAudioRecorder : IVoiceRecorder
{
    private const string Alias = "AliVoiceRecorder";
    private string? _currentFilePath;
    private DateTimeOffset _startedAt;

    public bool IsRecording { get; private set; }

    public Task StartAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRecording)
        {
            throw new InvalidOperationException("Voice recording is already active.");
        }

        Directory.CreateDirectory(outputDirectory);
        _currentFilePath = Path.Combine(outputDirectory, $"voice_{Guid.NewGuid():N}.wav");
        _startedAt = DateTimeOffset.UtcNow;

        SendMci($"close {Alias}", ignoreErrors: true);
        SendMci($"open new type waveaudio alias {Alias}");
        SendMci($"set {Alias} time format ms bitspersample 16 samplespersec 16000 channels 1 bytespersec 32000 alignment 2");
        SendMci($"record {Alias}");
        IsRecording = true;

        return Task.CompletedTask;
    }

    public Task<VoiceAudioInput> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRecording || string.IsNullOrWhiteSpace(_currentFilePath))
        {
            throw new InvalidOperationException("No voice recording is active.");
        }

        SendMci($"stop {Alias}", ignoreErrors: true);
        SendMci($"save {Alias} \"{_currentFilePath}\"");
        SendMci($"close {Alias}", ignoreErrors: true);
        IsRecording = false;

        return Task.FromResult(new VoiceAudioInput(
            _currentFilePath,
            "audio/wav",
            RetainAudio: false,
            _startedAt));
    }

    public void Cancel()
    {
        SendMci($"stop {Alias}", ignoreErrors: true);
        SendMci($"close {Alias}", ignoreErrors: true);
        IsRecording = false;

        if (!string.IsNullOrWhiteSpace(_currentFilePath) && File.Exists(_currentFilePath))
        {
            try
            {
                File.Delete(_currentFilePath);
            }
            catch
            {
                // Temporary audio cleanup is best-effort.
            }
        }

        _currentFilePath = null;
    }

    private static void SendMci(string command, bool ignoreErrors = false)
    {
        var error = mciSendString(command, null, 0, IntPtr.Zero);
        if (error == 0 || ignoreErrors)
        {
            return;
        }

        var message = new StringBuilder(256);
        _ = mciGetErrorString(error, message, message.Capacity);
        throw new InvalidOperationException($"Windows audio command failed: {message}");
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextLength);
}
