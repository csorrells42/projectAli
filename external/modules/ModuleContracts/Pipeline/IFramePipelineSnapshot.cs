using System;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Identifies one immutable result in the live frame pipeline.
/// </summary>
public interface IFramePipelineSnapshot : IModuleSnapshot
{
	long FrameId { get; }

	long CapturedAtTimestamp { get; }

	DateTime CapturedAtUtc { get; }

	long IModuleSnapshot.SequenceId => FrameId;

	long IModuleSnapshot.ProducedAtTimestamp => CapturedAtTimestamp;

	DateTime IModuleSnapshot.ProducedAtUtc => CapturedAtUtc;
}
