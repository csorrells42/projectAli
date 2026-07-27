using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

/// <summary>
/// MediaPipe-only module. Its worker pulls the newest camera snapshot, performs
/// the MediaPipe-owned texture readback, submits it to the official MediaPipe
/// Tasks Face Landmarker, owns all temporal measurement state, and publishes
/// one immutable completed result. It never calls or updates a viewport,
/// camera, identity system, or UI.
/// </summary>
public sealed class MediaPipeModule :
	LatestValueModule<CameraOutput, MediaPipeOutput>
{
	private static readonly TimeSpan TrackerRetryInterval =
		TimeSpan.FromSeconds(1);

	private readonly FaceLandmarkTemporalReconstructor _reconstructor = new();

	private readonly FaceLandmarkMetricCalculator _metricCalculator = new();

	private readonly FaceLockStabilityAnalyzer _stabilityAnalyzer = new();

	private MediaPipeOfficialTextureFrameReader? _frameReader;

	private MediaPipeFaceLandmarkerSidecarTracker? _tracker;
	private IModuleOutputSubscription<VisionTargetHintOutput>? _targetHints;
	private readonly SnapshotCursor<VisionTargetHintOutput> _targetHint = new();
	private Rect _activeTargetHint = Rect.Empty;
	private long _activeTargetHintExpiresAt;

	private long _nextTrackerRetryTimestamp;

	public MediaPipeModule(
		IModuleOutputSource<CameraOutput> camera)
		: base(
			camera,
			"Avatar Builder MediaPipe latest-frame producer")
	{
		_tracker = CreateTracker();
		if (!_tracker.IsAvailable)
		{
			string status = _tracker.Name +
				" is unavailable. Install the bundled MediaPipe Tasks " +
				"runtime before starting the camera.";
			_tracker.Dispose();
			_tracker = null;
			throw new InvalidOperationException(status);
		}
	}

	public long CompletedFrames => CompletedOutputs;

	public long SkippedFrames => DroppedInputs;

	public long FailedFrames => FailedOutputs;

	/// <summary>
	/// Connects one optional latest-value steering source. The source is sampled
	/// only by the existing MediaPipe worker immediately before detection; it
	/// never wakes, waits for, or calls the steering producer.
	/// </summary>
	public void ConnectTargetHints(
		IModuleOutputSource<VisionTargetHintOutput> source)
	{
		ArgumentNullException.ThrowIfNull(source);
		IModuleOutputSubscription<VisionTargetHintOutput> subscription =
			source.Subscribe();
		if (Interlocked.CompareExchange(
			ref _targetHints,
			subscription,
			null) is not null)
		{
			subscription.Dispose();
			throw new InvalidOperationException(
				"MediaPipe target hints are already connected.");
		}
	}

	protected override MediaPipeOutput Process(
		CameraOutput input)
	{
		var frame = input.OriginalFrame;

		try
		{
			UpdateTargetHint();
			FaceLandmarkTrackingResult tracking = Detect(frame);
			FaceLandmarkFrame observed =
				tracking.LandmarkFrame.HasFace
					? tracking.LandmarkFrame
					: tracking.FeatureDetection.ToLandmarkFrame(
						input.CapturedAtUtc);

			FaceLandmarkFrame reconstructed;
			FaceLandmarkMetrics metrics;
			FaceLockStabilityAnalysis stability;
			if (!tracking.FeatureDetection.HasFace)
			{
				ResetAnalysis();
				reconstructed = FaceLandmarkFrame.None;
				metrics = FaceLandmarkMetrics.None;
				stability = FaceLockStabilityAnalysis.Waiting;
			}
			else
			{
				// This is the validated MediaPipe behavior: a dense measured
				// frame is not run through the sparse temporal reconstructor.
				reconstructed = observed.HasDenseMesh
					? observed
					: _reconstructor.Update(observed);
				metrics = _metricCalculator.Update(reconstructed);
				stability = _stabilityAnalyzer.Update(
					tracking.FeatureDetection,
					reconstructed,
					metrics);
			}

			return new MediaPipeOutput(
				input,
				tracking,
				observed,
				reconstructed,
				metrics,
				stability);
		}
		catch
		{
			throw;
		}
	}

	protected override void OnProcessingFailure(Exception exception)
	{
		DisposeTracker();
		ResetAnalysis();
		Volatile.Write(
			ref _nextTrackerRetryTimestamp,
			Stopwatch.GetTimestamp()
				+ (long)(TrackerRetryInterval.TotalSeconds
					* Stopwatch.Frequency));
		base.OnProcessingFailure(exception);
	}

	protected override void DisposeModule()
	{
		Interlocked.Exchange(ref _targetHints, null)?.Dispose();
		_targetHint.Dispose();
		DisposeTracker();
		ResetAnalysis();
	}

	private FaceLandmarkTrackingResult Detect(
		TextureNativeFrameLease frame)
	{
		long retryAt = Volatile.Read(ref _nextTrackerRetryTimestamp);
		if (_tracker is null
			&& retryAt != 0L
			&& Stopwatch.GetTimestamp() < retryAt)
		{
			throw new InvalidOperationException(
				"Official MediaPipe Tasks is waiting for its bounded retry.");
		}

		if (_tracker is null)
		{
			_tracker = CreateTracker();
			Volatile.Write(ref _nextTrackerRetryTimestamp, 0L);
		}
		if (_frameReader is null || !_frameReader.CanRead(frame))
		{
			_frameReader?.Dispose();
			_frameReader =
				new MediaPipeOfficialTextureFrameReader(frame);
		}

		BitmapSource bitmap = _frameReader.ReadBgra(
			frame,
			1920);
		Rect targetHint = _activeTargetHint;
		long hintExpiry = Volatile.Read(ref _activeTargetHintExpiresAt);
		if (!targetHint.IsEmpty
			&& Stopwatch.GetTimestamp() < hintExpiry)
		{
			return _tracker.DetectFaceCrop(
				bitmap,
				targetHint,
				frame.CapturedAtUtc == default
					? DateTime.UtcNow
					: frame.CapturedAtUtc);
		}
		return _tracker.Detect(
			bitmap,
			frame.CapturedAtUtc == default
				? DateTime.UtcNow
				: frame.CapturedAtUtc);
	}

	private void UpdateTargetHint()
	{
		IModuleOutputSubscription<VisionTargetHintOutput>? source =
			Volatile.Read(ref _targetHints);
		if (source is null || !source.TryTake(_targetHint))
		{
			return;
		}
		try
		{
			VisionTargetHintOutput hint = _targetHint.Current;
			if (!hint.HasValidRegion
				|| hint.Confidence < 0.60d
				|| Stopwatch.GetTimestamp() >= hint.ExpiresAtTimestamp)
			{
				_activeTargetHint = Rect.Empty;
				Volatile.Write(ref _activeTargetHintExpiresAt, 0L);
				return;
			}
			_activeTargetHint = new Rect(
				hint.Left,
				hint.Top,
				hint.Right - hint.Left,
				hint.Bottom - hint.Top);
			Volatile.Write(
				ref _activeTargetHintExpiresAt,
				hint.ExpiresAtTimestamp);
		}
		finally
		{
			_targetHint.Release();
		}
	}

	private void DisposeTracker()
	{
		MediaPipeFaceLandmarkerSidecarTracker? tracker = _tracker;
		_tracker = null;
		tracker?.Dispose();
		MediaPipeOfficialTextureFrameReader? reader = _frameReader;
		_frameReader = null;
		reader?.Dispose();
	}

	private static MediaPipeFaceLandmarkerSidecarTracker CreateTracker()
	{
		return new MediaPipeFaceLandmarkerSidecarTracker()
		{
			MaxDetectionDimension = 1920
		};
	}

	private void ResetAnalysis()
	{
		_reconstructor.Reset();
		_metricCalculator.Reset();
		_stabilityAnalyzer.Reset();
	}
}
