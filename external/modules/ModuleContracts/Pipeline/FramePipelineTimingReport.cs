using System;

namespace AvatarBuilder.Modules.Pipeline;

public sealed record FramePipelineTimingRow(
	string Module,
	TimeSpan TimeWaited,
	TimeSpan TimeWorked);
