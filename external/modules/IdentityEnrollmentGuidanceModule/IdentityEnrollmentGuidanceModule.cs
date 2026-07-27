using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.MediaPipe;

namespace AvatarBuilder.Modules.Vision.IdentityEnrollment;

/// <summary>
/// Coordinates an explicitly started identity enrollment. It consumes only
/// published MediaPipe pose, asks IdentityModule to capture only after the
/// requested pose is stable, and speaks latest-value local guidance. It never
/// modifies or delays either producer.
/// </summary>
public sealed class IdentityEnrollmentGuidanceModule :
	IVisionModule,
	IDisposable
{
	private static readonly TimeSpan StopTimeout =
		TimeSpan.FromSeconds(3);

	private static readonly TimeSpan PoseStableDuration =
		TimeSpan.FromMilliseconds(350);

	private static readonly TimeSpan CountdownStepDuration =
		TimeSpan.FromMilliseconds(700);

	private readonly IModuleOutputSubscription<MediaPipeOutput> _tracking;

	private readonly IPersonIdentityReviewService _identity;

	private readonly ManualResetEvent _stop = new(false);

	private readonly AutoResetEvent _controlWake = new(false);

	private readonly WaitHandle[] _signals;

	private readonly Thread _worker;

	private readonly object _stateLock = new();

	private IdentityEnrollmentGuidanceState _state =
		IdentityEnrollmentGuidanceState.Waiting;

	private string? _pendingSpeech;

	private long _matchingSinceTimestamp;

	private long _captureDueTimestamp;

	private int _countdownValue;

	private int _activePoseIndex = -1;

	private int _captureRequestedPoseIndex = -1;

	private int _lastPromptedPoseIndex = -1;

	private bool _holdAnnounced;

	private bool _completionAnnounced;

	private int _started;

	private int _stopping;

	private long _idleStopwatchTicks;

	private long _workingStopwatchTicks;

	public IdentityEnrollmentGuidanceModule(
		IModuleOutputSource<MediaPipeOutput> tracking,
		IPersonIdentityReviewService identity)
	{
		ArgumentNullException.ThrowIfNull(tracking);
		_identity = identity
			?? throw new ArgumentNullException(nameof(identity));
		_tracking = tracking.Subscribe();
		_signals = [_tracking.OutputAvailable, _controlWake, _stop];
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = "Identity enrollment guidance worker",
			Priority = ThreadPriority.BelowNormal
		};
		_worker.SetApartmentState(ApartmentState.STA);
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _stopping) != 0,
			this);
		if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
		{
			_worker.Start();
		}
	}

	public TimeSpan GetIdleTime() => StopwatchTicksToTimeSpan(
		Interlocked.Read(ref _idleStopwatchTicks));

	public TimeSpan GetWorkingTime() => StopwatchTicksToTimeSpan(
		Interlocked.Read(ref _workingStopwatchTicks));

	public IdentityReviewUpdateResult BeginEnrollment(
		IdentityEnrollmentRequest request)
	{
		ObjectDisposedException.ThrowIf(
			Volatile.Read(ref _stopping) != 0,
			this);
		IdentityReviewUpdateResult result =
			_identity.BeginEnrollment(request);
		IdentityEnrollmentState enrollment =
			_identity.GetEnrollmentState();
		ResetPoseProgress();
		_completionAnnounced = false;
		if (result.Success && enrollment.IsActive)
		{
			QueueSpeech(EnrollmentPoseMatcher.PromptFor(
				enrollment.CapturedPoseCount));
			_lastPromptedPoseIndex = enrollment.CapturedPoseCount;
		}
		PublishState(
			enrollment,
			hasFace: false,
			poseConfirmed: false,
			yaw: 0d,
			pitch: 0d,
			roll: 0d,
			result.Status);
		return result;
	}

	public IdentityEnrollmentGuidanceState GetState()
	{
		lock (_stateLock)
		{
			return _state;
		}
	}

	public void CancelEnrollment()
	{
		if (Volatile.Read(ref _stopping) != 0)
		{
			return;
		}
		_identity.CancelEnrollment();
		ResetPoseProgress();
		Interlocked.Exchange(ref _pendingSpeech, "");
		_controlWake.Set();
		lock (_stateLock)
		{
			_state = IdentityEnrollmentGuidanceState.Waiting;
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopping, 1) != 0)
		{
			return;
		}
		_stop.Set();
		if (Volatile.Read(ref _started) != 0
			&& _worker != Thread.CurrentThread)
		{
			_worker.Join(StopTimeout);
		}
		_tracking.Dispose();
		_controlWake.Dispose();
		_stop.Dispose();
		GC.SuppressFinalize(this);
	}

	private void WorkerLoop()
	{
		using var speech = new WindowsSpeechPromptPlayer();
		using var cursor = new SnapshotCursor<MediaPipeOutput>();
		while (Volatile.Read(ref _stopping) == 0)
		{
			int signal;
			long waitStarted = Stopwatch.GetTimestamp();
			try
			{
				signal = WaitHandle.WaitAny(_signals);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			Interlocked.Exchange(
				ref _idleStopwatchTicks,
				Stopwatch.GetTimestamp() - waitStarted);
			if (signal == 2 || Volatile.Read(ref _stopping) != 0)
			{
				break;
			}
			if (signal == 1)
			{
				SpeakPending(speech);
				continue;
			}
			if (!_tracking.TryTake(cursor))
			{
				continue;
			}
			long workStarted = Stopwatch.GetTimestamp();
			try
			{
				ProcessTracking(cursor.Current);
			}
			catch (Exception exception)
			{
				IdentityEnrollmentState enrollment =
					_identity.GetEnrollmentState();
				PublishState(
					enrollment,
					false,
					false,
					0d,
					0d,
					0d,
					"Guided enrollment error: " + exception.Message);
			}
			finally
			{
				Interlocked.Exchange(
					ref _workingStopwatchTicks,
					Stopwatch.GetTimestamp() - workStarted);
				cursor.Release();
			}
			SpeakPending(speech);
		}
		speech.Purge();
	}

	private static TimeSpan StopwatchTicksToTimeSpan(long ticks)
	{
		return TimeSpan.FromSeconds(
			(double)Math.Max(0L, ticks) / Stopwatch.Frequency);
	}

	private void ProcessTracking(MediaPipeOutput tracking)
	{
		IdentityEnrollmentState enrollment =
			_identity.GetEnrollmentState();
		if (!enrollment.IsActive)
		{
			ResetPoseProgress();
			if (!_completionAnnounced
				&& !string.IsNullOrWhiteSpace(
					enrollment.CompletedIdentityId))
			{
				_completionAnnounced = true;
				QueueSpeech("Enrollment complete.");
			}
			PublishState(
				enrollment,
				false,
				false,
				0d,
				0d,
				0d,
				enrollment.Status);
			return;
		}

		int poseIndex = Math.Clamp(
			enrollment.CapturedPoseCount,
			0,
			EnrollmentPoseMatcher.RequiredPoseCount - 1);
		if (poseIndex != _activePoseIndex)
		{
			ResetPoseProgress();
			_activePoseIndex = poseIndex;
		}
		if (poseIndex != _lastPromptedPoseIndex)
		{
			QueueSpeech(EnrollmentPoseMatcher.PromptFor(poseIndex));
			_lastPromptedPoseIndex = poseIndex;
		}

		var landmarks = tracking.Tracking.LandmarkFrame;
		bool hasFace = tracking.Tracking.HasFace
			&& landmarks.HasFace;
		double yaw = landmarks.HeadYawDegrees;
		double pitch = landmarks.HeadPitchDegrees;
		double roll = landmarks.HeadRollDegrees;
		if (!hasFace)
		{
			bool cancelCountdown = _holdAnnounced;
			ResetHoldOnly();
			if (cancelCountdown)
			{
				QueueSpeech(EnrollmentPoseMatcher.PromptFor(poseIndex));
			}
			PublishState(
				enrollment,
				false,
				false,
				yaw,
				pitch,
				roll,
				"Keep exactly one face visible to MediaPipe.");
			return;
		}

		bool matches = EnrollmentPoseMatcher.Matches(
			poseIndex,
			yaw,
			pitch,
			roll);
		if (!matches)
		{
			bool cancelCountdown = _holdAnnounced;
			ResetHoldOnly();
			if (cancelCountdown)
			{
				QueueSpeech(EnrollmentPoseMatcher.PromptFor(poseIndex));
			}
			PublishState(
				enrollment,
				true,
				false,
				yaw,
				pitch,
				roll,
				EnrollmentPoseMatcher.PromptFor(poseIndex));
			return;
		}

		long now = Stopwatch.GetTimestamp();
		if (_matchingSinceTimestamp == 0L)
		{
			_matchingSinceTimestamp = now;
		}
		if (Stopwatch.GetElapsedTime(_matchingSinceTimestamp, now)
			< PoseStableDuration)
		{
			PublishState(
				enrollment,
				true,
				true,
				yaw,
				pitch,
				roll,
				"Good. Keep that position steady.");
			return;
		}

		if (!_holdAnnounced)
		{
			_holdAnnounced = true;
			_countdownValue = 3;
			_captureDueTimestamp = now
				+ (long)(CountdownStepDuration.TotalSeconds
					* Stopwatch.Frequency);
			QueueSpeech(EnrollmentPoseMatcher.CountdownFor(
				_countdownValue));
		}
		if (now < _captureDueTimestamp
			|| enrollment.CapturePending
			|| _captureRequestedPoseIndex == poseIndex)
		{
			PublishState(
				enrollment,
				true,
				true,
				yaw,
				pitch,
				roll,
				$"Hold still. Capturing in {_countdownValue}.");
			return;
		}
		if (_countdownValue > 1)
		{
			_countdownValue--;
			_captureDueTimestamp = now
				+ (long)(CountdownStepDuration.TotalSeconds
					* Stopwatch.Frequency);
			QueueSpeech(EnrollmentPoseMatcher.CountdownFor(
				_countdownValue));
			PublishState(
				enrollment,
				true,
				true,
				yaw,
				pitch,
				roll,
				$"Hold still. Capturing in {_countdownValue}.");
			return;
		}

		IdentityReviewUpdateResult capture =
			_identity.RequestEnrollmentCapture();
		if (capture.Success)
		{
			_captureRequestedPoseIndex = poseIndex;
		}
		else
		{
			ResetHoldOnly();
		}
		PublishState(
			enrollment,
			true,
			true,
			yaw,
			pitch,
			roll,
			capture.Status);
	}

	private void QueueSpeech(string text)
	{
		Interlocked.Exchange(ref _pendingSpeech, text.Trim());
		_controlWake.Set();
	}

	private void SpeakPending(WindowsSpeechPromptPlayer speech)
	{
		string? text = Interlocked.Exchange(
			ref _pendingSpeech,
			null);
		if (text is null)
		{
			return;
		}
		if (text.Length == 0)
		{
			speech.Purge();
			return;
		}
		speech.SpeakLatest(text);
	}

	private void PublishState(
		IdentityEnrollmentState enrollment,
		bool hasFace,
		bool poseConfirmed,
		double yaw,
		double pitch,
		double roll,
		string status)
	{
		lock (_stateLock)
		{
			_state = new IdentityEnrollmentGuidanceState(
				enrollment.IsActive,
				hasFace,
				poseConfirmed,
				enrollment.CapturedPoseCount,
				enrollment.RequiredPoseCount,
				yaw,
				pitch,
				roll,
				enrollment.IsActive
					? EnrollmentPoseMatcher.PromptFor(
						enrollment.CapturedPoseCount)
					: enrollment.Prompt,
				status,
				enrollment.CompletedIdentityId);
		}
	}

	private void ResetPoseProgress()
	{
		_activePoseIndex = -1;
		_captureRequestedPoseIndex = -1;
		_matchingSinceTimestamp = 0L;
		_captureDueTimestamp = 0L;
		_countdownValue = 0;
		_holdAnnounced = false;
	}

	private void ResetHoldOnly()
	{
		_matchingSinceTimestamp = 0L;
		_captureDueTimestamp = 0L;
		_countdownValue = 0;
		_holdAnnounced = false;
	}

	private sealed class WindowsSpeechPromptPlayer : IDisposable
	{
		private object? _voice;

		public WindowsSpeechPromptPlayer()
		{
			try
			{
				Type? voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
				_voice = voiceType is null
					? null
					: Activator.CreateInstance(voiceType);
			}
			catch
			{
				_voice = null;
			}
		}

		public void SpeakLatest(string text)
		{
			InvokeSpeak(text, 3);
		}

		public void Purge()
		{
			InvokeSpeak("", 3);
		}

		public void Dispose()
		{
			object? voice = _voice;
			_voice = null;
			if (voice is null)
			{
				return;
			}
			try
			{
				voice.GetType().InvokeMember(
					"Speak",
					BindingFlags.InvokeMethod,
					null,
					voice,
					["", 3]);
			}
			catch
			{
			}
			if (Marshal.IsComObject(voice))
			{
				Marshal.FinalReleaseComObject(voice);
			}
		}

		private void InvokeSpeak(string text, int flags)
		{
			object? voice = _voice;
			if (voice is null)
			{
				return;
			}
			try
			{
				voice.GetType().InvokeMember(
					"Speak",
					BindingFlags.InvokeMethod,
					null,
					voice,
					[text, flags]);
			}
			catch
			{
			}
		}
	}
}
