namespace AvatarBuilder.Modules.Audio.WakeWord;

public sealed record WakeWordModelInfo(
	string ModelFolder,
	string EncoderPath,
	string DecoderPath,
	string JoinerPath,
	string TokensPath,
	string EnglishLexiconPath,
	bool IsReady,
	string Status)
{
	public const string ModelFolderName =
		"sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20";
	public const string EncoderFileName =
		"encoder-epoch-13-avg-2-chunk-8-left-64.int8.onnx";
	public const string DecoderFileName =
		"decoder-epoch-13-avg-2-chunk-8-left-64.onnx";
	public const string JoinerFileName =
		"joiner-epoch-13-avg-2-chunk-8-left-64.int8.onnx";

	public static WakeWordModelInfo Load(string? explicitFolder = null)
	{
		foreach (string candidate in CandidateFolders(explicitFolder))
		{
			string folder = Path.GetFullPath(candidate);
			var info = new WakeWordModelInfo(
				folder,
				Path.Combine(folder, EncoderFileName),
				Path.Combine(folder, DecoderFileName),
				Path.Combine(folder, JoinerFileName),
				Path.Combine(folder, "tokens.txt"),
				Path.Combine(folder, "en.phone"),
				false,
				"Sherpa wake-word model is incomplete");
			if (File.Exists(info.EncoderPath)
				&& File.Exists(info.DecoderPath)
				&& File.Exists(info.JoinerPath)
				&& File.Exists(info.TokensPath)
				&& File.Exists(info.EnglishLexiconPath))
			{
				return info with
				{
					IsReady = true,
					Status = "Sherpa dynamic wake-word model ready"
				};
			}
		}
		return new("", "", "", "", "", "", false,
			"Sherpa wake-word model is not installed");
	}

	private static IEnumerable<string> CandidateFolders(string? explicitFolder)
	{
		if (!string.IsNullOrWhiteSpace(explicitFolder))
		{
			yield return explicitFolder;
		}
		string? configured = Environment.GetEnvironmentVariable(
			"ALI_SHERPA_KWS_MODEL");
		if (!string.IsNullOrWhiteSpace(configured))
		{
			yield return configured;
		}
		yield return Path.Combine(AppContext.BaseDirectory,
			"dependencies", "audio", "keyword-spotting", ModelFolderName);
		yield return Path.Combine(AppContext.BaseDirectory,
			"models", ModelFolderName);
	}
}
