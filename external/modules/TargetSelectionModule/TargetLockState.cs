using System.Diagnostics;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Vision.Identity;

namespace AvatarBuilder.Modules.Vision.TargetSelection;

internal readonly record struct TargetLockView(
	bool HasTarget,
	string UserId,
	string Username,
	string DisplayName,
	bool IsAuthorized,
	PersonFaceBox FaceRegion,
	double LockQuality,
	bool SpeakerCorroborated,
	bool HasIdentityLock,
	bool HasMediaPipeLock,
	bool IsInGracePeriod,
	PersonIdentityEvidenceState IdentityEvidenceState,
	double IdentityConfidence,
	bool SearchRequested,
	string SearchUserId,
	PersonFaceBox SearchFaceRegion,
	double SearchConfidence,
	long MediaPipeTrackGeneration,
	string Status);

/// <summary>
/// Owns correlation and retention policy only. Inputs are immutable facts
/// published by independent modules; this state never calls either producer.
/// </summary>
internal sealed class TargetLockState
{
	internal static readonly TimeSpan GracePeriod =
		TimeSpan.FromMilliseconds(1500);
	internal static readonly TimeSpan IdentityContradictionPeriod =
		TimeSpan.FromMilliseconds(250);
	internal static readonly TimeSpan MaximumIdentityLease =
		TimeSpan.FromSeconds(2);
	internal static readonly TimeSpan TargetSearchPeriod =
		TimeSpan.FromSeconds(3);

	private static readonly long GraceTicks = ToTicks(GracePeriod);
	private static readonly long IdentityContradictionTicks =
		ToTicks(IdentityContradictionPeriod);
	private static readonly long MaximumIdentityLeaseTicks =
		ToTicks(MaximumIdentityLease);
	private static readonly long TargetSearchTicks =
		ToTicks(TargetSearchPeriod);
	private static readonly long IdentityFreshnessTicks =
		ToTicks(TimeSpan.FromSeconds(1));
	private static readonly long MediaPipeFreshnessTicks =
		ToTicks(TimeSpan.FromMilliseconds(300));
	private static readonly long CorrelationWindowTicks =
		ToTicks(TimeSpan.FromMilliseconds(750));
	private static readonly long SpeakerFreshnessTicks =
		ToTicks(TimeSpan.FromSeconds(3));

	private PersonIdentityObservation? _latestIdentityCandidate;
	private IReadOnlyList<PersonIdentityObservation> _latestIdentityPeople =
		Array.Empty<PersonIdentityObservation>();
	private long _latestIdentityTimestamp;
	private bool _latestMediaPipeHasFace;
	private PersonFaceBox _latestMediaPipeRegion;
	private long _latestMediaPipeTimestamp;

	private string _userId = "";
	private string _username = "";
	private string _displayName = "Unknown";
	private bool _isAuthorized;
	private PersonFaceBox _lastRegion;
	private PersonFaceBox _previousMediaPipeRegion;
	private bool _hasPreviousMediaPipeRegion;
	private bool _identityLocked;
	private bool _mediaPipeLocked;
	private bool _edgeExitArmed;
	private long _lastIdentityLockTimestamp;
	private long _lastIdentityUnlockTimestamp;
	private long _lastMediaPipeLockTimestamp;
	private long _lastMediaPipeUnlockTimestamp;
	private long _bothLostTimestamp;
	private long _identityContradictionTimestamp;
	private long _mediaPipeTrackGeneration;
	private PersonIdentityEvidenceState _identityEvidenceState;
	private double _identityConfidence;

	private string _searchUserId = "";
	private PersonFaceBox _searchFaceRegion;
	private double _searchConfidence;
	private long _searchStartedTimestamp;

	private bool _speakerKnown;
	private string _speakerUserId = "";
	private double _speakerSimilarity;
	private long _lastSpeakerTimestamp;

	internal bool HasTarget => !string.IsNullOrWhiteSpace(_userId);

