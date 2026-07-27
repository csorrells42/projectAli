using System;

namespace AvatarBuilder.Modules.Pipeline;

/// <summary>
/// Public timing measurements owned and updated by one frame module.
/// </summary>
public interface IFrameModuleTimingSource
{
	TimeSpan TimeWaited { get; }

	TimeSpan TimeWorked { get; }
}
