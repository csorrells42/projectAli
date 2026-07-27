using System;

namespace AvatarBuilder.Modules.Viewports.DirectX12;

public sealed record Direct3D12PreviewDiagnostics(string PreviewPath, string DeviceDescription, string FrameFormat, int Width, int Height, double SourceFramesPerSecond, long SubmittedFrames, long RenderedFrames, long DroppedFrames, double RenderFramesPerSecond, bool DenoiseEnabled, double DenoiseStrength, bool ColorPolishEnabled, string RecordingMode, string? FallbackReason, long LastFrameNumber, DateTimeOffset? LastFrameUtc)
{
	public static Direct3D12PreviewDiagnostics Empty { get; } = new Direct3D12PreviewDiagnostics("DX12 preview path pending", "DX12 preview not initialized", "none", 0, 0, 0.0, 0L, 0L, 0L, 0.0, DenoiseEnabled: false, 0.0, ColorPolishEnabled: false, "not recording", null, 0L, null);

	public string FormatStatusLine()
	{
		string value = ((Width > 0 && Height > 0) ? $"{Width}x{Height}" : "no frame");
		string value2 = ((SourceFramesPerSecond > 0.0) ? $"{SourceFramesPerSecond:0.#} advertised source fps" : "advertised source fps unknown");
		string value3 = (string.IsNullOrWhiteSpace(FallbackReason) ? string.Empty : ("; fallback: " + FallbackReason));
		return $"{PreviewPath}; {FrameFormat}; {value}; {value2}; render {RenderFramesPerSecond:0.#} fps; submitted {SubmittedFrames}; rendered {RenderedFrames}; dropped {DroppedFrames}; {RecordingMode}{value3}";
	}
}
