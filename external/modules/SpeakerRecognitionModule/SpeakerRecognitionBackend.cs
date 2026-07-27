namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public sealed record SpeakerRecognitionEvidence(
	bool IsKnown,
	string PersonIdentityId,
	double Similarity,
	string Status,
	bool IsEnrollmentUtterance = false);

public interface ISpeakerRecognitionBackend : IDisposable
{
	bool IsAvailable { get; }
	string AvailabilityStatus { get; }

	SpeakerRecognitionEvidence Recognize(
		ReadOnlySpan<float> samples,
		int sampleRate);
}

public interface ISpeakerEnrollmentBackend
{
	bool TryComputeEmbedding(
		ReadOnlySpan<float> samples,
		int sampleRate,
		out float[] embedding,
		out string status);

	bool SaveEnrollment(
		string personIdentityId,
		IReadOnlyList<float[]> embeddings,
		out string status);

	bool DeleteEnrollment(
		string personIdentityId,
		out string status);
}

public sealed class UnknownSpeakerRecognitionBackend :
	ISpeakerRecognitionBackend
{
	public bool IsAvailable => false;
	public string AvailabilityStatus => "Speaker model not configured";

	public SpeakerRecognitionEvidence Recognize(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		return new SpeakerRecognitionEvidence(
			false,
			"",
			0,
			"Speaker model not configured");
	}

	public void Dispose()
	{
	}
}
