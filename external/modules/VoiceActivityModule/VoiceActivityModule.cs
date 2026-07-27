using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AvatarBuilder.Modules.Audio.Microphone;
using AvatarBuilder.Modules.Pipeline;
using SherpaOnnx;

namespace AvatarBuilder.Modules.Audio.VoiceActivity;

/// <summary>
/// Bounded utterance buffer and VAD boundary module. It stores only the active
/// utterance plus a short pre-roll and never queues future microphone blocks.
/// </summary>
public sealed class VoiceActivityModule :
	LatestValueAudioModule<MicrophoneOutput, UtteranceOutput>
{
	private const float SpeechRmsThreshold = 0.012f;
	private const int MaximumSeconds = 30;
	private const int PreRollMilliseconds = 300;
	private const int EndSilenceMilliseconds = 500;
	private const int MinimumSpeechMilliseconds = 250;

	private readonly Queue<float[]> _preRoll = new();
	private readonly List<float> _utterance = [];
	private int _preRollSamples;
	private int _silenceSamples;
	private int _speechSamples;
	private int _sampleRate = 16000;
	private long _utteranceSequence;
	private readonly VoiceActivityDetector? _nativeVad;

	public VoiceActivityModule(
		IModuleOutputSource<MicrophoneOutput> microphone)
		: base(microphone, "Voice activity and bounded utterance worker")
	{
		string? model =
			Environment.GetEnvironmentVariable("ALI_SHERPA_VAD_MODEL");
		if (!string.IsNullOrWhiteSpace(model) && File.Exists(model))
		{
			_nativeVad = new VoiceActivityDetector(
				new VadModelConfig
				{
					SileroVad = new SileroVadModelConfig
					{
						Model = model,
						Threshold = 0.35f,
						MinSilenceDuration = 0.5f,
						MinSpeechDuration = 0.25f,
						MaxSpeechDuration = MaximumSeconds,
						WindowSize = 512
					},
					SampleRate = 16000,
					NumThreads = 1,
					Provider = "cpu",
					Debug = 0
				},
				MaximumSeconds);
		}
	}

	protected override UtteranceOutput? Process(MicrophoneOutput input)
	{
		if (_nativeVad is not null)
		{
			_nativeVad.AcceptWaveform(input.Samples.ToArray());
			if (_nativeVad.IsEmpty())
			{
				return null;
			}
			SpeechSegment segment = _nativeVad.Front();
			_nativeVad.Pop();
			return new UtteranceOutput(
				++_utteranceSequence,
				Stopwatch.GetTimestamp(),
				DateTime.UtcNow,
				input.SampleRate,
				segment.Samples);
		}
		_sampleRate = input.SampleRate;
		float[] block = input.Samples.ToArray();
		bool speech = CalculateRms(block) >= SpeechRmsThreshold;
		if (_utterance.Count == 0)
		{
			AddPreRoll(block);
			if (!speech)
			{
				return null;
			}
			foreach (float[] prior in _preRoll)
			{
				_utterance.AddRange(prior);
			}
			_preRoll.Clear();
			_preRollSamples = 0;
		}
		else
		{
			_utterance.AddRange(block);
		}

		if (speech)
		{
			_speechSamples += block.Length;
			_silenceSamples = 0;
		}
		else
		{
			_silenceSamples += block.Length;
		}

		bool maximumReached =
			_utterance.Count >= _sampleRate * MaximumSeconds;
		bool silenceReached =
			_silenceSamples
				>= _sampleRate * EndSilenceMilliseconds / 1000;
		if (!maximumReached && !silenceReached)
		{
			return null;
		}

		float[] completed = _utterance.ToArray();
		bool longEnough =
			_speechSamples
				>= _sampleRate * MinimumSpeechMilliseconds / 1000;
		_utterance.Clear();
		_silenceSamples = 0;
		_speechSamples = 0;
		if (!longEnough)
		{
			return null;
		}
		return new UtteranceOutput(
			++_utteranceSequence,
			Stopwatch.GetTimestamp(),
			DateTime.UtcNow,
			_sampleRate,
			completed);
	}

	protected override void DisposeModule()
	{
		_nativeVad?.Dispose();
	}

	private void AddPreRoll(float[] block)
	{
		_preRoll.Enqueue(block);
		_preRollSamples += block.Length;
		int maximum =
			_sampleRate * PreRollMilliseconds / 1000;
		while (_preRollSamples > maximum && _preRoll.Count > 0)
		{
			_preRollSamples -= _preRoll.Dequeue().Length;
		}
	}

	private static float CalculateRms(float[] samples)
	{
		if (samples.Length == 0)
		{
			return 0;
		}
		double sum = samples.Sum(value => value * value);
		return (float)Math.Sqrt(sum / samples.Length);
	}
}
