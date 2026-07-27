using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Viewports.Contracts;
using AvatarBuilder.Modules.Viewports.DirectX12;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Producer;
using AvatarBuilder.Modules.Webcam;

namespace AvatarBuilder.Modules.Viewports;

/// <summary>
/// Optional terminal viewport module. It knows only how to pull immutable
/// texture snapshots and render the frame with overlay data from the same
/// FrameId. It has no camera, MediaPipe, identity, or application knowledge.
/// </summary>
public sealed class ViewportModule<TSnapshot> :
	IDx12ViewportModule,
	IVisionModule
	where TSnapshot :
		ModuleOutput,
		IVisionModuleOutput
{
	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

	private readonly IModuleOutputSubscription<CameraOutput> _frames;

	private readonly IModuleOutputSubscription<TSnapshot> _overlays;

	private readonly ManualResetEvent _stopSignal =
		new(initialState: false);

	private readonly WaitHandle[] _wakeSignals;

	private readonly Thread _worker;

	private readonly FrameModuleTiming _timing = new();

	private PresentationSettings _settings = new(
		VideoFrameColorSettings.Off,
		false,
		2d);

	private int _stopping;

	private int _started;

	private long _submittedFrameId;

	public Direct3D12PreviewHost Host { get; }

	public long SubmittedFrameId =>
		Interlocked.Read(ref _submittedFrameId);

	public long LastPresentedFrameTimestamp =>
		Host.LastRenderedFrameTimestamp;

	public Direct3D12PreviewDiagnostics Diagnostics => Host.Diagnostics;

	public TimeSpan TimeWaited => _timing.TimeWaited;

	public TimeSpan TimeWorked => _timing.TimeWorked;

	public event EventHandler<string>? StatusChanged;

	public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

	public ViewportModule(
		IModuleOutputSource<CameraOutput> frames,
		IModuleOutputSource<TSnapshot> overlays,
		nint nativeD3D12Device = 0)
	{
		ArgumentNullException.ThrowIfNull(frames);
		ArgumentNullException.ThrowIfNull(overlays);
		_frames = frames.Subscribe();
		_overlays = overlays.Subscribe();
		_wakeSignals =
		[
			_frames.OutputAvailable,
			_stopSignal
		];
		Host = new Direct3D12PreviewHost(nativeD3D12Device)
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch
		};
		Host.StatusChanged += HostStatusChanged;
		Host.DiagnosticsChanged += HostDiagnosticsChanged;
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = "Avatar Builder DX12 viewport module",
			Priority = ThreadPriority.AboveNormal
		};
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

	public TimeSpan GetIdleTime()
	{
		return TimeWaited;
	}

	public TimeSpan GetWorkingTime()
	{
		return TimeWorked;
	}

	public void ConfigurePresentation(
		VideoFrameColorSettings colorSettings,
		bool denoiseEnabled,
		double denoiseStrength,
		double maximumFramesPerSecond = 0d)
	{
		Volatile.Write(
			ref _settings,
			new PresentationSettings(
				colorSettings,
				denoiseEnabled,
				Math.Clamp(denoiseStrength, 0.5d, 5d)));
		Host.LimitRenderRate(maximumFramesPerSecond);
	}

	public void SetRecordingMode(string recordingMode)
	{
		Host.SetRecordingMode(recordingMode);
	}

	public void Resume()
	{
		Host.ResumeRendering();
	}

	public void Suspend()
	{
		Host.SuspendRendering();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopping, 1) != 0)
		{
			return;
		}
		_stopSignal.Set();
		if (Volatile.Read(ref _started) != 0
			&& _worker != Thread.CurrentThread)
		{
			_worker.Join(StopTimeout);
		}
		_stopSignal.Dispose();
		_overlays.Dispose();
		_frames.Dispose();
		Host.StatusChanged -= HostStatusChanged;
		Host.DiagnosticsChanged -= HostDiagnosticsChanged;
		if (Host.Dispatcher.CheckAccess())
		{
			Host.Dispose();
		}
		else
		{
			Host.Dispatcher.Invoke(Host.Dispose);
		}
	}

	private void WorkerLoop()
	{
		using SnapshotCursor<CameraOutput> frameCursor = new();
		using SnapshotCursor<TSnapshot> overlayCursor = new();
		while (Volatile.Read(ref _stopping) == 0)
		{
			int signalIndex;
			try
			{
				signalIndex = WaitHandle.WaitAny(_wakeSignals);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			if (signalIndex == 1
				|| Volatile.Read(ref _stopping) != 0)
			{
				break;
			}
			if (!_frames.TryTake(frameCursor))
			{
				continue;
			}
			CameraOutput frameSnapshot = frameCursor.Current;
			if (_overlays.OutputAvailable.WaitOne(0))
			{
				_overlays.TryTake(overlayCursor);
			}
			_timing.WorkStarted(Stopwatch.GetTimestamp());
			try
			{
				IVisionOverlay? overlay =
					overlayCursor.HasValue
						? overlayCursor.Current.GetOverlay()
						: null;
				if (frameSnapshot.GetFrame()
					is not TextureNativeFrameLease frame)
				{
					throw new InvalidOperationException(
						"Viewport received an unsupported vision frame.");
				}
				PreviewOverlayStack overlays =
					overlay as PreviewOverlayStack
					?? PreviewOverlayStack.Empty;
				PresentationSettings settings =
					Volatile.Read(ref _settings);
				Host.RenderTextureFrame(
					frame,
					settings.DenoiseEnabled,
					settings.DenoiseStrength,
					settings.ColorSettings,
					overlays);
				Interlocked.Exchange(
					ref _submittedFrameId,
					frameSnapshot.FrameId);
				_timing.FrameMovedOut(
					Stopwatch.GetTimestamp());
			}
			catch (ObjectDisposedException)
			{
				if (Volatile.Read(ref _stopping) == 0)
				{
					StatusChanged?.Invoke(
						this,
						"Viewport skipped a disposed source snapshot.");
				}
			}
			catch (Exception ex)
			{
				StatusChanged?.Invoke(
					this,
					"Viewport skipped one frame: " + ex.Message);
			}
			finally
			{
				frameCursor.Release();
			}
		}
	}

	private void HostStatusChanged(object? sender, string status)
	{
		StatusChanged?.Invoke(this, status);
	}

	private void HostDiagnosticsChanged(
		object? sender,
		Direct3D12PreviewDiagnostics diagnostics)
	{
		DiagnosticsChanged?.Invoke(this, diagnostics);
	}

	private sealed record PresentationSettings(
		VideoFrameColorSettings ColorSettings,
		bool DenoiseEnabled,
		double DenoiseStrength);
}
