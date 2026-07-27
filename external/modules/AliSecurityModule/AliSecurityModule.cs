using System;
using System.Diagnostics;
using System.Threading;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Audio.WakeWord;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.Attention;
using AvatarBuilder.Modules.Vision.TargetSelection;

namespace AvatarBuilder.Modules.Security;

/// <summary>
/// Consumes only the approved target, attention, speaker, wake, PTT, and login
/// evidence. Speaker owns the single retained utterance path into security.
/// </summary>
public sealed class AliSecurityModule :
	IAudioModule,
	IModuleOutputSource<AuthorizedInteractionOutput>,
	IDisposable
{
	private readonly IModuleOutputSubscription<TargetSelectionOutput> _target;
	private readonly IModuleOutputSubscription<AttentionOutput> _attention;
	private readonly IModuleOutputSubscription<SpeakerRecognitionOutput> _speaker;
	private readonly IModuleOutputSubscription<WakeWordOutput> _wake;
	private readonly ModuleOutputBroadcaster<AuthorizedInteractionOutput> _output = new();
	private readonly ManualResetEvent _stop = new(false);
	private readonly Thread _worker;
	private readonly FrameModuleTiming _timing = new();
	private TargetAuthorizationEvidence _currentTarget =
		TargetAuthorizationEvidence.None;
	private bool _hasAttention;
	private LoginEvidence _login = LoginEvidence.Unavailable;
	private PushToTalkEvidence _ptt = new(false, false);
	private readonly PushToTalkUtteranceGrant _pttGrant = new();
	private int _started;
	private int _disposed;
	private string _lastFailure = "";

	public string LastFailure => Volatile.Read(ref _lastFailure);

	public SecurityDecision LatestDecision { get; private set; } =
		AliSecurityPolicy.Evaluate(
			new(false, false),
			LoginEvidence.Unavailable,
			TargetAuthorizationEvidence.None,
			false,
			new(false, "", 0, ""),
			new(false, "Ali", 0, ""));

	public AliSecurityModule(
		IModuleOutputSource<TargetSelectionOutput> target,
		IModuleOutputSource<AttentionOutput> attention,
		IModuleOutputSource<SpeakerRecognitionOutput> speaker,
		IModuleOutputSource<WakeWordOutput> wake)
	{
		_target = target.Subscribe();
		_attention = attention.Subscribe();
		_speaker = speaker.Subscribe();
		_wake = wake.Subscribe();
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = "Ali security evidence join",
			Priority = ThreadPriority.AboveNormal
		};
	}

	public IModuleOutputSubscription<AuthorizedInteractionOutput> Subscribe() =>
		_output.Subscribe();

	public void UpdateLoginEvidence(LoginEvidence evidence) =>
		Volatile.Write(ref _login, evidence);

	public void UpdatePushToTalk(bool enabled, bool pressed) =>
		UpdatePushToTalkCore(enabled, pressed);

	private void UpdatePushToTalkCore(bool enabled, bool pressed)
	{
		_pttGrant.Update(enabled, pressed, Stopwatch.GetTimestamp());
		Volatile.Write(
			ref _ptt,
			new PushToTalkEvidence(enabled, pressed));
		RefreshCurrentGateDecision();
	}

	private void RefreshCurrentGateDecision()
	{
		LatestDecision = AliSecurityPolicy.Evaluate(
			Volatile.Read(ref _ptt),
			Volatile.Read(ref _login),
			Volatile.Read(ref _currentTarget),
			Volatile.Read(ref _hasAttention),
			new SpeakerRecognitionEvidence(
				false,
				"",
				0,
				"No current utterance"),
			new WakeWordEvidence(
				false,
				"",
				0,
				"No current utterance"));
	}

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
		using SnapshotCursor<TargetSelectionOutput> targetCursor = new();
		using SnapshotCursor<AttentionOutput> attentionCursor = new();
		using SnapshotCursor<SpeakerRecognitionOutput> speakerCursor = new();
		using SnapshotCursor<WakeWordOutput> wakeCursor = new();
		SpeakerRecognitionOutput? speaker = null;
		WakeWordOutput? wake = null;
		WaitHandle[] signals =
		[
			_target.OutputAvailable,
			_attention.OutputAvailable,
			_speaker.OutputAvailable,
			_wake.OutputAvailable,
			_stop
		];
		while (Volatile.Read(ref _disposed) == 0)
		{
			int signal = WaitHandle.WaitAny(signals);
			if (signal == 4)
			{
				break;
			}
			bool stateChanged = false;
			if (signal == 0 && _target.TryTake(targetCursor))
			{
				TargetSelectionOutput target = targetCursor.Current;
				Volatile.Write(
					ref _currentTarget,
					new TargetAuthorizationEvidence(
						target.HasTarget,
						target.IsAuthorized,
						target.PersonIdentityId,
						target.DisplayName,
						target.IdentityConfidence));
				stateChanged = true;
			}
			if (signal == 1 && _attention.TryTake(attentionCursor))
			{
				Volatile.Write(
					ref _hasAttention,
					attentionCursor.Current.HasStableAttention);
				stateChanged = true;
			}
			if (signal == 2 && _speaker.TryTake(speakerCursor))
			{
				speaker = speakerCursor.Current;
			}
			if (signal == 3 && _wake.TryTake(wakeCursor))
			{
				wake = wakeCursor.Current;
			}
			if (speaker is null || wake is null)
			{
				if (stateChanged)
				{
					RefreshCurrentGateDecision();
				}
				continue;
			}
			if (speaker.SequenceId != wake.SequenceId)
			{
				if (speaker.SequenceId < wake.SequenceId)
				{
					speakerCursor.Release();
					speaker = null;
				}
				else
				{
					wakeCursor.Release();
					wake = null;
				}
				continue;
			}
			try
			{
				_timing.WorkStarted(Stopwatch.GetTimestamp());
				PushToTalkEvidence currentPtt = Volatile.Read(ref _ptt);
				bool utteranceWasPtt = _pttGrant.Overlaps(speaker.Utterance);
				SecurityDecision decision = AliSecurityPolicy.Evaluate(
					new PushToTalkEvidence(
						currentPtt.IsEnabled || utteranceWasPtt,
						currentPtt.IsPressed || utteranceWasPtt),
					Volatile.Read(ref _login),
					Volatile.Read(ref _currentTarget),
					Volatile.Read(ref _hasAttention),
					speaker.Evidence,
					wake.Evidence);
				LatestDecision = decision;
				if (decision.AllowSpeechToText)
				{
					_output.Publish(new AuthorizedInteractionOutput(
						speaker.Utterance,
						decision));
				}
				_timing.FrameMovedOut(Stopwatch.GetTimestamp());
				Volatile.Write(ref _lastFailure, "");
			}
			catch (Exception exception)
			{
				Volatile.Write(ref _lastFailure, exception.ToString());
			}
			finally
			{
				speaker = null;
				wake = null;
				speakerCursor.Release();
				wakeCursor.Release();
			}
		}
	}

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
		_speaker.Dispose();
		_wake.Dispose();
		_attention.Dispose();
		_target.Dispose();
		_output.Dispose();
		_stop.Dispose();
	}
}
