namespace Ali.Modules.Voice;

public sealed class VoiceSampleProcessor
{
    private const double SampleRate = 44100d;
    private readonly VoiceProcessorSettings _settings;
    private double _previousHighPassInput;
    private double _previousHighPassOutput;
    private double _dePopperLow;
    private double _noiseFloor = 0.0005d;
    private double _noiseSuppressionEnvelope;
    private double _echoEnvelope;
    private double _echoRecentPeak;
    private double _previousDeEsserInput;
    private double _previousDeEsserOutput;
    private double _envelope;
    private double _lastGainReductionDb;
    private double _gateOpenness = 1;

    public VoiceSampleProcessor(VoiceProcessorSettings settings)
    {
        _settings = settings;
    }

    public VoiceProcessingTelemetry Telemetry { get; private set; } = new();

    public float[] Process(ReadOnlySpan<float> samples)
    {
        var processed = new float[samples.Length];
        var maxGainReduction = 0d;
        var maxCompressorInputLevelDb = -100d;
        var gateOpennessSum = 0d;

        for (var i = 0; i < samples.Length; i++)
        {
            double sample = samples[i];
            sample = ApplyDePopper(sample);
            sample = ApplyHighPass(sample);
            sample = ApplyNoiseSuppression(sample);
            sample = ApplyNoiseGate(sample);
            sample = ApplyEchoReducer(sample);
            sample = ApplyCompressor(sample);
            sample = ApplyDeEsser(sample);
            maxGainReduction = Math.Max(maxGainReduction, _lastGainReductionDb);
            maxCompressorInputLevelDb = Math.Max(maxCompressorInputLevelDb, LinearToDb(Math.Abs(sample)));
            gateOpennessSum += _gateOpenness;
            sample = ApplyMakeupGain(sample);
            sample = ApplyLimiter(sample);
            processed[i] = (float)Math.Clamp(sample, -1d, 1d);
        }

        Telemetry = new VoiceProcessingTelemetry
        {
            CompressorGainReductionDb = maxGainReduction,
            CompressorInputLevelDb = maxCompressorInputLevelDb,
            CompressorThresholdDb = _settings.CompressorThresholdDb,
            CompressorActive = maxGainReduction > 0.1d,
            GateOpenness = samples.Length == 0 ? 1d : gateOpennessSum / samples.Length,
            GateOpen = samples.Length == 0 || gateOpennessSum / samples.Length > 0.55d
        };

        return processed;
    }

    private double ApplyHighPass(double sample)
    {
        if (!_settings.HighPassEnabled)
        {
            return sample;
        }

        var rc = 1d / (2d * Math.PI * Math.Max(20d, _settings.HighPassFrequencyHz));
        var dt = 1d / SampleRate;
        var alpha = rc / (rc + dt);
        var output = alpha * (_previousHighPassOutput + sample - _previousHighPassInput);
        _previousHighPassInput = sample;
        _previousHighPassOutput = output;
        return output;
    }

    private double ApplyDePopper(double sample)
    {
        if (!_settings.DePopperEnabled || _settings.DePopperAmountDb <= 0)
        {
            return sample;
        }

        const double cutoffHz = 180d;
        var alpha = 1d - Math.Exp(-2d * Math.PI * cutoffHz / SampleRate);
        _dePopperLow += alpha * (sample - _dePopperLow);

        var low = _dePopperLow;
        var high = sample - low;
        var lowLevelDb = LinearToDb(Math.Abs(low));
        const double thresholdDb = -28d;
        if (lowLevelDb <= thresholdDb)
        {
            return sample;
        }

        var overThresholdDb = lowLevelDb - thresholdDb;
        var reductionDb = Math.Min(_settings.DePopperAmountDb, overThresholdDb * 0.9d);
        return high + low * DbToLinear(-reductionDb);
    }

    private double ApplyNoiseGate(double sample)
    {
        if (!_settings.NoiseGateEnabled)
        {
            _gateOpenness = 1d;
            return sample;
        }

        var threshold = DbToLinear(_settings.NoiseGateThresholdDb);
        var level = Math.Abs(sample);
        if (level >= threshold)
        {
            _gateOpenness = 1d;
            return sample;
        }

        var attenuation = Math.Clamp(level / Math.Max(0.000001d, threshold), 0.08d, 1d);
        _gateOpenness = attenuation;
        return sample * attenuation * attenuation;
    }