	internal void ObserveIdentity(
		long timestamp,
		IReadOnlyList<PersonIdentityObservation> people)
	{
		_latestIdentityPeople = people.ToArray();
		PersonIdentityObservation? spatial = SelectIdentityObservation();
		PersonIdentityObservation? matching = HasTarget
			&& spatial?.EvidenceState
				== PersonIdentityEvidenceState.ConfirmedRegisteredUser
			&& string.Equals(
				spatial.IdentityId,
				_userId,
				StringComparison.OrdinalIgnoreCase)
				? spatial
				: null;
		PersonIdentityObservation? candidate =
			spatial?.EvidenceState
				== PersonIdentityEvidenceState.ConfirmedRegisteredUser
				? spatial
				: null;
		_latestIdentityCandidate = candidate;
		_latestIdentityTimestamp = timestamp;

		if (!HasTarget)
		{
			UpdateSearchEvidence(timestamp);
			TryAcquire(timestamp);
			return;
		}

		if (matching is not null)
		{
			bool wasBothLost = !_identityLocked && !_mediaPipeLocked;
			if (!wasBothLost
				|| IsInsideReacquisitionRegion(
					matching.FaceBox,
					_lastRegion))
			{
				_identityLocked = true;
				_lastIdentityLockTimestamp = timestamp;
				RefreshIdentity(matching);
				if (!_mediaPipeLocked)
				{
					_lastRegion = matching.FaceBox;
				}
				_bothLostTimestamp = 0;
				_identityContradictionTimestamp = 0;
				return;
			}
		}

		if (_identityLocked)
		{
			_identityLocked = false;
			_lastIdentityUnlockTimestamp = timestamp;
		}

		if (candidate is not null
			&& !string.Equals(
				candidate.IdentityId,
				_userId,
				StringComparison.OrdinalIgnoreCase)
			&& _latestMediaPipeHasFace
			&& CanCorrelate(
				timestamp,
				_latestMediaPipeTimestamp,
				candidate.FaceBox,
				_latestMediaPipeRegion))
		{
			BeginSearchForCurrentTarget(timestamp);
			ClearTarget(preserveSearch: true);
			_latestIdentityPeople = people.ToArray();
			UpdateSearchEvidence(timestamp);
			TryAcquire(timestamp);
			return;
		}

		bool visibleContradiction = spatial is not null
			&& (spatial.EvidenceState
					== PersonIdentityEvidenceState.UsableUnknown
				|| spatial.EvidenceState
					== PersonIdentityEvidenceState.ConfirmedRegisteredUser)
			&& matching is null
			&& IsInsideReacquisitionRegion(spatial.FaceBox, _lastRegion);
		if (visibleContradiction)
		{
			if (_identityContradictionTimestamp == 0)
			{
				_identityContradictionTimestamp = timestamp;
			}
			else if (Elapsed(timestamp, _identityContradictionTimestamp)
				>= IdentityContradictionTicks)
			{
				BeginSearchForCurrentTarget(timestamp);
				ClearTarget(preserveSearch: true);
				_latestIdentityPeople = people.ToArray();
				UpdateSearchEvidence(timestamp);
			}
		}
		else
		{
			_identityContradictionTimestamp = 0;
		}
	}

	internal void ObserveMediaPipe(
		long timestamp,
		bool hasFace,
		PersonFaceBox region)
	{
		_latestMediaPipeHasFace = hasFace && IsValid(region);
		_latestMediaPipeTimestamp = timestamp;
		_latestMediaPipeRegion = region;

		if (!HasTarget)
		{
			TryAcquire(timestamp);
			RememberMediaPipeRegion(hasFace, region);
			return;
		}

		if (!_latestMediaPipeHasFace)
		{
			if (_mediaPipeLocked)
			{
				_mediaPipeLocked = false;
				_lastMediaPipeUnlockTimestamp = timestamp;
			}
			if (_edgeExitArmed)
			{
				ClearTarget();
			}
			return;
		}

		bool wasBothLost = !_identityLocked && !_mediaPipeLocked;
		bool samePhysicalTarget = _mediaPipeLocked
			? IsContinuous(region, _lastRegion)
			: !wasBothLost
				|| IsInsideReacquisitionRegion(region, _lastRegion);
		if (samePhysicalTarget)
		{
			if (!_mediaPipeLocked)
			{
				_mediaPipeTrackGeneration++;
			}
			_mediaPipeLocked = true;
			_lastMediaPipeLockTimestamp = timestamp;
			_bothLostTimestamp = 0;
			_lastRegion = region;
		}
		else if (_mediaPipeLocked)
		{
			_mediaPipeLocked = false;
			_lastMediaPipeUnlockTimestamp = timestamp;
			_mediaPipeTrackGeneration++;
		}

		RememberMediaPipeRegion(hasFace, region);
	}

