namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public sealed record EnrollmentTranscription(
	string Text,
	string Provider,
	bool Succeeded,
	string Status);

public interface IEnrollmentTranscriber : IDisposable
{
	string ProviderName { get; }
	bool IsConfigured { get; }

	EnrollmentTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate);
}

public static class EnrollmentTranscriptMatcher
{
	private const double MinimumWordSimilarity = 0.85d;
	private const double MinimumLengthCoverage = 0.9d;

	public static bool IsComplete(
		string expected,
		string transcript,
		out double similarity)
	{
		string[] expectedWords = Words(expected);
		string[] transcriptWords = Words(transcript);
		if (expectedWords.Length == 0 || transcriptWords.Length == 0)
		{
			similarity = 0d;
			return false;
		}

		int distance = WordEditDistance(expectedWords, transcriptWords);
		similarity = 1d - (double)distance
			/ Math.Max(expectedWords.Length, transcriptWords.Length);
		double lengthCoverage = (double)transcriptWords.Length
			/ expectedWords.Length;
		return similarity >= MinimumWordSimilarity
			&& lengthCoverage >= MinimumLengthCoverage;
	}

	private static string[] Words(string value) => value
		.ToLowerInvariant()
		.Split(
			[ ' ', '\t', '\r', '\n' ],
			StringSplitOptions.RemoveEmptyEntries)
		.Select(word => new string(word
			.Where(char.IsLetterOrDigit)
			.ToArray()))
		.Where(word => word.Length > 0)
		.ToArray();

	private static int WordEditDistance(
		IReadOnlyList<string> expected,
		IReadOnlyList<string> actual)
	{
		var previous = new int[actual.Count + 1];
		var current = new int[actual.Count + 1];
		for (int column = 0; column <= actual.Count; column++)
		{
			previous[column] = column;
		}
		for (int row = 1; row <= expected.Count; row++)
		{
			current[0] = row;
			for (int column = 1; column <= actual.Count; column++)
			{
				int substitution = string.Equals(
					expected[row - 1],
					actual[column - 1],
					StringComparison.Ordinal)
					? 0
					: 1;
				current[column] = Math.Min(
					Math.Min(
						previous[column] + 1,
						current[column - 1] + 1),
					previous[column - 1] + substitution);
			}
			(previous, current) = (current, previous);
		}
		return previous[actual.Count];
	}
}
