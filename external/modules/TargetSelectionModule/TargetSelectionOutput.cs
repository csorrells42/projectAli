using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Identity;

namespace AvatarBuilder.Modules.Vision.TargetSelection;

public sealed class TargetSelectionOutput :
	ModuleOutput,
	IModuleSnapshot
{
	public long SequenceId { get; }
	public long ProducedAtTimestamp { get; }
	public DateTime ProducedAtUtc { get; }
	public bool HasTarget { get; }
	public string CurrentTargetUserId { get; }
	public string CurrentTargetUsername { get; }
	public double LockQuality { get; }
	public string PersonIdentityId => CurrentTargetUserId;
	public string DisplayName { get; }
	public bool IsAuthorized { get; }
	public PersonFaceBox FaceRegion { get; }
	public bool SpeakerCorroborated { get; }
	public bool HasIdentityLock { get; }
	public bool HasMediaPipeLock { get; }
	public bool IsInGracePeriod { get; }
	public PersonIdentityEvidenceState IdentityEvidenceState { get; }
	public double IdentityConfidence { get; }
	public bool SearchRequested { get; }
	public string SearchUserId { get; }
	public PersonFaceBox SearchFaceRegion { get; }
	public double SearchConfidence { get; }
	public long MediaPipeTrackGeneration { get; }
	public string Status { get; }

	internal TargetSelectionOutput(
		long sequenceId,
		bool hasTarget,
		string currentTargetUserId,
		string currentTargetUsername,
		double lockQuality,
		string displayName,
		bool isAuthorized,
		PersonFaceBox faceRegion,
		bool speakerCorroborated,
		bool hasIdentityLock,
		bool hasMediaPipeLock,
		bool isInGracePeriod,
		PersonIdentityEvidenceState identityEvidenceState,
		double identityConfidence,
		bool searchRequested,
		string searchUserId,
		PersonFaceBox searchFaceRegion,
		double searchConfidence,
		long mediaPipeTrackGeneration,
		string status)
	{
		SequenceId = sequenceId;
		ProducedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		ProducedAtUtc = DateTime.UtcNow;
		HasTarget = hasTarget;
		CurrentTargetUserId = currentTargetUserId;
		CurrentTargetUsername = currentTargetUsername;
		LockQuality = Math.Clamp(lockQuality, 0d, 1d);
		DisplayName = displayName;
		IsAuthorized = isAuthorized;
		FaceRegion = faceRegion;
		SpeakerCorroborated = speakerCorroborated;
		HasIdentityLock = hasIdentityLock;
		HasMediaPipeLock = hasMediaPipeLock;
		IsInGracePeriod = isInGracePeriod;
		IdentityEvidenceState = identityEvidenceState;
		IdentityConfidence = Math.Clamp(identityConfidence, 0d, 1d);
		SearchRequested = searchRequested;
		SearchUserId = searchUserId;
		SearchFaceRegion = searchFaceRegion;
		SearchConfidence = Math.Clamp(searchConfidence, 0d, 1d);
		MediaPipeTrackGeneration = mediaPipeTrackGeneration;
		Status = status;
	}

	protected override void DisposeOwnedResources()
	{
	}
}
