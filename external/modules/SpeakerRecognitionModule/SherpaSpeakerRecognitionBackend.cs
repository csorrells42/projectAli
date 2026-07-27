using SherpaOnnx;

namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

/// <summary>
/// Native sherpa-onnx speaker embeddings on CPU. Enrollment files contain
/// little-endian float32 embeddings and are named &lt;identityId&gt;.embedding.
/// </summary>
public sealed class SherpaSpeakerRecognitionBackend :
	ISpeakerRecognitionBackend,
	ISpeakerEnrollmentBackend
{
	private readonly SpeakerEmbeddingExtractor _extractor;
	private SpeakerEmbeddingManager _manager;
	private readonly Dictionary<string, float[]> _enrollmentVectors =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly float _threshold;
	private readonly string _enrollmentFolder;

	public bool IsAvailable => true;
	public string AvailabilityStatus => "Sherpa speaker model ready";

	public static ISpeakerRecognitionBackend Create(
		string? modelPath = null,
		string? enrollmentFolder = null)
	{
		SpeakerRecognitionModelInfo model =
			SpeakerRecognitionModelInfo.Load(modelPath);
		if (!model.IsReady)
		{
			return new UnknownSpeakerRecognitionBackend();
		}
		string? configuredFolder = Environment.GetEnvironmentVariable(
			"ALI_SPEAKER_ENROLLMENTS");
		return new SherpaSpeakerRecognitionBackend(
			model.ModelPath,
			string.IsNullOrWhiteSpace(enrollmentFolder)
				? configuredFolder
				: enrollmentFolder);
	}

	public SherpaSpeakerRecognitionBackend(
		string modelPath,
		string? enrollmentFolder,
		float threshold = 0.65f)
	{
		_enrollmentFolder = string.IsNullOrWhiteSpace(enrollmentFolder)
			? Path.Combine(
				AppContext.BaseDirectory,
				"SpeakerRecognition",
				"Enrollments")
			: Path.GetFullPath(enrollmentFolder);
		_extractor = new SpeakerEmbeddingExtractor(
			new SpeakerEmbeddingExtractorConfig
			{
				Model = modelPath,
				NumThreads = 2,
				Debug = 0,
				Provider = "cpu"
			});
		_manager = new SpeakerEmbeddingManager(_extractor.Dim);
		_threshold = threshold;
		ReloadEnrollments();
	}

	public SpeakerRecognitionEvidence Recognize(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		if (_manager.NumSpeakers == 0)
		{
			return new SpeakerRecognitionEvidence(
				false,
				"",
				0,
				"Sherpa speaker model ready; no voices enrolled");
		}
		using OnlineStream stream = _extractor.CreateStream();
		stream.AcceptWaveform(sampleRate, samples.ToArray());
		stream.InputFinished();
		if (!_extractor.IsReady(stream))
		{
			return new SpeakerRecognitionEvidence(
				false,
				"",
				0,
				"Utterance is too short for speaker identification");
		}
		float[] embedding = _extractor.Compute(stream);
		string identityId = _manager.Search(embedding, _threshold);
		double similarity = BestSimilarity(embedding);
		return string.IsNullOrWhiteSpace(identityId)
			? new SpeakerRecognitionEvidence(
				false,
				"",
				similarity,
				$"No enrolled voice exceeded the threshold; best similarity {similarity:0.000}")
			: new SpeakerRecognitionEvidence(
				true,
				identityId,
				similarity,
				$"Sherpa speaker match similarity {similarity:0.000}");
	}

	public bool TryComputeEmbedding(
		ReadOnlySpan<float> samples,
		int sampleRate,
		out float[] embedding,
		out string status)
	{
		embedding = [];
		using OnlineStream stream = _extractor.CreateStream();
		stream.AcceptWaveform(sampleRate, samples.ToArray());
		stream.InputFinished();
		if (!_extractor.IsReady(stream))
		{
			status =
				"That phrase was too short. Speak naturally for two to five seconds.";
			return false;
		}
		embedding = _extractor.Compute(stream);
		status = "Voice embedding captured.";
		return true;
	}

	public bool SaveEnrollment(
		string personIdentityId,
		IReadOnlyList<float[]> embeddings,
		out string status)
	{
		if (string.IsNullOrWhiteSpace(personIdentityId)
			|| embeddings.Count == 0
			|| embeddings.Any(value => value.Length != _extractor.Dim))
		{
			status = "Voice enrollment data was invalid.";
			return false;
		}
		Directory.CreateDirectory(_enrollmentFolder);
		float[] averaged = AverageAndNormalize(embeddings, _extractor.Dim);
		byte[] bytes = new byte[averaged.Length * sizeof(float)];
		Buffer.BlockCopy(averaged, 0, bytes, 0, bytes.Length);
		string path = EnrollmentPath(personIdentityId);
		string temporary = path + ".new";
		File.WriteAllBytes(temporary, bytes);
		File.Move(temporary, path, true);
		ReloadEnrollments();
		status =
			$"Voice enrollment complete from {embeddings.Count} samples.";
		return true;
	}

	public bool DeleteEnrollment(
		string personIdentityId,
		out string status)
	{
		if (string.IsNullOrWhiteSpace(personIdentityId))
		{
			status = "A registered UserId is required.";
			return false;
		}
		string path = EnrollmentPath(personIdentityId);
		if (File.Exists(path))
		{
			File.Delete(path);
		}
		ReloadEnrollments();
		status = "Voice enrollment deleted.";
		return true;
	}

	public void Dispose()
	{
		_manager.Dispose();
		_extractor.Dispose();
	}

	private void ReloadEnrollments()
	{
		SpeakerEmbeddingManager previous = _manager;
		_manager = new SpeakerEmbeddingManager(_extractor.Dim);
		_enrollmentVectors.Clear();
		previous.Dispose();
		if (!Directory.Exists(_enrollmentFolder))
		{
			return;
		}
		foreach (string path in Directory.EnumerateFiles(
			_enrollmentFolder,
			"*.embedding",
			SearchOption.TopDirectoryOnly))
		{
			byte[] bytes = File.ReadAllBytes(path);
			if (bytes.Length != _extractor.Dim * sizeof(float))
			{
				continue;
			}
			var values = new float[_extractor.Dim];
			Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
			string identityId = Path.GetFileNameWithoutExtension(path);
			_manager.Add(identityId, values);
			_enrollmentVectors[identityId] = values;
		}
	}

	private double BestSimilarity(ReadOnlySpan<float> embedding)
	{
		double best = 0d;
		foreach (float[] enrolled in _enrollmentVectors.Values)
		{
			best = Math.Max(best, CosineSimilarity(embedding, enrolled));
		}
		return Math.Clamp(best, 0d, 1d);
	}

	private static double CosineSimilarity(
		ReadOnlySpan<float> left,
		ReadOnlySpan<float> right)
	{
		if (left.Length == 0 || left.Length != right.Length)
		{
			return 0d;
		}
		double dot = 0d;
		double leftMagnitude = 0d;
		double rightMagnitude = 0d;
		for (int index = 0; index < left.Length; index++)
		{
			dot += left[index] * right[index];
			leftMagnitude += left[index] * left[index];
			rightMagnitude += right[index] * right[index];
		}
		double denominator = Math.Sqrt(leftMagnitude * rightMagnitude);
		return denominator <= double.Epsilon ? 0d : dot / denominator;
	}

	private string EnrollmentPath(string personIdentityId)
	{
		foreach (char invalid in Path.GetInvalidFileNameChars())
		{
			if (personIdentityId.Contains(invalid))
			{
				throw new ArgumentException(
					"UserId contains an invalid file-name character.",
					nameof(personIdentityId));
			}
		}
		return Path.Combine(
			_enrollmentFolder,
			personIdentityId + ".embedding");
	}

	private static float[] AverageAndNormalize(
		IReadOnlyList<float[]> embeddings,
		int dimension)
	{
		var averaged = new float[dimension];
		foreach (float[] embedding in embeddings)
		{
			for (int index = 0; index < dimension; index++)
			{
				averaged[index] += embedding[index];
			}
		}
		double magnitude = Math.Sqrt(
			averaged.Sum(value => (double)value * value));
		if (magnitude > 0)
		{
			for (int index = 0; index < dimension; index++)
			{
				averaged[index] = (float)(averaged[index] / magnitude);
			}
		}
		return averaged;
	}
}
