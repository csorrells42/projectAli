namespace Ali.Modules.Voice;

public sealed record VoiceNormalizationResult(
    bool Applied,
    double GainMultiplier,
    double OriginalRms,
    double OriginalPeak,
    double TargetRms,
    double PeakCeiling);

public static class VoiceAudioNormalizer
{
    public static VoiceNormalizationResult NormalizePcm16WaveInPlace(
        string filePath,
        double targetRms = 0.08d,
        double peakCeiling = 0.92d,
        double maximumGainMultiplier = 8d)
    {
        var bytes = File.ReadAllBytes(filePath);
        var data = FindPcm16DataChunk(bytes);
        var sampleCount = data.Size / sizeof(short);
        if (sampleCount == 0)
        {
            return new VoiceNormalizationResult(false, 1d, 0d, 0d, targetRms, peakCeiling);
        }

        var peak = 0d;
        var sumSquares = 0d;
        for (var offset = data.Offset; offset < data.Offset + data.Size; offset += sizeof(short))
        {
            var sample = BitConverter.ToInt16(bytes, offset) / 32768d;
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        if (rms <= 0.000001d || peak <= 0.000001d)
        {
            return new VoiceNormalizationResult(false, 1d, rms, peak, targetRms, peakCeiling);
        }

        var gainForRms = targetRms / rms;
        var gainForPeak = peakCeiling / peak;
        var gain = Math.Clamp(Math.Min(gainForRms, gainForPeak), 0.1d, maximumGainMultiplier);
        if (Math.Abs(gain - 1d) < 0.01d)
        {
            return new VoiceNormalizationResult(false, 1d, rms, peak, targetRms, peakCeiling);
        }

        for (var offset = data.Offset; offset < data.Offset + data.Size; offset += sizeof(short))
        {
            var sample = BitConverter.ToInt16(bytes, offset) / 32768d;
            var normalized = (short)Math.Round(Math.Clamp(sample * gain, -1d, 1d) * short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, sizeof(short)), normalized);
        }

        File.WriteAllBytes(filePath, bytes);
        return new VoiceNormalizationResult(true, gain, rms, peak, targetRms, peakCeiling);
    }

    private static (int Offset, int Size) FindPcm16DataChunk(byte[] bytes)
    {
        if (bytes.Length < 44
            || bytes[0] != 'R'
            || bytes[1] != 'I'
            || bytes[2] != 'F'
            || bytes[3] != 'F'
            || bytes[8] != 'W'
            || bytes[9] != 'A'
            || bytes[10] != 'V'
            || bytes[11] != 'E')
        {
            throw new InvalidOperationException("Audio must be a WAV file.");
        }

        var position = 12;
        var isPcm16 = false;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            var chunkSize = BitConverter.ToInt32(bytes, position + 4);
            var chunkDataOffset = position + 8;
            if (chunkDataOffset + chunkSize > bytes.Length)
            {
                throw new InvalidOperationException("WAV file has an invalid chunk size.");
            }

            if (chunkId == "fmt ")
            {
                var audioFormat = BitConverter.ToInt16(bytes, chunkDataOffset);
                var bitsPerSample = BitConverter.ToInt16(bytes, chunkDataOffset + 14);
                isPcm16 = audioFormat == 1 && bitsPerSample == 16;
            }
            else if (chunkId == "data")
            {
                if (!isPcm16)
                {
                    throw new InvalidOperationException("Audio must be 16-bit PCM WAV.");
                }

                return (chunkDataOffset, chunkSize);
            }

            position = chunkDataOffset + chunkSize + (chunkSize % 2);
        }

        throw new InvalidOperationException("WAV data chunk was not found.");
    }
}
