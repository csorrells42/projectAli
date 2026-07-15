using NAudio.Wave;

namespace Ali.Modules.Voice;

public sealed class NAudioInputLevelMonitor : IDisposable
{
    private const int SampleRate = 44100;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(500);
    private readonly object _sync = new();
    private readonly SpectrumAnalyzer _spectrumAnalyzer = new();
    private WaveInEvent? _capture;
    private VoiceSampleProcessor? _processor;
    private int _deviceNumber;
    private string _deviceName = "microphone";

    public event EventHandler<VoiceInputLevelSnapshot>? LevelAvailable;

    public event EventHandler<SpectrumFrame>? SpectrumAvailable;

    public InputChannelMode ChannelMode { get; set; } = InputChannelMode.MonoSum;

    public VoiceProcessorSettings ProcessorSettings { get; set; } = new();

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
            _processor = new VoiceSampleProcessor(ProcessorSettings);
            try
            {
                _capture = StartCapture(deviceNumber);
            }
            catch
            {
                _processor = null;
                throw;
            }
        }
    }

    public void Stop()
    {
        WaveInEvent capture;
        lock (_sync)
        {
            if (_capture is null)
            {
                return;
            }

            capture = _capture;
            _capture = null;
            _processor = null;
        }

        StopAndDisposeCapture(capture);
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

    private void StopAndDisposeCapture(WaveInEvent capture)
    {
        using var stopped = new ManualResetEventSlim(false);
        void MarkStopped(object? _, StoppedEventArgs __) => stopped.Set();

        capture.RecordingStopped += MarkStopped;
        try
        {
            capture.StopRecording();
            stopped.Wait(StopTimeout);
        }
        catch
        {
            // Some audio drivers throw while stopping; the UI will report the meter as unavailable.
        }
        finally
        {
            capture.DataAvailable -= CaptureDataAvailable;
            capture.RecordingStopped -= CaptureRecordingStopped;
            capture.RecordingStopped -= MarkStopped;
            try
            {
                capture.Dispose();
            }
            catch
            {
                // Device cleanup is best-effort after a failed capture stop.
            }
        }
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveFormat? format;
        int deviceNumber;
        string deviceName;
        InputChannelMode channelMode;
        VoiceSampleProcessor? processor;
        lock (_sync)
        {
            format = _capture?.WaveFormat;
            deviceNumber = _deviceNumber;
            deviceName = _deviceName;
            channelMode = ChannelMode;
            processor = _processor;
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

        var processedSamples = processor?.Process(samples) ?? samples;

        var snapshot = VoiceInputLevelAnalyzer.Analyze(
            processedSamples,
            deviceNumber,
            deviceName,
            format.SampleRate,
            format.Channels);
        LevelAvailable?.Invoke(this, snapshot);
        SpectrumAvailable?.Invoke(this, _spectrumAnalyzer.AddSamples(processedSamples));
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
        var selectedChannel = ResolveSelectedChannel(buffer, frameCount, channels, channelMode);

        for (var frame = 0; frame < frameCount; frame++)
        {
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

    private static int? ResolveSelectedChannel(
        ReadOnlySpan<byte> buffer,
        int frameCount,
        int channels,
        InputChannelMode channelMode)
    {
        if (channelMode != InputChannelMode.HighestEnergy || channels <= 1)
        {
            return InputChannelModeCatalog.ChannelIndex(channelMode);
        }

        var bestChannel = 0;
        var bestEnergy = -1d;
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0d;
            for (var frame = 0; frame < frameCount; frame++)
            {
                energy += Math.Abs(BitConverter.ToInt16(buffer.Slice((frame * channels + channel) * sizeof(short), sizeof(short))));
            }

            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestChannel = channel;
            }
        }

        return bestChannel;
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