	internal void ObserveSpeaker(
		long timestamp,
		SpeakerRecognitionEvidence evidence)
	{
		_speakerKnown = evidence.IsKnown;
		_speakerUserId = evidence.PersonIdentityId ?? "";
		_speakerSimilarity = Math.Clamp(evidence.Similarity, 0d, 1d);
		_lastSpeakerTimestamp = timestamp;
	}

	internal TargetLockView Evaluate(long now)
	{
		ExpireStaleLocks(now);
		ExpireSearch(now);
		if (HasTarget
			&& _lastIdentityLockTimestamp != 0
			&& Elapsed(now, _lastIdentityLockTimestamp)
				>= MaximumIdentityLeaseTicks)
		{
			ClearTarget();
		}
		if (HasTarget && !_identityLocked && !_mediaPipeLocked)
		{
			if (_bothLostTimestamp == 0)
			{
				_bothLostTimestamp = Math.Max(
					_lastIdentityUnlockTimestamp,
					_lastMediaPipeUnlockTimestamp);
				if (_bothLostTimestamp == 0)
				{
					_bothLostTimestamp = now;
				}
			}
			if (Elapsed(now, _bothLostTimestamp) >= GraceTicks)
			{
				ClearTarget();
			}
		}

		if (!HasTarget)
		{
			return new TargetLockView(
				false,
				"",
				"",
				"Unknown",
				false,
				default,
				0d,
				false,
				false,
				false,
				false,
				PersonIdentityEvidenceState.Insufficient,
				0d,
				!string.IsNullOrWhiteSpace(_searchUserId)
					&& IsValid(_searchFaceRegion),
				_searchUserId,
				_searchFaceRegion,
				_searchConfidence,
				_mediaPipeTrackGeneration,
				string.IsNullOrWhiteSpace(_searchUserId)
					? "No confirmed target"
					: IsValid(_searchFaceRegion)
						? "Former target located; MediaPipe steering requested"
						: "Searching identity observations for former target");
		}

		bool speakerCorroborated =
			_speakerKnown
			&& string.Equals(
				_speakerUserId,
				_userId,
				StringComparison.OrdinalIgnoreCase)
			&& Elapsed(now, _lastSpeakerTimestamp)
				< SpeakerFreshnessTicks;
		double quality = CalculateVisualQuality(now);
		bool grace = !_identityLocked && !_mediaPipeLocked;
		string status = grace
			? "Target retained during visual grace period"
			: speakerCorroborated
				? "Visual target corroborated by speaker"
				: _identityLocked && _mediaPipeLocked
					? "Identity and MediaPipe locks agree"
					: _identityLocked
						? "Identity lock retained"
						: "MediaPipe lock retained";
		return new TargetLockView(
			true,
			_userId,
			_username,
			_displayName,
			_isAuthorized,
			_lastRegion,
			quality,
			speakerCorroborated,
			_identityLocked,
			_mediaPipeLocked,
			grace,
			_identityEvidenceState,
			_identityConfidence,
			false,
			"",
			default,
			0d,
			_mediaPipeTrackGeneration,
			status);
	}

	internal int GetWaitTimeoutMilliseconds(long now)
	{
		long deadline = long.MaxValue;
		if (HasTarget && _lastIdentityLockTimestamp != 0)
		{
			deadline = Math.Min(
				deadline,
				_lastIdentityLockTimestamp + MaximumIdentityLeaseTicks);
		}
		if (HasTarget && _identityContradictionTimestamp != 0)
		{
			deadline = Math.Min(
				deadline,
				_identityContradictionTimestamp
					+ IdentityContradictionTicks);
		}
		if (!string.IsNullOrWhiteSpace(_searchUserId)
			&& _searchStartedTimestamp != 0)
		{
			deadline = Math.Min(
				deadline,
				_searchStartedTimestamp + TargetSearchTicks);
		}
		if (_identityLocked)
		{
			deadline = Math.Min(
				deadline,
				_lastIdentityLockTimestamp + IdentityFreshnessTicks);
		}
		if (_mediaPipeLocked)
		{
			deadline = Math.Min(
				deadline,
				_lastMediaPipeLockTimestamp + MediaPipeFreshnessTicks);
		}
		if (HasTarget
			&& !_identityLocked
			&& !_mediaPipeLocked
			&& _bothLostTimestamp != 0)
		{
			deadline = Math.Min(
				deadline,
				_bothLostTimestamp + GraceTicks);
		}
		if (deadline == long.MaxValue)
		{
			return Timeout.Infinite;
		}
		long remaining = Math.Max(0, deadline - now);
		double milliseconds =
			remaining * 1000d / Stopwatch.Frequency;
		return Math.Max(1, (int)Math.Ceiling(milliseconds));
	}