    private double ApplyNoiseSuppression(double sample)
    {
        if (!_settings.NoiseSuppressionEnabled || _settings.NoiseSuppressionAmountDb <= 0)
        {
            return sample;
        }

        var instantLevel = Math.Abs(sample);
        _noiseSuppressionEnvelope = instantLevel > _noiseSuppressionEnvelope
            ? _noiseSuppressionEnvelope + (instantLevel - _noiseSuppressionEnvelope) * 0.12d
            : _noiseSuppressionEnvelope + (instantLevel - _noiseSuppressionEnvelope) * 0.004d;

        var level = _noiseSuppressionEnvelope;
        if (level < _noiseFloor * 3d)
        {
            _noiseFloor += (level - _noiseFloor) * 0.002d;
        }
        else
        {
            _noiseFloor += (level - _noiseFloor) * 0.00002d;
        }

        var noiseThreshold = Math.Max(0.000001d, _noiseFloor * 4d);
        if (level >= noiseThreshold)
        {
            return sample;
        }

        var depth = DbToLinear(-_settings.NoiseSuppressionAmountDb);
        var openness = Math.Clamp(level / noiseThreshold, 0d, 1d);
        var gain = depth + (1d - depth) * openness * openness;
        return sample * gain;
    }

    private double ApplyEchoReducer(double sample)
    {
        if (!_settings.EchoReducerEnabled || _settings.EchoReducerAmountDb <= 0)
        {
            _echoEnvelope = Math.Abs(sample);
            _echoRecentPeak = Math.Max(_echoRecentPeak * 0.999d, _echoEnvelope);
            return sample;
        }

        var level = Math.Abs(sample);
        _echoEnvelope = level > _echoEnvelope
            ? _echoEnvelope + (level - _echoEnvelope) * 0.08d
            : _echoEnvelope + (level - _echoEnvelope) * 0.004d;
        _echoRecentPeak = Math.Max(_echoRecentPeak * 0.9995d, _echoEnvelope);

        if (_echoRecentPeak < 0.0005d || _echoEnvelope > _echoRecentPeak * 0.45d)
        {
            return sample;
        }

        var tailRatio = Math.Clamp(_echoEnvelope / Math.Max(0.000001d, _echoRecentPeak * 0.45d), 0d, 1d);
        var maxReduction = DbToLinear(-_settings.EchoReducerAmountDb);
        var gain = maxReduction + (1d - maxReduction) * tailRatio;
        return sample * gain;
    }

    private double ApplyCompressor(double sample)
    {
        if (!_settings.CompressorEnabled)
        {
            _lastGainReductionDb = 0d;
            return sample;
        }

        var level = Math.Abs(sample);
        var attack = 0.25d;
        var release = 0.015d;
        _envelope = level > _envelope
            ? _envelope + (level - _envelope) * attack
            : _envelope + (level - _envelope) * release;

        var levelDb = LinearToDb(_envelope);
        if (levelDb <= _settings.CompressorThresholdDb)
        {
            _lastGainReductionDb = 0d;
            return sample;
        }

        var compressedDb = _settings.CompressorThresholdDb
            + (levelDb - _settings.CompressorThresholdDb) / Math.Max(1d, _settings.CompressorRatio);
        var gainDb = compressedDb - levelDb;
        _lastGainReductionDb = Math.Abs(gainDb);
        return sample * DbToLinear(gainDb);
    }

    private double ApplyDeEsser(double sample)
    {
        if (!_settings.DeEsserEnabled || _settings.DeEsserAmountDb <= 0)
        {
            return sample;
        }

        var sibilance = ApplyDeEsserHighPass(sample);
        var body = sample - sibilance;
        var sibilanceLevelDb = LinearToDb(Math.Abs(sibilance));
        const double thresholdDb = -34d;
        if (sibilanceLevelDb <= thresholdDb)
        {
            return sample;
        }

        var overThresholdDb = sibilanceLevelDb - thresholdDb;
        var reductionDb = Math.Min(_settings.DeEsserAmountDb, overThresholdDb * 0.65d);
        return body + sibilance * DbToLinear(-reductionDb);
    }

    private double ApplyDeEsserHighPass(double sample)
    {
        const double cutoffHz = 5200d;
        var rc = 1d / (2d * Math.PI * cutoffHz);
        var dt = 1d / SampleRate;
        var alpha = rc / (rc + dt);
        var output = alpha * (_previousDeEsserOutput + sample - _previousDeEsserInput);
        _previousDeEsserInput = sample;
        _previousDeEsserOutput = output;
        return output;
    }

    private double ApplyMakeupGain(double sample) => sample * DbToLinear(_settings.MakeupGainDb);

    private double ApplyLimiter(double sample)
    {
        if (!_settings.LimiterEnabled)
        {
            return sample;
        }

        var ceiling = DbToLinear(_settings.LimiterCeilingDb);
        return Math.Clamp(sample, -ceiling, ceiling);
    }

    private static double DbToLinear(double db) => Math.Pow(10d, db / 20d);

    private static double LinearToDb(double linear) => 20d * Math.Log10(Math.Max(0.0000001d, linear));
}
