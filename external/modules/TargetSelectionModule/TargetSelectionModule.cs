using System.Diagnostics;
using System.Threading;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.MediaPipe;

namespace AvatarBuilder.Modules.Vision.TargetSelection;

/// <summary>
/// Correlates independent visual identity, MediaPipe target continuity, and
/// optional speaker evidence. It publishes immutable target facts only and
/// never calls, modifies, or waits for any producer.
/// </summary>
public sealed class TargetSelectionModule :
	IVisionModule,
	IModuleOutputSource<TargetSelectionOutput>,
	IDisposable
{
	private readonly IModuleOutputSubscription<IdentityOutput> _identity;
	private readonly IModuleOutputSubscription<MediaPipeOutput> _mediaPipe;
	private readonly IModuleOutputSubscription<SpeakerRecognitionOutput>?
		_speaker;
	private readonly ModuleOutputBroadcaster<TargetSelectionOutput> _output =
		new();
	private readonly ModuleOutputBroadcaster<VisionTargetHintOutput>
		_steeringOutput = new();
	private readonly ManualResetEvent _stop = new(false);
	private readonly Thread _worker;
	private readonly FrameModuleTiming _timing = new();
	private readonly TargetLockState _state = new();
	private TargetLockView _lastPublished;
	private bool _hasPublished;
	private long _sequence;
	private long _lastPublishTimestamp;
	private int _started;
	private int _disposed;

	public TargetSelectionModule(
		IModuleOutputSource<IdentityOutput> identity,
		IModuleOutputSource<MediaPipeOutput> mediaPipe)
		: this(identity, mediaPipe, null)
	{
	}

	public TargetSelectionModule(
		IModuleOutputSource<IdentityOutput> identity,
		IModuleOutputSource<MediaPipeOutput> mediaPipe,
		IModuleOutputSource<SpeakerRecognitionOutput>? speaker)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(mediaPipe);
		_identity = identity.Subscribe();
		_mediaPipe = mediaPipe.Subscribe();
		_speaker = speaker?.Subscribe();
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = "Identity, MediaPipe, and speaker target lock"
		};
	}

	public IModuleOutputSubscription<TargetSelectionOutput> Subscribe() =>
		_output.Subscribe();

	public IModuleOutputSource<VisionTargetHintOutput> SteeringOutputSource =>
		_steeringOutput;

	public void Start()
	{
		if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
		{
			_worker.Start();
		}
	}

	public TimeSpan GetIdleTime() => _timing.TimeWaited;

	public TimeSpan GetWorkingTime() => _timing.TimeWorked;

	private void WorkerLoop()
	{
		using SnapshotCursor<IdentityOutput> identity = new();
		using SnapshotCursor<MediaPipeOutput> mediaPipe = new();
		using SnapshotCursor<SpeakerRecognitionOutput> speaker = new();
		WaitHandle[] signals = _speaker is null
			? [_identity.OutputAvailable, _mediaPipe.OutputAvailable, _stop]
			: [
				_identity.OutputAvailable,
				_mediaPipe.OutputAvailable,
				_speaker.OutputAvailable,
				_stop
			];
		int stopIndex = signals.Length - 1;
		while (Volatile.Read(ref _disposed) == 0)
		{
			long beforeWait = Stopwatch.GetTimestamp();
			int timeout = _state.GetWaitTimeoutMilliseconds(beforeWait);
			int signal = WaitHandle.WaitAny(signals, timeout);
			if (signal == stopIndex)
			{
				break;
			}

			bool inputChanged = false;
			if (_speaker is not null && _speaker.TryTake(speaker))
			{
				_state.ObserveSpeaker(
					speaker.Current.ProducedAtTimestamp,
					speaker.Current.Evidence);
				inputChanged = true;
			}
			if (_identity.TryTake(identity))
			{
				_state.ObserveIdentity(
					identity.Current.CapturedAtTimestamp,
					identity.Current.Identity.People);
				inputChanged = true;
			}
			if (_mediaPipe.TryTake(mediaPipe))
			{
				System.Windows.Rect box =
					mediaPipe.Current.Tracking.FeatureDetection.FaceBox;
				_state.ObserveMediaPipe(
					mediaPipe.Current.CapturedAtTimestamp,
					mediaPipe.Current.HasFace
						&& mediaPipe.Current.Tracking.FeatureDetection.HasFace,
					new PersonFaceBox(
						box.Left,
						box.Top,
						box.Right,
						box.Bottom));
				inputChanged = true;
			}

			long now = Stopwatch.GetTimestamp();
			TargetLockView view = _state.Evaluate(now);
			if (!inputChanged && signal != WaitHandle.WaitTimeout)
			{
				continue;
			}
			if (!ShouldPublish(view, now))
			{
				continue;
			}

			_timing.WorkStarted(now);
			long sequence = Interlocked.Increment(ref _sequence);
			_output.Publish(new TargetSelectionOutput(
				sequence,
				view.HasTarget,
				view.UserId,
				view.Username,
				view.LockQuality,
				view.DisplayName,
				view.IsAuthorized,
				view.FaceRegion,
				view.SpeakerCorroborated,
				view.HasIdentityLock,
				view.HasMediaPipeLock,
				view.IsInGracePeriod,
				view.IdentityEvidenceState,
				view.IdentityConfidence,
				view.SearchRequested,
				view.SearchUserId,
				view.SearchFaceRegion,
				view.SearchConfidence,
				view.MediaPipeTrackGeneration,
				view.Status));
			long hintExpiry = now + (long)(
				TimeSpan.FromMilliseconds(750).TotalSeconds
				* Stopwatch.Frequency);
			_steeringOutput.Publish(new VisionTargetHintOutput(
				sequence,
				view.SearchRequested,
				view.SearchUserId,
				view.SearchFaceRegion.Left,
				view.SearchFaceRegion.Top,
				view.SearchFaceRegion.Right,
				view.SearchFaceRegion.Bottom,
				view.SearchConfidence,
				hintExpiry));
			_lastPublished = view;
			_hasPublished = true;
			_lastPublishTimestamp = now;
			_timing.FrameMovedOut(Stopwatch.GetTimestamp());
		}
	}

	private bool ShouldPublish(TargetLockView view, long now)
	{
		if (!_hasPublished)
		{
			return true;
		}
		bool semanticChange =
			view.HasTarget != _lastPublished.HasTarget
			|| !string.Equals(
				view.UserId,
				_lastPublished.UserId,
				StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(
				view.Username,
				_lastPublished.Username,
				StringComparison.Ordinal)
			|| !string.Equals(
				view.DisplayName,
				_lastPublished.DisplayName,
				StringComparison.Ordinal)
			|| view.IsAuthorized != _lastPublished.IsAuthorized
			|| view.SpeakerCorroborated
				!= _lastPublished.SpeakerCorroborated
			|| view.HasIdentityLock != _lastPublished.HasIdentityLock
			|| view.HasMediaPipeLock != _lastPublished.HasMediaPipeLock
			|| view.IsInGracePeriod != _lastPublished.IsInGracePeriod
			|| view.IdentityEvidenceState
				!= _lastPublished.IdentityEvidenceState
			|| view.SearchRequested != _lastPublished.SearchRequested
			|| !string.Equals(
				view.SearchUserId,
				_lastPublished.SearchUserId,
				StringComparison.OrdinalIgnoreCase)
			|| SearchHintChanged(_lastPublished, view)
			|| view.MediaPipeTrackGeneration
				!= _lastPublished.MediaPipeTrackGeneration
			|| Math.Abs(
				view.LockQuality - _lastPublished.LockQuality) >= 0.02d;
		if (semanticChange)
		{
			return true;
		}
		return view.HasTarget
			&& Stopwatch.GetElapsedTime(
				_lastPublishTimestamp,
				now) >= TimeSpan.FromMilliseconds(100);
	}

	internal static bool SearchHintChanged(
		TargetLockView previous,
		TargetLockView current) =>
		!current.SearchFaceRegion.Equals(previous.SearchFaceRegion)
		|| Math.Abs(current.SearchConfidence - previous.SearchConfidence) >= 0.02d;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}
		_stop.Set();
		if (Volatile.Read(ref _started) != 0
			&& _worker != Thread.CurrentThread)
		{
			_worker.Join(TimeSpan.FromSeconds(3));
		}
		_identity.Dispose();
		_mediaPipe.Dispose();
		_speaker?.Dispose();
		_steeringOutput.Dispose();
		_output.Dispose();
		_stop.Dispose();
	}
}
