using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed record IdentityReviewUpdate(
	string IdentityId,
	string FirstName,
	string LastName,
	string Username,
	string Email,
	string PhoneNumber,
	string Address,
	bool RegisterAsUser,
	string PermissionLevel);

public sealed record IdentityReviewUpdateResult(
	bool Success,
	string Status);

public sealed record IdentityEnrollmentRequest(
	string FirstName,
	string LastName,
	string Username,
	string Email,
	string PhoneNumber,
	string Address,
	string PermissionLevel);

public sealed record IdentityEnrollmentState(
	bool IsAvailable,
	bool IsActive,
	bool CapturePending,
	int CapturedPoseCount,
	int RequiredPoseCount,
	string Prompt,
	string Status,
	string CompletedIdentityId)
{
	public static IdentityEnrollmentState Unavailable(string status) => new(
		false,
		false,
		false,
		0,
		0,
		"",
		status,
		"");
}

public interface IPersonIdentityReviewService
{
	string Status { get; }

	IReadOnlyList<PersonIdentityReviewItem> GetIdentityReviewItems();

	IdentityReviewUpdateResult UpdateIdentityReview(
		IdentityReviewUpdate update);

	IdentityReviewUpdateResult ReplaceContextPhoto(
		string identityId,
		ReadOnlyMemory<byte> jpegBytes);

	IdentityReviewUpdateResult DeleteIdentity(string identityId);

	IdentityReviewUpdateResult BeginEnrollment(
		IdentityEnrollmentRequest request);

	IdentityReviewUpdateResult RequestEnrollmentCapture();

	IdentityEnrollmentState GetEnrollmentState();

	void CancelEnrollment();
}
