namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public sealed record SpeakerRecognitionModelInfo(
	string ModelPath,
	bool IsReady,
	string Status)
{
	public const string ModelFileName =
		"3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx";

	public static SpeakerRecognitionModelInfo Load(
		string? explicitModelPath = null)
	{
		foreach (string candidate in CandidatePaths(explicitModelPath))
		{
			string path = Path.GetFullPath(candidate);
			if (File.Exists(path))
			{
				return new(path, true, "Sherpa speaker model ready");
			}
		}

		return new(
			explicitModelPath ?? "",
			false,
			"Sherpa speaker model is not installed");
	}

	private static IEnumerable<string> CandidatePaths(string? explicitPath)
	{
		if (!string.IsNullOrWhiteSpace(explicitPath))
		{
			yield return explicitPath;
		}
		string? configured = Environment.GetEnvironmentVariable(
			"ALI_SHERPA_SPEAKER_MODEL");
		if (!string.IsNullOrWhiteSpace(configured))
		{
			yield return configured;
		}
		yield return Path.Combine(
			AppContext.BaseDirectory,
			"dependencies",
			"audio",
			"speaker-identification",
			ModelFileName);
		yield return Path.Combine(
			AppContext.BaseDirectory,
			"models",
			ModelFileName);
	}
}
