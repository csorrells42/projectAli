using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class VisionPipelineDiagnostics
{
	public static VisionPipelineDiagnostics None { get; } = new VisionPipelineDiagnostics();

	public DateTime CapturedAtUtc { get; init; }

	public string Backend { get; init; } = "";

	public string Mode { get; init; } = "";

	public int SourceWidth { get; init; }

	public int SourceHeight { get; init; }

	public int InputWidth { get; init; }

	public int InputHeight { get; init; }

	public int EncodedPayloadBytes { get; init; }

	public bool HasFace { get; init; }

	public double ClientPrepareMilliseconds { get; init; }

	public double SidecarRoundTripMilliseconds { get; init; }

	public double ClientParseMilliseconds { get; init; }

	public double EndToEndMilliseconds { get; init; }

	public IReadOnlyDictionary<string, double> SidecarStagesMilliseconds { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

	public string Status { get; init; } = "";
}
