using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;

namespace AvatarBuilder.Modules.Confidence;

public sealed class InteractionConfidenceOutput : ModuleOutput, IModuleSnapshot
{
	public long SequenceId { get; }
	public long ProducedAtTimestamp { get; } =
		System.Diagnostics.Stopwatch.GetTimestamp();
	public DateTime ProducedAtUtc { get; } = DateTime.UtcNow;
	public long DatabaseRowId { get; }
	public string DatabasePath { get; }

	internal InteractionConfidenceOutput(
		long sequenceId,
		long databaseRowId,
		string databasePath)
	{
		SequenceId = sequenceId;
		DatabaseRowId = databaseRowId;
		DatabasePath = databasePath;
	}

	protected override void DisposeOwnedResources() { }
}
