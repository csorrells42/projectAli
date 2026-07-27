using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Audio.WakeWord;
using AvatarBuilder.Modules.Audio.VoiceActivity;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Attention;
using AvatarBuilder.Modules.Vision.TargetSelection;
using System.Diagnostics;

namespace AvatarBuilder.Modules.Security;

public static class AliSecurityModuleSelfTest
{
	public static string Run()
	{
		var pttGrant = new PushToTalkUtteranceGrant();
		pttGrant.Update(true, true, 1_000);
		pttGrant.Update(true, false, 2_000);
		Require(pttGrant.Overlaps(1_500, 2_500),
			"A released PTT interval must remain attached to its overlapping utterance.");
		Require(!pttGrant.Overlaps(2_001, 3_000),
			"A PTT interval must never authorize later unrelated speech.");
		RunDelayedWakeOwnershipJoin();

		SpeakerRecognitionEvidence unknown =
			new(false, "", 0, "unknown");
		WakeWordEvidence noWake =
			new(false, "Ali", 0, "none");
		SecurityDecision enrollment = AliSecurityPolicy.Evaluate(
			new(true, true),
			new(true, true, "person-1", "Chris Sorrell"),
			new(true, true, "person-1", "Chris Sorrell", .82),
			true,
			new(false, "", 0, "enrollment", true),
			new(true, "Ali", .95, "wake"));
		Require(!enrollment.AllowSpeechToText,
			"Enrollment speech must remain outside normal speech-to-text.");

		SecurityDecision ptt = AliSecurityPolicy.Evaluate(
			new(true, true),
			LoginEvidence.Unavailable,
			TargetAuthorizationEvidence.None,
			false,
			unknown,
			noWake);
		Require(ptt.AllowSpeechToText
			&& !ptt.IdentityKnown
			&& ptt.Route == "Attention"
			&& ptt.AttentionSource == AttentionGrantSource.PushToTalk,
			"PTT must open attention for an explicitly unknown participant.");

		SecurityDecision voiceOnly = AliSecurityPolicy.Evaluate(
			new(false, false),
			LoginEvidence.Unavailable,
			TargetAuthorizationEvidence.None,
			false,
			new(true, "person-1", .95, "known"),
			noWake);
		Require(!voiceOnly.AllowSpeechToText,
			"Voice identity by itself must not create attention.");

		SecurityDecision loginOnly = AliSecurityPolicy.Evaluate(
			new(false, false),
			new(true, true, "person-1", "Chris Sorrell"),
			TargetAuthorizationEvidence.None,
			false,
			unknown,
			noWake);
		Require(!loginOnly.AllowSpeechToText,
			"Login context by itself must not create attention.");

		SecurityDecision wakeUnknown = AliSecurityPolicy.Evaluate(
			new(false, false),
			LoginEvidence.Unavailable,
			TargetAuthorizationEvidence.None,
			false,
			unknown,
			new(true, "Ali", .95, "wake"));
		Require(wakeUnknown.AllowSpeechToText
			&& wakeUnknown.AttentionSource == AttentionGrantSource.WakeWord
			&& !wakeUnknown.IdentityKnown,
			"Wake phrase must open attention even when the speaker is unknown.");

		SecurityDecision attentive = AliSecurityPolicy.Evaluate(
			new(false, false),
			LoginEvidence.Unavailable,
			new(true, true, "person-1", "Chris Sorrell", .82),
			true,
			unknown,
			noWake);
		Require(attentive.AllowSpeechToText
			&& attentive.ParticipantDisplayName == "Chris Sorrell"
			&& attentive.VisualIdentityConfidence == .82
			&& attentive.VoiceIdentityConfidence == 0d
			&& attentive.AttentionSource == AttentionGrantSource.Visual,
			"Visual attention must pass its independent visual identity evidence.");

		SecurityDecision mismatch = AliSecurityPolicy.Evaluate(
			new(false, false),
			LoginEvidence.Unavailable,
			new(true, true, "person-1", "Chris Sorrell", .81),
			true,
			new(true, "person-2", .74, "known"),
			noWake);
		Require(mismatch.AllowSpeechToText
			&& mismatch.VisualPersonIdentityId == "person-1"
			&& mismatch.VoicePersonIdentityId == "person-2"
			&& mismatch.VisualIdentityConfidence == .81
			&& mismatch.VoiceIdentityConfidence == .74
			&& !mismatch.IdentitySignalsAgree,
			"Identity disagreement must be preserved, not blended or blocked.");

		SecurityDecision agreement = AliSecurityPolicy.Evaluate(
			new(false, false),
			LoginEvidence.Unavailable,
			new(true, true, "person-1", "Chris Sorrell", .81),
			true,
			new(true, "person-1", .74, "known"),
			new(true, "Ali", .93, "wake"));
		Require(agreement.AllowSpeechToText
			&& agreement.IdentitySignalsAgree
			&& agreement.AttentionSource ==
				(AttentionGrantSource.Visual | AttentionGrantSource.WakeWord),
			"Matching identity evidence and every attention route must remain explicit.");

		return "PASS: one attention gate, sequence-safe released PTT overlap, enrollment isolation, unknown PTT/wake speech, independent visual and voice confidence, and identity agreement rules passed.";
	}

	private static void RunDelayedWakeOwnershipJoin()
	{
		using var targets =
			new ModuleOutputBroadcaster<TargetSelectionOutput>();
		using var attention =
			new ModuleOutputBroadcaster<AttentionOutput>();
		using var speakers =
			new ModuleOutputBroadcaster<SpeakerRecognitionOutput>();
		using var wakes =
			new ModuleOutputBroadcaster<WakeWordOutput>();
		using var security = new AliSecurityModule(
			targets, attention, speakers, wakes);
		using IModuleOutputSubscription<AuthorizedInteractionOutput> result =
			security.Subscribe();
		using var cursor = new SnapshotCursor<AuthorizedInteractionOutput>();
		security.Start();
		security.UpdatePushToTalk(true, true);
		Thread.Sleep(5);
		var utterance = new UtteranceOutput(
			77,
			Stopwatch.GetTimestamp(),
			DateTime.UtcNow,
			16000,
			new float[1600]);
		var speaker = new SpeakerRecognitionOutput(
			utterance,
			new SpeakerRecognitionEvidence(false, "", 0d, "unknown"));
		utterance.Dispose();
		security.UpdatePushToTalk(true, false);
		speakers.Publish(speaker);
		Thread.Sleep(50);
		wakes.Publish(new WakeWordOutput(
			77,
			new WakeWordEvidence(false, "Ali", 0d, "no wake")));
		Require(result.OutputAvailable.WaitOne(TimeSpan.FromSeconds(2)),
			"Security did not publish after delayed wake evidence arrived.");
		Require(result.TryTake(cursor)
			&& cursor.Current.SequenceId == 77
			&& cursor.Current.Decision.AllowSpeechToText
			&& cursor.Current.Decision.AttentionSource.HasFlag(
				AttentionGrantSource.PushToTalk)
			&& string.IsNullOrWhiteSpace(security.LastFailure),
			"Delayed wake evidence lost the speaker-owned utterance or its PTT grant.");
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}
}
