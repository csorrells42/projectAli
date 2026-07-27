using System.Diagnostics;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Vision.Identity;

namespace AvatarBuilder.Modules.Vision.TargetSelection;

public sealed record TargetSelectionRetentionSelfTestResult(
	bool Succeeded,
	string Detail);

public static class TargetSelectionRetentionSelfTest
{
	public static TargetSelectionRetentionSelfTestResult Run()
	{
		long second = Stopwatch.Frequency;
		long start = 10L * second;
		var face = new PersonFaceBox(0.30d, 0.20d, 0.70d, 0.80d);
		var person = new PersonIdentityObservation(
			"track-1",
			"user-1",
			"Chris Sorrell",
			"chris",
			true,
			true,
			0.94d,
			face,
			PersonIdentityEvidenceState.ConfirmedRegisteredUser,
			0.94d);
		var state = new TargetLockState();

		state.ObserveIdentity(start, [person]);
		bool identityAloneCannotAcquire =
			!state.Evaluate(start).HasTarget;
		state.ObserveMediaPipe(
			start + second / 100,
			true,
			face);
		TargetLockView acquired =
			state.Evaluate(start + second / 100);
		bool correlatedAcquisition =
			acquired.HasTarget
			&& acquired.HasIdentityLock
			&& acquired.HasMediaPipeLock
			&& acquired.UserId == "user-1"
			&& acquired.Username == "chris"
			&& acquired.LockQuality >= 0.70d;

		TargetLockView beforeSpeaker =
			state.Evaluate(start + second / 50);
		state.ObserveSpeaker(
			start + second / 50,
			new SpeakerRecognitionEvidence(
				true,
				"user-1",
				1d,
				"speaker match"));
		TargetLockView spoken =
			state.Evaluate(start + second / 50);
		bool speakerIsSeparateCorroboration =
			spoken.SpeakerCorroborated
			&& Math.Abs(spoken.LockQuality - beforeSpeaker.LockQuality)
				< 0.000001d;

		state.ObserveIdentity(
			start + second / 10,
			Array.Empty<PersonIdentityObservation>());
		TargetLockView identityFlicker =
			state.Evaluate(start + second / 10);
		bool mediaPipeRetainsIdentity =
			identityFlicker.HasTarget
			&& !identityFlicker.HasIdentityLock
			&& identityFlicker.HasMediaPipeLock;

		state.ObserveMediaPipe(
			start + second / 5,
			false,
			default);
		TargetLockView grace =
			state.Evaluate(start + second / 5);
		bool dualLossEntersGrace =
			grace.HasTarget && grace.IsInGracePeriod;
		state.ObserveMediaPipe(
			start + second,
			true,
			new PersonFaceBox(0.32d, 0.21d, 0.72d, 0.81d));
		TargetLockView reacquired =
			state.Evaluate(start + second);
		bool spatialReacquisitionRetainsIdentity =
			reacquired.HasTarget
			&& reacquired.HasMediaPipeLock
			&& reacquired.UserId == "user-1";

		state.ObserveMediaPipe(
			start + second + second / 10,
			false,
			default);
		TargetLockView expired = state.Evaluate(
			start + second + second / 10
				+ (long)(TargetLockState.GracePeriod.TotalSeconds * second)
				+ 1);
		bool graceExpires = !expired.HasTarget;

		var voiceOnly = new TargetLockState();
		voiceOnly.ObserveSpeaker(
			start,
			new SpeakerRecognitionEvidence(
				true,
				"user-1",
				1d,
				"speaker match"));
		bool voiceAloneCannotAcquire =
			!voiceOnly.Evaluate(start).HasTarget;

		var replacement = new TargetLockState();
		replacement.ObserveIdentity(start, [person]);
		replacement.ObserveMediaPipe(start + second / 100, true, face);
		var unknownFace = new PersonIdentityObservation(
			"track-2",
			"",
			"",
			"",
			false,
			false,
			0d,
			face,
			PersonIdentityEvidenceState.UsableUnknown,
			0.96d);
		var ambiguousFace = unknownFace with
		{
			EvidenceState = PersonIdentityEvidenceState.Insufficient,
			EvidenceConfidence = 0d
		};
		var ambiguous = new TargetLockState();
		ambiguous.ObserveIdentity(start, [person]);
		ambiguous.ObserveMediaPipe(start + second / 100, true, face);
		ambiguous.ObserveIdentity(start + second / 10, [ambiguousFace]);
		ambiguous.ObserveIdentity(start + second / 2, [ambiguousFace]);
		bool insufficientEvidenceDoesNotContradict =
			ambiguous.Evaluate(start + second / 2).HasTarget;
		replacement.ObserveIdentity(start + second / 10, [unknownFace]);
		bool firstContradictionIsDebounced =
			replacement.Evaluate(start + second / 10).HasTarget;
		replacement.ObserveIdentity(
			start + second / 10
				+ (long)(TargetLockState.IdentityContradictionPeriod.TotalSeconds
					* second)
				+ 1,
			[unknownFace]);
		bool visibleReplacementClearsTarget =
			!replacement.Evaluate(
				start + second / 10
					+ (long)(TargetLockState.IdentityContradictionPeriod.TotalSeconds
						* second)
					+ 1).HasTarget;

		var search = new TargetLockState();
		search.ObserveIdentity(start, [person]);
		search.ObserveMediaPipe(start + second / 100, true, face);
		var relocatedPerson = person with
		{
			FaceBox = new PersonFaceBox(0.03d, 0.20d, 0.25d, 0.68d)
		};
		search.ObserveIdentity(
			start + second / 10,
			[unknownFace, relocatedPerson]);
		search.ObserveIdentity(
			start + second / 10
				+ (long)(TargetLockState.IdentityContradictionPeriod.TotalSeconds
					* second)
				+ 1,
			[unknownFace, relocatedPerson]);
		TargetLockView searching = search.Evaluate(
			start + second / 10
				+ (long)(TargetLockState.IdentityContradictionPeriod.TotalSeconds
					* second)
				+ 1);
		bool formerTargetProducesSearchHint =
			!searching.HasTarget
			&& searching.SearchRequested
			&& searching.SearchUserId == "user-1"
			&& searching.SearchFaceRegion.Equals(relocatedPerson.FaceBox);
		TargetLockView movedSearchHint = searching with
		{
			SearchFaceRegion = new PersonFaceBox(0.08d, 0.22d, 0.30d, 0.70d),
			SearchConfidence = Math.Max(0d, searching.SearchConfidence - 0.03d)
		};
		bool movingSearchHintRequiresPublication =
			TargetSelectionModule.SearchHintChanged(searching, movedSearchHint);
		search.ObserveMediaPipe(
			start + second / 2,
			true,
			relocatedPerson.FaceBox);
		bool searchHintCanReacquireFormerTarget =
			search.Evaluate(start + second / 2).UserId == "user-1";

		var staleIdentity = new TargetLockState();
		staleIdentity.ObserveIdentity(start, [person]);
		staleIdentity.ObserveMediaPipe(start + second / 100, true, face);
		staleIdentity.ObserveMediaPipe(start + second, true, face);
		bool mediaPipeCannotExtendIdentityForever =
			!staleIdentity.Evaluate(
				start
					+ (long)(TargetLockState.MaximumIdentityLease.TotalSeconds
						* second)
					+ 1).HasTarget;

		var nearbyFaces = new TargetLockState();
		var knownNearby = person with
		{
			FaceBox = new PersonFaceBox(0.25d, 0.20d, 0.58d, 0.75d)
		};
		var unknownNearby = unknownFace with
		{
			FaceBox = new PersonFaceBox(0.43d, 0.25d, 0.76d, 0.80d)
		};
		nearbyFaces.ObserveIdentity(start, [knownNearby, unknownNearby]);
		nearbyFaces.ObserveMediaPipe(
			start + second / 100,
			true,
			unknownNearby.FaceBox);
		bool nearestVisibleFaceOwnsCorrelation =
			!nearbyFaces.Evaluate(start + second / 100).HasTarget;

		var normalBounds = new TargetLockState();
		normalBounds.ObserveIdentity(start, [person]);
		normalBounds.ObserveMediaPipe(start + second / 100, true, face);
		normalBounds.ObserveIdentity(
			start + second / 10,
			Array.Empty<PersonIdentityObservation>());
		normalBounds.ObserveMediaPipe(start + second / 5, false, default);
		normalBounds.ObserveMediaPipe(
			start + second / 2,
			true,
			new PersonFaceBox(0.66d, 0.25d, 0.90d, 0.75d));
		bool expandedOnlyRegionCannotInheritLock =
			!normalBounds.Evaluate(start + second / 2).HasMediaPipeLock;

		bool passed =
			identityAloneCannotAcquire
			&& correlatedAcquisition
			&& speakerIsSeparateCorroboration
			&& mediaPipeRetainsIdentity
			&& dualLossEntersGrace
			&& spatialReacquisitionRetainsIdentity
			&& graceExpires
			&& voiceAloneCannotAcquire
			&& insufficientEvidenceDoesNotContradict
			&& firstContradictionIsDebounced
			&& visibleReplacementClearsTarget
			&& formerTargetProducesSearchHint
			&& movingSearchHintRequiresPublication
			&& searchHintCanReacquireFormerTarget
			&& mediaPipeCannotExtendIdentityForever
			&& nearestVisibleFaceOwnsCorrelation
			&& expandedOnlyRegionCannotInheritLock;
		return new TargetSelectionRetentionSelfTestResult(
			passed,
			passed
				? "PASS: visual agreement acquired one UserId; insufficient evidence preserved it; usable contradictory evidence cleared it; former-target identity evidence produced and republished a moving normal-bounds steering hint and reacquired it; nearby and expanded-only faces could not borrow a label; MediaPipe could not extend identity indefinitely; spatial grace expired; speaker corroboration remained separate from visual quality; voice alone never acquired a target."
				: "FAIL: target acquisition, retention, grace, separate speaker evidence, or voice-only rejection regressed.");
	}
}
