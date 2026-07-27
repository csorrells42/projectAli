using System.Diagnostics;
using AvatarBuilder.Modules.Audio.VoiceActivity;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public sealed class SpeakerRecognitionModule :
	LatestValueAudioModule<UtteranceOutput, SpeakerRecognitionOutput>,
	ISpeakerEnrollmentService
{
	private static readonly TimeSpan EnrollmentTranscriptionTimeout =
		TimeSpan.FromSeconds(15);

	private const int RequiredEnrollmentSamples = 8;
	private static readonly string[] EnrollmentPhrases =
	[
		"Ali, please remember my voice and recognize me when I speak.",
		"Today is a good day to build something useful together.",
		"The quick brown fox jumps over the lazy dog near the river.",
		"Please turn on the kitchen light and tell me what time it is.",
		"When the weather changes, I usually check the windows and doors.",
		"My voice may sound different when I am tired, excited, or speaking softly.",
		"Seven blue airplanes crossed the quiet morning sky before breakfast.",
		"This final sentence completes my secure voice recognition enrollment."
	];

	private readonly Lazy<ISpeakerRecognitionBackend> _backend;
	private readonly object _backendGate = new();
	private readonly bool _backendConfigured;
	private readonly string _backendConfigurationStatus;
	private readonly object _enrollmentGate = new();
	private readonly List<float[]> _enrollmentEmbeddings = [];
	private IEnrollmentTranscriber? _enrollmentTranscriber;
	private string _enrollmentIdentityId = "";
	private string _enrollmentDisplayName = "";
	private bool _enrollmentActive;
	private string _enrollmentStatus;
	private SpeakerEnrollmentOutcome _enrollmentOutcome;

	public SpeakerRecognitionModule(
		IModuleOutputSource<UtteranceOutput> utterances,
		string? enrollmentFolder = null,
		ISpeakerRecognitionBackend? backend = null,
		IEnrollmentTranscriber? enrollmentTranscriber = null)
		: base(utterances, "Speaker recognition worker")
	{
		_enrollmentTranscriber = enrollmentTranscriber;
		if (backend is not null)
		{
			_backendConfigured = backend.IsAvailable;
			_backendConfigurationStatus = backend.AvailabilityStatus;
			_backend = new Lazy<ISpeakerRecognitionBackend>(() => backend);
		}
		else
		{
			SpeakerRecognitionModelInfo model =
				SpeakerRecognitionModelInfo.Load();
			_backendConfigured = model.IsReady;
			_backendConfigurationStatus = model.Status;
			_backend = new Lazy<ISpeakerRecognitionBackend>(
				() => SherpaSpeakerRecognitionBackend.Create(
					model.ModelPath,
					enrollmentFolder),
				LazyThreadSafetyMode.ExecutionAndPublication);
		}
		_enrollmentStatus = _backendConfigurationStatus;
	}

	protected override SpeakerRecognitionOutput Process(UtteranceOutput input)
	{
		if (TryCaptureEnrollment(input, out SpeakerRecognitionEvidence enrollmentEvidence))
		{
			return new SpeakerRecognitionOutput(input, enrollmentEvidence);
		}
		SpeakerRecognitionEvidence evidence;
		lock (_backendGate)
		{
			evidence = Backend.Recognize(
				input.Samples.Span,
				input.SampleRate);
		}
		return new SpeakerRecognitionOutput(input, evidence);
	}

	public SpeakerEnrollmentState GetSpeakerEnrollmentState()
	{
		lock (_enrollmentGate)
		{
			return CreateEnrollmentState();
		}
	}

	public void SelectEnrollmentTranscriber(
		IEnrollmentTranscriber transcriber)
	{
		ArgumentNullException.ThrowIfNull(transcriber);
		Interlocked.Exchange(
			ref _enrollmentTranscriber,
			transcriber);
	}

	public SpeakerEnrollmentResult BeginSpeakerEnrollment(
		string personIdentityId,
		string displayName)
	{
		if (!_backendConfigured)
		{
			SetEnrollmentOutcome(
				SpeakerEnrollmentOutcome.Rejected,
				_backendConfigurationStatus);
			return new(false, _backendConfigurationStatus);
		}
		IEnrollmentTranscriber? transcriber = Volatile.Read(
			ref _enrollmentTranscriber);
		if (transcriber is null || !transcriber.IsConfigured)
		{
			string status = transcriber is null
				? "Voice enrollment transcription is not configured."
				: transcriber.ProviderName
					+ " is not configured for enrollment verification.";
			SetEnrollmentOutcome(
				SpeakerEnrollmentOutcome.Rejected,
				status);
			return new(false, status);
		}
		if (string.IsNullOrWhiteSpace(personIdentityId))
		{
			const string status = "A registered UserId is required.";
			SetEnrollmentOutcome(
				SpeakerEnrollmentOutcome.Rejected,
				status);
			return new(false, status);
		}

		lock (_enrollmentGate)
		{
			_enrollmentEmbeddings.Clear();
			_enrollmentIdentityId = personIdentityId.Trim();
			_enrollmentDisplayName = string.IsNullOrWhiteSpace(displayName)
				? "this user"
				: displayName.Trim();
			_enrollmentActive = true;
			_enrollmentOutcome = SpeakerEnrollmentOutcome.Capturing;
			_enrollmentStatus = PromptForSample(0);
			return new(true, _enrollmentStatus);
		}
	}

	public void CancelSpeakerEnrollment()
	{
		lock (_enrollmentGate)
		{
			_enrollmentActive = false;
			_enrollmentEmbeddings.Clear();
			_enrollmentOutcome = SpeakerEnrollmentOutcome.Canceled;
			_enrollmentStatus = "Voice enrollment canceled.";
		}
	}

	public SpeakerEnrollmentResult DeleteSpeakerEnrollment(
		string personIdentityId)
	{
		if (!_backendConfigured)
		{
			return new(false, _backendConfigurationStatus);
		}
		bool success;
		string status;
		lock (_backendGate)
		{
			if (Backend is not ISpeakerEnrollmentBackend enrollment)
			{
				return new(false, Backend.AvailabilityStatus);
			}
			success = enrollment.DeleteEnrollment(
				personIdentityId,
				out status);
		}
		return new(success, status);
	}

	private bool TryCaptureEnrollment(
		UtteranceOutput input,
		out SpeakerRecognitionEvidence evidence)
	{
		evidence = EnrollmentEvidence(
			"Voice enrollment is not active");
		string identityId;
		string expectedPhrase;
		lock (_enrollmentGate)
		{
			if (!_enrollmentActive)
			{
				return false;
			}
			identityId = _enrollmentIdentityId;
			expectedPhrase = EnrollmentPhrases[Math.Min(
				_enrollmentEmbeddings.Count,
				EnrollmentPhrases.Length - 1)];
		}

		IEnrollmentTranscriber? transcriber = Volatile.Read(
			ref _enrollmentTranscriber);
		if (transcriber is null || !transcriber.IsConfigured)
		{
			const string unavailable =
				"Enrollment transcription became unavailable.";
			SetEnrollmentOutcome(
				SpeakerEnrollmentOutcome.Rejected,
				unavailable);
			evidence = EnrollmentEvidence(unavailable);
			return true;
		}
		lock (_enrollmentGate)
		{
			_enrollmentStatus =
				"Verifying the sentence with " + transcriber.ProviderName + "...";
		}
		long transcriptionStarted = Stopwatch.GetTimestamp();
		EnrollmentTranscription transcription = transcriber.Transcribe(
			input.Samples.Span,
			input.SampleRate);
		TimeSpan transcriptionTime = Stopwatch.GetElapsedTime(
			transcriptionStarted);
		if (transcriptionTime > EnrollmentTranscriptionTimeout)
		{
			lock (_enrollmentGate)
			{
				_enrollmentStatus =
					$"Sentence not accepted because {transcription.Provider} "
					+ $"took {transcriptionTime.TotalSeconds:0.0} seconds. "
					+ "Please repeat the displayed sentence.";
			}
			evidence = EnrollmentEvidence(_enrollmentStatus);
			return true;
		}
		if (!transcription.Succeeded)
		{
			lock (_enrollmentGate)
			{
				_enrollmentStatus = transcription.Provider
					+ " could not verify that sentence: "
					+ transcription.Status;
			}
			evidence = EnrollmentEvidence(_enrollmentStatus);
			return true;
		}
		if (!EnrollmentTranscriptMatcher.IsComplete(
			expectedPhrase,
			transcription.Text,
			out double transcriptSimilarity))
		{
			lock (_enrollmentGate)
			{
				_enrollmentStatus =
					$"Sentence not accepted ({transcriptSimilarity:P0} match). "
					+ $"{transcription.Provider} heard: \"{transcription.Text}\". "
					+ "Please repeat the displayed sentence.";
			}
			evidence = EnrollmentEvidence(_enrollmentStatus);
			return true;
		}

		ISpeakerRecognitionBackend activeBackend = Backend;
		if (activeBackend is not ISpeakerEnrollmentBackend enrollment)
		{
			lock (_enrollmentGate)
			{
				_enrollmentActive = false;
				_enrollmentOutcome = SpeakerEnrollmentOutcome.Rejected;
				_enrollmentStatus = activeBackend.AvailabilityStatus;
			}
			evidence = EnrollmentEvidence(activeBackend.AvailabilityStatus);
			return true;
		}
		float[] embedding;
		string status;
		bool captured;
		lock (_backendGate)
		{
			captured = enrollment.TryComputeEmbedding(
				input.Samples.Span,
				input.SampleRate,
				out embedding,
				out status);
		}
		if (!captured)
		{
			lock (_enrollmentGate)
			{
				_enrollmentStatus = status;
			}
			evidence = EnrollmentEvidence(status);
			return true;
		}

		lock (_enrollmentGate)
		{
			if (!_enrollmentActive
				|| !string.Equals(
					identityId,
					_enrollmentIdentityId,
					StringComparison.Ordinal))
			{
				evidence = EnrollmentEvidence(
					"Voice enrollment changed before this sample completed");
				return true;
			}
			_enrollmentEmbeddings.Add(embedding);
			if (_enrollmentEmbeddings.Count < RequiredEnrollmentSamples)
			{
				_enrollmentStatus =
					$"Voice sample {_enrollmentEmbeddings.Count} of "
					+ $"{RequiredEnrollmentSamples} captured. "
					+ PromptForSample(_enrollmentEmbeddings.Count);
				evidence = EnrollmentEvidence(_enrollmentStatus);
				return true;
			}

			bool saved;
			lock (_backendGate)
			{
				saved = enrollment.SaveEnrollment(
					_enrollmentIdentityId,
					_enrollmentEmbeddings,
					out status);
			}
			_enrollmentActive = false;
			_enrollmentEmbeddings.Clear();
			_enrollmentOutcome = saved
				? SpeakerEnrollmentOutcome.Accepted
				: SpeakerEnrollmentOutcome.Rejected;
			_enrollmentStatus = saved
				? $"Voice enrollment accepted and saved for "
					+ $"{_enrollmentDisplayName} from "
					+ $"{RequiredEnrollmentSamples} samples."
				: "Voice enrollment was not accepted: " + status;
			// Enrollment utterances are biometric setup data. They are
			// explicitly marked so AliSecurity rejects them before policy.
			evidence = EnrollmentEvidence(_enrollmentStatus);
			return true;
		}
	}

	private static SpeakerRecognitionEvidence EnrollmentEvidence(
		string status) => new(false, "", 0, status, true);

	private SpeakerEnrollmentState CreateEnrollmentState()
	{
		int captured = _enrollmentEmbeddings.Count;
		string prompt = _enrollmentActive
			? PromptForSample(captured)
			: _enrollmentOutcome == SpeakerEnrollmentOutcome.Accepted
				? $"Voice enrollment accepted for {_enrollmentDisplayName}."
				: "Select a registered user, then capture eight verified sentences.";
		return new(
			_backendConfigured,
			_enrollmentActive,
			_enrollmentIdentityId,
			_enrollmentDisplayName,
			captured,
			RequiredEnrollmentSamples,
			prompt,
			_enrollmentStatus,
			_enrollmentOutcome);
	}

	private void SetEnrollmentOutcome(
		SpeakerEnrollmentOutcome outcome,
		string status)
	{
		lock (_enrollmentGate)
		{
			_enrollmentActive = false;
			_enrollmentEmbeddings.Clear();
			_enrollmentOutcome = outcome;
			_enrollmentStatus = status;
		}
	}

	private static string PromptForSample(int capturedSampleCount)
	{
		int index = Math.Clamp(
			capturedSampleCount,
			0,
			EnrollmentPhrases.Length - 1);
		return $"Please say: \"{EnrollmentPhrases[index]}\"";
	}

	protected override void DisposeModule()
	{
		Interlocked.Exchange(ref _enrollmentTranscriber, null);
		if (_backend.IsValueCreated)
		{
			_backend.Value.Dispose();
		}
	}

	private ISpeakerRecognitionBackend Backend => _backend.Value;
}
