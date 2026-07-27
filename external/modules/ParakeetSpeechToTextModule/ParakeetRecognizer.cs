using AvatarBuilder.Modules.Audio.SpeechToText;
using SherpaOnnx;

namespace AvatarBuilder.Modules.Audio.ParakeetSpeechToText;

internal sealed class ParakeetRecognizer : IDisposable
{
	private readonly OfflineRecognizer _recognizer;

	public ParakeetRecognizer(ParakeetModelInfo model, int numThreads)
	{
		var config = new OfflineRecognizerConfig();
		config.FeatConfig.SampleRate = 16000;
		config.FeatConfig.FeatureDim = 80;
		config.ModelConfig.Transducer.Encoder = model.EncoderPath;
		config.ModelConfig.Transducer.Decoder = model.DecoderPath;
		config.ModelConfig.Transducer.Joiner = model.JoinerPath;
		config.ModelConfig.Tokens = model.TokensPath;
		config.ModelConfig.ModelType = "nemo_transducer";
		config.ModelConfig.NumThreads = Math.Max(1, numThreads);
		config.ModelConfig.Debug = 0;
		config.DecodingMethod = "greedy_search";
		config.MaxActivePaths = 4;
		_recognizer = new OfflineRecognizer(config);
	}

	public SpeechTranscription Transcribe(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		if (samples.IsEmpty || sampleRate <= 0)
		{
			return new(
				"",
				ParakeetSpeechToTextModule.Provider,
				false,
				"Parakeet received no speech samples");
		}

		using OfflineStream stream = _recognizer.CreateStream();
		stream.AcceptWaveform(sampleRate, samples.ToArray());
		_recognizer.Decode(stream);
		string text = stream.Result.Text.Trim();
		return new(
			text,
			ParakeetSpeechToTextModule.Provider,
			!string.IsNullOrWhiteSpace(text),
			string.IsNullOrWhiteSpace(text)
				? "Parakeet returned no speech"
				: "Transcribed");
	}

	public void Dispose() => _recognizer.Dispose();
}
