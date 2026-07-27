using System.Diagnostics;
using AvatarBuilder.Modules.Audio.VoiceActivity;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Audio.SpeakerRecognition;

public static class SpeakerEnrollmentWorkflowSelfTest
{
	private static readonly string[] Phrases =
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

	public static string Run()
	{
		using var utterances =
			new ModuleOutputBroadcaster<UtteranceOutput>();
		var backend = new FakeEnrollmentBackend();
		using var transcriber = new FakeEnrollmentTranscriber(
			["Ali please remember", .. Phrases]);
		using var speaker = new SpeakerRecognitionModule(
			utterances,
			backend: backend,
			enrollmentTranscriber: transcriber);
		using IModuleOutputSubscription<SpeakerRecognitionOutput> output =
			speaker.Subscribe();
		using var cursor = new SnapshotCursor<SpeakerRecognitionOutput>();
		speaker.Start();
		SpeakerEnrollmentResult started = speaker.BeginSpeakerEnrollment(
			"speaker-enrollment-test",
			"Enrollment Test");
		if (!started.Success)
		{
			throw new InvalidOperationException(started.Status);
		}

		PublishAndWait(utterances, output, cursor, 1);
		SpeakerEnrollmentState partial =
			speaker.GetSpeakerEnrollmentState();
		if (!partial.IsActive || partial.CapturedSampleCount != 0)
		{
			throw new InvalidOperationException(
				"A partial sentence advanced voice enrollment.");
		}

		for (int index = 0; index < Phrases.Length; index++)
		{
			PublishAndWait(
				utterances,
				output,
				cursor,
				index + 2);
		}
		SpeakerEnrollmentState completed =
			speaker.GetSpeakerEnrollmentState();
		if (completed.Outcome != SpeakerEnrollmentOutcome.Accepted
			|| completed.IsActive
			|| !backend.Saved
			|| !cursor.Current.Evidence.IsEnrollmentUtterance)
		{
			throw new InvalidOperationException(
				"Eight verified sentences did not produce an accepted, security-marked enrollment.");
		}
		return "PASS: partial speech was rejected; eight complete sentences were accepted, saved, and security-marked.";
	}

	private static void PublishAndWait(
		ModuleOutputBroadcaster<UtteranceOutput> utterances,
		IModuleOutputSubscription<SpeakerRecognitionOutput> output,
		SnapshotCursor<SpeakerRecognitionOutput> cursor,
		long sequence)
	{
		cursor.Release();
		utterances.Publish(new UtteranceOutput(
			sequence,
			Stopwatch.GetTimestamp(),
			DateTime.UtcNow,
			16000,
			[0.1f]));
		if (!output.OutputAvailable.WaitOne(TimeSpan.FromSeconds(3))
			|| !output.TryTake(cursor))
		{
			throw new InvalidOperationException(
				"Speaker enrollment worker produced no result.");
		}
	}

	private sealed class FakeEnrollmentTranscriber(
		IEnumerable<string> transcripts) : IEnrollmentTranscriber
	{
		private readonly Queue<string> _transcripts = new(transcripts);

		public string ProviderName => "Enrollment verifier test";
		public bool IsConfigured => true;

		public EnrollmentTranscription Transcribe(
			ReadOnlySpan<float> samples,
			int sampleRate)
		{
			string text = _transcripts.Dequeue();
			return new(text, ProviderName, true, "Transcribed");
		}

		public void Dispose()
		{
		}
	}

	private sealed class FakeEnrollmentBackend :
		ISpeakerRecognitionBackend,
		ISpeakerEnrollmentBackend
	{
		public bool IsAvailable => true;
		public string AvailabilityStatus => "Test speaker backend ready";
		public bool Saved { get; private set; }

		public SpeakerRecognitionEvidence Recognize(
			ReadOnlySpan<float> samples,
			int sampleRate) => new(false, "", 0, "Unknown");

		public bool TryComputeEmbedding(
			ReadOnlySpan<float> samples,
			int sampleRate,
			out float[] embedding,
			out string status)
		{
			embedding = [1f];
			status = "Voice embedding captured.";
			return true;
		}

		public bool SaveEnrollment(
			string personIdentityId,
			IReadOnlyList<float[]> embeddings,
			out string status)
		{
			Saved = embeddings.Count == Phrases.Length;
			status = Saved ? "Saved" : "Wrong sample count";
			return Saved;
		}

		public bool DeleteEnrollment(
			string personIdentityId,
			out string status)
		{
			status = "Deleted";
			return true;
		}

		public void Dispose()
		{
		}
	}
}
