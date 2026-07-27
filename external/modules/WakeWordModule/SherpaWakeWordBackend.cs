using System.Threading;
using SherpaOnnx;

namespace AvatarBuilder.Modules.Audio.WakeWord;

/// <summary>
/// Native sherpa-onnx streaming keyword spotter. A fresh stream receives the
/// current dynamic phrase for each utterance, so changing the assistant name
/// does not require retraining.
/// </summary>
public sealed class SherpaWakeWordBackend : IWakeWordBackend
{
	private readonly KeywordSpotter _spotter;
	private readonly EnglishWakePhraseTokenizer _tokenizer;
	private string _assistantName;

	public string AssistantName => Volatile.Read(ref _assistantName);

	public static IWakeWordBackend CreateFromEnvironment(
		string assistantName)
	{
		WakeWordModelInfo model = WakeWordModelInfo.Load();
		if (!model.IsReady)
		{
			return new UnconfiguredWakeWordBackend(assistantName);
		}
		return new SherpaWakeWordBackend(
			assistantName,
			model);
	}

	public SherpaWakeWordBackend(
		string assistantName,
		WakeWordModelInfo model)
		: this(assistantName, model.EncoderPath, model.DecoderPath,
			model.JoinerPath, model.TokensPath,
			model.EnglishLexiconPath)
	{
	}

	public SherpaWakeWordBackend(
		string assistantName,
		string encoder,
		string decoder,
		string joiner,
		string tokens,
		string englishLexicon)
	{
		_assistantName = Normalize(assistantName);
		_tokenizer = new EnglishWakePhraseTokenizer(englishLexicon);
		if (!_tokenizer.TryBuild(
			_assistantName,
			out string initialKeyword,
			out string status))
		{
			throw new InvalidOperationException(status);
		}
		string initialKeywordBuffer = initialKeyword.Replace('/', '\n');
		_spotter = new KeywordSpotter(
			new KeywordSpotterConfig
			{
				FeatConfig = new FeatureConfig
				{
					SampleRate = 16000,
					FeatureDim = 80
				},
				ModelConfig = new OnlineModelConfig
				{
					Transducer = new OnlineTransducerModelConfig
					{
						Encoder = encoder,
						Decoder = decoder,
						Joiner = joiner
					},
					Tokens = tokens,
					NumThreads = 2,
					Provider = "cpu",
					Debug = 0
				},
				MaxActivePaths = 4,
				NumTrailingBlanks = 1,
				KeywordsScore = 1.0f,
				KeywordsThreshold = 0.25f,
				KeywordsBuf = initialKeywordBuffer,
				KeywordsBufSize = System.Text.Encoding.UTF8.GetByteCount(
					initialKeywordBuffer)
			});
	}

	public void SetAssistantName(string assistantName)
	{
		Volatile.Write(
			ref _assistantName,
			Normalize(assistantName));
	}

	public WakeWordEvidence Detect(
		ReadOnlySpan<float> samples,
		int sampleRate)
	{
		string name = AssistantName;
		if (!_tokenizer.TryBuild(name, out string phrase, out string status))
		{
			return new WakeWordEvidence(false, name, 0d, status);
		}
		using OnlineStream stream = _spotter.CreateStream(phrase);
		stream.AcceptWaveform(sampleRate, samples.ToArray());
		stream.AcceptWaveform(sampleRate,
			new float[(int)Math.Ceiling(sampleRate * 0.66d)]);
		stream.InputFinished();
		while (_spotter.IsReady(stream))
		{
			_spotter.Decode(stream);
		}
		string keyword = _spotter.GetResult(stream).Keyword;
		bool detected = !string.IsNullOrWhiteSpace(keyword);
		return new WakeWordEvidence(
			detected,
			name,
			detected ? 1d : 0d,
			detected
				? "Sherpa dynamic wake phrase detected"
				: "No dynamic wake phrase detected");
	}

	public void Dispose()
	{
		_spotter.Dispose();
	}

	private static string Normalize(string value) =>
		string.IsNullOrWhiteSpace(value) ? "Ali" : value.Trim();
}
