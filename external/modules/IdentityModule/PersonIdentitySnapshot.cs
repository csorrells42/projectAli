using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Identity;

public readonly record struct PersonFaceBox(
	double Left,
	double Top,
	double Right,
	double Bottom);

public enum PersonIdentityEvidenceState
{
	Insufficient,
	UsableUnknown,
	ConfirmedRegisteredUser
}
public sealed record PersonIdentityObservation(
	string TrackId,
	string IdentityId,
	string DisplayName,
	string Username,
	bool IsRemembered,
	bool IsRegisteredUser,
	double Similarity,
	PersonFaceBox FaceBox,
	PersonIdentityEvidenceState EvidenceState =
		PersonIdentityEvidenceState.Insufficient,
	double EvidenceConfidence = 0d);

public sealed record PersonIdentitySnapshot(
	DateTime CapturedAtUtc,
	IReadOnlyList<PersonIdentityObservation> People,
	int RememberedIdentityCount,
	string Backend,
	string Status)
{
	public static PersonIdentitySnapshot Waiting { get; } = new(
		DateTime.MinValue,
		Array.Empty<PersonIdentityObservation>(),
		0,
		"",
		"People memory waiting");
}

