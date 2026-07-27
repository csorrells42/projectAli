namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public sealed record SpeakerRecognitionModuleSelfTestResult(
	bool Succeeded,
	string Detail);

public static class SpeakerRecognitionModuleSelfTest
{
	public static SpeakerRecognitionModuleSelfTestResult Run(
		string modelPath,
		string enrollmentWavPath,
		string recognitionWavPath)
	{
		const string expected =
			"Please turn on the kitchen light and tell me what time it is.";
		if (!EnrollmentTranscriptMatcher.IsComplete(
			expected,
			"Please turn on the kitchen light and tell me what time it is",
			out _)
			|| !EnrollmentTranscriptMatcher.IsComplete(
				expected,
				"Please turn on kitchen light and tell me what time it is",
				out _)
			|| EnrollmentTranscriptMatcher.IsComplete(
				expected,
				"Please turn on the kitchen light",
				out _))
		{
			return new(
				false,
				"Enrollment transcript completeness policy failed.");
		}
		string temporary = Path.Combine(
			Path.GetTempPath(),
			"SpeakerRecognitionModule.Hub",
			Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(temporary);
			using var backend = new SherpaSpeakerRecognitionBackend(
				modelPath,
				temporary);
			(float[] enrollment, int sampleRate) = ReadPcm16Wave(
				enrollmentWavPath);
			(float[] recognition, int recognitionRate) = ReadPcm16Wave(
				recognitionWavPath);
			if (!backend.TryComputeEmbedding(
				enrollment,
				sampleRate,
				out float[] embedding,
				out string status))
			{
				return new(false, status);
			}
			const string testUserId = "offline-speaker-test";
			if (!backend.SaveEnrollment(
				testUserId,
				[embedding, embedding, embedding],
				out status))
			{
				return new(false, status);
			}
			SpeakerRecognitionEvidence evidence = backend.Recognize(
				recognition,
				recognitionRate);
			if (!evidence.IsKnown
				|| !string.Equals(
					evidence.PersonIdentityId,
					testUserId,
					StringComparison.Ordinal)
				|| evidence.Similarity <= 0d
				|| evidence.Similarity > 1d)
			{
				return new(
					false,
					"Second official utterance did not return the enrolled UserId and a real normalized similarity: "
					+ evidence.Status);
			}
			return new(
				true,
				$"Official Sherpa model initialized, enrolled one UserId, and recognized a second utterance with similarity {evidence.Similarity:F3}.");
		}
		catch (Exception exception)
		{
			return new(false, exception.ToString());
		}
		finally
		{
			try
			{
				if (Directory.Exists(temporary))
				{
					Directory.Delete(temporary, true);
				}
			}
			catch
			{
			}
		}
	}

	private static (float[] Samples, int SampleRate) ReadPcm16Wave(
		string path)
	{
		using var stream = File.OpenRead(path);
		using var reader = new BinaryReader(stream);
		if (new string(reader.ReadChars(4)) != "RIFF")
		{
			throw new InvalidDataException("Expected a RIFF wave file.");
		}
		reader.ReadUInt32();
		if (new string(reader.ReadChars(4)) != "WAVE")
		{
			throw new InvalidDataException("Expected a WAVE file.");
		}

		ushort format = 0;
		ushort channels = 0;
		int sampleRate = 0;
		ushort bitsPerSample = 0;
		byte[] data = [];
		while (stream.Position + 8 <= stream.Length)
		{
			string chunk = new(reader.ReadChars(4));
			uint length = reader.ReadUInt32();
			long next = stream.Position + length + (length & 1);
			if (chunk == "fmt ")
			{
				format = reader.ReadUInt16();
				channels = reader.ReadUInt16();
				sampleRate = reader.ReadInt32();
				reader.ReadUInt32();
				reader.ReadUInt16();
				bitsPerSample = reader.ReadUInt16();
			}
			else if (chunk == "data")
			{
				data = reader.ReadBytes(checked((int)length));
			}
			stream.Position = Math.Min(next, stream.Length);
		}
		if (format != 1 || channels == 0 || bitsPerSample != 16
			|| sampleRate <= 0 || data.Length == 0)
		{
			throw new InvalidDataException(
				"The hub requires a PCM16 wave file.");
		}

		int frames = data.Length / (channels * sizeof(short));
		var samples = new float[frames];
		for (int frame = 0; frame < frames; frame++)
		{
			int total = 0;
			for (int channel = 0; channel < channels; channel++)
			{
				int offset = (frame * channels + channel) * sizeof(short);
				total += BitConverter.ToInt16(data, offset);
			}
			samples[frame] = total / (channels * 32768f);
		}
		return (samples, sampleRate);
	}
}
