using System;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Attention;
using AvatarBuilder.Modules.Vision.TargetSelection;
using AvatarBuilder.Modules.Viewports.Contracts;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Vision.Overlays;

/// <summary>
/// The single immutable output class published by OverlayModule.
/// </summary>
public sealed class OverlayOutput :
	ModuleOutput,
	IOverlayFrameSnapshot,
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

	public FaceLandmarkTrackingResult Tracking =>
		MediaPipeOutput.Tracking;

	public FaceLandmarkFrame ObservedLandmarks =>
		MediaPipeOutput.ObservedLandmarks;

	public FaceLandmarkFrame ReconstructedLandmarks =>
		MediaPipeOutput.ReconstructedLandmarks;

	public FaceLandmarkMetrics Metrics =>
		MediaPipeOutput.Metrics;

	public FaceLockStabilityAnalysis Stability =>
		MediaPipeOutput.Stability;

	public bool HasStableAttention { get; }

	public TargetSelectionOutput? TargetSelectionOutput { get; }

	public AttentionOutput? AttentionOutput { get; }

	public PreviewOverlayLayers EnabledLayers { get; }

	public PreviewOverlayStack Overlays { get; }

	public bool HasOverlay()
	{
		return Overlays.HasContent;
	}

	public IVisionOverlay GetOverlay()
	{
		return Overlays;
	}

	public IVisionFrame GetFrame()
	{
		return OriginalFrame;
	}

	public bool IsOverlayEnabled(PreviewOverlayLayers layer)
	{
		return (EnabledLayers & layer) != 0;
	}

	internal OverlayOutput(
		MediaPipeOutput input,
		TargetSelectionOutput? targetSelectionOutput,
		AttentionOutput? attentionOutput,
		PreviewOverlayStack overlays,
		PreviewOverlayLayers enabledLayers)
	{
		ArgumentNullException.ThrowIfNull(input);
		Overlays = overlays
			?? throw new ArgumentNullException(nameof(overlays));
		EnabledLayers = enabledLayers;
		TargetSelectionOutput = targetSelectionOutput;
		AttentionOutput = attentionOutput;
		HasStableAttention =
			attentionOutput?.HasStableAttention == true;
		input.RetainForDownstream();
		targetSelectionOutput?.RetainForDownstream();
		attentionOutput?.RetainForDownstream();
		MediaPipeOutput = input;
	}

	protected override void DisposeOwnedResources()
	{
		AttentionOutput?.Dispose();
		TargetSelectionOutput?.Dispose();
		MediaPipeOutput.Dispose();
	}
}
