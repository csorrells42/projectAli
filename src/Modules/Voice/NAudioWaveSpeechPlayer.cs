using Ali.Modules.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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

            try
            {
                await PlayAttemptAsync(audioPath, useFallbackOutput: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableWasapiFailure(ex)
                && requestId == Volatile.Read(ref _playbackRequestId)
                && !cancellationToken.IsCancellationRequested)
            {
                // USB and multitrack endpoints can remain listed as active while
                // another application owns them exclusively. Retry through the
                // Windows default output so a spoken reply is not lost.
                StopCurrentPlayback();
                await PlayAttemptAsync(audioPath, useFallbackOutput: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            StopCurrentPlayback();
            _playbackGate.Release();
        }
    }

    private async Task PlayAttemptAsync(
        string audioPath,
        bool useFallbackOutput,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            try
            {
                _reader = new WaveFileReader(audioPath);
                _playbackProvider = CreatePlaybackProvider(_reader);
                _output = useFallbackOutput
                    ? CreateFallbackOutputDevice()
                    : CreateOutputDevice(OutputDeviceNumber);
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
                _output.Init(_playbackProvider);
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

    private static bool IsRecoverableWasapiFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var hResult = unchecked((uint)current.HResult);
            if (hResult is 0x88890004u // AUDCLNT_E_DEVICE_INVALIDATED
                or 0x88890008u        // AUDCLNT_E_UNSUPPORTED_FORMAT
                or 0x8889000Au        // AUDCLNT_E_DEVICE_IN_USE
                or 0x8889000Fu        // AUDCLNT_E_ENDPOINT_CREATE_FAILED
                or 0x88890010u        // AUDCLNT_E_SERVICE_NOT_RUNNING
                or 0x88890026u)       // AUDCLNT_E_RESOURCES_INVALIDATED
            {
                return true;
            }
        }

        return false;
    }
}