	private void TryAcquire(long now)
	{
		PersonIdentityObservation? candidate =
			SelectSearchIdentityObservation()
			?? (string.IsNullOrWhiteSpace(_searchUserId)
				? SelectIdentityObservation()
				: null);
		_latestIdentityCandidate = candidate;
		if (HasTarget
			|| candidate is null
			|| !candidate.IsRemembered
			|| !_latestMediaPipeHasFace
			|| !CanCorrelate(
				_latestIdentityTimestamp,
				_latestMediaPipeTimestamp,
				candidate.FaceBox,
				_latestMediaPipeRegion))
		{
			return;
		}

		_userId = candidate.IdentityId;
		RefreshIdentity(candidate);
		ClearSearch();
		_identityLocked = true;
		_mediaPipeLocked = true;
		_lastIdentityLockTimestamp = _latestIdentityTimestamp;
		_lastMediaPipeLockTimestamp = _latestMediaPipeTimestamp;
		_lastIdentityUnlockTimestamp = 0;
		_lastMediaPipeUnlockTimestamp = 0;
		_bothLostTimestamp = 0;
		_identityContradictionTimestamp = 0;
		_lastRegion = _latestMediaPipeRegion;
		_mediaPipeTrackGeneration++;
		RememberMediaPipeRegion(true, _latestMediaPipeRegion);
	}

	private PersonIdentityObservation? SelectIdentityObservation()
	{
		if (_latestIdentityPeople.Count == 0)
		{
			return null;
		}
		if (_latestMediaPipeHasFace
			&& Math.Abs(_latestIdentityTimestamp - _latestMediaPipeTimestamp)
				<= CorrelationWindowTicks)
		{
			PersonIdentityObservation? closest = _latestIdentityPeople
				.Where(person => IsValid(person.FaceBox))
				.OrderByDescending(person => IntersectionOverUnion(
					person.FaceBox,
					_latestMediaPipeRegion))
				.ThenBy(person => CenterDistanceSquared(
					person.FaceBox,
					_latestMediaPipeRegion))
				.FirstOrDefault();
			return closest is not null
				&& CanCorrelate(
					_latestIdentityTimestamp,
					_latestMediaPipeTimestamp,
					closest.FaceBox,
					_latestMediaPipeRegion)
					? closest
					: null;
		}
		if (HasTarget)
		{
			return _latestIdentityPeople.FirstOrDefault(person =>
				person.IsRemembered
				&& string.Equals(
					person.IdentityId,
					_userId,
					StringComparison.OrdinalIgnoreCase));
		}
		return _latestIdentityPeople
			.Where(person => person.IsRemembered)
			.OrderByDescending(person => person.Similarity)
			.FirstOrDefault();
	}

	private void RefreshIdentity(PersonIdentityObservation observation)
	{
		_username = observation.Username?.Trim() ?? "";
		_displayName = string.IsNullOrWhiteSpace(observation.DisplayName)
			? _username
			: observation.DisplayName.Trim();
		if (string.IsNullOrWhiteSpace(_displayName))
		{
			_displayName = "Known Needs Name";
		}
		_isAuthorized = observation.IsRegisteredUser;
		_identityEvidenceState = observation.EvidenceState;
		_identityConfidence = Math.Clamp(
			observation.EvidenceConfidence > 0d
				? observation.EvidenceConfidence
				: observation.Similarity,
			0d,
			1d);
	}

	private void ExpireStaleLocks(long now)
	{
		if (_identityLocked
			&& Elapsed(now, _lastIdentityLockTimestamp)
				>= IdentityFreshnessTicks)
		{
			_identityLocked = false;
			_lastIdentityUnlockTimestamp =
				_lastIdentityLockTimestamp + IdentityFreshnessTicks;
		}
		if (_mediaPipeLocked
			&& Elapsed(now, _lastMediaPipeLockTimestamp)
				>= MediaPipeFreshnessTicks)
		{
			_mediaPipeLocked = false;
			_lastMediaPipeUnlockTimestamp =
				_lastMediaPipeLockTimestamp + MediaPipeFreshnessTicks;
		}
		if (_identityLocked || _mediaPipeLocked)
		{
			_bothLostTimestamp = 0;
		}
	}

