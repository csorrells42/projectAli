namespace Ali.Modules.Voice;

public static class VoiceInputLevelAnalyzer
{
    public static VoiceInputLevelSnapshot Analyze(
        ReadOnlySpan<float> samples,
        int deviceNumber,
        string deviceName,
        int sampleRate,
        int channels)
    {
        if (samples.Length == 0)
        {
            return CreateSnapshot(deviceNumber, deviceName, sampleRate, channels, rms: 0, peak: 0);
        }

        double sumSquares = 0;
        var peak = 0d;
        foreach (var sample in samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            var abs = Math.Abs(clamped);
            peak = Math.Max(peak, abs);
            sumSquares += clamped * clamped;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);
        return CreateSnapshot(deviceNumber, deviceName, sampleRate, channels, rms, peak);
    }

    public static VoiceInputLevelSnapshot CreateSnapshot(
        int deviceNumber,
        string deviceName,
        int sampleRate,
        int channels,
        double rms,
        double peak)
    {
        var state = Classify(rms, peak);
        var levelPercent = Math.Clamp(Math.Max(rms * 220d, peak * 100d), 0d, 100d);
        return new VoiceInputLevelSnapshot(
            deviceNumber,
            deviceName,
            sampleRate,
            channels,
            Math.Clamp(rms, 0d, 1d),
            Math.Clamp(peak, 0d, 1d),
            levelPercent,
            state,
            DateTimeOffset.UtcNow);
    }

    public static VoiceInputLevelState Classify(double rms, double peak)
    {
        if (peak >= 0.98d || rms >= 0.55d)
        {
            return VoiceInputLevelState.Clipping;
        }

        if (peak < 0.01d && rms < 0.002d)
        {
            return VoiceInputLevelState.Silence;
        }

        if (peak < 0.08d || rms < 0.015d)
        {
            return VoiceInputLevelState.TooQuiet;
        }

        return VoiceInputLevelState.Good;
    }
}
