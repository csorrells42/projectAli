using System;
using AvatarBuilder.Modules.Contracts;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Exposes only the latest completely published immutable result.
/// Requesting a result never starts work and never waits for the producer.
/// </summary>
public interface ILatestFrameProducer<TSnapshot>
	where TSnapshot : ModuleOutput, IFramePipelineSnapshot
{
	bool TryGetLatest(
		long afterFrameId,
		SnapshotCursor<TSnapshot> destination);
}
