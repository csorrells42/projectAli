namespace AvatarBuilder.Modules.Audio.ParakeetSpeechToText;

public sealed record ParakeetSpeechToTextModuleSelfTestResult(
	bool Succeeded,
	string Detail,
	string Transcript);

public static class ParakeetSpeechToTextModuleSelfTest
{
	public static ParakeetSpeechToTextModuleSelfTestResult Run(
		string? modelFolder = null)
	{
		try
		{
			ParakeetModelInfo model =
				ParakeetModelInfo.Load(modelFolder);
			if (!model.IsReady)
			{
				return new(false, model.Status, "");
			}
			string wavePath = Path.Combine(
				model.ModelFolder,
				"test_wavs",
				"0.wav");
			if (!File.Exists(wavePath))
			{
				return new(
					false,
					"Parakeet test audio is missing",
					"");
			}
			(float[] samples, int sampleRate) = ReadPcm16MonoWave(wavePath);
			using var transcriber = new ParakeetEnrollmentTranscriber(
				model.ModelFolder,
				2);
			var result = transcriber.Transcribe(
				samples,
				sampleRate);
			return new(
				result.Succeeded,
				result.Status,
				result.Text);
		}
		catch (Exception exception)
		{
			return new(false, exception.ToString(), "");
		}
	}

	private static (float[] Samples, int SampleRate) ReadPcm16MonoWave(
		string path)
	{
		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		if (new string(reader.ReadChars(4)) != "RIFF")
		{
			throw new InvalidDataException("Test audio is not RIFF WAV.");
		}
		_ = reader.ReadUInt32();
		if (new string(reader.ReadChars(4)) != "WAVE")
		{
			throw new InvalidDataException("Test audio is not WAVE.");
		}
		int sampleRate = 0;
		short channels = 0;
		short bitsPerSample = 0;
		byte[]? pcm = null;
		while (stream.Position + 8 <= stream.Length)
		{
			string chunkId = new(reader.ReadChars(4));
			int chunkSize = checked((int)reader.ReadUInt32());
			long next = stream.Position + chunkSize + (chunkSize & 1);
			if (chunkId == "fmt ")
			{
				short format = reader.ReadInt16();
				channels = reader.ReadInt16();
				sampleRate = reader.ReadInt32();
				_ = reader.ReadInt32();
				_ = reader.ReadInt16();
				bitsPerSample = reader.ReadInt16();
				if (format != 1)
				{
					throw new InvalidDataException(
						"Test audio is not PCM.");
				}
			}
			else if (chunkId == "data")
			{
				pcm = reader.ReadBytes(chunkSize);
			}
			stream.Position = Math.Min(next, stream.Length);
		}
		if (channels != 1 || bitsPerSample != 16 || sampleRate <= 0
			|| pcm is null)
		{
			throw new InvalidDataException(
				"Test audio must be mono 16-bit PCM.");
		}
		float[] samples = new float[pcm.Length / 2];
		for (int index = 0; index < samples.Length; index++)
		{
			samples[index] = BitConverter.ToInt16(pcm, index * 2)
				/ 32768f;
		}
		return (samples, sampleRate);
	}
}
