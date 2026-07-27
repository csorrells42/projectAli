using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Audio.SpeechToText;

public sealed class FasterWhisperWorkerBackend : ISpeechToTextBackend
{
	private readonly string _pythonExecutable;
	private readonly string _modelRoot;
	private readonly string _workerScript;
	private readonly string _modelId;
	private readonly TimeSpan _responseTimeout;
	private readonly ConcurrentQueue<string> _standardError = new();
	private Process? _worker;
	private long _requestId;

	public string ProviderName =>
		"Local Faster-Whisper " + _modelId;

	public bool IsConfigured =>
		File.Exists(_pythonExecutable)
		&& Directory.Exists(_modelRoot)
		&& File.Exists(_workerScript);

	public FasterWhisperWorkerBackend(
		string pythonExecutable,
		string modelRoot,
		string workerScript,
		string modelId = "small.en",
		TimeSpan? responseTimeout = null)
	{
		_pythonExecutable = pythonExecutable;
		_modelRoot = modelRoot;
		_workerScript = workerScript;
		_modelId = modelId;
		_responseTimeout = responseTimeout ?? TimeSpan.FromMinutes(2);
	}

	public SpeechTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		if (!IsConfigured)
		{
			return new(
				"",
				ProviderName,
				false,
				"Local Faster-Whisper is not configured");
		}

		string root = Path.Combine(
			Path.GetTempPath(),
			"AvatarBuilderSpeech");
		Directory.CreateDirectory(root);
		string audioPath = Path.Combine(
			root,
			Guid.NewGuid().ToString("N") + ".wav");
		try
		{
			WritePcm16Wave(audioPath, samples, sampleRate);
			Process worker = EnsureWorker();
			long id = Interlocked.Increment(ref _requestId);
			string request = JsonSerializer.Serialize(new
			{
				id,
				audio = audioPath
			});
			worker.StandardInput.WriteLine(request);
			worker.StandardInput.Flush();

			string? line = worker.StandardOutput
				.ReadLineAsync()
				.WaitAsync(_responseTimeout)
				.GetAwaiter()
				.GetResult();
			if (string.IsNullOrWhiteSpace(line))
			{
				throw new InvalidOperationException(
					"Faster-Whisper worker ended without a response.");
			}
			WorkerResponse? response =
				JsonSerializer.Deserialize<WorkerResponse>(line);
			if (response is null || response.Id != id)
			{
				throw new InvalidOperationException(
					"Faster-Whisper returned an invalid response.");
			}
			if (!response.Ok)
			{
				return new(
					"",
					ProviderName,
					false,
					"Faster-Whisper failed: " + response.Error);
			}
			string text = response.Text.Trim();
			return new(
				text,
				ProviderName,
				!string.IsNullOrWhiteSpace(text),
				string.IsNullOrWhiteSpace(text)
					? "Faster-Whisper returned no speech"
					: "Transcribed");
		}
		catch (Exception exception)
		{
			ResetWorker();
			return new(
				"",
				ProviderName,
				false,
				"Faster-Whisper unavailable: "
					+ OneLine(exception.Message));
		}
		finally
		{
			TryDelete(audioPath);
		}
	}

	public void Dispose() => ResetWorker();

	private Process EnsureWorker()
	{
		if (_worker is { HasExited: false })
		{
			return _worker;
		}
		ResetWorker();
		var startInfo = new ProcessStartInfo
		{
			FileName = _pythonExecutable,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		startInfo.ArgumentList.Add(_workerScript);
		startInfo.ArgumentList.Add("--model-root");
		startInfo.ArgumentList.Add(_modelRoot);
		startInfo.ArgumentList.Add("--model-id");
		startInfo.ArgumentList.Add(_modelId);
		startInfo.Environment["PYTHONUTF8"] = "1";
		var worker = new Process
		{
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};
		worker.ErrorDataReceived += (_, eventArgs) =>
		{
			if (!string.IsNullOrWhiteSpace(eventArgs.Data))
			{
				_standardError.Enqueue(eventArgs.Data);
			}
		};
		worker.Start();
		worker.BeginErrorReadLine();
		_worker = worker;
		return worker;
	}

	private void ResetWorker()
	{
		Process? worker = _worker;
		_worker = null;
		if (worker is null)
		{
			return;
		}
		try
		{
			if (!worker.HasExited)
			{
				worker.StandardInput.Close();
				if (!worker.WaitForExit(500))
				{
					worker.Kill(entireProcessTree: true);
				}
			}
		}
		catch
		{
		}
		finally
		{
			worker.Dispose();
		}
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

	private sealed record WorkerResponse(
		[property: JsonPropertyName("id")] long Id,
		[property: JsonPropertyName("ok")] bool Ok,
		[property: JsonPropertyName("text")] string Text,
		[property: JsonPropertyName("error")] string Error);
}
