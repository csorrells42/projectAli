namespace AvatarBuilder.Modules.Audio.SpeechToText;

public static class WhisperBackendFactory
{
	public static ISpeechToTextBackend Create(
		TimeSpan? responseTimeout = null)
	{
		string? executable = ReadSetting("ALI_WHISPER_EXE");
		string? model = ReadSetting("ALI_WHISPER_MODEL");
		string workerScript = Path.Combine(
			AppContext.BaseDirectory,
			"dependencies",
			"audio",
			"whisper",
			"faster_whisper_worker.py");
		if (!string.IsNullOrWhiteSpace(executable)
			&& !string.IsNullOrWhiteSpace(model)
			&& File.Exists(executable)
			&& Directory.Exists(model)
			&& File.Exists(workerScript))
		{
			return new FasterWhisperWorkerBackend(
				executable,
				model,
				workerScript,
				ReadSetting("ALI_WHISPER_MODEL_ID") ?? "small.en",
				responseTimeout);
		}
		return new WhisperCliSpeechToTextBackend(
			executable,
			model,
			ReadSetting("ALI_WHISPER_ARGS"),
			responseTimeout);
	}

	private static string? ReadSetting(string name)
	{
		string? value = Environment.GetEnvironmentVariable(name);
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		try
		{
			return Environment.GetEnvironmentVariable(
				name,
				EnvironmentVariableTarget.User);
		}
		catch
		{
			return null;
		}
	}
}
