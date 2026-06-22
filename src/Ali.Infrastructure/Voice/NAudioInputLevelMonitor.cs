using NAudio.Wave;

namespace Ali.Infrastructure.Voice;

public sealed class NAudioInputLevelMonitor : IDisposable
{
    private const int SampleRate = 44100;
    private readonly object _sync = new();
    private readonly SpectrumAnalyzer _spectrumAnalyzer = new();
    private WaveInEvent? _capture;
    private int _deviceNumber;
    private string _deviceName = "microphone";

    public event EventHandler<VoiceInputLevelSnapshot>? LevelAvailable;

    public event EventHandler<SpectrumFrame>? SpectrumAvailable;

    public InputChannelMode ChannelMode { get; set; } = InputChannelMode.MonoSum;

    public bool IsMonitoring
    {
        get
        {
            lock (_sync)
            {
                return _capture is not null;
            }
        }
    }

    public void Start(int deviceNumber, string deviceName)
    {
        Stop();
        lock (_sync)
        {
            _deviceNumber = deviceNumber;
            _deviceName = string.IsNullOrWhiteSpace(deviceName) ? $"Device {deviceNumber}" : deviceName;
            _capture = StartCapture(deviceNumber);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_capture is null)
            {
                return;
            }

            _capture.DataAvailable -= CaptureDataAvailable;
            _capture.RecordingStopped -= CaptureRecordingStopped;
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }
    }

    public void Dispose() => Stop();

    private WaveInEvent StartCapture(int deviceNumber)
    {
        Exception? firstException = null;
        foreach (var channelCount in BuildCaptureChannelCandidates(deviceNumber, ChannelMode))
        {
            var capture = CreateCapture(deviceNumber, channelCount);
            try
            {
                capture.StartRecording();
                return capture;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
                capture.DataAvailable -= CaptureDataAvailable;
                capture.RecordingStopped -= CaptureRecordingStopped;
                capture.Dispose();
            }
        }

        throw firstException ?? new InvalidOperationException("Could not start microphone level monitor.");
    }

    private WaveInEvent CreateCapture(int deviceNumber, int channelCount)
    {
        var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, channelCount),
            BufferMilliseconds = 50
        };
        capture.DataAvailable += CaptureDataAvailable;
        capture.RecordingStopped += CaptureRecordingStopped;
        return capture;
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveFormat? format;
        int deviceNumber;
        string deviceName;
        InputChannelMode channelMode;
        lock (_sync)
        {
            format = _capture?.WaveFormat;
            deviceNumber = _deviceNumber;
            deviceName = _deviceName;
            channelMode = ChannelMode;
        }

        if (format is null || e.BytesRecorded == 0)
        {
            return;
        }

        var samples = ConvertPcm16ToMonoSamples(e.Buffer.AsSpan(0, e.BytesRecorded), format, channelMode);
        if (samples.Length == 0)
        {
            return;
        }

        var snapshot = VoiceInputLevelAnalyzer.Analyze(
            samples,
            deviceNumber,
            deviceName,
            format.SampleRate,
            format.Channels);
        LevelAvailable?.Invoke(this, snapshot);
        SpectrumAvailable?.Invoke(this, _spectrumAnalyzer.AddSamples(samples));
    }

    private void CaptureRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (ReferenceEquals(sender, _capture))
            {
                _capture = null;
            }
        }
    }

    private static float[] ConvertPcm16ToMonoSamples(
        ReadOnlySpan<byte> buffer,
        WaveFormat format,
        InputChannelMode channelMode)
    {
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16)
        {
            return [];
        }

        var channels = Math.Max(1, format.Channels);
        var sampleCount = buffer.Length / sizeof(short);
        var frameCount = sampleCount / channels;
        var samples = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var selectedChannel = InputChannelModeCatalog.ChannelIndex(channelMode);
            if (selectedChannel is not null)
            {
                var channelIndex = Math.Clamp(selectedChannel.Value, 0, channels - 1);
                samples[frame] = BitConverter.ToInt16(
                    buffer.Slice((frame * channels + channelIndex) * sizeof(short), sizeof(short))) / 32768f;
                continue;
            }

            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToInt16(
                    buffer.Slice((frame * channels + channel) * sizeof(short), sizeof(short))) / 32768f;
            }
            samples[frame] = sum / channels;
        }

        return samples;
    }

    private static IReadOnlyList<int> BuildCaptureChannelCandidates(int deviceNumber, InputChannelMode channelMode)
    {
        var deviceChannels = deviceNumber >= 0 && deviceNumber < WaveInEvent.DeviceCount
            ? WaveInEvent.GetCapabilities(deviceNumber).Channels
            : 2;
        var requiredChannels = InputChannelModeCatalog.RequiredChannelCount(channelMode);
        var candidates = new[]
        {
            Math.Clamp(deviceChannels, requiredChannels, InputChannelModeCatalog.MaximumSelectableInputs),
            requiredChannels,
            2,
            1
        };

        return candidates
            .Where(channelCount => channelCount > 0)
            .Distinct()
            .ToArray();
    }
}
