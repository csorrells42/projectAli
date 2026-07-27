using System;
using System.Diagnostics;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.TargetSelection;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Vision.Overlays;

/// <summary>
/// The only overlay module in the live pipeline. It consumes one completed
/// frame containing MediaPipe and optional identity data,
/// paints exactly the frame-bound layers selected by the user, then publishes
/// one immutable package directly to the viewport.
/// </summary>
public sealed class OverlayModule :
	LatestValueModule<MediaPipeOutput, OverlayOutput>
{
	// This module's output is synchronously bound to the exact frame consumed
	// by the viewport. Wall-clock expiration belonged to the former
	// asynchronous side-channel design and can incorrectly hide valid layers
	// after upstream analysis time. FrameId equality is the validity rule.
	private static readonly TimeSpan MaximumOverlayAge =
		TimeSpan.Zero;

	private readonly PreviewOverlaySelection _selection;
	private readonly IModuleOutputSubscription<TargetSelectionOutput> _targets;
	private readonly IModuleOutputSubscription<AttentionOutput> _attention;
	private readonly SnapshotCursor<TargetSelectionOutput> _targetCursor = new();
	private readonly SnapshotCursor<AttentionOutput> _attentionCursor = new();

	public OverlayModule(
		IModuleOutputSource<MediaPipeOutput> input,
		PreviewOverlaySelection selection,
		IModuleOutputSource<TargetSelectionOutput> targets,
		IModuleOutputSource<AttentionOutput> attention)
		: base(
			input,
			"Avatar Builder composite overlay producer")
	{
		_selection = selection
			?? throw new ArgumentNullException(nameof(selection));
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(attention);
		_targets = targets.Subscribe();
		_attention = attention.Subscribe();
	}

	protected override OverlayOutput Process(
		MediaPipeOutput input)
	{
		if (_targets.OutputAvailable.WaitOne(0))
		{
			_targets.TryTake(_targetCursor);
		}
		if (_attention.OutputAvailable.WaitOne(0))
		{
			_attention.TryTake(_attentionCursor);
		}
		TargetSelectionOutput? targetOutput =
			_targetCursor.HasValue ? _targetCursor.Current : null;
		AttentionOutput? attentionOutput =
			_attentionCursor.HasValue ? _attentionCursor.Current : null;
		bool attention =
			attentionOutput?.HasStableAttention == true;
		try
		{
			// The composite module is the sole rendering authority, so it reads
			// the current menu selection itself. Selection state is never
			// inferred from an older upstream frame.
			PreviewOverlayLayers enabledLayers =
				_selection.EnabledLayers;
			PreviewOverlayStack overlays =
				PreviewOverlayStack.Empty;
			if (IsEnabled(
				enabledLayers,
				PreviewOverlayLayers.Tracking))
			{
				overlays = TryDecorate(
					overlays,
					() => CreateTrackingOverlay(
						input,
						targetOutput));
			}
			if (IsEnabled(
				enabledLayers,
				PreviewOverlayLayers.FaceMesh))
			{
				overlays = TryDecorate(
					overlays,
					() => CreateFaceMeshOverlay(input));
			}
			overlays = TryDecorate(
				overlays,
				() => CreateAttentionOverlay(input, attention));
			return new OverlayOutput(
				input,
				targetOutput,
				attentionOutput,
				overlays,
				enabledLayers);
		}
		catch
		{
			// Even a failure in the common pass-through must publish the
			// completed frame. The viewport never waits for decoration.
			return new OverlayOutput(
				input,
				targetOutput,
				attentionOutput,
				PreviewOverlayStack.Empty,
				_selection.EnabledLayers);
		}
	}

	private static bool IsEnabled(
		PreviewOverlayLayers enabledLayers,
		PreviewOverlayLayers layer)
	{
		return (enabledLayers & layer) != 0;
	}

	private static PreviewOverlayStack TryDecorate(
		PreviewOverlayStack overlays,
		Func<IPreviewOverlay?> create)
	{
		try
		{
			IPreviewOverlay? layer = create();
			return layer is not null && layer.HasContent
				? overlays.Decorate(layer)
				: overlays;
		}
		catch
		{
			return overlays;
		}
	}

	private PreviewTrackingOverlay CreateTrackingOverlay(
		MediaPipeOutput input,
		TargetSelectionOutput? targetOutput)
	{
		PreviewTrackingOverlay tracking = TrackingOverlayFactory.Create(
			input.Tracking.FeatureDetection,
			input.ReconstructedLandmarks,
			input.CapturedAtTimestamp,
			MaximumOverlayAge,
			trackingRegion: null,
			includeFaceBox: false) with
		{
			FrameId = input.FrameId,
			CapturedAtUtc = input.CapturedAtUtc
		};
		if (!input.Tracking.HasFace
			|| !input.Tracking.FeatureDetection.HasFace)
		{
			return tracking;
		}
		System.Windows.Rect measuredBox =
			input.Tracking.FeatureDetection.FaceBox;
		bool identified = targetOutput is
			{
				HasTarget: true,
				HasMediaPipeLock: true
			};
		return tracking with
		{
			TrackedPersonBounds =
				TrackingOverlayFactory.CreateTrackedPersonLabelAnchor(
					input.ReconstructedLandmarks,
					measuredBox),
			TrackedPersonLabel = identified
				? GetIdentityLabel(targetOutput!)
				: "Unknown",
			IsTrackedPersonIdentified = identified
		};
	}

	private static string GetIdentityLabel(
		TargetSelectionOutput target)
	{
		if (string.IsNullOrWhiteSpace(target.DisplayName)
			|| target.DisplayName.StartsWith(
				"Remembered person ",
				StringComparison.OrdinalIgnoreCase))
		{
			return "Unknown";
		}
		return target.DisplayName.Trim();
	}

	private static PreviewFaceMeshOverlay? CreateFaceMeshOverlay(
		MediaPipeOutput input)
	{
		PreviewOverlayMesh? mesh =
			MediaPipePreviewOverlayFactory.CreateMesh(
				input.ReconstructedLandmarks.DenseMeshPoints);
		return mesh is null
			? null
			: new PreviewFaceMeshOverlay
			{
				FrameId = input.FrameId,
				CapturedAtTimestamp = input.CapturedAtTimestamp,
				CapturedAtUtc = input.CapturedAtUtc,
				FaceMesh = mesh,
				MaximumAge = MaximumOverlayAge
			};
	}

	private static PreviewAttentionOverlay CreateAttentionOverlay(
		MediaPipeOutput input,
		bool stableAttention)
	{
		return new PreviewAttentionOverlay
		{
			FrameId = input.FrameId,
			CapturedAtTimestamp = input.CapturedAtTimestamp,
			CapturedAtUtc = input.CapturedAtUtc,
			Indicator = new PreviewAttentionIndicator(
				stableAttention),
			MaximumAge = MaximumOverlayAge
		};
	}

	protected override void DisposeModule()
	{
		_attentionCursor.Dispose();
		_targetCursor.Dispose();
		_attention.Dispose();
		_targets.Dispose();
	}

}
