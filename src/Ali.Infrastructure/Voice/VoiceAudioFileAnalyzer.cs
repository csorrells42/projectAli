namespace Ali.Infrastructure.Voice;

public static class VoiceAudioFileAnalyzer
{
    public static VoiceCaptureDiagnostics AnalyzeWaveAudio(string filePath, int deviceNumber = 0, string deviceName = "recorded audio")
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        var riff = new string(reader.ReadChars(4));
        _ = reader.ReadInt32();
        var wave = new string(reader.ReadChars(4));
        if (riff != "RIFF" || wave != "WAVE")
        {
            throw new InvalidOperationException("Recorded audio is not a WAV file.");
        }

        short channels = 0;
        var sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (stream.Position < stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                _ = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
                if (chunkSize > 16)
                {
                    reader.ReadBytes(chunkSize - 16);
                }
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.ReadBytes(chunkSize);
            }

            if (chunkSize % 2 == 1 && stream.Position < stream.Length)
            {
                reader.ReadByte();
            }
        }

        if (data is null || bitsPerSample != 16 || channels <= 0 || sampleRate <= 0)
        {
            throw new InvalidOperationException("Recorded WAV must be 16-bit PCM audio.");
        }

        long sumSquares = 0;
        var peak = 0;
        var sampleCount = data.Length / 2;
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(data, index);
            var abs = Math.Abs((int)sample);
            peak = Math.Max(peak, abs);
            sumSquares += (long)sample * sample;
        }

        var rms = sampleCount == 0 ? 0 : (int)Math.Sqrt(sumSquares / (double)sampleCount);
        var duration = sampleCount / (double)(sampleRate * channels);
        var level = VoiceInputLevelAnalyzer.CreateSnapshot(
            deviceNumber,
            deviceName,
            sampleRate,
            channels,
            rms / (double)short.MaxValue,
            peak / (double)short.MaxValue);

        return new VoiceCaptureDiagnostics(filePath, duration, sampleRate, channels, rms, peak, level);
    }
}
