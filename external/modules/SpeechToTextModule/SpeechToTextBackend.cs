using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AvatarBuilder.Modules.Audio.SpeechToText;

public sealed record SpeechTranscription(
	string Text,
	string Provider,
	bool Succeeded,
	string Status);

public interface ISpeechToTextBackend : IDisposable
{
	string ProviderName { get; }
	bool IsConfigured { get; }
	SpeechTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate);
}

public sealed class WhisperCliSpeechToTextBackend :
	ISpeechToTextBackend
{
	private readonly string? _executable;
	private readonly string? _model;
	private readonly string _arguments;
	private readonly TimeSpan _responseTimeout;

	public string ProviderName => "Local Whisper CLI";

	public bool IsConfigured =>
		!string.IsNullOrWhiteSpace(_executable)
		&& File.Exists(_executable)
		&& (!_arguments.Contains(
				"{model}",
				StringComparison.OrdinalIgnoreCase)
			|| (!string.IsNullOrWhiteSpace(_model)
				&& (File.Exists(_model)
					|| Directory.Exists(_model))));

	public WhisperCliSpeechToTextBackend(
		string? executable = null,
		string? model = null,
		string? arguments = null,
		TimeSpan? responseTimeout = null)
	{
		_executable = executable
			?? Environment.GetEnvironmentVariable("ALI_WHISPER_EXE");
		_model = model
			?? Environment.GetEnvironmentVariable("ALI_WHISPER_MODEL");
		_arguments = arguments
			?? Environment.GetEnvironmentVariable("ALI_WHISPER_ARGS")
			?? "-m \"{model}\" -f \"{audio}\" -otxt -of \"{outputBase}\"";
		_responseTimeout = responseTimeout ?? TimeSpan.FromMinutes(2);
	}

	public SpeechTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		if (!IsConfigured)
		{
			return new SpeechTranscription(
				"",
				ProviderName,
				false,
				"Local Whisper is not configured");
		}
		string root = Path.Combine(
			Path.GetTempPath(),
			"AvatarBuilderSpeech");
		Directory.CreateDirectory(root);
		string id = Guid.NewGuid().ToString("N");
		string wavPath = Path.Combine(root, id + ".wav");
		string outputBase = Path.Combine(root, id + ".transcript");
		string outputPath = outputBase + ".txt";
		try
		{
			WritePcm16Wave(wavPath, samples, sampleRate);
			string rendered = _arguments
				.Replace("{audio}", wavPath, StringComparison.OrdinalIgnoreCase)
				.Replace("{outputBase}", outputBase, StringComparison.OrdinalIgnoreCase)
				.Replace("{model}", _model ?? "", StringComparison.OrdinalIgnoreCase);
			using var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = _executable!,
					Arguments = rendered,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			process.Start();
			string stdout = process.StandardOutput.ReadToEnd();
			string stderr = process.StandardError.ReadToEnd();
			if (!process.WaitForExit(_responseTimeout))
			{
				process.Kill(entireProcessTree: true);
				return new SpeechTranscription(
					"",
					ProviderName,
					false,
					"Local Whisper timed out");
			}
			if (process.ExitCode != 0)
			{
				return new SpeechTranscription(
					"",
					ProviderName,
					false,
					"Local Whisper failed: " + OneLine(stderr));
			}
			string text = File.Exists(outputPath)
				? File.ReadAllText(outputPath).Trim()
				: stdout.Trim();
			return new SpeechTranscription(
				text,
				ProviderName,
				!string.IsNullOrWhiteSpace(text),
				string.IsNullOrWhiteSpace(text)
					? "Local Whisper returned no speech"
					: "Transcribed");
		}
		finally
		{
			TryDelete(wavPath);
			TryDelete(outputPath);
			TryDelete(outputBase + ".json");
		}
	}

	public void Dispose()
	{
	}

	private static void WritePcm16Wave(
		string path,
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		using var stream = File.Create(path);
		using var writer = new BinaryWriter(stream, Encoding.ASCII);
		int dataLength = checked(samples.Length * 2);
		writer.Write(Encoding.ASCII.GetBytes("RIFF"));
		writer.Write(36 + dataLength);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
		writer.Write(16);
		writer.Write((short)1);
		writer.Write((short)1);
		writer.Write(sampleRate);
		writer.Write(sampleRate * 2);
		writer.Write((short)2);
		writer.Write((short)16);
		writer.Write(Encoding.ASCII.GetBytes("data"));
		writer.Write(dataLength);
		foreach (float sample in samples)
		{
			writer.Write((short)Math.Round(
				Math.Clamp(sample, -1f, 1f) * short.MaxValue));
		}
	}

	private static string OneLine(string text)
	{
		string value = text.ReplaceLineEndings(" ").Trim();
		return value.Length <= 200 ? value : value[..200];
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
		}
	}
}
