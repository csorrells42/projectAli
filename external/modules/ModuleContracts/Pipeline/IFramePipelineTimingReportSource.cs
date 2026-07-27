using System.Collections.Generic;

namespace AvatarBuilder.Modules.Pipeline;

public interface IFramePipelineTimingReportSource
{
	IReadOnlyList<FramePipelineTimingRow> GetTimingReport();
}