	private double CalculateVisualQuality(long now)
	{
		double quality = 0d;
		if (_lastIdentityLockTimestamp != 0)
		{
			double visualFreshness = 1d - Math.Clamp(
				Elapsed(now, _lastIdentityLockTimestamp)
					/ (double)MaximumIdentityLeaseTicks,
				0d,
				1d);
			quality += 0.75d * _identityConfidence * visualFreshness;
		}
		if (_mediaPipeLocked)
		{
			quality += 0.25d;
		}
		return Math.Clamp(quality, 0d, 1d);
	}

	private void RememberMediaPipeRegion(
		bool hasFace,
		PersonFaceBox region)
	{
		if (!hasFace || !IsValid(region))
		{
			return;
		}
		_edgeExitArmed = TouchesEdge(region)
			&& (!_hasPreviousMediaPipeRegion
				|| IsMovingOutward(_previousMediaPipeRegion, region));
		_previousMediaPipeRegion = region;
		_hasPreviousMediaPipeRegion = true;
	}

	private void BeginSearchForCurrentTarget(long timestamp)
	{
		if (!HasTarget)
		{
			return;
		}
		_searchUserId = _userId;
		_searchFaceRegion = default;
		_searchConfidence = 0d;
		_searchStartedTimestamp = timestamp;
	}

	private void UpdateSearchEvidence(long timestamp)
	{
		if (string.IsNullOrWhiteSpace(_searchUserId)
			|| Elapsed(timestamp, _searchStartedTimestamp) >= TargetSearchTicks)
		{
			return;
		}
		PersonIdentityObservation? located =
			SelectSearchIdentityObservation();
		if (located is null)
		{
			_searchFaceRegion = default;
			_searchConfidence = 0d;
			return;
		}
		_searchFaceRegion = located.FaceBox;
		_searchConfidence = Math.Clamp(
			located.EvidenceConfidence,
			0d,
			1d);
	}

