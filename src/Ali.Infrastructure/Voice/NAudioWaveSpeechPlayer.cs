using Ali.Core.Voice;
using NAudio.Wave;

namespace Ali.Infrastructure.Voice;

public sealed class NAudioWaveSpeechPlayer : ISpeechPlayer, IDisposable
{
    private readonly object _sync = new();
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;

    public int OutputDeviceNumber { get; set; } = -1;

    public bool IsSpeaking
    {
        get
        {
            lock (_sync)
            {
                return _output?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public static IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        return new[]
        {
            new AudioOutputDevice(-1, "Default playback device")
        };
    }

    public async Task PlayAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Speech audio file was not found.", audioPath);
        }

        Stop();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _reader = new WaveFileReader(audioPath);
            _output = new WaveOutEvent
            {
                DeviceNumber = OutputDeviceNumber,
                DesiredLatency = 80
            };
            _output.PlaybackStopped += (_, _) => completion.TrySetResult();
            _output.Init(_reader);
            _output.Play();
        }

        using var registration = cancellationToken.Register(Stop);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stop();
    }

    public void Stop()
    {
        lock (_sync)
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _reader?.Dispose();
            _reader = null;
        }
    }

    public void Dispose() => Stop();
}
