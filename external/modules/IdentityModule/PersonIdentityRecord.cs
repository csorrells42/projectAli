using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed class PersonIdentityRecord
{
	public string Id { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string FirstName { get; set; } = "";

	public string LastName { get; set; } = "";

	public string Username { get; set; } = "";

	public string Email { get; set; } = "";

	public string PhoneNumber { get; set; } = "";

	public string Address { get; set; } = "";

	public bool IsRegisteredUser { get; set; }

	public string PermissionLevel { get; set; } = "Default User";

	public DateTime FirstSeenAtUtc { get; set; }

	public DateTime LastSeenAtUtc { get; set; }

	public int ObservationCount { get; set; }

	public int EncounterCount { get; set; }

	public List<float[]> Prototypes { get; set; } = [];
}
