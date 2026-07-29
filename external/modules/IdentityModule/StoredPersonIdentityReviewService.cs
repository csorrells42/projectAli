using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Camera-independent identity review. It reads and updates only the current
/// persisted identity store; it does not initialize detection or recognition.
/// </summary>
public sealed class StoredPersonIdentityReviewService :
	IPersonIdentityReviewService
{
	private readonly object _sync = new();

	private readonly string _outputFolder;

	private readonly PersonIdentityMemoryStore _store = new();

	public string Status { get; private set; } =
		"Persistent identity review ready";

	public StoredPersonIdentityReviewService(string outputFolder)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		_outputFolder = outputFolder;
	}

	public IReadOnlyList<PersonIdentityReviewItem>
		GetIdentityReviewItems()
	{
		lock (_sync)
		{
			PersonIdentityReviewItem[] items =
				_store.Load(_outputFolder)
				.OrderByDescending(person => person.LastSeenAtUtc)
				.Select(ToReviewItem)
				.ToArray();
			Status = items.Length == 0
				? "No retained identities in the current store"
				: $"{items.Length} retained "
					+ (items.Length == 1 ? "identity" : "identities")
					+ " loaded from disk";
			return items;
		}
	}

	public IdentityReviewUpdateResult UpdateIdentityReview(
		IdentityReviewUpdate update)
	{
		ArgumentNullException.ThrowIfNull(update);
		ArgumentException.ThrowIfNullOrWhiteSpace(update.IdentityId);
		string firstName = update.FirstName.Trim();
		string lastName = update.LastName.Trim();
		string username = update.Username.Trim();
		if (update.RegisterAsUser
			&& (string.IsNullOrWhiteSpace(firstName)
				|| string.IsNullOrWhiteSpace(lastName)
				|| string.IsNullOrWhiteSpace(username)))
		{
			return new IdentityReviewUpdateResult(
				false,
				"Registered users require first name, last name, and username.");
		}

		lock (_sync)
		{
			List<PersonIdentityRecord> people =
				_store.Load(_outputFolder);
			PersonIdentityRecord? person =
				people.FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						update.IdentityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"That identity is no longer available.");
			}
			if (update.RegisterAsUser
				&& people.Any(candidate =>
					!ReferenceEquals(candidate, person)
					&& candidate.IsRegisteredUser
					&& string.Equals(
						candidate.Username,
						username,
						StringComparison.OrdinalIgnoreCase)))
			{
				return new IdentityReviewUpdateResult(
					false,
					$"Username '{username}' is already assigned.");
			}

			person.FirstName = firstName;
			person.LastName = lastName;
			person.Username = username;
			person.Email = update.Email.Trim();
			person.PhoneNumber = update.PhoneNumber.Trim();
			person.Address = update.Address.Trim();
			if (!string.IsNullOrWhiteSpace(firstName)
				|| !string.IsNullOrWhiteSpace(lastName))
			{
				person.DisplayName =
					(firstName + " " + lastName).Trim();
			}
			person.IsRegisteredUser = update.RegisterAsUser;
			person.PermissionLevel =
				update.RegisterAsUser
					? NormalizePermission(update.PermissionLevel)
					: "Default User";
			try
			{
				_store.Upsert(_outputFolder, [person]);
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity could not be saved: " + exception.Message);
			}
			return new IdentityReviewUpdateResult(
				true,
				$"{person.DisplayName} saved.");
		}
	}

	public IdentityReviewUpdateResult ReplaceContextPhoto(
		string identityId,
		ReadOnlyMemory<byte> jpegBytes)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
		if (jpegBytes.IsEmpty)
		{
			return new IdentityReviewUpdateResult(
				false,
				"The replacement photo is empty.");
		}
		lock (_sync)
		{
			PersonIdentityRecord? person =
				_store.Load(_outputFolder).FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						identityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"That identity is no longer available.");
			}
			try
			{
				_store.SaveContextPhoto(
					_outputFolder,
					person.Id,
					jpegBytes.Span);
				Status = $"{person.DisplayName}'s context photo was replaced";
				return new IdentityReviewUpdateResult(true, Status + ".");
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Replacement photo could not be saved: "
					+ exception.Message);
			}
		}
	}

	public IdentityReviewUpdateResult DeleteIdentity(string identityId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
		lock (_sync)
		{
			PersonIdentityRecord? person =
				_store.Load(_outputFolder).FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						identityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"That identity is no longer available.");
			}
			try
			{
				_store.Delete(_outputFolder, person.Id);
				Status = $"{person.DisplayName} was deleted";
				return new IdentityReviewUpdateResult(
					true,
					Status + ".");
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity could not be deleted: "
					+ exception.Message);
			}
		}
	}

	public IdentityReviewUpdateResult BeginEnrollment(
		IdentityEnrollmentRequest request)
	{
		return new IdentityReviewUpdateResult(
			false,
			"Turn the camera on to enroll a user.");
	}

	public IdentityReviewUpdateResult CreateUserProfile(
		IdentityEnrollmentRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		string firstName = request.FirstName.Trim();
		string lastName = request.LastName.Trim();
		string username = request.Username.Trim();
		if (string.IsNullOrWhiteSpace(firstName)
			|| string.IsNullOrWhiteSpace(lastName)
			|| string.IsNullOrWhiteSpace(username))
		{
			return new IdentityReviewUpdateResult(
				false,
				"User profiles require first name, last name, and username.");
		}

		lock (_sync)
		{
			List<PersonIdentityRecord> people = _store.Load(_outputFolder);
			if (people.Any(person => person.IsRegisteredUser
				&& string.Equals(person.Username, username, StringComparison.OrdinalIgnoreCase)))
			{
				return new IdentityReviewUpdateResult(false, $"Username '{username}' is already assigned.");
			}

			DateTime now = DateTime.UtcNow;
			var person = new PersonIdentityRecord
			{
				Id = "person-" + Guid.NewGuid().ToString("N"),
				DisplayName = (firstName + " " + lastName).Trim(),
				FirstName = firstName,
				LastName = lastName,
				Username = username,
				Email = request.Email.Trim(),
				PhoneNumber = request.PhoneNumber.Trim(),
				Address = request.Address.Trim(),
				IsRegisteredUser = true,
				PermissionLevel = NormalizePermission(request.PermissionLevel),
				FirstSeenAtUtc = now,
				LastSeenAtUtc = now,
				ObservationCount = 0,
				EncounterCount = 0,
				Prototypes = []
			};
			try
			{
				_store.Upsert(_outputFolder, [person]);
				Status = $"{person.DisplayName} was created without camera enrollment";
				return new IdentityReviewUpdateResult(true, Status + ".");
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(false, "User profile could not be saved: " + exception.Message);
			}
		}
	}

	public IdentityReviewUpdateResult RequestEnrollmentCapture()
	{
		return new IdentityReviewUpdateResult(
			false,
			"Turn the camera on to capture enrollment angles.");
	}

	public IdentityEnrollmentState GetEnrollmentState()
	{
		return IdentityEnrollmentState.Unavailable(
			"Turn the camera on to enroll a user.");
	}

	public void CancelEnrollment()
	{
	}

	private PersonIdentityReviewItem ToReviewItem(
		PersonIdentityRecord person)
	{
		return new PersonIdentityReviewItem(
			person.Id,
			person.DisplayName,
			person.FirstName,
			person.LastName,
			person.Username,
			person.Email,
			person.PhoneNumber,
			person.Address,
			_store.GetContextPhotoPath(_outputFolder, person.Id),
			person.IsRegisteredUser,
			NormalizePermission(person.PermissionLevel),
			person.FirstSeenAtUtc,
			person.LastSeenAtUtc,
			person.ObservationCount,
			person.EncounterCount);
	}

	private static string NormalizePermission(string? permission)
	{
		return string.Equals(
			permission?.Trim(),
			"Superuser",
			StringComparison.OrdinalIgnoreCase)
			? "Superuser"
			: "Default User";
	}
}
