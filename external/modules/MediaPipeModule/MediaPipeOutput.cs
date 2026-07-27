using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class MediaPipeOutput :
	ModuleOutput,
	ITextureFrameSnapshot,
	IVisionModuleOutput
{
	public CameraOutput CameraOutput { get; }

	public long FrameId => CameraOutput.FrameId;

	public long CapturedAtTimestamp => CameraOutput.CapturedAtTimestamp;

	public DateTime CapturedAtUtc => CameraOutput.CapturedAtUtc;

	public TextureFrameReference TextureReference =>
		CameraOutput.TextureReference;

	public TextureNativeFrameLease OriginalFrame =>
		CameraOutput.OriginalFrame;

	public FaceLandmarkTrackingResult Tracking { get; }

	public FaceLandmarkFrame ObservedLandmarks { get; }

	public FaceLandmarkFrame ReconstructedLandmarks { get; }

	public FaceLandmarkMetrics Metrics { get; }

	public FaceLockStabilityAnalysis Stability { get; }

	public bool HasFace => Tracking.HasFace;

	public bool HasOverlay()
	{
		return false;
	}

	public IVisionOverlay? GetOverlay()
	{
		return null;
	}

	public IVisionFrame GetFrame()
	{
		return OriginalFrame;
	}

	internal MediaPipeOutput(
		CameraOutput cameraOutput,
		FaceLandmarkTrackingResult tracking,
		FaceLandmarkFrame observedLandmarks,
		FaceLandmarkFrame reconstructedLandmarks,
		FaceLandmarkMetrics metrics,
		FaceLockStabilityAnalysis stability)
	{
		ArgumentNullException.ThrowIfNull(cameraOutput);
		Tracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
		ObservedLandmarks = observedLandmarks
			?? throw new ArgumentNullException(nameof(observedLandmarks));
		ReconstructedLandmarks = reconstructedLandmarks
			?? throw new ArgumentNullException(nameof(reconstructedLandmarks));
		Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
		Stability = stability
			?? throw new ArgumentNullException(nameof(stability));
		cameraOutput.RetainForDownstream();
		CameraOutput = cameraOutput;
	}

	protected override void DisposeOwnedResources()
	{
		CameraOutput.Dispose();
	}
}
