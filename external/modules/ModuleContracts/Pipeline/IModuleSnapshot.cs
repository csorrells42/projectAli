using System;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Common immutable publication identity for vision, audio, and interaction
/// outputs. SequenceId is monotonic within one producing module.
/// </summary>
public interface IModuleSnapshot
{
	long SequenceId { get; }

	long ProducedAtTimestamp { get; }

	DateTime ProducedAtUtc { get; }
}
