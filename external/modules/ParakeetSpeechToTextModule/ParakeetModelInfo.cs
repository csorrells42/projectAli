namespace AvatarBuilder.Modules.Audio.ParakeetSpeechToText;

public sealed record ParakeetModelInfo(
	string ModelFolder,
	string EncoderPath,
	string DecoderPath,
	string JoinerPath,
	string TokensPath,
	bool IsReady,
	string Status)
{
	public const string ModelName =
		"sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8";

	public static ParakeetModelInfo Load(string? modelFolder = null)
	{
		foreach (string candidate in CandidateFolders(modelFolder))
		{
			string folder = Path.GetFullPath(candidate);
			string encoder = Path.Combine(folder, "encoder.int8.onnx");
			string decoder = Path.Combine(folder, "decoder.int8.onnx");
			string joiner = Path.Combine(folder, "joiner.int8.onnx");
			string tokens = Path.Combine(folder, "tokens.txt");
			if (File.Exists(encoder)
				&& File.Exists(decoder)
				&& File.Exists(joiner)
				&& File.Exists(tokens))
			{
				return new(
					folder,
					encoder,
					decoder,
					joiner,
					tokens,
					true,
					"Parakeet model ready");
			}
		}

		return new(
			modelFolder ?? "",
			"",
			"",
			"",
			"",
			false,
			"Parakeet model is not installed");
	}

	private static IEnumerable<string> CandidateFolders(string? explicitFolder)
	{
		if (!string.IsNullOrWhiteSpace(explicitFolder))
		{
			yield return explicitFolder;
		}
		string? configured =
			Environment.GetEnvironmentVariable("ALI_PARAKEET_MODEL");
		if (!string.IsNullOrWhiteSpace(configured))
		{
			yield return configured;
		}
		yield return Path.Combine(
			AppContext.BaseDirectory,
			"dependencies",
			"audio",
			"parakeet",
			ModelName);
		yield return Path.Combine(
			AppContext.BaseDirectory,
			"models",
			ModelName);
	}
}
