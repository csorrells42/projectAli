using Ali.Core.Voice;
using NAudio.Wave;

namespace Ali.Infrastructure.Voice;

public sealed class NAudioVoiceRecorder : IVoiceRecorder, IDisposable
{
    private const int SampleRate = 44100;
    private readonly object _sync = new();
    private readonly VoiceProcessorSettings _settings;
    private readonly InputChannelMode _channelMode;
    private WaveInEvent? _capture;
    private WaveFileWriter? _writer;
    private VoiceSampleProcessor? _processor;
    private string? _currentFilePath;
    private DateTimeOffset _startedAt;

    public NAudioVoiceRecorder(
        int inputDeviceNumber = 0,
        VoiceProcessorSettings? settings = null,
        InputChannelMode channelMode = InputChannelMode.MonoSum)
    {
        InputDeviceNumber = inputDeviceNumber;
        _settings = settings ?? new VoiceProcessorSettings();
        _channelMode = channelMode;
    }

    public int InputDeviceNumber { get; set; }

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _capture is not null;
            }
        }
    }

    public static IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        var devices = new List<AudioInputDevice>();
        for (var deviceNumber = 0; deviceNumber < WaveInEvent.DeviceCount; deviceNumber++)
        {
            var capabilities = WaveInEvent.GetCapabilities(deviceNumber);
            devices.Add(new AudioInputDevice(deviceNumber, capabilities.ProductName));
        }

        return devices;
    }

    public Task StartAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Voice recording is already active.");
            }

            Directory.CreateDirectory(outputDirectory);
            _currentFilePath = Path.Combine(outputDirectory, $"voice_{Guid.NewGuid():N}.wav");
            _startedAt = DateTimeOffset.UtcNow;
            _processor = new VoiceSampleProcessor(_settings);
            _writer = new WaveFileWriter(_currentFilePath, new WaveFormat(SampleRate, 16, 1));
            _capture = StartCapture(InputDeviceNumber);
        }

        return Task.CompletedTask;
    }

    public Task<VoiceAudioInput> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? filePath;
        DateTimeOffset startedAt;

        lock (_sync)
        {
            if (_capture is null || string.IsNullOrWhiteSpace(_currentFilePath))
            {
                throw new InvalidOperationException("No voice recording is active.");
            }

            filePath = _currentFilePath;
            startedAt = _startedAt;
            _capture.StopRecording();
            ReleaseCapture();
        }

        return Task.FromResult(new VoiceAudioInput(
            filePath,
            "audio/wav",
            RetainAudio: false,
            startedAt));
    }

    public void Cancel()
    {
        string? filePath;
        lock (_sync)
        {
            filePath = _currentFilePath;
            _capture?.StopRecording();
            ReleaseCapture();
        }

        TryDelete(filePath);
    }

    public void Dispose() => Cancel();

    private WaveInEvent StartCapture(int deviceNumber)
    {
        Exception? firstException = null;
        foreach (var channelCount in new[] { 2, 1 })
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

        throw firstException ?? new InvalidOperationException("Could not start microphone capture.");
    }

    private WaveInEvent CreateCapture(int deviceNumber, int channelCount)
    {
        var capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, 16, channelCount),
            BufferMilliseconds = 15
        };
        capture.DataAvailable += CaptureDataAvailable;
        capture.RecordingStopped += CaptureRecordingStopped;
        return capture;
    }

    private void CaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveFileWriter? writer;
        VoiceSampleProcessor? processor;
        WaveFormat? format;

        lock (_sync)
        {
            writer = _writer;
            processor = _processor;
            format = _capture?.WaveFormat;
        }

        if (writer is null || processor is null || format is null || e.BytesRecorded == 0)
        {
            return;
        }

        var samples = ConvertToMonoSamples(e.Buffer.AsSpan(0, e.BytesRecorded), format, _channelMode);
        if (samples.Length == 0)
        {
            return;
        }

        var processed = processor.Process(samples);
        var bytes = ConvertFloatSamplesToPcm16(processed);

        lock (_sync)
        {
            _writer?.Write(bytes, 0, bytes.Length);
        }
    }

    private void CaptureRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            ReleaseCapture();
        }
    }

    private void ReleaseCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= CaptureDataAvailable;
            _capture.RecordingStopped -= CaptureRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _writer?.Dispose();
        _writer = null;
        _processor = null;
        _currentFilePath = null;
    }

    private static float[] ConvertToMonoSamples(ReadOnlySpan<byte> buffer, WaveFormat format, InputChannelMode channelMode)
    {
        var channels = Math.Max(1, format.Channels);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var sampleCount = buffer.Length / sizeof(float);
            var frameCount = sampleCount / channels;
            var samples = new float[frameCount];

            for (var frame = 0; frame < frameCount; frame++)
            {
                samples[frame] = PickFloatChannel(buffer, frame, channels, channelMode);
            }

            return samples;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var sampleCount = buffer.Length / sizeof(short);
            var frameCount = sampleCount / channels;
            var samples = new float[frameCount];

            for (var frame = 0; frame < frameCount; frame++)
            {
                samples[frame] = PickPcm16Channel(buffer, frame, channels, channelMode);
            }

            return samples;
        }

        return [];
    }

    private static float PickFloatChannel(ReadOnlySpan<byte> buffer, int frame, int channels, InputChannelMode channelMode)
    {
        if (channelMode == InputChannelMode.Input1Left || channels == 1)
        {
            return BitConverter.ToSingle(buffer.Slice(frame * channels * sizeof(float), sizeof(float)));
        }

        if (channelMode == InputChannelMode.Input2Right)
        {
            var channel = Math.Min(1, channels - 1);
            return BitConverter.ToSingle(buffer.Slice((frame * channels + channel) * sizeof(float), sizeof(float)));
        }

        var sum = 0f;
        for (var channel = 0; channel < channels; channel++)
        {
            sum += BitConverter.ToSingle(buffer.Slice((frame * channels + channel) * sizeof(float), sizeof(float)));
        }

        return sum / channels;
    }

    private static float PickPcm16Channel(ReadOnlySpan<byte> buffer, int frame, int channels, InputChannelMode channelMode)
    {
        if (channelMode == InputChannelMode.Input1Left || channels == 1)
        {
            return BitConverter.ToInt16(buffer.Slice(frame * channels * sizeof(short), sizeof(short))) / 32768f;
        }

        if (channelMode == InputChannelMode.Input2Right)
        {
            var channel = Math.Min(1, channels - 1);
            return BitConverter.ToInt16(buffer.Slice((frame * channels + channel) * sizeof(short), sizeof(short))) / 32768f;
        }

        var sum = 0f;
        for (var channel = 0; channel < channels; channel++)
        {
            sum += BitConverter.ToInt16(buffer.Slice((frame * channels + channel) * sizeof(short), sizeof(short))) / 32768f;
        }

        return sum / channels;
    }

    private static byte[] ConvertFloatSamplesToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(short), sizeof(short)), sample);
        }

        return bytes;
    }

    private static void TryDelete(string? filePath)
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
            // Temporary audio cleanup is best-effort.
        }
    }
}
