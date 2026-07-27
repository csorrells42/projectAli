using System;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed record PersonIdentityReviewItem(
	string IdentityId,
	string DisplayName,
	string FirstName,
	string LastName,
	string Username,
	string Email,
	string PhoneNumber,
	string Address,
	string ContextPhotoPath,
	bool IsRegisteredUser,
	string PermissionLevel,
	DateTime FirstSeenAtUtc,
	DateTime LastSeenAtUtc,
	int ObservationCount,
	int EncounterCount);
