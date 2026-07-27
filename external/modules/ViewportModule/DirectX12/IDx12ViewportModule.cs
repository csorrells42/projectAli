using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Viewports.DirectX12;

public interface IDx12ViewportModule :
	IVisionModule,
	IFrameModuleTimingSource,
	IDisposable
{
	Direct3D12PreviewHost Host { get; }

	long SubmittedFrameId { get; }

	long LastPresentedFrameTimestamp { get; }

	Direct3D12PreviewDiagnostics Diagnostics { get; }

	event EventHandler<string>? StatusChanged;

	event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

	void ConfigurePresentation(
		VideoFrameColorSettings colorSettings,
		bool denoiseEnabled,
		double denoiseStrength,
		double maximumFramesPerSecond = 0d);

	void SetRecordingMode(string recordingMode);

	void Resume();

	void Suspend();
}
