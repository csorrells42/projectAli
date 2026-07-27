using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Confidence;

public sealed class InteractionConfidenceStore : IDisposable
{
	private readonly SqliteConnection _connection;

	public string DatabasePath { get; }

	public InteractionConfidenceStore(string databasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		DatabasePath = Path.GetFullPath(databasePath);
		Directory.CreateDirectory(
			Path.GetDirectoryName(DatabasePath)
				?? throw new InvalidOperationException(
					"Interaction database folder was invalid."));
		_connection = new SqliteConnection(
			new SqliteConnectionStringBuilder
			{
				DataSource = DatabasePath,
				Mode = SqliteOpenMode.ReadWriteCreate,
				Cache = SqliteCacheMode.Private
			}.ToString());
		_connection.Open();
		Configure();
		EnsureSchema();
	}

	public long Write(InteractionConfidenceRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);
		using SqliteCommand command = _connection.CreateCommand();
		command.CommandText =
			"""
			INSERT INTO interaction_utterances (
				sequence_id, utterance_captured_utc, transcribed_utc,
				attention_sources, attention_source_names,
				transcript, transcription_provider, transcription_status,
				participant_identity_id, participant_display_name,
				visual_identity_id, visual_identity_confidence,
				voice_identity_id, voice_identity_confidence,
				identity_signals_agree, security_reason,
				audio_sample_rate, audio_duration_ms, audio_wav)
			VALUES (
				$sequence_id, $utterance_captured_utc, $transcribed_utc,
				$attention_sources, $attention_source_names,
				$transcript, $transcription_provider, $transcription_status,
				$participant_identity_id, $participant_display_name,
				$visual_identity_id, $visual_identity_confidence,
				$voice_identity_id, $voice_identity_confidence,
				$identity_signals_agree, $security_reason,
				$audio_sample_rate, $audio_duration_ms, $audio_wav);
			SELECT last_insert_rowid();
			""";
		command.Parameters.AddWithValue("$sequence_id", record.SequenceId);
		command.Parameters.AddWithValue("$utterance_captured_utc",
			record.UtteranceCapturedAtUtc.ToUniversalTime().ToString("O"));
		command.Parameters.AddWithValue("$transcribed_utc",
			record.TranscribedAtUtc.ToUniversalTime().ToString("O"));
		command.Parameters.AddWithValue("$attention_sources",
			(int)record.AttentionSources);
		command.Parameters.AddWithValue("$attention_source_names",
			record.AttentionSources.ToString());
		command.Parameters.AddWithValue("$transcript", record.Transcript);
		command.Parameters.AddWithValue("$transcription_provider",
			record.TranscriptionProvider);
		command.Parameters.AddWithValue("$transcription_status",
			record.TranscriptionStatus);
		command.Parameters.AddWithValue("$participant_identity_id",
			record.ParticipantIdentityId);
		command.Parameters.AddWithValue("$participant_display_name",
			record.ParticipantDisplayName);
		command.Parameters.AddWithValue("$visual_identity_id",
			record.VisualIdentityId);
		command.Parameters.AddWithValue("$visual_identity_confidence",
			Math.Clamp(record.VisualIdentityConfidence, 0d, 1d));
		command.Parameters.AddWithValue("$voice_identity_id",
			record.VoiceIdentityId);
		command.Parameters.AddWithValue("$voice_identity_confidence",
			Math.Clamp(record.VoiceIdentityConfidence, 0d, 1d));
		command.Parameters.AddWithValue("$identity_signals_agree",
			record.IdentitySignalsAgree ? 1 : 0);
		command.Parameters.AddWithValue("$security_reason",
			record.SecurityReason);
		command.Parameters.AddWithValue("$audio_sample_rate",
			record.AudioSampleRate);
		command.Parameters.AddWithValue("$audio_duration_ms",
			record.AudioDuration.TotalMilliseconds);
		command.Parameters.AddWithValue("$audio_wav", record.AudioWav);
		return Convert.ToInt64(command.ExecuteScalar());
	}

	public void Dispose() => _connection.Dispose();

	private void Configure()
	{
		using SqliteCommand command = _connection.CreateCommand();
		command.CommandText =
			"PRAGMA journal_mode=WAL;"
			+ "PRAGMA synchronous=NORMAL;"
			+ "PRAGMA busy_timeout=1000;";
		command.ExecuteNonQuery();
	}

	private void EnsureSchema()
	{
		using SqliteCommand command = _connection.CreateCommand();
		command.CommandText =
			"""
			CREATE TABLE IF NOT EXISTS interaction_utterances (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				sequence_id INTEGER NOT NULL,
				utterance_captured_utc TEXT NOT NULL,
				transcribed_utc TEXT NOT NULL,
				attention_sources INTEGER NOT NULL,
				attention_source_names TEXT NOT NULL,
				transcript TEXT NOT NULL,
				transcription_provider TEXT NOT NULL,
				transcription_status TEXT NOT NULL,
				participant_identity_id TEXT NOT NULL,
				participant_display_name TEXT NOT NULL,
				visual_identity_id TEXT NOT NULL,
				visual_identity_confidence REAL NOT NULL,
				voice_identity_id TEXT NOT NULL,
				voice_identity_confidence REAL NOT NULL,
				identity_signals_agree INTEGER NOT NULL,
				security_reason TEXT NOT NULL,
				audio_sample_rate INTEGER NOT NULL,
				audio_duration_ms REAL NOT NULL,
				audio_wav BLOB NOT NULL)
			STRICT;
			CREATE INDEX IF NOT EXISTS
				ix_interaction_utterances_sequence
				ON interaction_utterances(sequence_id);
			CREATE INDEX IF NOT EXISTS
				ix_interaction_utterances_captured
				ON interaction_utterances(utterance_captured_utc);
			""";
		command.ExecuteNonQuery();
	}
}