	private PersonIdentityObservation? SelectSearchIdentityObservation()
	{
		return string.IsNullOrWhiteSpace(_searchUserId)
			? null
			: _latestIdentityPeople
				.Where(person =>
					person.EvidenceState
						== PersonIdentityEvidenceState.ConfirmedRegisteredUser
					&& string.Equals(
						person.IdentityId,
						_searchUserId,
						StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(person => person.EvidenceConfidence)
				.FirstOrDefault();
	}

	private void ExpireSearch(long now)
	{
		if (!string.IsNullOrWhiteSpace(_searchUserId)
			&& _searchStartedTimestamp != 0
			&& Elapsed(now, _searchStartedTimestamp) >= TargetSearchTicks)
		{
			ClearSearch();
		}
	}

	private void ClearSearch()
	{
		_searchUserId = "";
		_searchFaceRegion = default;
		_searchConfidence = 0d;
		_searchStartedTimestamp = 0;
	}

	private void ClearTarget(bool preserveSearch = false)
	{
		_userId = "";
		_username = "";
		_displayName = "Unknown";
		_isAuthorized = false;
		_identityLocked = false;
		_mediaPipeLocked = false;
		_bothLostTimestamp = 0;
		_edgeExitArmed = false;
		_latestIdentityCandidate = null;
		_latestIdentityPeople = Array.Empty<PersonIdentityObservation>();
		_identityContradictionTimestamp = 0;
		_identityEvidenceState = PersonIdentityEvidenceState.Insufficient;
		_identityConfidence = 0d;
		if (!preserveSearch)
		{
			ClearSearch();
		}
	}

	private static bool CanCorrelate(
		long firstTimestamp,
		long secondTimestamp,
		PersonFaceBox identity,
		PersonFaceBox mediaPipe)
	{
		if (Math.Abs(firstTimestamp - secondTimestamp)
			> CorrelationWindowTicks)
		{
			return false;
		}
		double identityWidth = identity.Right - identity.Left;
		double identityHeight = identity.Bottom - identity.Top;
		double mediaPipeWidth = mediaPipe.Right - mediaPipe.Left;
		double mediaPipeHeight = mediaPipe.Bottom - mediaPipe.Top;
		double centerDeltaX = Math.Abs(
			(identity.Left + identity.Right
				- mediaPipe.Left - mediaPipe.Right) * 0.5d);
		double centerDeltaY = Math.Abs(
			(identity.Top + identity.Bottom
				- mediaPipe.Top - mediaPipe.Bottom) * 0.5d);
		return IntersectionOverUnion(identity, mediaPipe) >= 0.45d
			&& centerDeltaX <= Math.Max(identityWidth, mediaPipeWidth) * 0.20d
			&& centerDeltaY <= Math.Max(identityHeight, mediaPipeHeight) * 0.20d;
	}

	private static double CenterDistanceSquared(
		PersonFaceBox first,
		PersonFaceBox second)
	{
		double deltaX =
			(first.Left + first.Right - second.Left - second.Right) * 0.5d;
		double deltaY =
			(first.Top + first.Bottom - second.Top - second.Bottom) * 0.5d;
		return deltaX * deltaX + deltaY * deltaY;
	}

	private static bool IsContinuous(
		PersonFaceBox current,
		PersonFaceBox previous)
	{
		return IntersectionOverUnion(current, previous) >= 0.08d
			|| IsInsideReacquisitionRegion(current, previous);
	}

	private static bool IsInsideReacquisitionRegion(
		PersonFaceBox candidate,
		PersonFaceBox previous)
	{
		if (!IsValid(candidate) || !IsValid(previous))
		{
			return false;
		}
		double centerX = (candidate.Left + candidate.Right) * 0.5d;
		double centerY = (candidate.Top + candidate.Bottom) * 0.5d;
		double left = previous.Left;
		double right = previous.Right;
		double top = previous.Top;
		double bottom = previous.Bottom;
		return centerX >= left
			&& centerX <= right
			&& centerY >= top
			&& centerY <= bottom;
	}

	private static double IntersectionOverUnion(
		PersonFaceBox first,
		PersonFaceBox second)
	{
		double left = Math.Max(first.Left, second.Left);
		double top = Math.Max(first.Top, second.Top);
		double right = Math.Min(first.Right, second.Right);
		double bottom = Math.Min(first.Bottom, second.Bottom);
		double intersection =
			Math.Max(0d, right - left)
			* Math.Max(0d, bottom - top);
		double firstArea =
			Math.Max(0d, first.Right - first.Left)
			* Math.Max(0d, first.Bottom - first.Top);
		double secondArea =
			Math.Max(0d, second.Right - second.Left)
			* Math.Max(0d, second.Bottom - second.Top);
		double union = firstArea + secondArea - intersection;
		return union <= 1e-9d ? 0d : intersection / union;
	}

	private static bool TouchesEdge(PersonFaceBox region)
	{
		const double threshold = 0.02d;
		return region.Left <= threshold
			|| region.Top <= threshold
			|| region.Right >= 1d - threshold
			|| region.Bottom >= 1d - threshold;
	}

	private static bool IsMovingOutward(
		PersonFaceBox previous,
		PersonFaceBox current)
	{
		const double movement = 0.003d;
		double oldX = (previous.Left + previous.Right) * 0.5d;
		double oldY = (previous.Top + previous.Bottom) * 0.5d;
		double newX = (current.Left + current.Right) * 0.5d;
		double newY = (current.Top + current.Bottom) * 0.5d;
		return current.Left <= 0.02d && newX < oldX - movement
			|| current.Right >= 0.98d && newX > oldX + movement
			|| current.Top <= 0.02d && newY < oldY - movement
			|| current.Bottom >= 0.98d && newY > oldY + movement;
	}

	private static bool IsValid(PersonFaceBox region)
	{
		return double.IsFinite(region.Left)
			&& double.IsFinite(region.Top)
			&& double.IsFinite(region.Right)
			&& double.IsFinite(region.Bottom)
			&& region.Right > region.Left
			&& region.Bottom > region.Top;
	}

	private static long Elapsed(long now, long then)
	{
		return Math.Max(0L, now - then);
	}

	private static long ToTicks(TimeSpan duration)
	{
		return (long)Math.Ceiling(
			duration.TotalSeconds * Stopwatch.Frequency);
	}
}
