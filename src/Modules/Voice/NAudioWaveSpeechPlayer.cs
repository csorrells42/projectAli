using Ali.Modules.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace Ali.Modules.Voice;

public sealed class NAudioWaveSpeechPlayer : ISpeechPlayer, IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private IWavePlayer? _output;
    private WaveFileReader? _reader;
    private IWaveProvider? _playbackProvider;
    private TaskCompletionSource? _playbackCompletion;
    private long _playbackRequestId;

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

        var requestId = Interlocked.Increment(ref _playbackRequestId);
        StopCurrentPlayback();
        await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (requestId != Volatile.Read(ref _playbackRequestId))
            {
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                try
                {
                    _reader = new WaveFileReader(audioPath);
                    _playbackProvider = CreatePlaybackProvider(_reader);
                    _output = CreateOutputDevice(OutputDeviceNumber);
                    _playbackCompletion = completion;
                    _output.PlaybackStopped += (_, args) =>
                    {
                        if (args.Exception is not null)
                        {
                            completion.TrySetException(args.Exception);
                        }
                        else
                        {
                            completion.TrySetResult();
                        }
                    };
                    try
                    {
                        _output.Init(_playbackProvider);
                    }
                    catch (COMException ex) when (IsWasapiUnsupportedFormat(ex))
                    {
                        _output.Dispose();
                        _reader.Position = 0;
                        _playbackProvider = CreatePlaybackProvider(_reader);
                        _output = CreateFallbackOutputDevice();
                        _output.PlaybackStopped += (_, args) =>
                        {
                            if (args.Exception is not null)
                            {
                                completion.TrySetException(args.Exception);
                            }
                            else
                            {
                                completion.TrySetResult();
                            }
                        };
                        _output.Init(_playbackProvider);
                    }

                    _output.Play();
                }
                catch
                {
                    CleanupPlaybackLocked();
                    completion.TrySetResult();
                    throw;
                }
            }

            using var registration = cancellationToken.Register(Stop);
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StopCurrentPlayback();
            _playbackGate.Release();
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref _playbackRequestId);
        StopCurrentPlayback();
    }

    private void StopCurrentPlayback()
    {
        lock (_sync)
        {
            _output?.Stop();
            _playbackCompletion?.TrySetResult();
            CleanupPlaybackLocked();
        }
    }

    public void Dispose() => Stop();

    private void CleanupPlaybackLocked()
    {
        _output?.Dispose();
        _output = null;
        _playbackProvider = null;
        _reader?.Dispose();
        _reader = null;
        _playbackCompletion = null;
    }

    private static IWaveProvider CreatePlaybackProvider(WaveFileReader reader)
    {
        ISampleProvider sampleProvider = reader.ToSampleProvider();
        if (reader.WaveFormat.Channels == 1)
        {
            sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
        }

        if (sampleProvider.WaveFormat.SampleRate != 48000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 48000);
        }

        return new SampleToWaveProvider16(sampleProvider);
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

    private static IWavePlayer CreateFallbackOutputDevice() =>
        new WaveOutEvent
        {
            DeviceNumber = -1,
            DesiredLatency = 80
        };

    private static bool IsWasapiUnsupportedFormat(COMException exception) =>
        unchecked((uint)exception.HResult) == 0x88890004;
}
