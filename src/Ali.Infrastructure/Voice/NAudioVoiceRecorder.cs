using Ali.Core.Voice;
using NAudio.Wave;

namespace Ali.Infrastructure.Voice;

public sealed class NAudioVoiceRecorder : IVoiceRecorder, IDisposable
{
    private const int SampleRate = 44100;
    private readonly object _sync = new();
    private readonly SpectrumAnalyzer _spectrumAnalyzer = new();
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
        ProcessorSettings = settings ?? new VoiceProcessorSettings();
        ChannelMode = channelMode;
    }

    public event EventHandler<VoiceInputLevelSnapshot>? LevelAvailable;

    public event EventHandler<SpectrumFrame>? SpectrumAvailable;

    public int InputDeviceNumber { get; set; }

    public VoiceProcessorSettings ProcessorSettings { get; set; }

    public InputChannelMode ChannelMode { get; set; }

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

    public static int GetInputDeviceChannelCount(int deviceNumber)
    {
        if (deviceNumber < 0 || deviceNumber >= WaveInEvent.DeviceCount)
        {
            return 1;
        }

        return Math.Max(1, WaveInEvent.GetCapabilities(deviceNumber).Channels);
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
            _processor = new VoiceSampleProcessor(ProcessorSettings);
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

        var samples = ConvertToMonoSamples(e.Buffer.AsSpan(0, e.BytesRecorded), format, ChannelMode);
        if (samples.Length == 0)
        {
            return;
        }

        var processed = processor.Process(samples);
        LevelAvailable?.Invoke(
            this,
            VoiceInputLevelAnalyzer.Analyze(
                processed,
                InputDeviceNumber,
                GetInputDeviceName(InputDeviceNumber),
                format.SampleRate,
                format.Channels));
        SpectrumAvailable?.Invoke(this, _spectrumAnalyzer.AddSamples(processed));

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
            var selectedChannel = ResolveSelectedChannel(buffer, frameCount, channels, channelMode, isFloat: true);

            for (var frame = 0; frame < frameCount; frame++)
            {
                samples[frame] = PickFloatChannel(buffer, frame, channels, selectedChannel);
            }

            return samples;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var sampleCount = buffer.Length / sizeof(short);
            var frameCount = sampleCount / channels;
            var samples = new float[frameCount];
            var selectedChannel = ResolveSelectedChannel(buffer, frameCount, channels, channelMode, isFloat: false);

            for (var frame = 0; frame < frameCount; frame++)
            {
                samples[frame] = PickPcm16Channel(buffer, frame, channels, selectedChannel);
            }

            return samples;
        }

        return [];
    }

    private static float PickFloatChannel(ReadOnlySpan<byte> buffer, int frame, int channels, int? selectedChannel)
    {
        if (selectedChannel is null)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToSingle(buffer.Slice((frame * channels + channel) * sizeof(float), sizeof(float)));
            }

            return sum / channels;
        }

        var channelIndex = Math.Clamp(selectedChannel.Value, 0, channels - 1);
        return BitConverter.ToSingle(buffer.Slice((frame * channels + channelIndex) * sizeof(float), sizeof(float)));
    }

    private static float PickPcm16Channel(ReadOnlySpan<byte> buffer, int frame, int channels, int? selectedChannel)
    {
        if (selectedChannel is null)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToInt16(buffer.Slice((frame * channels + channel) * sizeof(short), sizeof(short))) / 32768f;
            }

            return sum / channels;
        }

        var channelIndex = Math.Clamp(selectedChannel.Value, 0, channels - 1);
        return BitConverter.ToInt16(buffer.Slice((frame * channels + channelIndex) * sizeof(short), sizeof(short))) / 32768f;
    }

    private static int? ResolveSelectedChannel(
        ReadOnlySpan<byte> buffer,
        int frameCount,
        int channels,
        InputChannelMode channelMode,
        bool isFloat)
    {
        if (channelMode == InputChannelMode.HighestEnergy && channels > 1)
        {
            return isFloat
                ? HighestEnergyFloatChannel(buffer, frameCount, channels)
                : HighestEnergyPcm16Channel(buffer, frameCount, channels);
        }

        return InputChannelModeCatalog.ChannelIndex(channelMode);
    }

    private static int HighestEnergyFloatChannel(ReadOnlySpan<byte> buffer, int frameCount, int channels)
    {
        var bestChannel = 0;
        var bestEnergy = -1d;
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0d;
            for (var frame = 0; frame < frameCount; frame++)
            {
                energy += Math.Abs(BitConverter.ToSingle(buffer.Slice((frame * channels + channel) * sizeof(float), sizeof(float))));
            }

            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestChannel = channel;
            }
        }

        return bestChannel;
    }

    private static int HighestEnergyPcm16Channel(ReadOnlySpan<byte> buffer, int frameCount, int channels)
    {
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

    private static string GetInputDeviceName(int deviceNumber) =>
        GetInputDevices().FirstOrDefault(device => device.DeviceNumber == deviceNumber)?.Name
        ?? $"Device {deviceNumber}";
}
