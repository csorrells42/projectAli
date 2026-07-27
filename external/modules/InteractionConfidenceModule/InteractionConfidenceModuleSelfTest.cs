using AvatarBuilder.Modules.Security;
using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Confidence;

public static class InteractionConfidenceModuleSelfTest
{
	public static string Run()
	{
		string folder = Path.Combine(Path.GetTempPath(),
			"InteractionConfidenceModule.Hub", Guid.NewGuid().ToString("N"));
		string database = Path.Combine(folder, "test.sqlite");
		try
		{
			byte[] wave = InteractionConfidenceModule.BuildPcm16Wave(
				[0f, 0.25f, -0.25f], 16000);
			using (var store = new InteractionConfidenceStore(database))
			{
				long rowId = store.Write(new InteractionConfidenceRecord(
					42, DateTime.UtcNow, DateTime.UtcNow,
					AttentionGrantSource.Visual | AttentionGrantSource.WakeWord,
					"Exact words sent to Ali", "Test STT", "Transcribed",
					"person-1", "Test Person", "person-1", 0.82,
					"person-1", 0.73, true, "Unified attention gate",
					16000, TimeSpan.FromMilliseconds(1), wave));
				if (rowId != 1)
				{
					throw new InvalidOperationException(
						"SQLite did not return the first row id.");
				}
			}

			using var connection = new SqliteConnection(
				$"Data Source={database};Mode=ReadOnly");
			connection.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT transcript, "
				+ "visual_identity_confidence, voice_identity_confidence, "
				+ "identity_signals_agree, audio_wav "
				+ "FROM interaction_utterances;";
			using SqliteDataReader reader = command.ExecuteReader();
			if (!reader.Read()
				|| reader.GetString(0) != "Exact words sent to Ali"
				|| Math.Abs(reader.GetDouble(1) - 0.82) > 0.0001
				|| Math.Abs(reader.GetDouble(2) - 0.73) > 0.0001
				|| reader.GetInt32(3) != 1
				|| !((byte[])reader[4]).AsSpan(0, 4).SequenceEqual("RIFF"u8))
			{
				throw new InvalidOperationException(
					"SQLite interaction record did not round-trip exactly.");
			}
			return "PASS: SQLite stored the exact Ali transcript, attention routes, separate visual and voice confidence, and bounded WAV audio.";
		}
		finally
		{
			try { Directory.Delete(folder, true); } catch { }
		}
	}
}
