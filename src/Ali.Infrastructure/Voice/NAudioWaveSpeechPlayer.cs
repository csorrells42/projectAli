using Ali.Core.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ali.Infrastructure.Voice;

public sealed class NAudioWaveSpeechPlayer : ISpeechPlayer, IDisposable
{
    private readonly object _sync = new();
    private IWavePlayer? _output;
    private WaveFileReader? _reader;
    private IWaveProvider? _playbackProvider;

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
        var devices = new List<AudioOutputDevice>
        {
            new AudioOutputDevice(-1, "Default playback device")
        };

        using var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        for (var deviceNumber = 0; deviceNumber < renderDevices.Count; deviceNumber++)
        {
            devices.Add(new AudioOutputDevice(deviceNumber, renderDevices[deviceNumber].FriendlyName));
        }

        return devices;
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
            _playbackProvider = CreatePlaybackProvider(_reader);
            _output = CreateOutputDevice(OutputDeviceNumber);
            _output.PlaybackStopped += (_, _) => completion.TrySetResult();
            _output.Init(_playbackProvider);
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
            _playbackProvider = null;
            _reader?.Dispose();
            _reader = null;
        }
    }

    public void Dispose() => Stop();

    private static IWaveProvider CreatePlaybackProvider(WaveFileReader reader)
    {
        if (reader.WaveFormat.Channels != 1)
        {
            return reader;
        }

        var stereo = new MonoToStereoSampleProvider(reader.ToSampleProvider());
        return new SampleToWaveProvider16(stereo);
    }

    private static IWavePlayer CreateOutputDevice(int outputDeviceNumber)
    {
        if (outputDeviceNumber < 0)
        {
            return new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, latency: 80);
        }

        using var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        if (outputDeviceNumber >= renderDevices.Count)
        {
            return new WasapiOut(AudioClientShareMode.Shared, useEventSync: false, latency: 80);
        }

        return new WasapiOut(renderDevices[outputDeviceNumber], AudioClientShareMode.Shared, useEventSync: false, latency: 80);
    }
}
