using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Vision.Attention;

public sealed class AttentionOutput :
	ModuleOutput,
	ITextureFrameSnapshot,
	IVisionModuleOutput
{
	public MediaPipeOutput MediaPipeOutput { get; }
	public long FrameId => MediaPipeOutput.FrameId;
	public long CapturedAtTimestamp => MediaPipeOutput.CapturedAtTimestamp;
	public DateTime CapturedAtUtc => MediaPipeOutput.CapturedAtUtc;
	public TextureFrameReference TextureReference =>
		MediaPipeOutput.TextureReference;
	public TextureNativeFrameLease OriginalFrame =>
		MediaPipeOutput.OriginalFrame;
	public bool IsLookingAtCamera { get; }
	public bool HasStableAttention { get; }
	public double HeadYawDegrees { get; }
	public double HeadPitchDegrees { get; }
	public double HeadRollDegrees { get; }

	public bool HasOverlay() => false;
	public IVisionOverlay? GetOverlay() => null;
	public IVisionFrame GetFrame() => OriginalFrame;

	internal AttentionOutput(
		MediaPipeOutput mediaPipeOutput,
		bool isLookingAtCamera,
		bool hasStableAttention)
	{
		ArgumentNullException.ThrowIfNull(mediaPipeOutput);
		mediaPipeOutput.RetainForDownstream();
		MediaPipeOutput = mediaPipeOutput;
		IsLookingAtCamera = isLookingAtCamera;
		HasStableAttention = hasStableAttention;
		HeadYawDegrees =
			mediaPipeOutput.Tracking.LandmarkFrame.HeadYawDegrees;
		HeadPitchDegrees =
			mediaPipeOutput.Tracking.LandmarkFrame.HeadPitchDegrees;
		HeadRollDegrees =
			mediaPipeOutput.Tracking.LandmarkFrame.HeadRollDegrees;
	}

	protected override void DisposeOwnedResources()
	{
		MediaPipeOutput.Dispose();
	}
}
