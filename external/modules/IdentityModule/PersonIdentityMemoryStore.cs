using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Vision.Identity;

internal sealed class PersonIdentityMemoryStore
{
	private const string RootFolderName = "AvatarSystem";

	private const string MemoryFolderName = "IdentityMemory";

	private const string MemoryFileName =
		"person_identity_memory_current.sqlite";

	public List<PersonIdentityRecord> Load(string outputFolder)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		string path = GetPath(outputFolder);
		if (!File.Exists(path))
		{
			return [];
		}
		try
		{
			using SqliteConnection connection = Open(path);
			EnsureSchema(connection);
			var people = new List<PersonIdentityRecord>();
			var byId = new Dictionary<string, PersonIdentityRecord>(
				StringComparer.OrdinalIgnoreCase);
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText =
					"""
					SELECT id, display_name, first_name, last_name,
						username, email, phone_number, address,
						is_registered_user,
						permission_level, first_seen_utc, last_seen_utc,
						observation_count, encounter_count
					FROM people
					ORDER BY first_seen_utc, id;
					""";
				using SqliteDataReader reader = command.ExecuteReader();
				while (reader.Read())
				{
					var person = new PersonIdentityRecord
					{
						Id = reader.GetString(0).Trim(),
						DisplayName = reader.GetString(1).Trim(),
						FirstName = reader.GetString(2).Trim(),
						LastName = reader.GetString(3).Trim(),
						Username = reader.GetString(4).Trim(),
						Email = reader.GetString(5).Trim(),
						PhoneNumber = reader.GetString(6).Trim(),
						Address = reader.GetString(7).Trim(),
						IsRegisteredUser = reader.GetInt32(8) != 0,
						PermissionLevel = NormalizePermission(
							reader.GetString(9)),
						FirstSeenAtUtc = ReadUtc(reader.GetString(10)),
						LastSeenAtUtc = ReadUtc(reader.GetString(11)),
						ObservationCount = Math.Max(1, reader.GetInt32(12)),
						EncounterCount = Math.Max(1, reader.GetInt32(13))
					};
					if (string.IsNullOrWhiteSpace(person.Id)
						|| byId.ContainsKey(person.Id))
					{
						continue;
					}
					byId.Add(person.Id, person);
					people.Add(person);
				}
			}
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText =
					"""
					SELECT person_id, embedding
					FROM person_prototypes
					ORDER BY person_id, ordinal;
					""";
				using SqliteDataReader reader = command.ExecuteReader();
				while (reader.Read())
				{
					if (!byId.TryGetValue(
						reader.GetString(0),
						out PersonIdentityRecord? person))
					{
						continue;
					}
					float[] embedding = ReadEmbedding((byte[])reader[1]);
					if (embedding.Length
						== SFaceEmbeddingExtractor.ExpectedEmbeddingLength)
					{
						person.Prototypes.Add(embedding);
					}
				}
			}
			// Registered users are durable identities even before a camera is
			// available. Face prototypes are optional recognition evidence, not
			// the existence or ownership key for the user profile.
			return people
				.Where(person => person.IsRegisteredUser
					|| person.Prototypes.Count > 0)
				.ToList();
		}
		catch
		{
			return [];
		}
	}

	public void Upsert(
		string outputFolder,
		IReadOnlyCollection<PersonIdentityRecord> changedPeople)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		if (changedPeople.Count == 0)
		{
			return;
		}
		string path = GetPath(outputFolder);
		Directory.CreateDirectory(
			Path.GetDirectoryName(path)
			?? Path.Combine(outputFolder, RootFolderName));
		using SqliteConnection connection = Open(path);
		EnsureSchema(connection);
		using SqliteTransaction transaction = connection.BeginTransaction();
		foreach (PersonIdentityRecord person in changedPeople)
		{
			UpsertPerson(connection, transaction, person);
		}
		transaction.Commit();
	}

	public string GetPath(string outputFolder)
	{
		return Path.Combine(
			outputFolder,
			RootFolderName,
			MemoryFolderName,
			MemoryFileName);
	}

	public string GetContextPhotoPath(
		string outputFolder,
		string identityId)
	{
		return Path.Combine(
			outputFolder,
			RootFolderName,
			MemoryFolderName,
			"ContextPhotos",
			identityId + ".jpg");
	}

	public void SaveContextPhoto(
		string outputFolder,
		string identityId,
		ReadOnlySpan<byte> jpegBytes)
	{
		if (jpegBytes.IsEmpty)
		{
			return;
		}
		string path = GetContextPhotoPath(outputFolder, identityId);
		Directory.CreateDirectory(
			Path.GetDirectoryName(path)
			?? throw new InvalidOperationException(
				"Identity context-photo directory is missing."));
		using FileStream stream = new(
			path,
			FileMode.Create,
			FileAccess.Write,
			FileShare.Read);
		stream.Write(jpegBytes);
		stream.Flush(flushToDisk: true);
	}

	public void Delete(string outputFolder, string identityId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
		string path = GetPath(outputFolder);
		if (File.Exists(path))
		{
			using SqliteConnection connection = Open(path);
			EnsureSchema(connection);
			using SqliteTransaction transaction =
				connection.BeginTransaction();
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM people WHERE id = $id;";
			command.Parameters.AddWithValue("$id", identityId.Trim());
			command.ExecuteNonQuery();
			transaction.Commit();
		}
		string photo = GetContextPhotoPath(outputFolder, identityId);
		if (File.Exists(photo))
		{
			File.Delete(photo);
		}
	}

	private static SqliteConnection Open(string path)
	{
		var connection = new SqliteConnection(
			new SqliteConnectionStringBuilder
			{
				DataSource = path,
				Mode = SqliteOpenMode.ReadWriteCreate,
				Cache = SqliteCacheMode.Private,
				Pooling = true
			}.ToString());
		connection.Open();
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"""
			PRAGMA journal_mode=WAL;
			PRAGMA synchronous=NORMAL;
			PRAGMA foreign_keys=ON;
			PRAGMA busy_timeout=1000;
			""";
		command.ExecuteNonQuery();
		return connection;
	}

	private static void EnsureSchema(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"""
			CREATE TABLE IF NOT EXISTS people (
				id TEXT PRIMARY KEY NOT NULL,
				display_name TEXT NOT NULL,
				first_name TEXT NOT NULL,
				last_name TEXT NOT NULL,
				username TEXT NOT NULL,
				email TEXT NOT NULL,
				phone_number TEXT NOT NULL,
				address TEXT NOT NULL,
				is_registered_user INTEGER NOT NULL,
				permission_level TEXT NOT NULL,
				first_seen_utc TEXT NOT NULL,
				last_seen_utc TEXT NOT NULL,
				observation_count INTEGER NOT NULL,
				encounter_count INTEGER NOT NULL
			);
			CREATE UNIQUE INDEX IF NOT EXISTS
				people_registered_username_unique
			ON people(username COLLATE NOCASE)
			WHERE is_registered_user = 1 AND username <> '';
			CREATE TABLE IF NOT EXISTS person_prototypes (
				person_id TEXT NOT NULL,
				ordinal INTEGER NOT NULL,
				embedding BLOB NOT NULL,
				PRIMARY KEY (person_id, ordinal),
				FOREIGN KEY (person_id)
					REFERENCES people(id) ON DELETE CASCADE
			);
			""";
		command.ExecuteNonQuery();
	}

	private static void UpsertPerson(
		SqliteConnection connection,
		SqliteTransaction transaction,
		PersonIdentityRecord person)
	{
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText =
				"""
				INSERT INTO people (
					id, display_name, first_name, last_name,
					username, email, phone_number, address,
					is_registered_user, permission_level,
					first_seen_utc, last_seen_utc,
					observation_count, encounter_count)
				VALUES (
					$id, $display_name, $first_name, $last_name,
					$username, $email, $phone_number, $address,
					$is_registered_user, $permission_level,
					$first_seen_utc, $last_seen_utc,
					$observation_count, $encounter_count)
				ON CONFLICT(id) DO UPDATE SET
					display_name = excluded.display_name,
					first_name = excluded.first_name,
					last_name = excluded.last_name,
					username = excluded.username,
					email = excluded.email,
					phone_number = excluded.phone_number,
					address = excluded.address,
					is_registered_user = excluded.is_registered_user,
					permission_level = excluded.permission_level,
					first_seen_utc = excluded.first_seen_utc,
					last_seen_utc = excluded.last_seen_utc,
					observation_count = excluded.observation_count,
					encounter_count = excluded.encounter_count;
				""";
			command.Parameters.AddWithValue("$id", person.Id.Trim());
			command.Parameters.AddWithValue(
				"$display_name",
				person.DisplayName.Trim());
			command.Parameters.AddWithValue(
				"$first_name",
				person.FirstName.Trim());
			command.Parameters.AddWithValue(
				"$last_name",
				person.LastName.Trim());
			command.Parameters.AddWithValue(
				"$username",
				person.Username.Trim());
			command.Parameters.AddWithValue(
				"$email",
				person.Email.Trim());
			command.Parameters.AddWithValue(
				"$phone_number",
				person.PhoneNumber.Trim());
			command.Parameters.AddWithValue(
				"$address",
				person.Address.Trim());
			command.Parameters.AddWithValue(
				"$is_registered_user",
				person.IsRegisteredUser ? 1 : 0);
			command.Parameters.AddWithValue(
				"$permission_level",
				NormalizePermission(person.PermissionLevel));
			command.Parameters.AddWithValue(
				"$first_seen_utc",
				WriteUtc(person.FirstSeenAtUtc));
			command.Parameters.AddWithValue(
				"$last_seen_utc",
				WriteUtc(person.LastSeenAtUtc));
			command.Parameters.AddWithValue(
				"$observation_count",
				Math.Max(1, person.ObservationCount));
			command.Parameters.AddWithValue(
				"$encounter_count",
				Math.Max(1, person.EncounterCount));
			command.ExecuteNonQuery();
		}
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText =
				"DELETE FROM person_prototypes WHERE person_id = $person_id;";
			command.Parameters.AddWithValue("$person_id", person.Id.Trim());
			command.ExecuteNonQuery();
		}
		for (int ordinal = 0;
			ordinal < person.Prototypes.Count;
			ordinal++)
		{
			float[] embedding = person.Prototypes[ordinal];
			if (!IsUsableEmbedding(embedding))
			{
				continue;
			}
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText =
				"""
				INSERT INTO person_prototypes (
					person_id, ordinal, embedding)
				VALUES ($person_id, $ordinal, $embedding);
				""";
			command.Parameters.AddWithValue("$person_id", person.Id.Trim());
			command.Parameters.AddWithValue("$ordinal", ordinal);
			command.Parameters.Add(
				"$embedding",
				SqliteType.Blob).Value = WriteEmbedding(embedding);
			command.ExecuteNonQuery();
		}
	}

	private static bool IsUsableEmbedding(float[]? embedding)
	{
		return embedding is
			{ Length: SFaceEmbeddingExtractor.ExpectedEmbeddingLength }
			&& embedding.All(float.IsFinite);
	}

	private static byte[] WriteEmbedding(float[] embedding)
	{
		byte[] bytes = new byte[embedding.Length * sizeof(float)];
		Buffer.BlockCopy(
			embedding,
			0,
			bytes,
			0,
			bytes.Length);
		return bytes;
	}

	private static float[] ReadEmbedding(byte[] bytes)
	{
		if (bytes.Length
			!= SFaceEmbeddingExtractor.ExpectedEmbeddingLength
				* sizeof(float))
		{
			return [];
		}
		float[] embedding =
			new float[SFaceEmbeddingExtractor.ExpectedEmbeddingLength];
		Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
		if (!embedding.All(float.IsFinite))
		{
			return [];
		}
		double squaredNorm = 0d;
		foreach (float value in embedding)
		{
			squaredNorm += value * value;
		}
		double norm = Math.Sqrt(squaredNorm);
		if (!double.IsFinite(norm) || norm < 1e-8d)
		{
			return [];
		}
		float inverseNorm = (float)(1d / norm);
		for (int index = 0; index < embedding.Length; index++)
		{
			embedding[index] *= inverseNorm;
		}
		return embedding;
	}

	private static string WriteUtc(DateTime value)
	{
		return (value == default ? DateTime.UtcNow : value)
			.ToUniversalTime()
			.ToString("O");
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

	private static DateTime ReadUtc(string value)
	{
		return DateTime.TryParse(
			value,
			null,
			System.Globalization.DateTimeStyles.RoundtripKind,
			out DateTime parsed)
			? parsed.ToUniversalTime()
			: DateTime.UtcNow;
	}
}
